using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace DeepSeek.Agents.AI.UnitTests;

public sealed class DeepSeekJsonOutputTests
{
    private const string ObjectSchema = """{"type":"object","properties":{"state":{"type":"string","enum":["ok"]},"count":{"type":"integer","minimum":1}},"required":["state","count"],"additionalProperties":false}""";

    public static IEnumerable<object[]> Protocols()
    {
        foreach (var protocol in new[] { "chat", "anthropic", "responses" })
        foreach (var stream in new[] { false, true })
            yield return [protocol, stream];
    }

    public static IEnumerable<object[]> InvalidDocuments()
    {
        foreach (var row in Protocols())
        foreach (var json in new[] { "", " \r\n\t", "{", "```json\n{}\n```", "{} {}", "{\"x\":1,}", "{\"x\":NaN}", "null", "[]", "42" })
            yield return [row[0], row[1], json];
    }

    public static IEnumerable<object[]> InvalidSchemaDocuments()
    {
        foreach (var row in Protocols())
        foreach (var json in new[] { "{}", "{\"state\":\"ok\",\"count\":\"1\"}", "{\"state\":\"bad\",\"count\":1}", "{\"state\":\"ok\",\"count\":0}", "{\"state\":\"ok\",\"count\":1,\"extra\":true}" })
            yield return [row[0], row[1], json];
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonInstructions_PreserveCallerLanguageAndUseTheSameConciseChineseContract(string protocol, bool stream)
    {
        using var handler = new JsonHandler(protocol, stream, "{\"state\":\"ok\",\"count\":1}");
        using var client = CreateClient(protocol, handler);
        var options = new ChatOptions { Instructions = "字符串使用英语。", ResponseFormat = ChatResponseFormat.Json };
        await ReadResponse(client, options, stream);
        var plain = ReadInstructions(handler.Body!, protocol);
        Assert.StartsWith("字符串使用英语。", plain);
        Assert.Contains("仅输出一个合法的 JSON 对象。", plain);
        Assert.Contains("不要添加解释、Markdown 代码块或其他前后缀。", plain);
        Assert.Contains("字符串内容的语言遵循调用方要求。", plain);
        Assert.DoesNotContain("Alice", plain);
        Assert.DoesNotContain("You must", plain);
        Assert.Equal("字符串使用英语。", options.Instructions);

        options.ResponseFormat = Schema(ObjectSchema);
        await ReadResponse(client, options, stream);
        var schema = ReadInstructions(handler.Body!, protocol);
        if (protocol != "responses")
        {
            Assert.Contains("仅输出符合下方 JSON Schema 的 JSON 结果，不要输出 Schema 本身。", schema);
            Assert.Contains("不要添加解释、Markdown 代码块或其他前后缀。", schema);
            Assert.Contains("字符串内容的语言遵循调用方要求。", schema);
            Assert.Contains(ObjectSchema, schema);
            Assert.DoesNotContain("Alice", schema);
        }
        using var body = JsonDocument.Parse(handler.Body!);
        if (protocol == "anthropic") Assert.False(body.RootElement.TryGetProperty("response_format", out _));
        if (protocol == "responses") Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public async Task JsonOutput_RejectsUnusableDocumentsInsteadOfReturningSuccess(string protocol, bool stream, string json)
    {
        using var handler = new JsonHandler(protocol, stream, json);
        using var client = CreateClient(protocol, handler);
        var exception = await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, stream));
        Assert.NotNull(exception.Data["RawRepresentation"]);
    }

