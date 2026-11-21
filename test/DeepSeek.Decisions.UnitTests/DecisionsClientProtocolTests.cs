using System.Text.Json;
using Xunit;

namespace DeepSeek.Decisions.UnitTests;

public partial class DecisionsClientTests
{
    [Fact]
    public async Task CreateOpenAiDecision_OfficialHistoryAndMixedQuestions_PreservesOrderAndDoesNotRunTools()
    {
        using var handler = new StubHandler((body, _, _) =>
        {
            Assert.False(body.TryGetProperty("tools", out _));
            var question = body.GetProperty("messages").EnumerateArray().Last().GetRawText();
            if (question.Contains("decline", StringComparison.Ordinal))
                return Task.FromResult(Completion("", finish: "refusal"));
            return Task.FromResult(Completion("A", Logs("A", ("A", 0.8), ("B", 0.2))));
        });
        var result = await Client(handler).CreateOpenAiDecisionAsync(ProtocolJson("""
            {
              "model":"deepseek-flash",
              "input":[
                {"type":"message","role":"system","content":"policy marker"},
                {"role":"developer","content":[{"type":"input_text","text":"developer marker"}]},
                {"role":"assistant","phase":"final_answer","content":[{"type":"output_text","text":"previous answer"},{"type":"refusal","refusal":"historical refusal"}]},
                {"type":"message","role":"user","content":[{"type":"input_json","data":{"counter":5}},{"type":"input_image","image_url":"https://unit.test/photo.png","detail":"low"}]},
                {"type":"function_call","call_id":"f1","name":"lookup.entry","arguments":"{}","status":"completed"},
                {"type":"function_call_output","call_id":"f1","output":"lookup result"},
                {"type":"custom_tool_call","call_id":"c1","name":"plain_tool","input":"opaque historical input"},
                {"type":"custom_tool_call_output","call_id":"c1","output":[{"type":"input_text","text":"custom result"}]}
              ],
              "questions":[
                {"type":"predicate","name":"first","instructions":"Check the record."},
                {"type":"choice","name":"second","instructions":"Pick an option.","choices":[{"value":true},{"value":"true"}]},
                {"type":"score","name":"third","instructions":"Grade.","levels":[{"label":"low"},{"label":"high"}]},
                {"type":"predicate","instructions":"decline only this question"}
              ]
            }
            """));

        var answers = result.GetProperty("answers").EnumerateArray().ToArray();
        Assert.Equal(new[] { "predicate", "choice", "score", "refusal" }, answers.Select(item => item.GetProperty("type").GetString()));
        Assert.Equal(0.8, answers[0].GetProperty("probability").GetDouble(), 9);
        Assert.True(answers[1].GetProperty("choice").GetBoolean());
        Assert.Equal(JsonValueKind.String, answers[1].GetProperty("probabilities")[1].GetProperty("value").ValueKind);
        Assert.Equal(0.2, answers[2].GetProperty("score").GetDouble(), 9);
        Assert.Equal("high", answers[2].GetProperty("probabilities")[1].GetProperty("label").GetString());
        Assert.Equal(JsonValueKind.Null, answers[3].GetProperty("name").ValueKind);
        Assert.False(answers[3].TryGetProperty("probability", out _));
        var usage = result.GetProperty("usage");
        Assert.Equal(48, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(4, handler.Requests.Count);

        var messages = handler.Requests.First().GetProperty("messages").EnumerateArray().ToArray();
        Assert.Single(messages, message => message.GetProperty("role").GetString() == "system");
        Assert.All(messages.Skip(1), message => Assert.Equal("user", message.GetProperty("role").GetString()));
        var parts = messages[4].GetProperty("content").EnumerateArray().ToArray();
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "image_url");
        Assert.Contains(parts, part => part.TryGetProperty("text", out var value) && value.GetString()!.Contains("counter"));
        Assert.Contains("plain_tool", messages[7].GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"prompt_cache_key\":\"tenant\",", "\"hello\"", "unsupported_capability", "prompt_cache_key")]
    [InlineData("\"prompt_cache_options\":{\"mode\":\"explicit\"},", "\"hello\"", "unsupported_capability", "prompt_cache_options")]
    [InlineData("\"safety_identifier\":\"tenant\",", "\"hello\"", "unsupported_capability", "safety_identifier")]
    [InlineData("", "[{\"type\":\"function_call_output\",\"call_id\":\"unknown\",\"output\":\"hello\"}]", "invalid_request", "input[0].call_id")]
    [InlineData("", "[{\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"hi\",\"prompt_cache_breakpoint\":{\"mode\":\"explicit\"}}]}]",
        "unsupported_capability", "input[0].content[0].prompt_cache_breakpoint")]
    public async Task CreateOpenAiDecision_InvalidOrUnsupportedRequest_DoesNotSendToProvider(string additional, string input, string code, string parameter)
    {
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        var request = ProtocolJson($$"""
            {"model":"deepseek-flash",{{additional}}"input":{{input}},"questions":[{"type":"predicate","instructions":"Evaluate."}]}
            """);
        var error = await Assert.ThrowsAsync<DecisionProtocolException>(() => Client(handler).CreateOpenAiDecisionAsync(request));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(code, error.Code);
        Assert.Equal(parameter, error.Parameter);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateOpenAiDecision_MissingProviderUsage_DoesNotInventTokenCounts()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", Logs("A", ("A", 0.8), ("B", 0.2)), usage: false)));
        var request = ProtocolJson("""
            {"model":"deepseek-flash","input":[],"questions":[{"type":"predicate","instructions":"Evaluate."}]}
            """);
        var error = await Assert.ThrowsAsync<DecisionProtocolException>(() => Client(handler).CreateOpenAiDecisionAsync(request));
        Assert.Equal(502, error.StatusCode);
        Assert.Equal("upstream_usage_missing", error.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(",\"confidence\":null", null)]
    [InlineData(",\"confidence\":0", 0.0)]
    [InlineData(",\"confidence\":0.93", 0.93)]
    [InlineData(",\"confidence\":1", 1.0)]
    public async Task CreateJevSystemOne_MixedQuestionsIncludingBox_PreservesKeyedAnswersAndNullableConfidence(
        string confidenceProperty, double? expectedConfidence)
    {
        using var handler = new StubHandler((body, _, _) =>
        {
            var question = body.GetProperty("messages").EnumerateArray().Last().GetRawText();
            if (question.Contains("Question (box)", StringComparison.Ordinal))
            {
                Assert.Equal("json_object", body.GetProperty("response_format").GetProperty("type").GetString());
                Assert.False(body.TryGetProperty("logprobs", out _));
                var instructions = body.GetProperty("messages")[0].GetProperty("content").GetString();
                Assert.Contains("\"confidence\"", instructions, StringComparison.Ordinal);
                var state = body.GetProperty("messages")[1].GetProperty("content").EnumerateArray().ToArray();
                Assert.Contains(state, part => part.GetProperty("type").GetString() == "image_url");
                return Task.FromResult(Completion($$"""{"box":{"top":0.15,"bottom":0.85,"left":0.2,"right":0.8}{{confidenceProperty}} }"""));
            }
            return Task.FromResult(Completion("A", Logs("A", ("A", 0.8), ("B", 0.2))));
        });
        var result = await Client(handler).CreateJevSystemOneAsync(ProtocolJson(JevWithBox()), true, false);
        var answers = result.GetProperty("answers");
        Assert.Equal(0.8, answers.GetProperty("visible").GetProperty("noul").GetDouble(), 9);
        Assert.Equal("mug", answers.GetProperty("kind").GetProperty("choice").GetString());
        Assert.Equal(0.2, answers.GetProperty("size").GetProperty("score").GetDouble(), 9);
        Assert.Equal("large", answers.GetProperty("size").GetProperty("legend").GetProperty("1").GetString());
        var box = answers.GetProperty("where");
        Assert.Equal("box", box.GetProperty("type").GetString());
        Assert.Equal(0.2, box.GetProperty("box").GetProperty("left").GetDouble());
        var confidence = box.GetProperty("confidence");
        if (expectedConfidence.HasValue)
            Assert.Equal(expectedConfidence.Value, confidence.GetDouble());
        else
            Assert.Equal(JsonValueKind.Null, confidence.ValueKind);
        Assert.Equal(40, result.GetProperty("usage").GetProperty("input_tokens").GetInt32());
        Assert.Equal(2, result.GetProperty("usage").EnumerateObject().Count());
        Assert.False(result.GetProperty("usage").TryGetProperty("total_tokens", out _));
    }

    [Theory]
    [InlineData("""{"box":{"top":0.9,"bottom":0.1,"left":0,"right":1}}""")]
    [InlineData("""{"box":{"top":0.1,"bottom":0.9,"left":0,"right":1},"confidence":-0.01}""")]
    [InlineData("""{"box":{"top":0.1,"bottom":0.9,"left":0,"right":1},"confidence":1.01}""")]
    [InlineData("""{"box":{"top":0.1,"bottom":0.9,"left":0,"right":1},"confidence":"0.9"}""")]
    public async Task CreateJevSystemOne_RejectsInvalidBoxAfterOneCorrection(string invalidBox)
    {
        using var handler = new StubHandler((body, _, _) =>
        {
            var question = body.GetProperty("messages").EnumerateArray().Last().GetRawText();
            return Task.FromResult(question.Contains("Question (box)", StringComparison.Ordinal) ||
                question.Contains("output format", StringComparison.Ordinal)
                ? Completion(invalidBox)
                : Completion("A", Logs("A", ("A", 0.8), ("B", 0.2))));
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Client(handler).CreateJevSystemOneAsync(ProtocolJson(JevWithBox()), true, false));
        Assert.Equal("where", error.Data["QuestionId"]);
        Assert.Equal(2, handler.Requests.Count(body => body.GetRawText().Contains("Question (box)", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CreateJevSystemOne_ImagesRequireOptInAndEveryAssetMustBeReferenced()
    {
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        var client = Client(handler);
        var imagesOff = await Assert.ThrowsAsync<DecisionProtocolException>(() =>
            client.CreateJevSystemOneAsync(ProtocolJson(JevWithBox()), false, false));
        Assert.Equal("unsupported_capability", imagesOff.Code);
        var unusedRequest = JsonSerializer.SerializeToElement(new
        {
            model = "deepseek-flash",
            state = new { picture = true },
            assets = new { picture = new { format = "png", data_base64 = Png } },
            questions = new { item = new { type = "noul", instructions = "Evaluate." } },
        });
        var unusedAsset = await Assert.ThrowsAsync<DecisionProtocolException>(() =>
            client.CreateJevSystemOneAsync(unusedRequest, true, false));
        Assert.Equal(400, unusedAsset.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateJevSystemOne_LongContextOptIn_PermitsLargerValidatedRequest()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", Logs("A", ("A", 0.8), ("B", 0.2)))));
        var client = Client(handler);
        var request = JsonSerializer.SerializeToElement(new
        {
            model = "deepseek-flash",
            state = new string('a', 33000),
            questions = new { yes = new { type = "noul", instructions = "Does the text contain A?" } },
        });

        var error = await Assert.ThrowsAsync<DecisionProtocolException>(() => client.CreateJevSystemOneAsync(request, false, false));
        Assert.Equal("max_tokens_exceeded", error.Code);
        Assert.Empty(handler.Requests);
        var result = await client.CreateJevSystemOneAsync(request, false, true);
        Assert.Equal(0.8, result.GetProperty("answers").GetProperty("yes").GetProperty("noul").GetDouble(), 9);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateJevSystemOne_ExpandedImagesRespectByteLimitBeforeEvaluation()
    {
        var bytes = new byte[512 * 1024];
        bytes[0] = 0xff;
        bytes[1] = 0xd8;
        bytes[2] = 0xff;
        bytes[^2] = 0xff;
        bytes[^1] = 0xd9;
        var request = JsonSerializer.SerializeToElement(new
        {
            model = "deepseek-flash",
            state = Enumerable.Repeat("image-ref", 65).ToArray(),
            assets = new Dictionary<string, object> { ["image-ref"] = new { format = "jpeg", data_base64 = Convert.ToBase64String(bytes) } },
            questions = new { item = new { type = "noul", instructions = "Evaluate." } },
        });
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call."));
        var error = await Assert.ThrowsAsync<DecisionProtocolException>(() =>
            Client(handler).CreateJevSystemOneAsync(request, true, true));
        Assert.Equal(413, error.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateOpenAiDecision_InfersZeroOnlyWhenThinkingIsDisabledAndNoReasoningAppears()
    {
        var request = ProtocolJson("""
            {"model":"deepseek-flash","input":"evidence","questions":[{"type":"predicate","instructions":"Evaluate."}]}
            """);
        using var handler = new StubHandler((_, number, _) =>
        {
            var body = JsonSerializer.SerializeToElement(new
            {
                choices = new[]
                {
                    new { finish_reason = "stop", message = new { content = "A", reasoning_content = number == 1 ? null : "unexpected" },
                          logprobs = ProtocolJson(Logs("A", ("A", 0.9), ("B", 0.1))) },
                },
                usage = new { prompt_tokens = 10, completion_tokens = 2, prompt_cache_hit_tokens = 3 },
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body.GetRawText(), System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var client = Client(handler);
        var success = await client.CreateOpenAiDecisionAsync(request);
        Assert.Equal(0, success.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
        var error = await Assert.ThrowsAsync<DecisionProtocolException>(() => client.CreateOpenAiDecisionAsync(request));
        Assert.Equal("upstream_usage_missing", error.Code);
    }

    private static string JevWithBox() => $$$"""
        {
          "model":"deepseek-flash",
          "state":{"photo":"picture"},
          "assets":{"picture":{"format":"png","data_base64":"{{{Png}}}"}},
          "questions":{
            "visible":{"type":"noul","instructions":"Can you see the mug?"},
            "kind":{"type":"choice","criteria":{"mug":"Cup with handle","bottle":"Closed container"}},
            "size":{"type":"score","criteria":["small","large"]},
            "where":{"type":"box","instructions":"Locate the mug in photo."}
          }
        }
        """;

    private static JsonElement ProtocolJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
