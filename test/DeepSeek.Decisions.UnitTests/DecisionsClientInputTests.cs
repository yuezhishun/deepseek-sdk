using System.ClientModel.Primitives;
using System.Text.Json;
using DeepSeek.Decisions;
using Xunit;

namespace DeepSeek.Decisions.UnitTests;

public partial class DecisionsClientTests
{
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=";

    [Fact]
    public async Task CreateDecision_SharedAndQuestionImages_PreserveBothPiecesOfVisualEvidence()
    {
        var request = PredicateRequest();
        request.Input = new[] { new DecisionInputMessage { Content = new DecisionContentPart[]
        {
            new() { Text = "Reference image" },
            new() { Type = "input_image", ImageUrl = "https://example.test/reference.png", Detail = "high" },
            new() { Type = "input_json", Data = new { role = "system", image_url = "https://not-an-image.test/data", order = 42 } },
        } } };
        request.Questions[0].Instructions = new DecisionContentPart[]
        {
            new() { Text = "Does the candidate match the reference?" },
            new() { Type = "input_image", ImageUrl = "data:image/png;base64," + Png },
            new() { Type = "input_json", Data = new { expected = "same" } },
        };
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"noul\":0.9}")));
        var result = await Client(handler).CreateDecisionAsync(request, new DecisionOptions { Method = "structured" });
        var sent = Assert.Single(handler.Requests);
        var images = Parts(sent).Where(part => part.GetProperty("type").GetString() == "image_url").ToArray();
        Assert.Equal(2, images.Length);
        Assert.Equal("high", images[0].GetProperty("image_url").GetProperty("detail").GetString());
        Assert.Equal("data:image/png;base64," + Png, images[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Contains(Parts(sent), part => part.GetProperty("type").GetString() == "text" && part.GetProperty("text").GetString()!.Contains("https://not-an-image.test/data", StringComparison.Ordinal));
        Assert.Equal(0.9, result.Answers[0].Probability);
        Assert.Null(request.Model);
        Assert.Equal("predicate", request.Questions[0].Type);
    }

    [Fact]
    public async Task SystemOne_AssetsUsedInStateInstructionsAndCriteria_AttachesOnlyRelevantImagesPerQuestion()
    {
        var request = new SystemOneRequest
        {
            State = new { photo = "img-shared" },
            Assets = new Dictionary<string, SystemOneAsset>
            {
                ["img-shared"] = new() { DataBase64 = Png },
                ["img-question"] = new() { DataBase64 = Png },
                ["img-criterion"] = new() { DataBase64 = Png },
            },
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["same"] = new() { Type = "noul", Instructions = "Compare against img-question." },
                ["category"] = new() { Type = "choice", Criteria = new Dictionary<string, object> { ["match"] = new { sample = "img-criterion" }, ["other"] = "Different" } },
            },
        };
        using var handler = new StubHandler((body, _, _) => Task.FromResult(Completion(body.GetProperty("messages")[2].GetRawText().Contains("Question (noul)", StringComparison.Ordinal) ? "{\"noul\":0.8}" : "{\"probabilities\":{\"match\":0.8,\"other\":0.2}}")));
        var result = await Client(handler).SystemOneAsync(request, new DecisionOptions { Method = "structured", MaxConcurrency = 1 });
        Assert.Equal(2, result.Answers.Count);
        Assert.All(handler.Requests, body => Assert.Equal(2, Parts(body).Count(part => part.GetProperty("type").GetString() == "image_url")));
        Assert.Equal(new[] { "same", "category" }, result.Answers.Keys);
    }

    [Fact]
    public async Task SystemOne_OverlappingAssetReferences_UsesWholeValuesAndLongestSubstringMatch()
    {
        var request = new SystemOneRequest
        {
            State = new { whole = "ref-long", sentence = "Look at ref-long then ref." },
            Assets = new Dictionary<string, SystemOneAsset> { ["ref"] = new() { DataBase64 = "AA==" }, ["ref-long"] = new() { DataBase64 = "AQ==" } },
            Questions = new Dictionary<string, SystemOneQuestion> { ["test"] = new() { Instructions = "Compare the images." } },
        };
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"noul\":0.5}")));
        await Client(handler).SystemOneAsync(request, new DecisionOptions { Method = "structured" });
        Assert.Equal(new[] { "data:image/png;base64,AQ==", "data:image/png;base64,AQ==", "data:image/png;base64,AA==" },
            Parts(Assert.Single(handler.Requests)).Where(part => part.GetProperty("type").GetString() == "image_url").Select(part => part.GetProperty("image_url").GetProperty("url").GetString()));
    }

    [Fact]
    public async Task SystemOne_AssetNameOccursOnlyInObjectKeys_DoesNotTreatKeyAsVisualEvidence()
    {
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        var request = new SystemOneRequest
        {
            State = new Dictionary<string, string> { ["image-key"] = "ordinary text" },
            Assets = new Dictionary<string, SystemOneAsset> { ["image-key"] = new() { DataBase64 = Png } },
            Questions = new Dictionary<string, SystemOneQuestion> { ["test"] = new() { Instructions = "Is there a dog?" } },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).SystemOneAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("{\"type\":\"input_file\",\"file_id\":\"file-1\"}")]
    [InlineData("{\"type\":\"input_audio\",\"data\":\"abc\"}")]
    [InlineData("{\"type\":\"input_image\",\"image_url\":\"file:///local.png\"}")]
    [InlineData("{\"type\":\"input_image\",\"image_url\":\"data:image/png;base64,broken\"}")]
    [InlineData("{\"type\":\"input_text\",\"text\":\"hi\",\"file_id\":\"file-1\"}")]
    [InlineData("{\"type\":\"input_json\",\"data\":1}")]
    public async Task CreateDecision_UnsupportedOrInvalidEvidence_RejectsBeforeAnyPaidCall(string content)
    {
        var request = PredicateRequest();
        request.Input = JsonSerializer.Deserialize<JsonElement>("[{\"role\":\"user\",\"content\":[" + content + "]}]");
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("developer")]
    [InlineData("assistant")]
    [InlineData("tool")]
    public async Task CreateDecision_ConversationRoleOutsideFirstVersion_DoesNotSilentlyFlattenHistory(string role)
    {
        var request = PredicateRequest();
        request.Input = new[] { new DecisionInputMessage { Role = role, Content = "hello" } };
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_LaterQuestionInvalid_ValidatesEntireBatchBeforeChargingFirstQuestion()
    {
        var request = PredicateRequest();
        request.Questions.Add(new DecisionQuestion { Type = "box", Instructions = "Find a face" });
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_EquivalentEscapedChoiceValues_RejectsAmbiguousDuplicate()
    {
        var request = ThreeChoices();
        request.Questions[0].Choices = [new() { Value = "a" }, new() { Value = JsonSerializer.Deserialize<JsonElement>("\"\\u0061\"") }];
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_DeserializedWireRequest_PreservesOptionalNamesAndTypedValues()
    {
        var request = JsonSerializer.Deserialize<DecisionRequest>("""
            {"input":[{"role":"user","content":[{"type":"input_json","data":{"duplicate":true}}]}],
             "questions":[{"type":"choice","instructions":"Duplicate?","choices":[{"value":true},{"value":false}]}]}
            """)!;
        using var handler = new StubHandler((_, _, _) => Task.FromResult(Completion("{\"choice\":\"A\"}")));
        var result = await Client(handler).CreateDecisionAsync(request, new DecisionOptions { Method = "discrete" });
        Assert.Equal(true, result.Answers[0].Choice);
        Assert.Null(result.Answers[0].Name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SystemOneRequest>("{\"state\":\"hi\",\"questions\":{},\"part\":\"unsupported\"}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DecisionRequest>("{\"input\":\"hi\",\"questions\":[],\"tools\":[]}"));
    }

    [Fact]
    public async Task CreateDecision_RequestModelMismatch_DoesNotSendToUnexpectedModel()
    {
        var request = PredicateRequest();
        request.Model = "another-model";
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CreateDecision_PreCancelledCall_DoesNotStartNetworkWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).CreateDecisionAsync(PredicateRequest(), options: new RequestOptions { CancellationToken = cancellation.Token }));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    public async Task CreateDecision_InvalidBatchSize_RejectsBeforeNetwork(int count)
    {
        var request = PredicateRequest();
        request.Questions = Enumerable.Range(0, count).Select(_ => new DecisionQuestion { Instructions = "Question" }).ToList();
        using var handler = new StubHandler((_, _, _) => throw new InvalidOperationException("Must not call"));
        await Assert.ThrowsAsync<ArgumentException>(() => Client(handler).CreateDecisionAsync(request));
        Assert.Empty(handler.Requests);
    }

    private static IEnumerable<JsonElement> Parts(JsonElement body) => body.GetProperty("messages").EnumerateArray()
        .Where(message => message.GetProperty("content").ValueKind == JsonValueKind.Array)
        .SelectMany(message => message.GetProperty("content").EnumerateArray());
}