    [Theory]
    [MemberData(nameof(InvalidSchemaDocuments))]
    public async Task JsonSchema_RejectsMissingFieldsWrongTypesEnumsBoundsAndExtraFields(string protocol, bool stream, string json)
    {
        using var handler = new JsonHandler(protocol, stream, json);
        using var client = CreateClient(protocol, handler);
        await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, new ChatOptions { ResponseFormat = Schema(ObjectSchema) }, stream));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonOutput_PreservesUnicodeEscapesAndStandaloneSpacesInsideStrings(string protocol, bool stream)
    {
        const string json = " {\"message\":\"中文😀a b\\n\\\"\\\\\"} \n";
        using var handler = new JsonHandler(protocol, stream, json);
        using var client = CreateClient(protocol, handler);
        var response = await ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, stream);
        Assert.Equal(json, response.Text);
        using var document = JsonDocument.Parse(response.Text);
        Assert.Equal("中文😀a b\n\"\\", document.RootElement.GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonSchema_ValidatesArrayRootsLocalReferencesAndComposition(string protocol, bool stream)
    {
        const string schema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"array","minItems":1,"items":{"$ref":"#/$defs/item"},"$defs":{"item":{"anyOf":[{"type":"integer","minimum":1},{"type":"null"}]}}}""";
        using var handler = new JsonHandler(protocol, stream, "[1,null]");
        using var client = CreateClient(protocol, handler);
        var options = new ChatOptions { ResponseFormat = Schema(schema) };
        Assert.Equal("[1,null]", (await ReadResponse(client, options, stream)).Text);
        handler.Json = "[0]";
        await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, options, stream));
        handler.Json = "[]";
        await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, options, stream));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonOutput_RejectsTruncationEvenWhenThePrefixIsValidJson(string protocol, bool stream)
    {
        using var handler = new JsonHandler(protocol, stream, "{}") { Incomplete = true };
        using var client = CreateClient(protocol, handler);
        await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, stream));
    }

    [Theory]
    [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "[1,2]")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}", "{\"message\":\"hello\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"nested\":{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}}}", "{\"nested\":{\"a\":\"b\"}}")]
    public async Task ResponsesSchema_UsesPromptFallbackForShapesRejectedByTheNativeApi(string schema, string json)
    {
        foreach (var stream in new[] { false, true })
        {
            using var handler = new JsonHandler("responses", stream, json);
            using var client = CreateClient("responses", handler);
            Assert.Equal(json, (await ReadResponse(client, new ChatOptions { ResponseFormat = Schema(schema) }, stream)).Text);
            using var body = JsonDocument.Parse(handler.Body!);
            Assert.Equal("json_object", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            Assert.Contains(schema, ReadInstructions(handler.Body!, "responses"));
            handler.Json = "false";
            await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, new ChatOptions { ResponseFormat = Schema(schema) }, stream));
        }
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("anthropic")]
    [InlineData("responses")]
    public async Task JsonStreaming_RequiresACompletionSignalBeforeReportingSuccess(string protocol)
    {
        using var handler = new JsonHandler(protocol, true, "{}") { OmitCompletion = true };
        using var client = CreateClient(protocol, handler);
        await Assert.ThrowsAnyAsync<Exception>(() => ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, true));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonOutput_AllowsIntermediateToolCallsAndValidatesOnlyTheFinalDocument(string protocol, bool stream)
    {
        using var handler = new JsonHandler(protocol, stream, "") { ToolCall = true };
        using var client = CreateClient(protocol, handler);
        var response = await ReadResponse(client, new ChatOptions { ResponseFormat = Schema(ObjectSchema) }, stream);
        var call = Assert.Single(response.Messages.SelectMany(x => x.Contents).OfType<FunctionCallContent>());
        Assert.Equal("lookup", call.Name);
        Assert.Equal("call1", call.CallId);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task ResponseFormat_UsesMergedDefaultsAndAllowsExplicitTextOverride(string protocol, bool stream)
    {
        using var handler = new JsonHandler(protocol, stream, "plain text");
        using var client = CreateClient(protocol, handler, new DeepSeekChatClientOptions { ResponseFormat = ChatResponseFormat.Json });
        await Assert.ThrowsAsync<JsonException>(() => ReadResponse(client, new ChatOptions(), stream));
        Assert.Equal("plain text", (await ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Text }, stream)).Text);
        Assert.DoesNotContain("仅输出", ReadInstructions(handler.Body!, protocol));
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task JsonOutput_DoesNotReplaceCancellationWithValidationErrors(string protocol, bool stream)
    {
        using var handler = new JsonHandler(protocol, stream, "{}");
        using var client = CreateClient(protocol, handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadResponse(client, new ChatOptions { ResponseFormat = ChatResponseFormat.Json }, stream, cancellation.Token));
    }

    private static ChatResponseFormat Schema(string schema)
        => ChatResponseFormat.ForJsonSchema(JsonDocument.Parse(schema).RootElement.Clone());

    private static Task<ChatResponse> ReadResponse(IChatClient client, ChatOptions options, bool stream, CancellationToken cancellationToken = default)
        => stream ? client.GetStreamingResponseAsync([new(ChatRole.User, "测试")], options, cancellationToken).ToChatResponseAsync(cancellationToken: cancellationToken)
            : client.GetResponseAsync([new(ChatRole.User, "测试")], options, cancellationToken);

    private static string ReadInstructions(string body, string protocol)
    {
        using var document = JsonDocument.Parse(body);
        return protocol switch
        {
            "chat" => document.RootElement.GetProperty("messages").EnumerateArray().FirstOrDefault(x => x.GetProperty("role").GetString() == "system") is var system && system.ValueKind != JsonValueKind.Undefined ? system.GetProperty("content").GetString()! : "",
            "anthropic" => document.RootElement.TryGetProperty("system", out var system) ? system.GetString()! : "",
            _ => document.RootElement.TryGetProperty("instructions", out var instructions) ? instructions.GetString()! : "",
        };
    }

    private static IChatClient CreateClient(string protocol, HttpMessageHandler handler, DeepSeekChatClientOptions? defaults = null)
    {
        var sdk = new DeepSeekClient("test-key", new DeepSeekClientOptions { Transport = new HttpClientPipelineTransport(new HttpClient(handler)) });
        return protocol switch
        {
            "chat" => sdk.GetChatClient("deepseek-flash").AsIChatClient(defaults),
            "anthropic" => sdk.GetAnthropicClient("deepseek-flash").AsIChatClient(defaults),
            _ => sdk.GetResponsesClient("deepseek-flash").AsIChatClient(defaults),
        };
    }

    private sealed class JsonHandler(string protocol, bool stream, string json) : HttpMessageHandler
    {
        public string Json { get; set; } = json;
        public string? Body { get; private set; }
        public bool Incomplete { get; init; }
        public bool OmitCompletion { get; init; }
        public bool ToolCall { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var text = JsonSerializer.Serialize(Json);
            const string function = """{"id":"call1","type":"function","function":{"name":"lookup","arguments":"{}"}}""";
            const string responseCall = """{"id":"fc1","type":"function_call","call_id":"call1","name":"lookup","arguments":"{}"}""";
            const string anthropicCall = """{"type":"tool_use","id":"call1","name":"lookup","input":{}}""";
            var stop = ToolCall ? "tool_calls" : Incomplete ? "length" : "stop";
            var anthropicStop = ToolCall ? "tool_use" : Incomplete ? "max_tokens" : "end_turn";
            var content = protocol switch
            {
                "chat" => $$"""{"id":"chat1","choices":[{"index":0,"message":{"role":"assistant","content":{{text}}{{(ToolCall ? ",\"tool_calls\":[" + function + "]" : "")}}},"finish_reason":"{{stop}}"}]}""",
                "anthropic" => $$"""{"id":"msg1","content":[{{(ToolCall ? anthropicCall : "{\"type\":\"text\",\"text\":" + text + "}")}}],"stop_reason":"{{anthropicStop}}"}""",
                _ => $$"""{"id":"resp1","status":"{{(Incomplete ? "incomplete" : "completed")}}","output":[{{(ToolCall ? responseCall : "{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":" + text + "}]}")}}]}""",
            };
            if (stream)
            {
                var events = new StringBuilder();
                void Event(string data) => events.Append("data: ").Append(data).Append("\n\n");
                if (protocol == "anthropic")
                {
                    Event("""{"type":"message_start","message":{"id":"msg1","content":[]}}""");
                    Event("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":" + (ToolCall ? anthropicCall : "{\"type\":\"text\",\"text\":\"\"}") + "}");
                }
                if (ToolCall && protocol == "chat") Event("{\"id\":\"chat1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[" + function + "]}}]}");
                if (ToolCall && protocol == "responses") Event("{\"type\":\"response.output_item.done\",\"item\":" + responseCall + "}");
                // A whitespace-only delta inside a string must survive aggregation.
                foreach (var fragment in Json.Split(' ').SelectMany((part, index) => index == 0 ? new[] { part } : new[] { " ", part }))
                {
                    if (ToolCall || fragment.Length == 0) continue;
                    var delta = JsonSerializer.Serialize(fragment);
                    Event(protocol switch
                    {
                        "chat" => "{\"id\":\"chat1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":" + delta + "}}]}",
                        "anthropic" => "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":" + delta + "}}",
                        _ => "{\"type\":\"response.output_text.delta\",\"delta\":" + delta + "}",
                    });
                }
                if (!OmitCompletion)
                {
                    if (protocol == "chat")
                    {
                        Event("{\"id\":\"chat1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + stop + "\"}]}");
                        Event("[DONE]");
                    }
                    else if (protocol == "anthropic")
                    {
                        Event("""{"type":"content_block_stop","index":0}""");
                        Event("{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"" + anthropicStop + "\"}}");
                        Event("""{"type":"message_stop"}""");
                    }
                    else Event("{\"type\":\"response." + (Incomplete ? "incomplete" : "completed") + "\",\"response\":" + content + "}");
                }
                content = events.ToString();
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
        }
    }
}
