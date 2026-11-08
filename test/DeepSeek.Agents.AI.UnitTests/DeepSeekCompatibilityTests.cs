using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeek.Chat;
using Microsoft.Extensions.AI;
using Xunit;
using AiMessage = Microsoft.Extensions.AI.ChatMessage;

namespace DeepSeek.Agents.AI.Tests;

public sealed class DeepSeekCompatibilityTests
{
    [Theory]
    [InlineData("chat", ReasoningEffort.None, "none")]
    [InlineData("chat", ReasoningEffort.Low, "low")]
    [InlineData("chat", ReasoningEffort.High, "high")]
    [InlineData("chat", ReasoningEffort.ExtraHigh, "max")]
    [InlineData("anthropic", ReasoningEffort.None, "none")]
    [InlineData("anthropic", ReasoningEffort.Low, "low")]
    [InlineData("anthropic", ReasoningEffort.High, "high")]
    [InlineData("anthropic", ReasoningEffort.ExtraHigh, "max")]
    [InlineData("responses", ReasoningEffort.None, "none")]
    [InlineData("responses", ReasoningEffort.Low, "low")]
    [InlineData("responses", ReasoningEffort.High, "high")]
    [InlineData("responses", ReasoningEffort.ExtraHigh, "max")]
    public async Task Adapter_RequestedReasoning_PreservesTheCallersThinkingAndCostChoice(string protocol, ReasoningEffort effort, string expected)
    {
        var handler = new RecordingHandler();
        await CreateClient(protocol, handler).GetResponseAsync([new AiMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = effort } });

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, ReadEffort(body.RootElement, protocol));
        if (effort == ReasoningEffort.None && protocol != "responses")
        {
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Adapter_PerCallReasoning_OverridesProviderSpecificDefaults(string protocol)
    {
        var handler = new RecordingHandler();
        var defaults = new DeepSeekChatClientOptions { ReasoningEffort = ChatReasoningEffort.Max };
        await CreateClient(protocol, handler, defaults).GetResponseAsync([new AiMessage(ChatRole.User, "hello")],
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.Low } });
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("low", ReadEffort(body.RootElement, protocol));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Adapter_PerCallProviderOptions_OverrideGenericDefaultsAndRespectThinkingOff(string protocol)
    {
        var handler = new RecordingHandler();
        var defaults = new DeepSeekChatClientOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };
        await CreateClient(protocol, handler, defaults).GetResponseAsync([new AiMessage(ChatRole.User, "hello")],
            new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [DeepSeekChatOptionKeys.ReasoningEffort] = "low",
                    [DeepSeekChatOptionKeys.Thinking] = "disabled",
                },
            });
        using var body = JsonDocument.Parse(handler.Body!);
        if (protocol == "responses") Assert.Equal("none", ReadEffort(body.RootElement, protocol));
        else
        {
            Assert.Equal("low", ReadEffort(body.RootElement, protocol));
            Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Adapter_ToolImages_RemainImagesInTheirOriginalOrder(string protocol)
    {
        var handler = new RecordingHandler();
        await CreateClient(protocol, handler).GetResponseAsync(
        [
            new AiMessage(ChatRole.User, "inspect screenshot"),
            new AiMessage(ChatRole.Assistant, [new FunctionCallContent("call1", "screenshot", new Dictionary<string, object?>())]),
            new AiMessage(ChatRole.Tool,
            [new FunctionResultContent("call1", new List<AIContent>
            {
                new TextContent("screenshot"),
                new UriContent("https://example.com/image.png", "image/png"),
                new DataContent(new byte[] { 1, 2, 3 }, "image/png"),
                new HostedFileContent("file-api-1") { MediaType = "image/png" },
            })]),
        ]);

        using var body = JsonDocument.Parse(handler.Body!);
        var parts = protocol switch
        {
            "chat" => body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content"),
            "anthropic" => body.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content")[0].GetProperty("content"),
            _ => body.RootElement.GetProperty("input").EnumerateArray().Last().GetProperty("output"),
        };
        Assert.Equal(JsonValueKind.Array, parts.ValueKind);
        Assert.Equal(4, parts.GetArrayLength());
        Assert.Equal("screenshot", parts[0].GetProperty("text").GetString());
        Assert.Contains("https://example.com/image.png", parts[1].GetRawText());
        Assert.Contains("AQID", parts[2].GetRawText());
        Assert.Contains("file-api-1", parts[3].GetRawText());
        if (protocol == "anthropic")
        {
            Assert.Equal("files-api-2025-04-14", Assert.Single(handler.Request!.Headers.GetValues("anthropic-beta")));
        }
    }

    [Theory]
    [InlineData("anthropic", "web_search_20250305")]
    [InlineData("responses", "web_search")]
    public async Task Adapter_HostedWebSearch_UsesTheServerToolWithoutClientExecution(string protocol, string expectedType)
    {
        var handler = new RecordingHandler();
        await CreateClient(protocol, handler).GetResponseAsync([new AiMessage(ChatRole.User, "search")], new ChatOptions
        {
            Tools = [new HostedWebSearchTool(new Dictionary<string, object?> { ["max_uses"] = 1 })],
        });
        using var body = JsonDocument.Parse(handler.Body!);
        var tool = Assert.Single(body.RootElement.GetProperty("tools").EnumerateArray());
        Assert.Equal(expectedType, tool.GetProperty("type").GetString());
        if (protocol == "anthropic") Assert.Equal(1, tool.GetProperty("max_uses").GetInt32());
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Adapter_ServerSearchHistoryAndUsage_ArePreservedAcrossStatelessTurns(string protocol)
    {
        var handler = new RecordingHandler
        {
            ResponseBody = protocol == "anthropic" ? """
                {"id":"msg","model":"deepseek-v4-pro","content":[
                  {"type":"thinking","thinking":"plan","signature":"sig"},
                  {"type":"server_tool_use","id":"search1","name":"web_search","input":{"query":"docs"},"caller":{"type":"direct"}},
                  {"type":"web_search_tool_result","tool_use_id":"search1","content":[{"type":"web_search_result","url":"https://example.com","title":"docs","encrypted_content":"opaque"}]},
                  {"type":"text","text":"answer"}],
                "usage":{"input_tokens":10,"cache_read_input_tokens":4,"cache_creation_input_tokens":0,"output_tokens":2,
                  "server_tool_use":{"web_search_requests":3,"tool_search":2}}}
                """ : """
                {"id":"resp","model":"deepseek-v4-pro","status":"completed","output":[
                  {"type":"reasoning","content":[{"type":"reasoning_text","text":"plan"}]},
                  {"type":"web_search_call","id":"search1","status":"completed","action":{"type":"search","query":"docs"},"encrypted_content":"opaque"},
                  {"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}],
                "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":4},"output_tokens":2,"output_tokens_details":{"reasoning_tokens":1},"total_tokens":12,
                  "server_tool_use":{"tool_search":2,"tool_fetch":1}}}
                """,
        };
        var client = CreateClient(protocol, handler);
        var first = await client.GetResponseAsync([new AiMessage(ChatRole.User, "search")]);
        Assert.Equal(protocol == "anthropic" ? 14 : 10, first.Usage!.InputTokenCount);
        Assert.Equal(protocol == "anthropic" ? 16 : 12, first.Usage.TotalTokenCount);
        Assert.Equal(4, first.Usage.CachedInputTokenCount);
        Assert.Equal(2, first.Usage.AdditionalCounts!["tool_search"]);
        Assert.Equal(protocol == "anthropic" ? 3 : 1, first.Usage.AdditionalCounts[protocol == "anthropic" ? "web_search_requests" : "tool_fetch"]);

        await client.GetResponseAsync([new AiMessage(ChatRole.User, "search"), .. first.Messages, new AiMessage(ChatRole.User, "continue")]);
        using var body = JsonDocument.Parse(handler.Body!);
        if (protocol == "responses")
        {
            var items = body.RootElement.GetProperty("input").EnumerateArray().ToList();
            Assert.Single(items, x => x.GetProperty("type").GetString() == "web_search_call");
            Assert.Single(items, x => x.TryGetProperty("role", out var role) && role.GetString() == "assistant");
            Assert.Contains("opaque", handler.Body!);
        }
        else
        {
            var blocks = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Equal(new[] { "thinking", "server_tool_use", "web_search_tool_result", "text" }, blocks.EnumerateArray().Select(x => x.GetProperty("type").GetString()));
            Assert.Contains("opaque", blocks.GetRawText());
            Assert.Equal("sig", blocks[0].GetProperty("signature").GetString());
        }
    }

    [Fact]
    public async Task Anthropic_InterleavedStreamingTools_AreEmittedOnlyAfterTheirArgumentsAreComplete()
    {
        var handler = new RecordingHandler
        {
            ResponseBody = string.Join("\n\n", new[]
            {
                """data: {"type":"message_start","message":{"id":"m","model":"deepseek-flash","content":[],"usage":{"input_tokens":10,"cache_read_input_tokens":4,"output_tokens":0}}}""",
                """data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}""",
                """data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"plan"}}""",
                """data: {"type":"content_block_stop","index":0}""",
                """data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"c1","name":"weather","input":{}}}""",
                """data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"c2","name":"clock","input":{}}}""",
                """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"city\":"}}""",
                """data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{}"}}""",
                """data: {"type":"content_block_stop","index":2}""",
                """data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"\"Hangzhou\"}"}}""",
                """data: {"type":"content_block_stop","index":1}""",
                """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":10,"cache_read_input_tokens":4,"output_tokens":2,"server_tool_use":{"tool_search":1,"web_search_requests":1}}}""",
                """data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":2}}""",
                """data: {"type":"message_stop"}""",
            }) + "\n\n",
        };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in CreateClient("anthropic", handler).GetStreamingResponseAsync([new AiMessage(ChatRole.User, "tools")])) updates.Add(update);

        var calls = updates.SelectMany(x => x.Contents).OfType<FunctionCallContent>().ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal("Hangzhou", calls.Single(x => x.CallId == "c1").Arguments!["city"]!.ToString());
        Assert.Empty(calls.Single(x => x.CallId == "c2").Arguments!);
        var usage = Assert.Single(updates.SelectMany(x => x.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(14, usage.InputTokenCount);
        Assert.Equal(16, usage.TotalTokenCount);
        Assert.Equal(1, usage.AdditionalCounts!["tool_search"]);
        Assert.Contains(updates.SelectMany(x => x.Contents), x => x is TextReasoningContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Anthropic_IncompleteStreamOrToolArguments_CannotBecomeSuccessfulToolCalls(bool missingTerminal)
    {
        var handler = new RecordingHandler
        {
            ResponseBody = """
                data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"c1","name":"weather","input":{}}}

                data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}

                """ + (missingTerminal ? "" : "\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\ndata: {\"type\":\"message_stop\"}\n\n"),
        };
        var calls = new List<FunctionCallContent>();
        async Task Read()
        {
            await foreach (var update in CreateClient("anthropic", handler).GetStreamingResponseAsync([new AiMessage(ChatRole.User, "tools")]))
                calls.AddRange(update.Contents.OfType<FunctionCallContent>());
        }
        if (missingTerminal) await Assert.ThrowsAsync<InvalidDataException>(Read);
        else await Assert.ThrowsAsync<JsonException>(Read);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Responses_StreamingCumulativeUsage_IsEmittedOnceInStandardUsageContent()
    {
        var handler = new RecordingHandler
        {
            ResponseBody = """
                data: {"type":"response.created","response":{"id":"r","status":"in_progress","output":[],"usage":null}}

                data: {"type":"response.output_text.delta","delta":"ok"}

                data: {"type":"response.completed","response":{"id":"r","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}],"usage":{"input_tokens":10,"output_tokens":2,"total_tokens":12,"input_tokens_details":{"cached_tokens":4},"server_tool_use":{"tool_search":2,"tool_fetch":1}}}}

                """,
        };
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in CreateClient("responses", handler).GetStreamingResponseAsync([new AiMessage(ChatRole.User, "hello")])) updates.Add(update);
        var usage = Assert.Single(updates.SelectMany(x => x.Contents).OfType<UsageContent>()).Details;
        Assert.Equal(12, usage.TotalTokenCount);
        Assert.Equal(4, usage.CachedInputTokenCount);
        Assert.Equal(2, usage.AdditionalCounts!["tool_search"]);
    }

    [Theory]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Adapter_StreamingSearchHistory_SurvivesStandardChatResponseAggregation(string protocol)
    {
        var handler = new RecordingHandler
        {
            ResponseBody = protocol == "responses" ? """
                data: {"type":"response.completed","response":{"id":"r","status":"completed","output":[{"type":"web_search_call","id":"s1","action":{"type":"search","query":"docs"},"encrypted_content":"opaque"},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}}

                """ : """
                data: {"type":"message_start","message":{"id":"m","model":"deepseek-v4-pro","content":[]}}

                data: {"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"s1","name":"web_search","input":{"query":"docs"}}}

                data: {"type":"content_block_stop","index":0}

                data: {"type":"content_block_start","index":1,"content_block":{"type":"web_search_tool_result","tool_use_id":"s1","content":[{"type":"web_search_result","encrypted_content":"opaque","title":"docs","url":"https://example.com"}]}}

                data: {"type":"content_block_stop","index":1}

                data: {"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}

                data: {"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"answer"}}

                data: {"type":"content_block_stop","index":2}

                data: {"type":"message_stop"}

                """,
        };
        var client = CreateClient(protocol, handler);
        var response = await client.GetStreamingResponseAsync([new AiMessage(ChatRole.User, "search")]).ToChatResponseAsync();
        handler.ResponseBody = null;
        await client.GetResponseAsync([new AiMessage(ChatRole.User, "search"), .. response.Messages, new AiMessage(ChatRole.User, "continue")]);
        Assert.Contains("opaque", handler.Body!);
        using var body = JsonDocument.Parse(handler.Body!);
        if (protocol == "responses")
        {
            Assert.Single(body.RootElement.GetProperty("input").EnumerateArray(), x => x.GetProperty("type").GetString() == "web_search_call");
        }
        else
        {
            var blocks = body.RootElement.GetProperty("messages")[1].GetProperty("content");
            Assert.Single(blocks.EnumerateArray(), x => x.GetProperty("type").GetString() == "web_search_tool_result");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Chat_Usage_AllowsConsumersToAccountForCachedInputAndStreamingCost(bool streaming)
    {
        const string usage = "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"total_tokens\":12,\"prompt_cache_hit_tokens\":4,\"prompt_cache_miss_tokens\":6,\"completion_tokens_details\":{\"reasoning_tokens\":1}}";
        var handler = new RecordingHandler
        {
            ResponseBody = streaming
                ? "data: {\"id\":\"c\",\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\ndata: {\"id\":\"c\",\"choices\":[]," + usage + "}\n\ndata: [DONE]\n\n"
                : "{\"id\":\"c\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]," + usage + "}",
        };
        var client = CreateClient("chat", handler);
        var response = streaming
            ? await client.GetStreamingResponseAsync([new AiMessage(ChatRole.User, "hello")]).ToChatResponseAsync()
            : await client.GetResponseAsync([new AiMessage(ChatRole.User, "hello")]);
        Assert.NotNull(response.Usage);
        Assert.Equal(12, response.Usage.TotalTokenCount);
        Assert.Equal(4, response.Usage.CachedInputTokenCount);
        Assert.Equal(1, response.Usage.ReasoningTokenCount);
        Assert.Equal(6, response.Usage.AdditionalCounts!["prompt_cache_miss_tokens"]);
    }

    [Fact]
    public async Task Responses_CustomAndUnknownHistory_IsRoundTrippedWithoutCoercingItIntoFunctionCalls()
    {
        var handler = new RecordingHandler
        {
            ResponseBody = """
                {"id":"r","output":[{"type":"custom_tool_call","id":"custom1","call_id":"call1","name":"apply_patch","input":"*** Begin Patch\n*** End Patch","future_custom":7},{"type":"future_item","id":"opaque1","future":{"value":true}}]}
                """,
        };
        var client = CreateClient("responses", handler);
        var first = await client.GetResponseAsync([new AiMessage(ChatRole.User, "patch")]);
        Assert.Empty(first.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>());
        await client.GetResponseAsync([.. first.Messages, new AiMessage(ChatRole.User, "continue")]);
        using var document = JsonDocument.Parse(handler.Body!);
        var items = document.RootElement.GetProperty("input").EnumerateArray().ToList();
        var custom = Assert.Single(items, x => x.GetProperty("type").GetString() == "custom_tool_call");
        Assert.Equal("call1", custom.GetProperty("call_id").GetString());
        Assert.Equal("*** Begin Patch\n*** End Patch", custom.GetProperty("input").GetString());
        Assert.Equal(7, custom.GetProperty("future_custom").GetInt32());
        var unknown = Assert.Single(items, x => x.GetProperty("type").GetString() == "future_item");
        Assert.True(unknown.GetProperty("future").GetProperty("value").GetBoolean());
    }

    private static string? ReadEffort(JsonElement body, string protocol)
        => protocol switch
        {
            "chat" => body.GetProperty("reasoning_effort").GetString(),
            "anthropic" => body.GetProperty("output_config").GetProperty("effort").GetString(),
            _ => body.GetProperty("reasoning").GetProperty("effort").GetString(),
        };

    private static IChatClient CreateClient(string protocol, RecordingHandler handler, DeepSeekChatClientOptions? defaults = null)
    {
        var sdk = new DeepSeekClient("test-key", new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) });
        return protocol switch
        {
            "chat" => sdk.GetChatClient("deepseek-flash").AsIChatClient(defaults),
            "anthropic" => sdk.GetAnthropicClient("deepseek-flash").AsIChatClient(defaults),
            _ => sdk.GetResponsesClient("deepseek-flash").AsIChatClient(defaults),
        };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? Body { get; private set; }
        public string? ResponseBody { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var json = ResponseBody ?? request.RequestUri!.AbsolutePath switch
            {
                "/anthropic/v1/messages" => """{"id":"msg","model":"deepseek-flash","content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""",
                "/responses" => """{"id":"resp","model":"deepseek-flash","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"ok"}]}]}""",
                _ => """{"id":"chat","model":"deepseek-flash","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}""",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
