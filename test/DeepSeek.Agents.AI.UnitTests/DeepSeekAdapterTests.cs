using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.ClientModel;
using System.Threading.Channels;
using DeepSeek;
using DeepSeek.Anthropic;
using DeepSeek.Chat;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using DeepSeek.Agents.AI;
using Xunit;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using WireChatMessage = DeepSeek.Chat.ChatMessage;

namespace DeepSeek.Agents.AI.Tests;

#pragma warning disable MAAI001
public class DeepSeekAdapterTests
{
    [Fact]
    public async Task AsIChatClient_MapsReasoningToolCallsAndUsage()
    {
        var client = new FakeChatClient(new ChatCompletion
        {
            Model = "deepseek-v4-pro",
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = "done",
                        ReasoningContent = "think",
                        ToolCalls =
                        [
                            new ToolCall
                            {
                                Id = "call_weather",
                                Function = new ToolCallFunction
                                {
                                    Name = "get_weather",
                                    Arguments = "{\"city\":\"Hangzhou\"}",
                                },
                            },
                        ],
                    },
                    FinishReason = "tool_calls",
                },
            ],
            Usage = new TokenUsage
            {
                PromptTokens = 10,
                CompletionTokens = 5,
                TotalTokens = 15,
            },
        }).AsIChatClient();

        var response = await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("deepseek-v4-pro", response.ModelId);
        Assert.Equal(15, response.Usage?.TotalTokenCount);
        Assert.Equal("tool_calls", response.AdditionalProperties?["finish_reason"]);
        var assistant = Assert.Single(response.Messages);
        Assert.Equal("done", assistant.Text);
        Assert.Equal("think", assistant.AdditionalProperties?["reasoning_content"]);
        Assert.Single(assistant.Contents.OfType<FunctionCallContent>());
    }

    [Fact]
    public async Task AsIChatClient_MapsAIFunctionDeclarationWithoutServerSideContinuation()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(toolCalls: [CreateToolCall(0, "call_frontend", "show_haiku", "{\"topic\":\"spring\"}")], finishReason: "tool_calls"),
            ]).AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Show a haiku card.")],
            new ChatOptions
            {
                Tools =
                [
                    CreateDeclaration(
                        "show_haiku",
                        "Render a haiku card.",
                        """
                        {
                          "type": "object",
                          "properties": {
                            "topic": { "type": "string" }
                          },
                          "required": ["topic"]
                        }
                        """),
                ],
            }))
        {
            updates.Add(update);
        }

        var request = Assert.Single(RecordingStreamingChatClient.RecordedRequests);
        var tool = Assert.Single(request.Tools!);
        Assert.Equal("show_haiku", tool.Function.Name);
        Assert.Equal("Render a haiku card.", tool.Function.Description);
        Assert.Equal(1, updates.OfType<ChatResponseUpdate>().Count(static update => update.Contents.OfType<FunctionCallContent>().Any()));
        Assert.DoesNotContain(updates, static update => update.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task AsIChatClient_MapsJsonResponseFormatToJsonObjectAndAppendsJsonInstructions()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = """{"ok":true}""",
                    },
                    FinishReason = "stop",
                },
            ],
        }).AsIChatClient();

        await client.GetResponseAsync(
            [new AiChatMessage(ChatRole.User, "Return a machine-readable answer.")],
            new ChatOptions
            {
                Instructions = "Keep the answer concise.",
                ResponseFormat = ChatResponseFormat.Json,
            });

        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.Equal(ChatResponseFormatTypes.JsonObject, request.ResponseFormat?.Type);

        var systemMessage = Assert.Single(request.Messages, static message => message.Role == "system");
        Assert.Contains("Keep the answer concise.", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("valid json", systemMessage.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("single JSON object", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE INPUT:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON OUTPUT:", systemMessage.Content, StringComparison.Ordinal);
        Assert.True(systemMessage.Content!.IndexOf("Keep the answer concise.", StringComparison.Ordinal)
            < systemMessage.Content.IndexOf("You must reply with valid json.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AsIChatClient_MapsJsonSchemaResponseFormatToJsonObjectAndEmbedsSchemaGuidance()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = """{"assistantMessage":"done","state":{"stage":"收集需求中"}}""",
                    },
                    FinishReason = "stop",
                },
            ],
        }).AsIChatClient();

        await client.GetResponseAsync(
            [new AiChatMessage(ChatRole.User, "Summarize the leasing state.")],
            new ChatOptions
            {
                Instructions = "Use only the provided facts.",
                ResponseFormat = ChatResponseFormat.ForJsonSchema<StructuredLeasingSnapshot>(
                    schemaName: "leasing_snapshot",
                    schemaDescription: "Complete leasing state and the next reply."),
            });

        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.Equal(ChatResponseFormatTypes.JsonObject, request.ResponseFormat?.Type);

        var systemMessage = Assert.Single(request.Messages, static message => message.Role == "system");
        Assert.Contains("Use only the provided facts.", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("你必须严格只输出一个合法的 JSON 实例", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("根据它生成一个符合该模式的数据实例", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("以 { 开始", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("以 [ 开始", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"object\"", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("\"assistantMessage\"", systemMessage.Content, StringComparison.Ordinal);
        Assert.Contains("\"state\"", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("{\r\n", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0022", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE INPUT:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("EXAMPLE JSON OUTPUT:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("JSON OUTPUT:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Which is the highest mountain in the world? Mount Everest.", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("""{"question":"Which is the highest mountain in the world?","answer":"Mount Everest"}""", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("You must reply with valid json.", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Return only a single JSON object.", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not wrap the response in Markdown fences.", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Do not add explanatory text before or after the json object.", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Schema name:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Schema description:", systemMessage.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Follow this guidance exactly", systemMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsAIAgent_PreservesSequentialToolHistoryAcrossStreamingRounds()
    {
        var firstServiceCallChunks = new[]
        {
            CreateChunk(reasoning: "Need both tools before answering."),
            CreateChunk(toolCalls: [CreateToolCall(0, "call_alpha", "get_alpha", "{}")], finishReason: "tool_calls"),
        };
        var secondServiceCallChunks = new[]
        {
            CreateChunk(reasoning: "Need both tools before answering."),
            CreateChunk(toolCalls: [CreateToolCall(0, "call_beta", "get_beta", "{}")], finishReason: "tool_calls"),
        };
        var thirdServiceCallChunks = new[]
        {
            CreateChunk(content: "tool-alpha|tool-beta", finishReason: "stop"),
        };
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(firstServiceCallChunks, secondServiceCallChunks, thirdServiceCallChunks);
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "StreamingHistoryAgent",
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(() => "tool-alpha", "get_alpha", "Returns alpha."),
                    AIFunctionFactory.Create(() => "tool-beta", "get_beta", "Returns beta."),
                ],
                AllowMultipleToolCalls = true,
            },
        });

        var session = await agent.CreateSessionAsync(CancellationToken.None);
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(
            "Call both tools before answering.",
            session,
            new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    ToolMode = ChatToolMode.Auto,
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                    AllowMultipleToolCalls = true,
                },
            },
            CancellationToken.None))
        {
            updates.Add(update);
        }

        Assert.Equal(3, RecordingStreamingChatClient.RecordedRequests.Count);
        var secondRequest = RecordingStreamingChatClient.RecordedRequests[1].Messages;
        Assert.Collection(
            secondRequest,
            message => Assert.Equal("user", message.Role),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Need both tools before answering.", message.ReasoningContent);
                Assert.Single(message.ToolCalls!);
                Assert.Equal("call_alpha", message.ToolCalls![0].Id);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_alpha", message.ToolCallId);
                Assert.Equal("\"tool-alpha\"", message.Content);
            });

        Assert.Contains(updates, static update => update.AsChatResponseUpdate().Text == "tool-alpha|tool-beta");
    }

    [Fact]
    public async Task AsIChatClient_DoesNotAutoInvokeSingleExecutableTool()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(
                    reasoning: "Need one tool before answering.",
                    toolCalls: [CreateToolCall(0, "call_alpha", "get_alpha", "{}")],
                    finishReason: "tool_calls"),
            ]).AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Call one tool.")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(() => "tool-alpha", "get_alpha", "Returns alpha."),
                ],
            }))
        {
            updates.Add(update);
        }

        Assert.Single(RecordingStreamingChatClient.RecordedRequests);
        var toolCallUpdate = Assert.Single(updates, static update => update.Contents.OfType<FunctionCallContent>().Any());
        Assert.Equal("Need one tool before answering.", toolCallUpdate.AdditionalProperties?["reasoning_content"]);
        Assert.Single(toolCallUpdate.Contents.OfType<FunctionCallContent>());
        Assert.DoesNotContain(updates, static update => update.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task AsIChatClient_MapsGroupedToolResultsToSeparateWireMessages()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = "done",
                    },
                    FinishReason = "stop",
                },
            ],
        }).AsIChatClient();

        await client.GetResponseAsync(
            [
                new AiChatMessage(ChatRole.User, "Call both tools."),
                CreateAssistantToolCallMessage("Need both tools first.", ("call_alpha", "get_alpha"), ("call_beta", "get_beta")),
                new AiChatMessage(ChatRole.Tool,
                [
                    new FunctionResultContent("call_alpha", "tool-alpha"),
                    new FunctionResultContent("call_beta", "tool-beta"),
                ]),
            ]);

        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.Collection(
            request.Messages,
            message => Assert.Equal("user", message.Role),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Need both tools first.", message.ReasoningContent);
                Assert.Equal(2, message.ToolCalls?.Count);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_alpha", message.ToolCallId);
                Assert.Equal("tool-alpha", message.Content);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_beta", message.ToolCallId);
                Assert.Equal("tool-beta", message.Content);
            });
    }

    [Fact]
    public async Task AsIChatClient_StreamingToolUpdate_DoesNotReplayPriorText()
    {
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(reasoning: "Think first."),
                CreateChunk(content: "Draft answer."),
                CreateChunk(
                    toolCalls: [CreateToolCall(0, "call_weather", "get_weather", "{\"city\":\"Hangzhou\"}")],
                    finishReason: "tool_calls"),
            ]).AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Need weather.")],
            new ChatOptions
            {
                Tools =
                [
                    CreateDeclaration(
                        "get_weather",
                        "Get weather.",
                        """
                        {
                          "type": "object",
                          "properties": {
                            "city": { "type": "string" }
                          },
                          "required": ["city"]
                        }
                        """),
                ],
            }))
        {
            updates.Add(update);
        }

        var finalAssistantUpdate = Assert.Single(updates, static update => update.Contents.OfType<FunctionCallContent>().Any());
        Assert.DoesNotContain(finalAssistantUpdate.Contents, static content => content is TextContent);
        Assert.Equal("Think first.", finalAssistantUpdate.AdditionalProperties?["reasoning_content"]);
        var functionCall = Assert.Single(finalAssistantUpdate.Contents.OfType<FunctionCallContent>());
        Assert.Equal("get_weather", functionCall.Name);
        Assert.Equal(1, updates.Count(static update => update.Text == "Draft answer."));
    }

    [Fact]
    public async Task AsAIAgent_PreservesGroupedToolResultsInSingleContinuationRound()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(reasoning: "Need both tools before answering."),
                CreateChunk(
                    toolCalls:
                    [
                        CreateToolCall(0, "call_alpha", "get_alpha", "{}"),
                        CreateToolCall(1, "call_beta", "get_beta", "{}"),
                    ],
                    finishReason: "tool_calls"),
            ],
            [
                CreateChunk(content: "tool-alpha|tool-beta", finishReason: "stop"),
            ]);
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "GroupedToolRoundAgent",
            RequirePerServiceCallChatHistoryPersistence = true,
            ChatOptions = new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(() => "tool-alpha", "get_alpha", "Returns alpha."),
                    AIFunctionFactory.Create(() => "tool-beta", "get_beta", "Returns beta."),
                ],
                AllowMultipleToolCalls = true,
            },
        });

        var session = await agent.CreateSessionAsync(CancellationToken.None);
        await foreach (var _ in agent.RunStreamingAsync(
            "Call both tools before answering.",
            session,
            new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    ToolMode = ChatToolMode.Auto,
                    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High },
                    AllowMultipleToolCalls = true,
                },
            },
            CancellationToken.None))
        {
        }

        Assert.Equal(2, RecordingStreamingChatClient.RecordedRequests.Count);
        var continuationRequest = RecordingStreamingChatClient.RecordedRequests[1];
        Assert.Collection(
            continuationRequest.Messages,
            message => Assert.Equal("user", message.Role),
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Need both tools before answering.", message.ReasoningContent);
                Assert.Equal(2, message.ToolCalls?.Count);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_alpha", message.ToolCallId);
                Assert.Equal("\"tool-alpha\"", message.Content);
            },
            message =>
            {
                Assert.Equal("tool", message.Role);
                Assert.Equal("call_beta", message.ToolCallId);
                Assert.Equal("\"tool-beta\"", message.Content);
            });
    }

    [Fact]
    public async Task AsIChatClient_BindsIntegerArgumentsToIntParametersDuringGroupedContinuation()
    {
        int? receivedIndex = null;
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(
                    toolCalls:
                    [
                        CreateToolCall(0, "call_step", "update_plan_step", "{\"index\":1}"),
                        CreateToolCall(1, "call_noop", "noop", "{}"),
                    ],
                    finishReason: "tool_calls"),
            ],
            [
                CreateChunk(content: "done", finishReason: "stop"),
            ]).AsIChatClient();

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Update the first step.")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create((int index) =>
                    {
                        receivedIndex = index;
                        return "ok";
                    }, "update_plan_step", "Update the step."),
                    AIFunctionFactory.Create(() => "noop", "noop", "No operation."),
                ],
            }))
        {
        }

        Assert.Equal(1, receivedIndex);
    }

    [Fact]
    public async Task AsIChatClient_PreservesInt64ArgumentsOutsideInt32RangeDuringGroupedContinuation()
    {
        long? receivedIndex = null;
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(
                    toolCalls:
                    [
                        CreateToolCall(0, "call_step", "update_plan_step", "{\"index\":2147483648}"),
                        CreateToolCall(1, "call_noop", "noop", "{}"),
                    ],
                    finishReason: "tool_calls"),
            ],
            [
                CreateChunk(content: "done", finishReason: "stop"),
            ]).AsIChatClient();

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Update the large step.")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create((long index) =>
                    {
                        receivedIndex = index;
                        return "ok";
                    }, "update_plan_step", "Update the step."),
                    AIFunctionFactory.Create(() => "noop", "noop", "No operation."),
                ],
            }))
        {
        }

        Assert.Equal(2147483648L, receivedIndex);
    }

    [Fact]
    public async Task AsIChatClient_StopsAutomaticContinuationForRepeatedGroupedToolRounds()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(
                    toolCalls:
                    [
                        CreateToolCall(0, "call_alpha_1", "get_alpha", "{}"),
                        CreateToolCall(1, "call_beta_1", "get_beta", "{}"),
                    ],
                    finishReason: "tool_calls"),
            ],
            [
                CreateChunk(
                    toolCalls:
                    [
                        CreateToolCall(0, "call_alpha_2", "get_alpha", "{}"),
                        CreateToolCall(1, "call_beta_2", "get_beta", "{}"),
                    ],
                    finishReason: "tool_calls"),
            ]).AsIChatClient();

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "Call both tools twice.")],
            new ChatOptions
            {
                Tools =
                [
                    AIFunctionFactory.Create(() => "tool-alpha", "get_alpha", "Returns alpha."),
                    AIFunctionFactory.Create(() => "tool-beta", "get_beta", "Returns beta."),
                ],
                AllowMultipleToolCalls = true,
            }))
        {
            updates.Add(update);
        }

        Assert.Equal(2, RecordingStreamingChatClient.RecordedRequests.Count);
        Assert.Equal(2, updates.Count(static update => update.Role == ChatRole.Tool));
        var finalToolCallUpdate = updates.Last(static update => update.Contents.OfType<FunctionCallContent>().Any());
        Assert.Equal(2, finalToolCallUpdate.Contents.OfType<FunctionCallContent>().Count());
        Assert.All(finalToolCallUpdate.Contents.OfType<FunctionCallContent>(), static call => Assert.False(call.InformationalOnly));
    }

    [Fact]
    public async Task AsIChatClient_StreamingRequest_RecordsStreamAndStreamOptions()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(content: "hello", finishReason: "stop"),
                new ChatCompletion
                {
                    Usage = new TokenUsage
                    {
                        PromptTokens = 1,
                        CompletionTokens = 1,
                        TotalTokens = 2,
                    },
                },
            ]).AsIChatClient(new DeepSeekChatClientOptions
            {
                IncludeUsage = true,
            });

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "hi")]))
        {
        }

        var request = Assert.Single(RecordingStreamingChatClient.RecordedRequests);
        Assert.True(request.Stream);
        Assert.NotNull(request.StreamOptions);
        Assert.True(request.StreamOptions!.IncludeUsage);
    }

    [Fact]
    public async Task AsIChatClient_NonStreamingRequest_DoesNotSendStreamOptions()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = "done",
                    },
                    FinishReason = "stop",
                },
            ],
        }).AsIChatClient(new DeepSeekChatClientOptions
        {
            IncludeUsage = true,
        });

        var response = await client.GetResponseAsync([new AiChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("done", Assert.Single(response.Messages).Text);
        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.False(request.Stream);
        Assert.Null(request.StreamOptions);
    }

    [Fact]
    public async Task AsIChatClient_StreamingResponse_YieldsReasoningAndContentAsSeparateUpdates()
    {
        var stream = new DelayedChunkStream();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetChatClient("deepseek-v4-pro").AsIChatClient();
        var firstUpdate = new TaskCompletionSource<ChatResponseUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new List<ChatResponseUpdate>();

        var enumeration = Task.Run(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new AiChatMessage(ChatRole.User, "hi")]))
            {
                updates.Add(update);
                firstUpdate.TrySetResult(update);
            }
        });

        stream.WriteChunk("data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Need facts.\"}}]}\n\n");

        var reasoningUpdate = await firstUpdate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(reasoningUpdate.AdditionalProperties?["is_reasoning"] as bool?);
        Assert.Equal("Need facts.", Assert.Single(reasoningUpdate.Contents.OfType<TextReasoningContent>()).Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: {\"choices\":[{\"delta\":{\"content\":\"Answer\"}}]}\n\n");
        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();

        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(updates, static update => update.Text == "Answer");
    }

    [Fact]
    public async Task AsIChatClient_GetResponseAsync_IgnoresAdditionalPropertiesStream()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = "done",
                    },
                    FinishReason = "stop",
                },
            ],
        }).AsIChatClient();

        var response = await client.GetResponseAsync(
            [new AiChatMessage(ChatRole.User, "hi")],
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.Stream] = true,
                    [DeepSeekChatOptionKeys.IncludeUsage] = true,
                },
            });

        Assert.Equal("done", Assert.Single(response.Messages).Text);
        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.False(request.Stream);
        Assert.Null(request.StreamOptions);
    }

    [Fact]
    public async Task AsIChatClient_GetStreamingResponseAsync_IgnoresAdditionalPropertiesStream()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(content: "hello", finishReason: "stop"),
                new ChatCompletion
                {
                    Usage = new TokenUsage
                    {
                        PromptTokens = 1,
                        CompletionTokens = 1,
                        TotalTokens = 2,
                    },
                },
            ]).AsIChatClient();

        await foreach (var _ in client.GetStreamingResponseAsync(
            [new AiChatMessage(ChatRole.User, "hi")],
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.Stream] = false,
                    [DeepSeekChatOptionKeys.IncludeUsage] = true,
                },
            }))
        {
        }

        var request = Assert.Single(RecordingStreamingChatClient.RecordedRequests);
        Assert.True(request.Stream);
        Assert.NotNull(request.StreamOptions);
        Assert.True(request.StreamOptions!.IncludeUsage);
    }

    [Fact]
    public async Task AsAIAgent_RunAsync_IgnoresExplicitStreamTrue()
    {
        var client = new RecordingResponseChatClient(new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Message = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = "done",
                    },
                    FinishReason = "stop",
                },
            ],
        });
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "NonStreamingAgent",
            ChatOptions = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.Stream] = false,
                },
            },
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        var response = await agent.RunAsync(
            "Reply once.",
            session,
            new ChatClientAgentRunOptions
            {
                ChatOptions = new ChatOptions
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        [DeepSeekChatOptionKeys.Stream] = true,
                        [DeepSeekChatOptionKeys.IncludeUsage] = true,
                    },
                },
            },
            CancellationToken.None);

        Assert.Equal("done", response.Text);
        var request = Assert.Single(RecordingResponseChatClient.RecordedRequests);
        Assert.False(request.Stream);
        Assert.Null(request.StreamOptions);
    }

    [Fact]
    public async Task AsAIAgent_RunStreamingAsync_DefaultsStreamTrue()
    {
        RecordingStreamingChatClient.ClearRecordedRequests();
        var client = new RecordingStreamingChatClient(
            [
                CreateChunk(content: "hello", finishReason: "stop"),
                new ChatCompletion
                {
                    Usage = new TokenUsage
                    {
                        PromptTokens = 1,
                        CompletionTokens = 1,
                        TotalTokens = 2,
                    },
                },
            ]);
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "StreamingAgent",
            ChatOptions = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.Stream] = false,
                    [DeepSeekChatOptionKeys.IncludeUsage] = true,
                },
            },
        });
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        await foreach (var _ in agent.RunStreamingAsync("Reply once.", session, null, CancellationToken.None))
        {
        }

        var request = Assert.Single(RecordingStreamingChatClient.RecordedRequests);
        Assert.True(request.Stream);
        Assert.NotNull(request.StreamOptions);
        Assert.True(request.StreamOptions!.IncludeUsage);
    }

    [Fact]
    public async Task AnthropicAsIChatClient_UsesCallShapeForStreamAndIgnoresChatOnlyFields()
    {
        var nonStreamingHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var chatClient = CreateAnthropicClient(nonStreamingHandler).AsIChatClient(new DeepSeekChatClientOptions
        {
            IncludeUsage = true,
            Logprobs = true,
            TopLogprobs = 3,
            ToolChoiceName = "named_tool",
            UserId = "user-123",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeepSeekChatOptionKeys.Stream] = true,
            },
        });

        var response = await chatClient.GetResponseAsync([new AiChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("ok", Assert.Single(response.Messages).Text);
        Assert.NotNull(nonStreamingHandler.LastRequestBody);
        Assert.Contains("\"stream\":false", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("include_usage", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("logprobs", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("top_logprobs", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_choice_name", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("user_id", nonStreamingHandler.LastRequestBody!, StringComparison.Ordinal);

        var streamingSse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"ok\"}}\n\n" +
                           "data: [DONE]\n\n";
        var streamingHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(streamingSse))),
        });
        streamingHandler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var streamingClient = CreateAnthropicClient(streamingHandler).AsIChatClient(new DeepSeekChatClientOptions
        {
            IncludeUsage = true,
            Logprobs = true,
            TopLogprobs = 3,
            ToolChoiceName = "named_tool",
            UserId = "user-123",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DeepSeekChatOptionKeys.Stream] = false,
            },
        });

        await foreach (var _ in streamingClient.GetStreamingResponseAsync([new AiChatMessage(ChatRole.User, "hi")]))
        {
        }

        Assert.NotNull(streamingHandler.LastRequestBody);
        Assert.Contains("\"stream\":true", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("include_usage", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("logprobs", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("top_logprobs", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("tool_choice_name", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("user_id", streamingHandler.LastRequestBody!, StringComparison.Ordinal);
    }

    private static AiChatMessage CreateAssistantToolCallMessage(string reasoning, params (string Id, string Name)[] toolCalls)
    {
        var assistant = new AiChatMessage(ChatRole.Assistant, string.Empty)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["reasoning_content"] = reasoning,
            },
        };

        foreach (var toolCall in toolCalls)
        {
            assistant.Contents.Add(new FunctionCallContent(toolCall.Id, toolCall.Name, new Dictionary<string, object?>()));
        }

        return assistant;
    }

    private static AITool CreateDeclaration(string name, string description, string jsonSchema)
        => AIFunctionFactory.CreateDeclaration(name, description, JsonDocument.Parse(jsonSchema).RootElement);

    private static ChatCompletion CreateChunk(string? reasoning = null, string? content = null, IList<ToolCall>? toolCalls = null, string? finishReason = null)
    {
        return new ChatCompletion
        {
            Choices =
            [
                new ChatChoice
                {
                    Delta = new WireChatMessage
                    {
                        Role = "assistant",
                        Content = content,
                        ReasoningContent = reasoning,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finishReason,
                },
            ],
        };
    }

    private static AnthropicClient CreateAnthropicClient(RecordingHandler handler)
    {
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new AnthropicClient("deepseek-v4-pro", "test-key", options);
    }

    private static ToolCall CreateToolCall(int index, string id, string name, string arguments)
    {
        return new ToolCall
        {
            Index = index,
            Id = id,
            Function = new ToolCallFunction
            {
                Name = name,
                Arguments = arguments,
            },
        };
    }

    private sealed class FakeChatClient : ChatClient
    {
        private readonly ChatCompletion _response;

        public FakeChatClient(ChatCompletion response) : base("deepseek-v4-pro", "test-key")
        {
            _response = response;
        }

        public override Task<ClientResult<ChatCompletion>> CompleteChatAsync(ChatCompletionRequest request, System.ClientModel.Primitives.RequestOptions? options = null)
            => Task.FromResult(ClientResult.FromValue(_response, new FakePipelineResponse()));
    }

    private sealed class RecordingStreamingChatClient : ChatClient
    {
        private readonly Queue<IReadOnlyList<ChatCompletion>> _responses;

        public RecordingStreamingChatClient(params IReadOnlyList<ChatCompletion>[] responses) : base("deepseek-v4-pro", "test-key")
        {
            _responses = new Queue<IReadOnlyList<ChatCompletion>>(responses);
        }

        public static List<ChatCompletionRequest> RecordedRequests { get; } = [];

        public override AsyncCollectionResult<ChatCompletion> CompleteChatStreaming(ChatCompletionRequest request, System.ClientModel.Primitives.RequestOptions? options = null)
        {
            RecordedRequests.Add(Clone(request));
            var response = _responses.Dequeue();
            return new InlineAsyncCollectionResult<ChatCompletion>(response);
        }

        public static void ClearRecordedRequests() => RecordedRequests.Clear();

        internal static ChatCompletionRequest Clone(ChatCompletionRequest request)
        {
            return new ChatCompletionRequest
            {
                Messages = request.Messages.Select(static message => new WireChatMessage
                {
                    Role = message.Role,
                    Content = message.Content,
                    Name = message.Name,
                    Prefix = message.Prefix,
                    ReasoningContent = message.ReasoningContent,
                    ToolCallId = message.ToolCallId,
                    ToolCalls = message.ToolCalls?.Select(static toolCall => new ToolCall
                    {
                        Index = toolCall.Index,
                        Id = toolCall.Id,
                        Type = toolCall.Type,
                        Function = new ToolCallFunction
                        {
                            Name = toolCall.Function.Name,
                            Arguments = toolCall.Function.Arguments,
                        },
                    }).ToList(),
                }).ToList(),
                ResponseFormat = request.ResponseFormat is null
                    ? null
                    : new ResponseFormat
                    {
                        Type = request.ResponseFormat.Type,
                    },
                Stream = request.Stream,
                StreamOptions = request.StreamOptions is null
                    ? null
                    : new StreamOptions
                    {
                        IncludeUsage = request.StreamOptions.IncludeUsage,
                    },
                Tools = request.Tools?.Select(static tool =>
                {
                    var function = tool.Function;
                    if (function is null)
                    {
                        return new ChatTool
                        {
                            Type = tool.Type,
                        };
                    }

                    return new ChatTool
                    {
                        Type = tool.Type,
                        Function = new ChatToolFunction
                        {
                            Name = function.Name,
                            Description = function.Description,
                            Strict = function.Strict,
                            Parameters = function.Parameters?.DeepClone()!,
                        },
                    };
                }).ToList(),
            };
        }
    }

    private sealed class RecordingResponseChatClient : ChatClient
    {
        private readonly ChatCompletion _response;

        public RecordingResponseChatClient(ChatCompletion response) : base("deepseek-v4-pro", "test-key")
        {
            _response = response;
            RecordedRequests.Clear();
        }

        public static List<ChatCompletionRequest> RecordedRequests { get; } = [];

        public override Task<ClientResult<ChatCompletion>> CompleteChatAsync(ChatCompletionRequest request, System.ClientModel.Primitives.RequestOptions? options = null)
        {
            RecordedRequests.Add(RecordingStreamingChatClient.Clone(request));
            return Task.FromResult(ClientResult.FromValue(_response, new FakePipelineResponse()));
        }
    }

    private sealed class StructuredLeasingSnapshot
    {
        public StructuredLeasingState State { get; set; } = new();

        public string AssistantMessage { get; set; } = string.Empty;
    }

    private sealed class StructuredLeasingState
    {
        public string Stage { get; set; } = string.Empty;

        public string? FloorPlanType { get; set; }
    }

    private sealed class InlineAsyncCollectionResult<T> : AsyncCollectionResult<T>
    {
        private readonly IReadOnlyList<T> _values;

        public InlineAsyncCollectionResult(IReadOnlyList<T> values)
        {
            _values = values;
        }

        protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
        {
            foreach (var value in _values)
            {
                yield return value;
                await Task.Yield();
            }
        }

        public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
        {
            yield return ClientResult.FromResponse(new FakePipelineResponse());
            await Task.CompletedTask;
        }

        public override ContinuationToken? GetContinuationToken(ClientResult page) => null;
    }

    private sealed class FakePipelineResponse : System.ClientModel.Primitives.PipelineResponse
    {
        private Stream _contentStream = Stream.Null;

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        protected override System.ClientModel.Primitives.PipelineResponseHeaders HeadersCore => throw new NotSupportedException();

        public override Stream? ContentStream
        {
            get => _contentStream;
            set => _contentStream = value ?? Stream.Null;
        }

        public override BinaryData Content => BinaryData.FromString(string.Empty);

        protected override bool IsErrorCore
        {
            get => false;
            set { }
        }

        public override BinaryData BufferContent(CancellationToken cancellationToken)
        {
            return BinaryData.FromString(string.Empty);
        }

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(BinaryData.FromString(string.Empty));

        public override void Dispose()
        {
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private Action<HttpResponseMessage>? _configureResponse;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public string? LastRequestBody { get; private set; }

        public void ConfigureResponse(Action<HttpResponseMessage> configureResponse)
        {
            _configureResponse = configureResponse;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = request.Content is null ? null : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            var response = _responseFactory(request);
            _configureResponse?.Invoke(response);
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedChunkStream : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentChunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void WriteChunk(string chunk)
            => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(chunk));

        public void Complete()
            => _chunks.Writer.TryComplete();

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                if (_currentChunk is not null && _offset < _currentChunk.Length)
                {
                    var bytesToCopy = Math.Min(buffer.Length, _currentChunk.Length - _offset);
                    _currentChunk.AsMemory(_offset, bytesToCopy).CopyTo(buffer);
                    _offset += bytesToCopy;
                    if (_offset >= _currentChunk.Length)
                    {
                        _currentChunk = null;
                        _offset = 0;
                    }

                    return bytesToCopy;
                }

                if (await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false) &&
                    _chunks.Reader.TryRead(out var chunk))
                {
                    _currentChunk = chunk;
                    _offset = 0;
                    continue;
                }

                return 0;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
#pragma warning restore MAAI001
