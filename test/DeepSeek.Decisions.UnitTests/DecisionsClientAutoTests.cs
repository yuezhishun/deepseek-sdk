using System.ClientModel.Primitives;
using System.Text.Json;
using DeepSeek.Decisions;
using Xunit;

namespace DeepSeek.Decisions.UnitTests;

public partial class DecisionsClientTests
{
    [Fact]
    public async Task CreateDecision_TwoWeakAbsences_RemembersCapabilityOnlyAfterRepeatedEvidence()
    {
        using var handler = new StubHandler((body, _, _) => Task.FromResult(IsLogprobs(body) ? Completion("A") : Completion("{\"noul\":0.8}")));
        var client = Client(handler);
        var first = await client.CreateDecisionAsync(PredicateRequest());
        var second = await client.CreateDecisionAsync(PredicateRequest());
        var third = await client.CreateDecisionAsync(PredicateRequest());
        Assert.Equal(2, first.Diagnostics[0].CallCount);
        Assert.Equal(2, second.Diagnostics[0].CallCount);
        Assert.Equal(1, third.Diagnostics[0].CallCount);
        Assert.Equal(2, handler.Requests.Count(IsLogprobs));
        Assert.Equal(20, first.Usage.InputTokens);
        Assert.Equal("model_reported", third.Diagnostics[0].ProbabilitySource);
    }

    [Fact]
    public async Task CreateDecision_ReadableDistribution_ResetsWeakAbsenceEvidence()
    {
        var probes = 0;
        using var handler = new StubHandler((body, _, _) => Task.FromResult(!IsLogprobs(body) ? Completion("{\"noul\":0.7}")
            : ++probes % 2 == 0 ? Completion("A", Logs("A", ("A", 0.8), ("B", 0.2))) : Completion("A")));
        var client = Client(handler);
        for (var i = 0; i < 4; i++) await client.CreateDecisionAsync(PredicateRequest());
        Assert.Equal(4, probes);
        Assert.Equal(6, handler.Requests.Count);
    }

    [Fact]
    public async Task CreateDecision_ExplicitFieldRefusal_RemembersImmediatelyButAllowsExplicitRecovery()
    {
        using var handler = new StubHandler((body, number, _) => Task.FromResult(number == 1 ? Error(400, "Unknown name logprobs: unsupported field")
            : IsLogprobs(body) ? Completion("A", Logs("A", ("A", 0.8), ("B", 0.2))) : Completion("{\"noul\":0.7}")));
        var client = Client(handler);
        var first = await client.CreateDecisionAsync(PredicateRequest());
        var cached = await client.CreateDecisionAsync(PredicateRequest());
        Assert.Equal(2, first.Diagnostics[0].CallCount);
        Assert.Equal(1, cached.Diagnostics[0].CallCount);
        Assert.False(IsLogprobs(handler.Requests.Last()));
        await client.CreateDecisionAsync(PredicateRequest(), new DecisionOptions { Method = "logprobs" });
        await client.CreateDecisionAsync(PredicateRequest());
        Assert.True(IsLogprobs(handler.Requests.Last()));
        Assert.Null(first.Usage.InputTokens);
    }

    [Fact]
    public async Task CreateDecision_ServerFailure_FallsBackWithoutCondemningModelCapability()
    {
        using var handler = new StubHandler((body, number, _) => Task.FromResult(number == 1 ? Error(503, "Busy")
            : IsLogprobs(body) ? Completion("A", Logs("A", ("A", 0.9), ("B", 0.1))) : Completion("{\"noul\":0.7}")));
        var client = Client(handler);
        var first = await client.CreateDecisionAsync(PredicateRequest());
        var next = await client.CreateDecisionAsync(PredicateRequest());
        Assert.Equal("structured", first.Diagnostics[0].Method);
        Assert.Equal("logprobs", next.Diagnostics[0].Method);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Null(first.Usage.TotalTokens);
    }

