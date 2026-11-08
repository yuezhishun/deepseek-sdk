using System.Collections.Concurrent;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeek.Decisions;
using Xunit;

namespace DeepSeek.Decisions.UnitTests;

public partial class DecisionsClientTests
{
    [Fact]
    public async Task CreateDecision_EquivalentJevQuestion_PreservesDecisionAndConfidence()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", Logs("A", ("A", 0.8), ("B", 0.15), ("C", 0.05)))));
        var client = Client(handler);
        var decision = await client.CreateDecisionAsync(ThreeChoices());
        var jev = await client.SystemOneAsync(new SystemOneRequest
        {
            State = "I was charged twice.",
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["intent"] = new() { Type = "choice", Instructions = "Route this ticket.", Criteria = new Dictionary<string, string> { ["billing"] = "Payments", ["technical"] = "Errors", ["sales"] = "Buying" } },
            },
        });

        Assert.Equal("billing", decision.Answers[0].Choice);
        Assert.Equal(jev.Answers["intent"].Choice, decision.Answers[0].Choice);
        Assert.Equal(0.7, decision.Answers[0].Confidence!.Value, 12);
        Assert.Equal(jev.Answers["intent"].Confidence, decision.Answers[0].Confidence);
        Assert.Equal("token_logprobs", decision.Diagnostics[0].ProbabilitySource);
        Assert.Equal(10, decision.Usage.InputTokens);
        Assert.Equal(2, decision.Usage.OutputTokens);
        Assert.Equal(12, decision.Usage.TotalTokens);
        Assert.Equal(3, decision.Usage.InputTokensDetails["cached_tokens"]);
        Assert.Null(decision.Usage.InputTokensDetails["cache_write_tokens"]);
        var sent = handler.Requests.First();
        Assert.Equal("deepseek-flash", sent.GetProperty("model").GetString());
        Assert.Equal("disabled", sent.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(20, sent.GetProperty("top_logprobs").GetInt32());
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(decision));
        Assert.Equal(new[] { "model", "answers", "usage" }, wire.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.False(wire.RootElement.TryGetProperty("Diagnostics", out _));
    }

    [Fact]
    public async Task CreateDecision_MissingLabel_ExposesAbsenceInsteadOfInventingProbability()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", Logs("A", ("A", 0.6), ("B", 0.2)))));
        var result = await Client(handler).CreateDecisionAsync(ThreeChoices());
        Assert.Equal(new[] { "C" }, result.Diagnostics[0].MissingLabels);
        Assert.Equal(new[] { 0.75, 0.25, 0.0 }, result.Answers[0].Probabilities!.Select(value => Math.Round(value.Probability, 12)));
        Assert.Equal(1, result.Diagnostics[0].CallCount);
    }

    [Theory]
    [InlineData("{\"probabilities\":{\"0\":0,\"1\":0,\"2\":0}}", 1.0 / 3, 0)]
    [InlineData("{\"probabilities\":{\"0\":1e308,\"1\":1e308,\"2\":0}}", 0.5, 0.25)]
    public async Task CreateDecision_UnnormalizedWeights_ReportsUncertaintyWithoutOverflow(string output, double peak, double confidence)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion(output)));
        var result = await Client(handler).CreateDecisionAsync(ThreeChoices(), new DecisionOptions { Method = "structured" });
        Assert.Equal(peak, result.Answers[0].Probabilities![0].Probability, 12);
        Assert.Equal(confidence, result.Answers[0].Confidence!.Value, 12);
        Assert.NotNull(result.Diagnostics[0].OriginalProbabilities);
        Assert.Equal("model_reported", result.Diagnostics[0].ProbabilitySource);
        Assert.Equal("json_object", handler.Requests.First().GetProperty("response_format").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{\"probabilities\":{\"0\":0,\"1\":0.5,\"2\":0.5}}", 1.5, 0.25)]
    [InlineData("{\"probabilities\":{\"0\":0.5,\"1\":0,\"2\":0.5}}", 1, 0)]
    public async Task CreateDecision_EquallySplitScores_DistantLevelsHaveLowerConfidence(string output, double score, double confidence)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion(output)));
        var result = await Client(handler).CreateDecisionAsync(ScoreRequest(), new DecisionOptions { Method = "structured" });
        Assert.Equal(score, result.Answers[0].Score);
        Assert.Equal(confidence, result.Answers[0].Confidence!.Value, 12);
        Assert.Equal(new[] { "None", "Minor", "Major" }, result.Answers[0].Probabilities!.Select(value => value.Label));
        Assert.Equal(new object[] { 0, 1, 2 }, result.Answers[0].Probabilities!.Select(value => value.Value));
    }

    [Fact]
    public void SystemOne_SingleOption_HasNoCompetingDecision()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", Logs("A", ("A", 0.6)))));
        var response = Client(handler).SystemOne(new SystemOneRequest
        {
            State = new { message = "Hi" },
            Questions = new Dictionary<string, SystemOneQuestion> { ["route"] = new() { Type = "choice", Criteria = new Dictionary<string, string> { ["only"] = "Only available route" } } },
        });
        Assert.Equal("only", response.Answers["route"].Choice);
        Assert.Equal(1, response.Answers["route"].Probabilities!["only"]);
        Assert.Equal(1, response.Answers["route"].Confidence);
    }

    [Fact]
    public async Task CreateDecision_TypedBooleanAndTextOptions_RemainDistinct()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"choice\":\"B\"}")));
        var request = ThreeChoices();
        request.Questions[0].Choices = [new() { Value = true }, new() { Value = "true" }];
        var response = await Client(handler).CreateDecisionAsync(request, new DecisionOptions { Method = "discrete" });
        Assert.IsType<string>(response.Answers[0].Choice);
        Assert.Equal("true", response.Answers[0].Choice);
        Assert.IsType<bool>(response.Answers[0].Probabilities![0].Value);
        Assert.Equal("one_hot", response.Diagnostics[0].ProbabilitySource);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(response));
        Assert.Equal(JsonValueKind.True, wire.RootElement.GetProperty("answers")[0].GetProperty("probabilities")[0].GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task SystemOne_Score_KeepsStructuredLegendWhileNormalizingOnlyForCalculation()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"probabilities\":{\"0\":2,\"1\":6}}")));
        var result = await Client(handler).SystemOneAsync(new SystemOneRequest
        {
            State = "bug",
            Questions = new Dictionary<string, SystemOneQuestion> { ["severity"] = new() { Type = "score", Criteria = new object[] { new { label = "minor" }, new { label = "major" } } } },
        }, new DecisionOptions { Method = "structured", NormalizeProbabilities = false });
        Assert.Equal(0.75, result.Answers["severity"].Score);
        Assert.Equal(6, result.Answers["severity"].Probabilities!["1"]);
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(result));
        Assert.Equal("major", wire.RootElement.GetProperty("answers").GetProperty("severity").GetProperty("legend").GetProperty("1").GetProperty("label").GetString());
    }

    private static DecisionRequest ThreeChoices() => new()
    {
        Input = "I was charged twice.",
        Questions = [new() { Type = "choice", Name = "intent", Instructions = "Route this ticket.", Choices = [new() { Value = "billing", Description = "Payments" }, new() { Value = "technical", Description = "Errors" }, new() { Value = "sales", Description = "Buying" }] }],
    };

    private static DecisionRequest ScoreRequest() => new()
    {
        Input = "damaged parcel",
        Questions = [new() { Type = "score", Name = "severity", Instructions = "Rate the damage.", Levels = [new() { Label = "None" }, new() { Label = "Minor" }, new() { Label = "Major" }] }],
    };

    private static DecisionRequest PredicateRequest() => new() { Input = "Hello", Questions = [new() { Name = "greeting", Instructions = "Is this a greeting?" }] };

    private static DecisionsClient Client(StubHandler handler) => new DeepSeekClient("test-key", new DeepSeekClientOptions
    {
        Endpoint = new Uri("https://unit.test/v1"),
        Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        RetryPolicy = new ClientRetryPolicy(0),
    }).GetDecisionsClient("deepseek-flash");

    private static string Logs(string sampled, params (string Label, double Probability)[] values) => JsonSerializer.Serialize(new
    {
        content = new[] { new { token = sampled, logprob = Math.Log(values.First(value => value.Label == sampled).Probability), top_logprobs = values.Select(value => new { token = value.Label, logprob = Math.Log(value.Probability) }) } },
    });

    private static HttpResponseMessage Completion(string text, string? logprobs = null, bool usage = true, string finish = "stop") => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = finish, logprobs = logprobs is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(logprobs) } },
            usage = usage ? new { prompt_tokens = 10, completion_tokens = 2, prompt_cache_hit_tokens = 3, completion_tokens_details = new { reasoning_tokens = 0 } } : null,
        }), Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(int status, string message) => new((HttpStatusCode)status)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { error = new { message } }), Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<JsonElement, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _count;
        internal ConcurrentQueue<JsonElement> Requests { get; } = new();
        internal ConcurrentQueue<IReadOnlyDictionary<string, string>> Headers { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement.Clone();
            Requests.Enqueue(body);
            Headers.Enqueue(request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase));
            return await respond(body, Interlocked.Increment(ref _count), cancellationToken);
        }
    }
}
