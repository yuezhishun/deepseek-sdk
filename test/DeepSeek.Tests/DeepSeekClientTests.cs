using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using System.ClientModel.Primitives;
using DeepSeek;
using DeepSeek.Anthropic;
using DeepSeek.Chat;
using DeepSeek.Completions;
using DeepSeek.Files;
using DeepSeek.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DeepSeek.Tests;

public class DeepSeekClientTests
{
    [Fact]
    public async Task ResponsesClient_SendsExpectedRequestAndParsesResponse()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "id":"resp_1",
                  "object":"response",
                  "created_at":1710000000,
                  "status":"completed",
                  "model":"deepseek-v4-flash",
                  "output":[
                    {"type":"reasoning","id":"rs_1","content":[{"type":"reasoning_text","text":"Think."}]},
                    {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Hello"}]},
                    {"type":"function_call","id":"fc_1","call_id":"call_1","name":"lookup","arguments":"{\"city\":\"Hangzhou\"}"}
                  ],
                  "usage":{"input_tokens":10,"input_tokens_details":{"cached_tokens":2},"output_tokens":8,"output_tokens_details":{"reasoning_tokens":3},"total_tokens":18}
                }
                """, Encoding.UTF8, "application/json"),
        });
        var client = CreateResponsesClient(handler);

        var response = await client.CreateResponseAsync(new ResponseRequest
        {
            Instructions = "Be concise.",
            Input = "Hello",
            Temperature = 0.2,
            TopP = 0.8,
            MaxOutputTokens = 100,
            TopLogprobs = 3,
            Reasoning = new ResponseReasoningOptions { Effort = "high" },
            Text = new ResponseTextOptions { Format = new ResponseTextFormat { Type = "json_object" } },
            Tools =
            [
                new ResponseTool
                {
                    Name = "lookup",
                    Description = "Look up a city.",
                    Parameters = new JsonObject { ["type"] = "object" },
                },
            ],
            ToolChoice = new ResponseToolChoice { Type = "function", Name = "lookup" },
            User = "user-1",
        });

        Assert.Equal("resp_1", response.Value.Id);
        Assert.IsType<ResponseReasoningOutputItem>(response.Value.Output[0]);
        Assert.IsType<ResponseMessageOutputItem>(response.Value.Output[1]);
        Assert.IsType<ResponseFunctionCallOutputItem>(response.Value.Output[2]);
        Assert.Equal("Hello", response.Value.Output[1].Content[0].Text);
        Assert.Equal(18, response.Value.Usage?.TotalTokens);
        Assert.Equal("https://api.deepseek.com/responses", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);

        var body = Assert.IsType<string>(handler.LastRequestBody);
        Assert.Contains("\"model\":\"deepseek-v4-flash\"", body, StringComparison.Ordinal);
        Assert.Contains("\"stream\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"instructions\":\"Be concise.\"", body, StringComparison.Ordinal);
        Assert.Contains("\"input\":\"Hello\"", body, StringComparison.Ordinal);
        Assert.Contains("\"reasoning\":{\"effort\":\"high\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"format\":{\"type\":\"json_object\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"tool_choice\":{\"type\":\"function\",\"name\":\"lookup\"}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesClient_SerializesStructuredInputAndJsonExtensions()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateResponsesClient(handler);
        using var document = JsonDocument.Parse("{\"type\":\"custom_input\",\"value\":42}");

        await client.CreateResponseAsync(new ResponseRequest
        {
            Input =
            [
                new ResponseInputMessage
                {
                    Role = "user",
                    Content = new List<ResponseContentPart>
                    {
                        new ResponseInputTextContent { Text = "Hello" },
                    },
                },
                new ResponseInputItem { Type = "custom", AdditionalProperties = new Dictionary<string, JsonElement> { ["value"] = document.RootElement.Clone() } },
            ],
        });

        var body = Assert.IsType<string>(handler.LastRequestBody);
        Assert.Contains("\"type\":\"message\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"custom\"", body, StringComparison.Ordinal);
        Assert.Contains("\"value\":42", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesClient_MapsErrorToDeepSeekException()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid response request\",\"type\":\"invalid_request_error\"}}", Encoding.UTF8, "application/json"),
        });
        var client = CreateResponsesClient(handler);

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.CreateResponseAsync(new ResponseRequest { Input = "hello" }));

        Assert.Equal(400, exception.Status);
        Assert.Equal("invalid response request", exception.Message);
        Assert.Equal("https://api.deepseek.com/responses", exception.RequestUri.ToString());
    }

    [Fact]
    public async Task ResponsesClient_ParsesSemanticStreamingEventsAndTerminalCompleted()
    {
        var sse = "event: response.created\n" +
                  "data: {\"type\":\"response.created\",\"sequence_number\":0}\n\n" +
                  "event: response.reasoning_text.delta\n" +
                  "data: {\"sequence_number\":1,\"delta\":\"Think\"}\n\n" +
                  "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"sequence_number\":2,\"delta\":\"Hello\"}\n\n" +
                  "event: response.completed\n" +
                  "data: {\"type\":\"response.completed\",\"sequence_number\":3,\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":1,\"output_tokens\":2,\"total_tokens\":3}}}\n\n";
        var handler = CreateStreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var client = CreateResponsesClient(handler);

        var events = new List<ResponseStreamEvent>();
        await foreach (var item in client.CreateResponseStreaming(new ResponseRequest { Input = "hello" }))
        {
            events.Add(item);
        }

        Assert.Equal(4, events.Count);
        Assert.Equal("response.created", events[0].Type);
        Assert.Equal("response.created", events[0].Event);
        Assert.Equal("Think", events[1].Delta);
        Assert.Equal("Hello", events[2].Delta);
        Assert.Equal("response.completed", events[3].Type);
        Assert.Equal("resp_1", events[3].Response?.Id);
        Assert.Equal(3, events[3].Response?.Usage?.TotalTokens);
    }

    [Theory]
    [InlineData("response.incomplete")]
    [InlineData("response.failed")]
    public async Task ResponsesClient_YieldsIncompleteAndFailedTerminalEvents(string terminalType)
    {
        var sse = $"event: {terminalType}\n" +
                  $"data: {{\"type\":\"{terminalType}\",\"response\":{{\"id\":\"resp_1\",\"status\":\"{(terminalType == "response.failed" ? "failed" : "incomplete")}\"}}}}\n\n";
        var handler = CreateStreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(sse)));
        var client = CreateResponsesClient(handler);

        var events = new List<ResponseStreamEvent>();
        await foreach (var item in client.CreateResponseStreaming(new ResponseRequest { Input = "hello" }))
        {
            events.Add(item);
        }

        var terminalEvent = Assert.Single(events);
        Assert.Equal(terminalType, terminalEvent.Type);
        Assert.Equal(terminalType, terminalEvent.Event);
        Assert.NotNull(terminalEvent.Response);
    }

    [Fact]
    public async Task ResponsesClient_DeserializesAllOutputVariantsAnnotationsAndUnknownFields()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "id":"resp_1",
                  "status":"completed",
                  "output":[
                    {"type":"message","content":[{"type":"output_text","text":"ok","annotations":[{"type":"url_citation","start_index":0,"end_index":2,"url":"https://example.com","title":"Example"}]}]},
                    {"type":"reasoning","summary":[{"type":"summary_text","text":"thinking"}],"encrypted_content":"opaque"},
                    {"type":"custom_tool_call","id":"ct_1","name":"apply_patch","input":"*** Begin Patch"},
                    {"type":"future_item","future_value":{"answer":42}}
                  ],
                  "future_response_field":true
                }
                """, Encoding.UTF8, "application/json"),
        });

        var response = await CreateResponsesClient(handler).CreateResponseAsync(new ResponseRequest { Input = "hello" });

        Assert.IsType<ResponseMessageOutputItem>(response.Value.Output[0]);
        Assert.IsType<ResponseReasoningOutputItem>(response.Value.Output[1]);
        Assert.IsType<ResponseCustomToolCallOutputItem>(response.Value.Output[2]);
        Assert.IsType<ResponseOutputItem>(response.Value.Output[3]);
        Assert.IsType<ResponseUrlCitationAnnotation>(response.Value.Output[0].Content[0].Annotations![0]);
        Assert.Equal("*** Begin Patch", response.Value.Output[2].Input);
        Assert.Contains("future_response_field", response.Value.AdditionalProperties!.Keys);
        Assert.Contains("future_value", response.Value.Output[3].AdditionalProperties!.Keys);
    }

    [Fact]
    public async Task ResponsesClient_SerializesTypedToolsAndJsonSchema()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[]}", Encoding.UTF8, "application/json"),
        });
        var schema = new
        {
            type = "object",
            properties = new
            {
                firstName = new { type = "string" },
            },
            additionalProperties = false,
        };

        await CreateResponsesClient(handler).CreateResponseAsync(new ResponseRequest
        {
            Input = "hello",
            Text = new ResponseTextOptions
            {
                Format = ResponseTextFormat.JsonSchema("answer", schema),
            },
            Tools =
            [
                new ResponseFunctionTool { Name = "lookup", Parameters = schema, Strict = true },
                new ResponseWebSearchTool(),
                new ResponseCustomTool { Name = "apply_patch" },
            ],
            ToolChoice = ResponseToolChoice.WebSearch(),
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"type\":\"json_schema\"", body, StringComparison.Ordinal);
        Assert.Contains("\"name\":\"answer\"", body, StringComparison.Ordinal);
        Assert.Contains("\"additionalProperties\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"firstName\":{\"type\":\"string\"}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("additional_properties", body, StringComparison.Ordinal);
        Assert.DoesNotContain("first_name", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"web_search\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"custom\"", body, StringComparison.Ordinal);
        Assert.Contains("\"tool_choice\":{\"type\":\"web_search\"}", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResponsesClient_DeserializesInputItemsAndMultilineUnknownSseEvents()
    {
        var sse = "event: response.output_item.added\n" +
                  "data: {\"type\":\"response.output_item.added\",\"item\":{\"type\":\"custom_tool_call\",\"input\":\"patch\"}}\n\n" +
                  "event: response.custom_tool_call_input.delta\n" +
                  "data: {\"delta\":\n" +
                  "data: \"first\"}\n\n" +
                  "event: response.completed\n" +
                  "data: {\"type\":\"response.completed\",\"response\":{\"status\":\"completed\"}}\n\n";
        var handler = CreateStreamingHandler(new MemoryStream(Encoding.UTF8.GetBytes(sse)));

        var events = new List<ResponseStreamEvent>();
        await foreach (var item in CreateResponsesClient(handler).CreateResponseStreaming(new ResponseRequest { Input = "hello" }))
        {
            events.Add(item);
        }

        Assert.Equal(3, events.Count);
        Assert.IsType<ResponseCustomToolCallOutputItem>(events[0].Item);
        Assert.Equal("response.custom_tool_call_input.delta", events[1].Type);
        Assert.Equal("response.custom_tool_call_input.delta", events[1].Event);
        Assert.NotNull(events[1].RawData);
        Assert.Equal("response.completed", events[2].Type);
    }

    [Fact]
    public void ResponsesClient_RequiresInputOrInstructions()
    {
        var client = CreateResponsesClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        }));

        Assert.Throws<ArgumentException>(() => client.CreateResponse(new ResponseRequest()));
    }

    [Fact]
    public async Task ChatClient_SendsExpectedHeadersPathAndBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            UserAgentApplicationId = "tests",
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            Thinking = ThinkingMode.Enabled,
            ReasoningEffort = ChatReasoningEffort.High,
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content?.Text);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.LastRequest.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Contains("tests", string.Join(" ", handler.LastRequest.Headers.UserAgent.Select(static part => part.ToString())));

        var body = handler.LastRequestBody!;
        Assert.Contains("\"model\":\"deepseek-v4-pro\"", body);
        Assert.Contains("\"stream\":false", body);
        Assert.Contains("\"thinking\":{\"type\":\"enabled\"}", body);
        Assert.Contains("\"reasoning_effort\":\"high\"", body);
    }

    [Fact]
    public async Task ChatClient_SerializesMultimodalContentParts()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = new List<ChatContentPart>
                    {
                        new ChatTextContentPart("Describe this."),
                        new ChatImageContentPart("data:image/png;base64,AAAA", "high"),
                        new ChatFileContentPart { FileId = "file-api-1" },
                    },
                },
            ],
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"type\":\"text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"image_url\"", body, StringComparison.Ordinal);
        Assert.Contains("\"detail\":\"high\"", body, StringComparison.Ordinal);
        Assert.Contains("\"url\":\"data:image/png;base64,AAAA\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"file\"", body, StringComparison.Ordinal);
        Assert.Contains("\"file_id\":\"file-api-1\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_RejectsFilePartWithBothFileIdAndFileData()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages =
            [new ChatMessage
            {
                Role = "user",
                Content = new ChatContentPart[]
                {
                    new ChatFileContentPart { FileId = "file-api-1", FileData = "data:image/png;base64,AAAA" },
                },
            }],
        }));
    }

    [Fact]
    public async Task ResponsesClient_SerializesInputImageAndFunctionOutputContent()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"resp_1\",\"status\":\"completed\",\"output\":[]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateResponsesClient(handler);

        await client.CreateResponseAsync(new ResponseRequest
        {
            Input =
            [new ResponseInputMessage
            {
                Role = "user",
                Content = new ResponseContentPart[]
                {
                    new ResponseInputTextContent { Text = "What is this?" },
                    new ResponseInputImageContent { ImageUrl = "data:image/jpeg;base64,AAAA", Detail = "original" },
                    new ResponseInputImageContent { FileId = "file-api-1" },
                },
            }],
        });

        var body = handler.LastRequestBody!;
        Assert.Contains("\"type\":\"input_text\"", body, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"input_image\"", body, StringComparison.Ordinal);
        Assert.Contains("\"image_url\":\"data:image/jpeg;base64,AAAA\"", body, StringComparison.Ordinal);
        Assert.Contains("\"detail\":\"original\"", body, StringComparison.Ordinal);
        Assert.Contains("\"file_id\":\"file-api-1\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicClient_AddsFilesBetaHeaderOnlyForFileImages()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var client = new AnthropicClient("deepseek-v4-flash-vision-exp", "test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        });

        await client.CreateMessageAsync(new AnthropicMessageRequest
        {
            Messages =
            [new AnthropicMessage
            {
                Content =
                [new AnthropicContentBlock
                {
                    Type = "image",
                    Source = new AnthropicImageSource { Type = "file", FileId = "file-api-1" },
                }],
            }],
        });

        Assert.True(handler.LastRequest!.Headers.TryGetValues("anthropic-beta", out var beta));
        Assert.Contains("files-api-2025-04-14", beta);

        await client.CreateMessageAsync(new AnthropicMessageRequest
        {
            Messages =
            [new AnthropicMessage
            {
                Content =
                [new AnthropicContentBlock
                {
                    Type = "image",
                    Source = new AnthropicImageSource { Type = "url", Url = "https://example.com/image.png" },
                }],
            }],
        });

        Assert.False(handler.LastRequest!.Headers.Contains("anthropic-beta"));
    }

    [Fact]
    public async Task FilesClient_UploadsMultipartAndSupportsFileOperations()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = request.Method == HttpMethod.Post
                ? "{\"id\":\"file-api-1\",\"object\":\"file\",\"bytes\":3,\"created_at\":1,\"filename\":\"image.png\",\"purpose\":\"user_data\",\"future\":true}"
                : path == "/files/file-api-1"
                    ? "{\"id\":\"file-api-1\",\"object\":\"file\",\"bytes\":3,\"filename\":\"image.png\",\"purpose\":\"user_data\"}"
                    : "{\"id\":\"file-api-1\",\"object\":\"file\",\"deleted\":true}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetFilesClient();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc"));

        var uploaded = await client.UploadFileAsync(stream, "image.png", new FileUploadOptions { ExpiresAfterSeconds = 3600 });
        Assert.Equal("file-api-1", uploaded.Value.Id);
        Assert.Contains("name=file", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("filename=image.png", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("name=purpose", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("user_data", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("expires_after[seconds]", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.True(stream.CanRead);

        var listed = await client.ListFilesAsync(new FileListOptions { After = "file-api-0", Limit = 10, Order = "desc", Purpose = "user_data" });
        Assert.Equal("https://api.deepseek.com/files?after=file-api-0&limit=10&order=desc&purpose=user_data", handler.LastRequest!.RequestUri!.ToString());
        Assert.NotNull(listed.Value);

        await client.RetrieveFileAsync("file-api-1");
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        await client.DeleteFileAsync("file-api-1");
        Assert.Equal(HttpMethod.Delete, handler.LastRequest!.Method);
    }

    [Fact]
    public void FilesClient_ValidatesUploadExpiryAndListLimit()
    {
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            }))),
        }).GetFilesClient();

        using var stream = new MemoryStream([1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.UploadFile(stream, "a.png", new FileUploadOptions { ExpiresAfterSeconds = 3599 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => client.ListFiles(new FileListOptions { Limit = 1001 }));
    }

    [Fact]
    public void FilesClient_RejectsSynchronousUploadLargerThan64MiBBeforeSendingRequest()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetFilesClient();
        using var stream = new GeneratedStream((64L * 1024 * 1024) + 1);

        var exception = Assert.Throws<InvalidDataException>(() => client.UploadFile(stream, "large.bin"));

        Assert.Contains("64 MiB", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
        Assert.Equal((64L * 1024 * 1024) + 1, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task FilesClient_RejectsAsynchronousUploadLargerThan64MiBBeforeSendingRequest()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetFilesClient();
        using var stream = new GeneratedStream((64L * 1024 * 1024) + 1);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => client.UploadFileAsync(stream, "large.bin"));

        Assert.Contains("64 MiB", exception.Message, StringComparison.Ordinal);
        Assert.Null(handler.LastRequest);
        Assert.Equal((64L * 1024 * 1024) + 1, stream.BytesRead);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ChatClient_RequestBody_UsesRelaxedJsonEscapingForEmbeddedJsonAndUnicode()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "请按这个 JSON 返回：{\"question\":\"今天天气\",\"answer\":\"晴天😀\"}",
                },
            ],
        });

        var body = Assert.IsType<string>(handler.LastRequestBody);
        Assert.DoesNotContain("\\u0022question\\u0022", body, StringComparison.Ordinal);
        Assert.Contains("\\\"question\\\":\\\"今天天气\\\"", body, StringComparison.Ordinal);
        Assert.Contains("请按这个 JSON 返回", body, StringComparison.Ordinal);
        Assert.Contains("今天天气", body, StringComparison.Ordinal);
        Assert.Contains("晴天", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_MapsErrorToDeepSeekException()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid request\",\"type\":\"invalid_request_error\"}}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }));

        Assert.Equal(400, exception.Status);
        Assert.Equal("invalid request", exception.Message);
        Assert.Equal("https://api.deepseek.com/chat/completions", exception.RequestUri.ToString());
        Assert.Contains("invalid_request_error", exception.ResponseContent);
    }

    [Fact]
    public async Task ChatClient_ParsesStreamingServerSentEvents()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        var chunks = new List<ChatCompletion>();
        await foreach (var chunk in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hel", chunks[0].Choices[0].Delta?.Content?.Text);
        Assert.Equal("lo", chunks[1].Choices[0].Delta?.Content?.Text);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_StopsAtDoneBeforeConnectionCloses()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var client = CreateChatClient(handler);
        var enumeration = Task.Run(async () =>
        {
            var chunks = new List<ChatCompletion>();
            await foreach (var chunk in client.CompleteChatStreaming(new ChatCompletionRequest
            {
                Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            }))
            {
                chunks.Add(chunk);
            }

            return chunks;
        });

        stream.WriteChunk("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n");
        stream.WriteChunk("data: [DONE]\n\n: heartbeat\n\n");

        try
        {
            Assert.Same(enumeration, await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(2))));
            var chunks = await enumeration;
            var chunk = Assert.Single(chunks);
        Assert.Equal("ok", chunk.Choices[0].Delta?.Content?.Text);
        }
        finally
        {
            stream.Complete();
            await enumeration;
        }
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_WithDefaultLogging_DoesNotConsumeResponseStream()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"role\":\"assistant\",\"content\":\"Hel\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{\"content\":\"lo\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(new RecordingLoggerProvider());
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var chunks = new List<ChatCompletion>();
        await foreach (var chunk in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hel", chunks[0].Choices[0].Delta?.Content?.Text);
        Assert.Equal("lo", chunks[1].Choices[0].Delta?.Content?.Text);
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var client = CreateChatClient(handler);
        var firstChunk = new TaskCompletionSource<ChatCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CompleteChatStreaming(
                new ChatCompletionRequest
                {
                    Messages = [new ChatMessage { Role = "user", Content = "hello" }],
                },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"choices\":[{\"delta\":{\"content\":\"Hel\"}}]}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Choices[0].Delta?.Content?.Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ChatClient_StreamingRequest_SendsStreamOptionsInRequestBody()
    {
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");

        await foreach (var _ in client.CompleteChatStreaming(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            StreamOptions = new StreamOptions { IncludeUsage = true },
        }))
        {
        }

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("\"stream_options\":{\"include_usage\":true}", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChatClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var client = CreateChatClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteChatAsync(
            new ChatCompletionRequest
            {
                Messages = [new ChatMessage { Role = "user", Content = "hello" }],
            },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CompleteChatAsync'.", exception.Message);
    }

    [Fact]
    public async Task CompletionClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetCompletionsClient("deepseek-v4-pro");
        var firstChunk = new TaskCompletionSource<Completion>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CompleteTextStreaming(
                new CompletionRequest { Prompt = "hello" },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"choices\":[{\"text\":\"Hel\"}]}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Choices[0].Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletionClient_StreamingRequest_StopsAtDoneBeforeConnectionCloses()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var client = CreateCompletionsClient(handler);
        async Task<List<Completion>> ReadStream()
        {
            var chunks = new List<Completion>();
            await foreach (var chunk in client.CompleteTextStreaming(new CompletionRequest { Prompt = "hello" }))
            {
                chunks.Add(chunk);
            }

            return chunks;
        }

        var enumeration = ReadStream();

        stream.WriteChunk("data: {\"choices\":[{\"text\":\"ok\"}]}\n\n");
        stream.WriteChunk("data: [DONE]\n\n: heartbeat\n\n");

        try
        {
            Assert.Same(enumeration, await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(2))));
            var chunks = await enumeration;
            var chunk = Assert.Single(chunks);
            Assert.Equal("ok", chunk.Choices[0].Text);
        }
        finally
        {
            stream.Complete();
            await enumeration;
        }
    }

    [Fact]
    public async Task CompletionClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new DeepSeekClient("test-key", options).GetCompletionsClient("deepseek-v4-pro");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteTextAsync(
            new CompletionRequest { Prompt = "hello" },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CompleteTextAsync'.", exception.Message);
    }

    [Fact]
    public async Task AnthropicClient_NonStreamingRequest_SendsStreamFalseInRequestBody()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        var response = await client.CreateMessageAsync(new AnthropicMessageRequest
        {
            Stream = true,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                },
            ],
        });

        Assert.Equal("ok", response.Value.Content[0].Text);
        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":false", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicClient_StreamingRequest_SendsStreamTrueInRequestBody()
    {
        var sse = "data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"ok\"}}\n\n" +
                  "data: [DONE]\n\n";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(sse))),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        await foreach (var _ in client.CreateMessageStreaming(new AnthropicMessageRequest
        {
            Stream = false,
            Messages =
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                },
            ],
        }))
        {
        }

        Assert.NotNull(handler.LastRequestBody);
        Assert.Contains("\"stream\":true", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnthropicClient_StreamingRequest_WithRequestOptions_YieldsBeforeStreamCompletes()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);
        var firstChunk = new TaskCompletionSource<AnthropicStreamEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        var enumeration = Task.Run(async () =>
        {
            await foreach (var chunk in client.CreateMessageStreaming(
                new AnthropicMessageRequest
                {
                    Messages =
                    [
                        new AnthropicMessage
                        {
                            Role = "user",
                            Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                        },
                    ],
                },
                new System.ClientModel.Primitives.RequestOptions()))
            {
                firstChunk.TrySetResult(chunk);
            }
        });

        stream.WriteChunk("data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"Hel\"}}\n\n");

        var first = await firstChunk.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Hel", first.Delta?.Text);

        await Task.Delay(100);
        Assert.False(enumeration.IsCompleted);

        stream.WriteChunk("data: [DONE]\n\n");
        stream.Complete();
        await enumeration.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AnthropicClient_StreamingRequest_StopsAtDoneBeforeConnectionCloses()
    {
        var stream = new DelayedChunkStream();
        var handler = CreateStreamingHandler(stream);
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        });
        var enumeration = Task.Run(async () =>
        {
            var events = new List<AnthropicStreamEvent>();
            await foreach (var item in client.CreateMessageStreaming(new AnthropicMessageRequest
            {
                Messages =
                [
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                    },
                ],
            }))
            {
                events.Add(item);
            }

            return events;
        });

        stream.WriteChunk("data: {\"type\":\"content_block_delta\",\"delta\":{\"text\":\"ok\"}}\n\n");
        stream.WriteChunk("data: [DONE]\n\n: heartbeat\n\n");

        try
        {
            Assert.Same(enumeration, await Task.WhenAny(enumeration, Task.Delay(TimeSpan.FromSeconds(2))));
            var events = await enumeration;
            var item = Assert.Single(events);
            Assert.Equal("ok", item.Delta?.Text);
        }
        finally
        {
            stream.Complete();
            await enumeration;
        }
    }

    [Fact]
    public void ResponsesJson_SerializesUnknownBaseContentAndOutputItems()
    {
        var options = GetDeepSeekSerializerOptions();
        var content = JsonSerializer.Deserialize<ResponseContentPart>(
            "{\"type\":\"future_content\",\"text\":\"value\",\"future_value\":42}", options);
        var output = JsonSerializer.Deserialize<ResponseOutputItem>(
            "{\"type\":\"future_item\",\"content\":[{\"type\":\"future_content\",\"future_value\":42}],\"future_value\":true}", options);

        var contentJson = JsonSerializer.Serialize(content, options);
        var outputJson = JsonSerializer.Serialize(output, options);
        using var contentDocument = JsonDocument.Parse(contentJson);
        using var outputDocument = JsonDocument.Parse(outputJson);

        Assert.Equal("future_content", contentDocument.RootElement.GetProperty("type").GetString());
        Assert.Equal(42, contentDocument.RootElement.GetProperty("future_value").GetInt32());
        Assert.Equal("future_item", outputDocument.RootElement.GetProperty("type").GetString());
        Assert.True(outputDocument.RootElement.GetProperty("future_value").GetBoolean());
        Assert.Equal(42, outputDocument.RootElement.GetProperty("content")[0].GetProperty("future_value").GetInt32());
    }

    [Fact]
    public async Task AnthropicClient_NonStreamingRequest_WithBufferResponseFalse_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"content\":[{\"type\":\"text\",\"text\":\"ok\"}]}", Encoding.UTF8, "application/json"),
        });
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        var client = new AnthropicClient("deepseek-v4-pro", "test-key", options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.CreateMessageAsync(
            new AnthropicMessageRequest
            {
                Messages =
                [
                    new AnthropicMessage
                    {
                        Role = "user",
                        Content = [new AnthropicContentBlock { Type = "text", Text = "hello" }],
                    },
                ],
            },
            new System.ClientModel.Primitives.RequestOptions { BufferResponse = false }));

        Assert.Equal("'RequestOptions.BufferResponse' must be 'true' when calling 'CreateMessageAsync'.", exception.Message);
    }

    [Fact]
    public void ClientOptions_CloneAndFreeze_DisablesMessageContentLoggingByDefault()
    {
        var options = new DeepSeekClientOptions
        {
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = NullLoggerFactory.Instance,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 2048,
            },
        };
        options.ClientLoggingOptions.AllowedHeaderNames.Add("x-test-header");
        options.ClientLoggingOptions.AllowedQueryParameters.Add("api-version");

        var client = new DeepSeekClient("test-key", options);
        var frozenLoggingOptions = Assert.IsType<System.ClientModel.Primitives.ClientLoggingOptions>(client.Options.ClientLoggingOptions);

        Assert.NotSame(options.ClientLoggingOptions, frozenLoggingOptions);
        Assert.Same(NullLoggerFactory.Instance, frozenLoggingOptions.LoggerFactory);
        Assert.True(frozenLoggingOptions.EnableLogging);
        Assert.True(frozenLoggingOptions.EnableMessageLogging);
        Assert.False(frozenLoggingOptions.EnableMessageContentLogging);
        Assert.Equal(2048, frozenLoggingOptions.MessageContentSizeLimit);
        Assert.Contains("x-test-header", frozenLoggingOptions.AllowedHeaderNames);
        Assert.Contains("api-version", frozenLoggingOptions.AllowedQueryParameters);

        options.ClientLoggingOptions.MessageContentSizeLimit = 1024;
        options.ClientLoggingOptions.AllowedHeaderNames.Add("x-second-header");

        Assert.Equal(2048, frozenLoggingOptions.MessageContentSizeLimit);
        Assert.DoesNotContain("x-second-header", frozenLoggingOptions.AllowedHeaderNames);
    }

    [Fact]
    public void ClientOptions_CloneAndFreeze_EnablesMessageContentLogging_WhenExplicitlyAllowed()
    {
        var options = new DeepSeekClientOptions
        {
            AllowMessageContentLogging = true,
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = NullLoggerFactory.Instance,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 2048,
            },
        };

        var client = new DeepSeekClient("test-key", options);
        var frozenLoggingOptions = Assert.IsType<System.ClientModel.Primitives.ClientLoggingOptions>(client.Options.ClientLoggingOptions);

        Assert.True(client.Options.AllowMessageContentLogging);
        Assert.True(frozenLoggingOptions.EnableMessageContentLogging);
    }

    [Fact]
    public async Task ChatClient_EmitsPipelineMetadataLogs_WhenMessageContentLoggingIsNotOptedIn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content?.Text);
        Assert.Contains(provider.Entries, static entry => entry.Category.StartsWith("System.ClientModel", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("Bearer test-key", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("hello", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("\"choices\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatClient_EmitsPipelineLogsAndMessageContent_WhenExplicitlyOptedIn()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}", Encoding.UTF8, "application/json"),
        });
        var provider = new RecordingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(provider);
            builder.AddFilter("System.ClientModel", LogLevel.Trace);
        });

        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            AllowMessageContentLogging = true,
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
            ClientLoggingOptions = new System.ClientModel.Primitives.ClientLoggingOptions
            {
                LoggerFactory = loggerFactory,
                EnableLogging = true,
                EnableMessageLogging = true,
                EnableMessageContentLogging = true,
                MessageContentSizeLimit = 1024,
            },
        }).GetChatClient("deepseek-v4-pro");

        var response = await client.CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Role = "user", Content = "hello" }],
        });

        Assert.Equal("ok", response.Value.Choices[0].Message?.Content?.Text);
        Assert.Contains(provider.Entries, static entry => entry.Category.StartsWith("System.ClientModel", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Entries, static entry => entry.Message.Contains("Bearer test-key", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("hello", StringComparison.Ordinal));
        Assert.Contains(provider.Entries, static entry => entry.Message.Contains("\"choices\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesClient_FileOperations_TargetTheSelectedUploadedFile(bool synchronous)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"id":"file-api-1","deleted":true}""", Encoding.UTF8, "application/json"),
        });
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetFilesClient();

        if (synchronous) client.RetrieveFile("file-api-1");
        else await client.RetrieveFileAsync("file-api-1");
        Assert.Equal("/files/file-api-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.LastRequest.Method);

        if (synchronous) client.DeleteFile("file-api-1");
        else await client.DeleteFileAsync("file-api-1");
        Assert.Equal("/files/file-api-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Delete, handler.LastRequest.Method);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("none")]
    public void ChatClient_ReasoningEffort_PreservesTheRequestedCostAndThinkingMode(string effort)
    {
        var options = GetDeepSeekSerializerOptions();
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>("{\"reasoning_effort\":\"" + effort + "\"}", options)!;

        Assert.NotNull(request.ReasoningEffort);
        using var roundTrip = JsonDocument.Parse(JsonSerializer.Serialize(request, options));
        Assert.Equal(effort, roundTrip.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0, (int)ChatReasoningEffort.High);
        Assert.Equal(1, (int)ChatReasoningEffort.Max);
    }

    [Fact]
    public async Task ChatClient_UserIsolation_UsesTheDocumentedUserIdField()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"choices":[]}""", Encoding.UTF8, "application/json"),
        });
        await CreateChatClient(handler).CompleteChatAsync(new ChatCompletionRequest
        {
            Messages = [new ChatMessage { Content = "hello" }],
            UserId = "tenant-user",
        });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("tenant-user", body.RootElement.GetProperty("user_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("user", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesClient_AnthropicProtocol_PreservesSharedFileMetadataAndUsesBetaHeaders(bool synchronous)
    {
        const string file = """{"id":"file-api-1","type":"file","size_bytes":3,"created_at":"2026-10-17T00:00:00Z","filename":"image.png","mime_type":"image/png"}""";
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.Method == HttpMethod.Delete
                ? """{"id":"file-api-1","type":"file_deleted"}"""
                : request.RequestUri!.Query.Length > 0 ? "{\"data\":[" + file + "],\"has_more\":false}" : file,
                Encoding.UTF8, "application/json"),
        });
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetAnthropicFilesClient();
        using var stream = new MemoryStream([1, 2, 3]);

        var uploaded = synchronous ? client.UploadFile(stream, "image.png") : await client.UploadFileAsync(stream, "image.png");
        Assert.Equal("/anthropic/v1/files", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("files-api-2025-04-14", Assert.Single(handler.LastRequest.Headers.GetValues("anthropic-beta")));
        Assert.Equal(3, uploaded.Value.Bytes);
        Assert.Equal("image/png", uploaded.Value.MimeType);
        Assert.Equal(DateTimeOffset.Parse("2026-10-17T00:00:00Z").ToUnixTimeSeconds(), uploaded.Value.CreatedAt);
        Assert.Contains("size_bytes", uploaded.GetRawResponse().Content.ToString());
        Assert.DoesNotContain("name=purpose", handler.LastRequestBody!);
        Assert.True(stream.CanRead);

        var listOptions = new DeepSeek.Files.FileListOptions { Before = "file-api-2", Limit = 1 };
        var list = synchronous ? client.ListFiles(listOptions) : await client.ListFilesAsync(listOptions);
        Assert.Equal("?before_id=file-api-2&limit=1", handler.LastRequest!.RequestUri!.Query);
        Assert.Equal("image/png", Assert.Single(list.Value.Data).MimeType);
        Assert.Equal("files-api-2025-04-14", Assert.Single(handler.LastRequest.Headers.GetValues("anthropic-beta")));

        var retrieved = synchronous ? client.RetrieveFile("file-api-1") : await client.RetrieveFileAsync("file-api-1");
        Assert.Equal("/anthropic/v1/files/file-api-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("file-api-1", retrieved.Value.Id);

        var deleted = synchronous ? client.DeleteFile("file-api-1") : await client.DeleteFileAsync("file-api-1");
        Assert.Equal("/anthropic/v1/files/file-api-1", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.True(deleted.Value.Deleted);
        Assert.Contains("file_deleted", deleted.GetRawResponse().Content.ToString());
        Assert.Equal("files-api-2025-04-14", Assert.Single(handler.LastRequest.Headers.GetValues("anthropic-beta")));
    }

    [Fact]
    public void FilesClient_ProtocolSpecificOptions_AreNotSilentlyIgnored()
    {
        var sdk = new DeepSeekClient("test-key");
        Assert.Throws<ArgumentException>(() => sdk.GetFilesClient().ListFiles(new DeepSeek.Files.FileListOptions { Before = "file-api-1" }));
        var anthropic = sdk.GetAnthropicFilesClient();
        Assert.Throws<ArgumentException>(() => anthropic.ListFiles(new DeepSeek.Files.FileListOptions { After = "a", Before = "b" }));
        Assert.Throws<ArgumentException>(() => anthropic.ListFiles(new DeepSeek.Files.FileListOptions { Order = "desc" }));
        Assert.Throws<ArgumentException>(() => anthropic.ListFiles(new DeepSeek.Files.FileListOptions { Purpose = "user_data" }));
        using var stream = new MemoryStream([1]);
        Assert.Throws<ArgumentException>(() => anthropic.UploadFile(stream, "image.png", new DeepSeek.Files.FileUploadOptions { ExpiresAfterSeconds = 3600 }));
    }

    private static ChatClient CreateChatClient(RecordingHandler handler)
    {
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new DeepSeekClient("test-key", options).GetChatClient("deepseek-v4-pro");
    }

    private static ResponsesClient CreateResponsesClient(RecordingHandler handler)
    {
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new DeepSeekClient("test-key", options).GetResponsesClient("deepseek-v4-flash");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilesClient_DocumentedUploadBoundaries_AcceptMaximumPayloadAndUtf8Name(bool synchronous)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"boundary\",\"bytes\":67108864}", Encoding.UTF8, "application/json"),
        });
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        }).GetFilesClient();
        var name = new string('界', 170) + "ab"; // 512 UTF-8 bytes, not 512 UTF-16 code units.
        using var stream = new GeneratedStream(64L * 1024 * 1024);
        var result = synchronous ? client.UploadFile(stream, name) : await client.UploadFileAsync(stream, name);
        Assert.Equal(67108864, result.Value.Bytes);
        Assert.Equal(67108864, stream.BytesRead);
        Assert.True(stream.CanRead);
        using var small = new MemoryStream([1]);
        Assert.Throws<ArgumentException>(() => client.UploadFile(small, name + "x"));
    }

    [Theory]
    [InlineData(3600)]
    [InlineData(2592000)]
    public async Task FilesClient_ExpiryBoundary_KeepsTheCallersRetentionPolicy(int seconds)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) }).GetFilesClient();
        using var stream = new MemoryStream([1]);
        await client.UploadFileAsync(stream, "a.png", new FileUploadOptions { ExpiresAfterSeconds = seconds });
        Assert.Contains(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), handler.LastRequestBody!);
        Assert.Throws<ArgumentOutOfRangeException>(() => client.UploadFile(stream, "a.png", new FileUploadOptions { ExpiresAfterSeconds = 2592001 }));
        Assert.Throws<ArgumentException>(() => client.UploadFile(stream, "a.png", new FileUploadOptions { ExpiresAfterAnchor = "created_at" }));
        Assert.Throws<ArgumentException>(() => client.UploadFile(stream, "a.png", new FileUploadOptions { ExpiresAfterSeconds = seconds, ExpiresAfterAnchor = "last_active_at" }));
    }

    private sealed class GeneratedStream : Stream
    {
        private long _remaining;

        public GeneratedStream(long length)
            => _remaining = length;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = (int)Math.Min(_remaining, count);
            _remaining -= read;
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Read(buffer, offset, count));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static CompletionsClient CreateCompletionsClient(RecordingHandler handler)
    {
        var options = new DeepSeekClientOptions
        {
            Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(new HttpClient(handler)),
        };
        return new DeepSeekClient("test-key", options).GetCompletionsClient("deepseek-v4-pro");
    }

    private static JsonSerializerOptions GetDeepSeekSerializerOptions()
    {
        var jsonType = typeof(DeepSeekClient).Assembly.GetType("DeepSeek.DeepSeekJson", throwOnError: true)!;
        return (JsonSerializerOptions)jsonType
            .GetProperty("SerializerOptions", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
    }

    [Fact]
    public async Task AnthropicClient_MessageStop_EndsBeforeTheHttpConnectionCloses()
    {
        var stream = new DelayedChunkStream();
        var sdk = new DeepSeekClient("test-key", new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(CreateStreamingHandler(stream))) });
        async Task<List<AnthropicStreamEvent>> Read()
        {
            var events = new List<AnthropicStreamEvent>();
            await foreach (var item in sdk.GetAnthropicClient("any-model").CreateMessageStreaming(new AnthropicMessageRequest())) events.Add(item);
            return events;
        }
        var task = Read();
        stream.WriteChunk("data: {\"type\":\"message_stop\"}\n\n");
        try
        {
            var events = await task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("message_stop", Assert.Single(events).Type);
        }
        finally { stream.Complete(); await task; }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task Streaming_RequestCancellation_InterruptsAStalledBody(string protocol)
    {
        var stream = new DelayedChunkStream();
        var sdk = new DeepSeekClient("test-key", new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(CreateStreamingHandler(stream))) });
        using var source = new CancellationTokenSource();
        var options = new RequestOptions { CancellationToken = source.Token };
        async Task Read()
        {
            if (protocol == "chat") await foreach (var item in sdk.GetChatClient("any").CompleteChatStreaming(new ChatCompletionRequest(), options)) { }
            else if (protocol == "anthropic") await foreach (var item in sdk.GetAnthropicClient("any").CreateMessageStreaming(new AnthropicMessageRequest(), options)) { }
            else await foreach (var item in sdk.GetResponsesClient("any").CreateResponseStreaming(new ResponseRequest { Input = "hello" }, options)) { }
        }
        var task = Read();
        source.Cancel();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2))); }
        finally
        {
            stream.Complete();
            try { await task; } catch (OperationCanceledException) { }
        }
    }

    private static RecordingHandler CreateStreamingHandler(Stream stream)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        });
        handler.ConfigureResponse(static response => response.Content.Headers.ContentType = new("text/event-stream"));
        return handler;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;
        private Action<HttpResponseMessage>? _configureResponse;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public void ConfigureResponse(Action<HttpResponseMessage> configureResponse)
        {
            _configureResponse = configureResponse;
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
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

        protected override void Dispose(bool disposing)
        {
            if (disposing) Complete();
            base.Dispose(disposing);
        }

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

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly List<LogEntry> _entries;

        public RecordingLogger(string categoryName, List<LogEntry> entries)
        {
            _categoryName = categoryName;
            _entries = entries;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(_categoryName, logLevel, formatter(state, exception)));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