    [Theory]
    [InlineData(401, "Invalid API key")]
    [InlineData(403, "Permission denied for logprobs")]
    [InlineData(429, "Rate limit exceeded")]
    [InlineData(422, "top_logprobs must be between 0 and 5")]
    public async Task CreateDecision_AuthRateOrParameterErrors_DoNotMaskFailureAsCapabilityFallback(int status, string message)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Error(status, message)));
        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => Client(handler).CreateDecisionAsync(PredicateRequest()));
        Assert.Equal(status, exception.Status);
        Assert.Equal("0", exception.Data["QuestionId"]);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_PinnedLogprobs_NeverSubstitutesSelfReportedProbabilities()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).CreateDecisionAsync(PredicateRequest(), new DecisionOptions { Method = "logprobs" }));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"probabilities\":{\"0\":0.8,\"1\":0.2}}")]
    [InlineData("{\"probabilities\":{\"0\":-0.2,\"1\":0.6,\"2\":0.6}}")]
    [InlineData("{\"probabilities\":{\"0\":1e400,\"1\":0,\"2\":0}}")]
    [InlineData("{\"probabilities\":{\"0\":\"0.8\",\"1\":0.1,\"2\":0.1}}")]
    [InlineData("{\"probabilities\":{\"0\":0.8,\"0\":0.1,\"2\":0.1}}")]
    [InlineData("{\"probabilities\":{\"0\":0.8,\"1\":0.1,\"2\":0.1},\"choice\":\"wrong\"}")]
    public async Task CreateDecision_InvalidDistribution_CorrectsOnceInsteadOfUsingMisleadingNumbers(string malformed)
    {
        using var handler = new StubHandler((_, number, _) => Task.FromResult(Completion(number == 1 ? malformed : "{\"probabilities\":{\"0\":0.8,\"1\":0.1,\"2\":0.1}}")));
        var result = await Client(handler).CreateDecisionAsync(ThreeChoices(), new DecisionOptions { Method = "structured" });
        Assert.Equal("billing", result.Answers[0].Choice);
        Assert.Equal(1, result.Diagnostics[0].MalformedRetries);
        Assert.Equal(2, result.Diagnostics[0].CallCount);
        Assert.Equal(20, result.Usage.InputTokens);
    }

    [Fact]
    public async Task CreateDecision_RepeatedMalformedOutput_FailsWithQuestionAndUsageDiagnostics()
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{}")));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).CreateDecisionAsync(ThreeChoices(), new DecisionOptions { Method = "structured" }));
        var diagnostic = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<DecisionDiagnostic>>(exception.Data["DecisionDiagnostics"]));
        Assert.Equal("0", exception.Data["QuestionId"]);
        Assert.Equal(2, diagnostic.CallCount);
        Assert.Equal(1, diagnostic.MalformedRetries);
        Assert.Equal(24, Assert.IsType<DecisionUsage>(exception.Data["DecisionUsage"]).TotalTokens);
    }

    [Theory]
    [InlineData("length")]
    [InlineData("content_filter")]
    [InlineData("refusal")]
    public async Task CreateDecision_TruncatedOrRefusedAnswer_DoesNotRetryTerminalOutcome(string finish)
    {
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"noul\":0.5}", finish: finish)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).CreateDecisionAsync(PredicateRequest(), new DecisionOptions { Method = "structured" }));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_InvalidNonOptionLogprob_DoesNotHideCorruptProviderNumbers()
    {
        var corrupt = "{\"content\":[{\"token\":\"A\",\"logprob\":-0.2,\"top_logprobs\":[{\"token\":\"A\",\"logprob\":-0.2},{\"token\":\"not-a-label\",\"logprob\":0.1}]}]}";
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("A", corrupt)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).CreateDecisionAsync(PredicateRequest(), new DecisionOptions { MaxMalformedRetries = 0 }));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_TooManySingleTokenLabels_UsesStructuredWithoutPaidProbe()
    {
        var request = ThreeChoices();
        request.Questions[0].Choices = Enumerable.Range(0, 27).Select(index => new DecisionChoice { Value = "route" + index }).ToList();
        var probabilities = Enumerable.Range(0, 27).ToDictionary(index => index.ToString(), _ => 1);
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion(JsonSerializer.Serialize(new { probabilities }))));
        var client = Client(handler);
        var result = await client.CreateDecisionAsync(request);
        Assert.Equal("structured", result.Diagnostics[0].Method);
        Assert.False(IsLogprobs(Assert.Single(handler.Requests)));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CreateDecisionAsync(request, new DecisionOptions { Method = "logprobs" }));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_FrozenRequestOptions_PreservesHeadersPoliciesAndCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var options = new RequestOptions { CancellationToken = cancellation.Token };
        options.SetHeader("X-Tenant", "tenant-1");
        options.AddPolicy(new TestHeaderPolicy(), PipelinePosition.PerCall);
        options.Freeze();
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"noul\":0.9}")));
        await Client(handler).CreateDecisionAsync(PredicateRequest(), new DecisionOptions { Method = "structured" }, options);
        var headers = Assert.Single(handler.Headers);
        Assert.Equal("tenant-1", headers["X-Tenant"]);
        Assert.Equal("kept", headers["X-Decision-Policy"]);
        Assert.Equal(cancellation.Token, options.CancellationToken);
        Assert.False(cancellation.IsCancellationRequested);
    }

    [Fact]
    public async Task CreateDecision_OneQuestionFails_CancelsInFlightAndDoesNotChargeQueuedQuestions()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var siblingCancelled = false;
        using var handler = new StubHandler(async (_, number, token) =>
        {
            if (number == 1)
            {
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                return Error(401, "Invalid API key");
            }
            secondStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { siblingCancelled = true; throw; }
            throw new InvalidOperationException("Unreachable");
        });
        var request = PredicateRequest();
        request.Questions = Enumerable.Range(0, 5).Select(_ => new DecisionQuestion { Instructions = "Is this a greeting?" }).ToList();
        var exception = await Assert.ThrowsAsync<DeepSeekException>(() => Client(handler).CreateDecisionAsync(request, new DecisionOptions { MaxConcurrency = 2 }));
        Assert.Equal(401, exception.Status);
        Assert.True(siblingCancelled);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CreateDecision_CallerCancels_StopsCurrentAndQueuedCalls()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler(async (_, _, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Unreachable");
        });
        var request = PredicateRequest();
        request.Questions.Add(new DecisionQuestion { Instructions = "Second question" });
        var pending = Client(handler).CreateDecisionAsync(request, new DecisionOptions { MaxConcurrency = 1 }, new RequestOptions { CancellationToken = cancellation.Token });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_ConcurrentCompletions_PreserveQuestionOrderAndBoundCost()
    {
        var active = 0;
        var peak = 0;
        using var handler = new StubHandler(async (body, _, token) =>
        {
            var count = Interlocked.Increment(ref active);
            int previous;
            do { previous = peak; } while (count > previous && Interlocked.CompareExchange(ref peak, count, previous) != previous);
            var question = body.GetProperty("messages")[2].GetProperty("content").EnumerateArray().Select(part => part.GetProperty("text").GetString()!).Single(text => text.StartsWith("ticket-", StringComparison.Ordinal));
            var index = int.Parse(question.Substring(7));
            await Task.Delay((6 - index) * 8, token);
            Interlocked.Decrement(ref active);
            return Completion(JsonSerializer.Serialize(new { noul = index / 10.0 }));
        });
        var request = PredicateRequest();
        request.Questions = Enumerable.Range(0, 6).Select(index => new DecisionQuestion { Name = index % 2 == 0 ? null : "duplicate", Instructions = "ticket-" + index }).ToList();
        var result = await Client(handler).CreateDecisionAsync(request, new DecisionOptions { Method = "structured", MaxConcurrency = 2 });
        Assert.Equal(2, peak);
        Assert.Equal(Enumerable.Range(0, 6).Select(index => index / 10.0), result.Answers.Select(answer => answer.Probability!.Value));
        Assert.Equal(new string?[] { null, "duplicate", null, "duplicate", null, "duplicate" }, result.Answers.Select(answer => answer.Name));
        Assert.Equal(60, result.Usage.InputTokens);
    }

    [Fact]
    public async Task CreateDecision_OneResponseOmitsUsage_DoesNotInventZeroTokens()
    {
        using var handler = new StubHandler((_, number, _) => Task.FromResult(Completion("{\"noul\":0.8}", usage: number == 1)));
        var request = PredicateRequest();
        request.Questions.Add(new DecisionQuestion { Instructions = "Second question" });
        var result = await Client(handler).CreateDecisionAsync(request, new DecisionOptions { Method = "structured" });
        Assert.Null(result.Usage.InputTokens);
        Assert.Null(result.Usage.OutputTokens);
        Assert.Null(result.Usage.TotalTokens);
        Assert.Null(result.Usage.InputTokensDetails["cached_tokens"]);
    }

    private static bool IsLogprobs(JsonElement body) => body.TryGetProperty("logprobs", out var value) && value.ValueKind == JsonValueKind.True;

    [Fact]
    public async Task CreateDecision_UnreportedCandidateLogprobs_FallsBackInsteadOfPretendingFormatCorrectionCanRecoverData()
    {
        var unavailable = "{\"content\":[{\"token\":\"A\",\"logprob\":-0.1,\"top_logprobs\":[{\"token\":\"A\",\"logprob\":-0.1},{\"token\":\"B\",\"logprob\":null}]}]}";
        using var handler = new StubHandler((body, _, _) => Task.FromResult(IsLogprobs(body) ? Completion("A", unavailable) : Completion("{\"noul\":0.7}")));
        var result = await Client(handler).CreateDecisionAsync(PredicateRequest());
        Assert.Equal("model_reported", result.Diagnostics[0].ProbabilitySource);
        Assert.Equal(0, result.Diagnostics[0].MalformedRetries);
        Assert.Equal(2, result.Diagnostics[0].CallCount);
    }

    [Fact]
    public async Task CreateDecision_UnknownModelMentionsLogprobs_DoesNotLearnFalseCapabilityRestriction()
    {
        using var handler = new StubHandler((body, _, _) => Task.FromResult(IsLogprobs(body)
            ? Error(400, "Unknown model deepseek-flash (logprobs request)") : Completion("{\"noul\":0.7}")));
        await Assert.ThrowsAsync<DeepSeekException>(() => Client(handler).CreateDecisionAsync(PredicateRequest()));
        Assert.Single(handler.Requests);
    }

    private sealed class TestHeaderPolicy : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("X-Decision-Policy", "kept");
            ProcessNext(message, pipeline, currentIndex);
        }

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            message.Request.Headers.Set("X-Decision-Policy", "kept");
            return ProcessNextAsync(message, pipeline, currentIndex);
        }
    }
}
