using System.ClientModel.Primitives;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Xunit;

namespace DeepSeek.Agents.AI.UnitTests;

public sealed class DeepSeekApplicationExtensionsTests
{
    [Theory]
    [InlineData("chat", "file")]
    [InlineData("responses", "input_file")]
    [InlineData("messages", "document")]
    public async Task Adapter_MapsOptInFlagsAndDoesNotTreatDocumentAsImage(string protocol, string documentType)
    {
        using var handler = new WireHandler(Success(protocol), stream: false);
        using var client = CreateClient(protocol, handler, new DeepSeekChatClientOptions
        {
            EnableWebSearch = true,
            EnableReadUrl = true,
            SafetyCheckLevel = "high",
            GenerateTitle = true,
        });
        var options = new ChatOptions { AdditionalProperties = new()
        {
            [DeepSeekChatOptionKeys.WebSearch] = false,
            [DeepSeekChatOptionKeys.SafetyCheckLevel] = "low",
        } };
        var user = new ChatMessage(ChatRole.User, [new TextContent("Read this"), new HostedFileContent("doc-1") { MediaType = "application/pdf" }]);
        var messages = protocol == "responses" ? new[] { user } :
            new[] { new ChatMessage(new ChatRole("latest_reminder"), "2026-11-18,Wednesday,,Web,English"), user };

        var result = await client.GetResponseAsync(messages, options);

        Assert.Equal("Read a document", result.AdditionalProperties!["title"]);
        var request = Assert.Single(handler.Requests);
        var tool = Assert.Single(request.GetProperty("tools").EnumerateArray());
        Assert.Equal("deepseek_read_url", tool.GetProperty("type").GetString());
        Assert.False(tool.TryGetProperty("function", out _));
        Assert.False(tool.TryGetProperty("input_schema", out _));
        Assert.Equal("low", request.GetProperty("safety_check").GetProperty("level").GetString());
        Assert.True(request.GetProperty("title").GetProperty("enabled").GetBoolean());
        var input = request.GetProperty(protocol == "responses" ? "input" : "messages");
        Assert.Equal(documentType, input[input.GetArrayLength() - 1].GetProperty("content")[1].GetProperty("type").GetString());
        if (protocol != "responses")
        {
            Assert.Equal("latest_reminder", input[0].GetProperty("role").GetString());
            Assert.Equal("2026-11-18,Wednesday,,Web,English", input[0].GetProperty("content").GetString());
        }
        if (protocol == "messages") Assert.Contains("files-api-2025-04-14", handler.BetaHeaders);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public async Task Streaming_ReplaysSignedHistoryOnceWithoutExecutingServerTool(string protocol)
    {
        using var handler = new WireHandler(Stream(protocol), stream: true);
        using var client = CreateClient(protocol, handler, new DeepSeekChatClientOptions { EnableReadUrl = true, EnableWebSearch = true });
        var user = new ChatMessage(ChatRole.User, "Read https://example.com");
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync([user])) updates.Add(update);

        Assert.Empty(updates.SelectMany(static update => update.Contents).OfType<FunctionCallContent>());
        Assert.Single(updates.SelectMany(static update => update.Contents).OfType<UsageContent>());
        Assert.Contains(updates, static update => update.AdditionalProperties?.TryGetValue("title", out var title) == true && Equals(title, "A page"));
        var response = updates.ToChatResponse();
        Assert.Equal("A page", response.AdditionalProperties?["title"]);
        Assert.Single(response.Messages);
        Assert.Equal(1, response.Usage?.AdditionalCounts?["tool_fetch"]);
        Assert.Equal(1, response.Usage?.AdditionalCounts?["tool_image_search"]);
        Assert.Equal(2, response.Usage?.AdditionalCounts?["safety_tokens"]);
        await client.GetResponseAsync([user, .. response.Messages, new ChatMessage(ChatRole.User, "Follow up")]);

        var serialized = handler.Requests[1].GetRawText();
        Assert.Equal(1, serialized.Split("opaque-signature", StringSplitOptions.None).Length - 1);
        Assert.Contains("future-wire-value", serialized);
        Assert.Equal(1, serialized.Split("https://example.com/source.png", StringSplitOptions.None).Length - 1);
        Assert.Contains("image_search", serialized);
        if (protocol == "messages")
        {
            Assert.DoesNotContain("future_finish_field", serialized);
            Assert.Contains(updates, static update => update.RawRepresentation is Anthropic.AnthropicStreamEvent { Type: "message_delta" });
        }
        if (protocol == "chat")
        {
            var assistant = handler.Requests[1].GetProperty("messages")[1];
            Assert.Equal("final reasoning", assistant.GetProperty("reasoning_content").GetString());
            Assert.DoesNotContain("step reasoning", assistant.GetProperty("reasoning_content").GetString());
            Assert.Contains(updates, static update => update.AdditionalProperties?.ContainsKey("server_tool_event") == true);
        }
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Adapter_SafetyRefusalIsDistinctFromJsonFailure(string protocol, bool stream)
    {
        var payload = protocol switch
        {
            "chat" => """{"choices":[{"message":{"content":"","refusal":"blocked image"},"delta":{"refusal":"blocked image"},"finish_reason":"content_filter"}]}""",
            "messages" => """{"type":"message_delta","stop_reason":"refusal","stop_details":{"type":"refusal","explanation":"blocked image"},"delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","explanation":"blocked image"}}}""",
            _ => """{"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[{"type":"message","content":[{"type":"refusal","refusal":"blocked image"}]}]}""",
        };
        using var handler = new WireHandler(stream ? ToEvents(protocol, payload, error: false) : payload, stream);
        using var client = CreateClient(protocol, handler);
        var options = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var message = new ChatMessage(ChatRole.User, "Return JSON");
        if (stream)
        {
            var updates = new List<ChatResponseUpdate>();
            await foreach (var update in client.GetStreamingResponseAsync([message], options)) updates.Add(update);
            var terminal = updates.Last();
            Assert.Equal(true, terminal.AdditionalProperties?["is_refusal"]);
            Assert.Equal("blocked image", terminal.AdditionalProperties?["refusal"]);
            Assert.Equal(true, updates.ToChatResponse().AdditionalProperties?["is_refusal"]);
        }
        else
        {
            var response = await client.GetResponseAsync([message], options);
            Assert.Equal(true, response.AdditionalProperties?["is_refusal"]);
            Assert.Equal("blocked image", response.AdditionalProperties?["refusal"]);
            Assert.Equal("", response.Text);
        }
    }

    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    public async Task Adapter_Http200GenerationErrorKeepsStatusAndRawFailure(string protocol, bool stream)
    {
        const string payload = """{"type":"error","status":"failed","error":{"code":"server_error","message":"failed during generation"},"usage":{"output_tokens":5}}""";
        using var handler = new WireHandler(stream ? ToEvents(protocol, payload, error: true) : payload, stream);
        using var client = CreateClient(protocol, handler);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            var user = new ChatMessage(ChatRole.User, "hello");
            if (stream) await client.GetStreamingResponseAsync([user]).ToChatResponseAsync();
            else await client.GetResponseAsync([user]);
        });
        Assert.Equal("failed during generation", error.Message);
        Assert.Equal("server_error", error.Data["code"]);
        Assert.NotNull(error.Data["RawRepresentation"]);
    }

    [Fact]
    public async Task Responses_ExtensionsRejectJsonAndLegacySearchInsteadOfSilentlyIgnoringOptions()
    {
        using var handler = new WireHandler(Success("responses"), false);
        using var client = CreateClient("responses", handler, new DeepSeekChatClientOptions { EnableWebSearch = true });
        var message = new ChatMessage(ChatRole.User, "search");
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([message], new ChatOptions { ResponseFormat = ChatResponseFormat.Json }));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetResponseAsync([message], new ChatOptions { Tools = [new HostedWebSearchTool()] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => client.GetResponseAsync([new ChatMessage(new ChatRole("latest_reminder"), "context"), message]));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("chat", "{\"choices\":[{\"delta\":{\"content\":\"provisional\"}}]}")]
    [InlineData("messages", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"text\":\"provisional\"}}")]
    [InlineData("responses", "{\"type\":\"response.output_text.delta\",\"delta\":\"provisional\"}")]
    public async Task Streaming_ExtensionsFailWhenFinalSafetyStatusIsMissing(string protocol, string payload)
    {
        using var handler = new WireHandler(Event(payload), stream: true);
        using var client = CreateClient(protocol, handler, new DeepSeekChatClientOptions { SafetyCheckLevel = "high" });
        await using var updates = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("provisional", updates.Current.Text);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await updates.MoveNextAsync());
    }

    private static string ToEvents(string protocol, string payload, bool error) => protocol switch
    {
        "chat" => Event(payload) + (error ? "" : Event("[DONE]")),
        "messages" => Event(payload) + (error ? "" : Event("""{"type":"message_stop"}""")),
        _ => Event("{\"type\":\"response." + (error ? "failed" : "incomplete") + "\",\"response\":" + payload + "}"),
    };

    private static string Stream(string protocol) => protocol switch
    {
        "chat" => Event("""{"id":"c","choices":[{"delta":{"reasoning_content":"step reasoning"}}]}""")
            + Event("""{"id":"c","choices":[{"delta":{},"server_tool_event":{"type":"step_reasoning_done","step":0}}]}""")
            + Event("""{"id":"c","choices":[{"delta":{"reasoning_content":"final reasoning","content":"An answer"}}]}""")
            + Event("""{"id":"c","choices":[{"delta":{"title":"A page","server_tool_calls":[{"step":0,"calls":[{"kind":"read_link","signature":"opaque-signature","future":"future-wire-value"},{"kind":"image_search","sources":[{"type":"url","image_url":"https://example.com/source.png"}]}]}],"annotations":[{"type":"url_citation","url_citation":{"url":"https://example.com","start_index":0,"end_index":2}}]},"finish_reason":"stop"}],"usage":{"total_tokens":10,"server_tool_use":{"tool_fetch":1,"tool_image_search":1},"completion_tokens_details":{"safety_tokens":2}}}""")
            + Event("[DONE]"),
        "messages" => Event("""{"type":"message_start","message":{"id":"m","content":[],"usage":{"input_tokens":7,"server_tool_use":{"tool_fetch":1}}}}""")
            + Event("""{"type":"content_block_start","index":0,"content_block":{"type":"server_tool_use","id":"srv-1","name":"deepseek_read_url","input":{"action":"read_link"}}}""")
            + Event("""{"type":"content_block_stop","index":0}""")
            + Event("""{"type":"content_block_start","index":1,"content_block":{"type":"web_fetch_tool_result","tool_use_id":"srv-1","signature":"opaque-signature","future":"future-wire-value"}}""")
            + Event("""{"type":"content_block_stop","index":1}""")
            + Event("""{"type":"content_block_start","index":2,"content_block":{"type":"text","text":""}}""")
            + Event("""{"type":"content_block_delta","index":2,"delta":{"type":"text_delta","text":"An answer"}}""")
            + Event("""{"type":"content_block_delta","index":2,"delta":{"type":"citations_delta","citation":{"type":"web_search_result_location","url":"https://example.com"}}}""")
            + Event("""{"type":"content_block_stop","index":2}""")
            + Event("""{"type":"content_block_start","index":3,"content_block":{"type":"web_search_tool_result","tool_use_id":"srv-2","kind":"image_search","content":[{"type":"web_search_result","image_url":"https://example.com/source.png"}]}}""")
            + Event("""{"type":"content_block_stop","index":3}""")
            + Event("""{"type":"message_delta","delta":{"stop_reason":"end_turn","title":"A page","future_finish_field":"terminal"},"usage":{"output_tokens":3,"server_tool_use":{"tool_fetch":1,"tool_image_search":1},"output_tokens_details":{"safety_tokens":2}}}""")
            + Event("""{"type":"message_stop"}"""),
        _ => Event("""{"type":"response.output_item.added","output_index":0,"item":{"type":"web_search_call","id":"srv-1","kind":"read_link"}}""")
            + Event("""{"type":"response.output_item.done","output_index":0,"item":{"type":"web_search_call","id":"srv-1","signature":"opaque-signature"}}""")
            + Event("""{"type":"response.output_text.delta","delta":"An answer"}""")
            + Event("""{"type":"response.completed","response":{"status":"completed","title":"A page","usage":{"input_tokens":7,"output_tokens":3,"total_tokens":10,"server_tool_use":{"tool_fetch":1,"tool_image_search":1},"output_tokens_details":{"safety_tokens":2}},"output":[{"type":"web_search_call","id":"srv-1","kind":"read_link","signature":"opaque-signature","future":"future-wire-value"},{"type":"web_search_call","id":"srv-2","kind":"image_search","action":{"type":"search","queries":[],"sources":[{"type":"url","image_url":"https://example.com/source.png"}]}},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"An answer"}]}]}}"""),
    };

    private static string Event(string data) => "data: " + data + "\n\n";

    private static string Success(string protocol) => protocol switch
    {
        "chat" => """{"choices":[{"message":{"role":"assistant","content":"done","title":"Read a document"},"finish_reason":"stop"}]}""",
        "messages" => """{"title":"Read a document","stop_reason":"end_turn","content":[{"type":"text","text":"done"}]}""",
        _ => """{"title":"Read a document","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"done"}]}]}""",
    };

    private static IChatClient CreateClient(string protocol, WireHandler handler, DeepSeekChatClientOptions? options = null)
    {
        var client = new DeepSeekClient("test-key", new DeepSeekClientOptions
        {
            Transport = new HttpClientPipelineTransport(new HttpClient(handler)), RetryPolicy = new ClientRetryPolicy(0),
        });
        return protocol switch
        {
            "chat" => client.GetChatClient("deepseek-flash").AsIChatClient(options),
            "messages" => client.GetAnthropicClient("deepseek-flash").AsIChatClient(options),
            _ => client.GetResponsesClient("deepseek-flash").AsIChatClient(options),
        };
    }

    private sealed class WireHandler(string body, bool stream) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<string> BetaHeaders { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(cancellationToken));
            Requests.Add(payload.RootElement.Clone());
            if (request.Headers.TryGetValues("anthropic-beta", out var beta)) BetaHeaders.AddRange(beta);
            if (stream && Requests.Count == 1)
                return new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/event-stream") };
            var protocol = request.RequestUri!.AbsolutePath switch { "/chat/completions" => "chat", "/anthropic/v1/messages" => "messages", _ => "responses" };
            return new(HttpStatusCode.OK) { Content = new StringContent(stream ? Success(protocol) : body) };
        }
    }
}
