using System.ClientModel.Primitives;
using DeepSeek.Decisions;
using TestSupport;
using Xunit;

namespace DeepSeek.IntegrationTests;

[Trait("Category", "Live")]
public sealed class DecisionsIntegrationTests(DeepSeekIntegrationFixture fixture) : IClassFixture<DeepSeekIntegrationFixture>
{
    // 固定 32×32 纯色 PNG，不依赖外部图片地址或图片生成服务。
    private const string Red = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAKElEQVR4nO3NsQ0AAAzCMP5/un0CNkuZ41wybXsHAAAAAAAAAAAAxR4yw/wuPL6QkAAAAABJRU5ErkJggg==";
    private const string Blue = "iVBORw0KGgoAAAANSUhEUgAAACAAAAAgCAIAAAD8GO2jAAAAJklEQVR4nO3NsQkAAAjAsP7/tF7hIASyp5pjAoFAIBAIBAKB4EmwOkv8Lm7+zY4AAAAASUVORK5CYII=";

    [LiveFact]
    public async Task Decisions_Text_Live()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await fixture.Client.GetDecisionsClient(DeepSeekTestModels.FlashModel).CreateDecisionAsync(new DecisionRequest
        {
            Input = "My credit card was charged twice for the same subscription. Please refund the duplicate charge.",
            Questions =
            [
                new() { Name = "duplicate", Instructions = "Does the customer report a duplicate charge?" },
                new() { Type = "choice", Name = "route", Instructions = "Which department should handle this ticket?", Choices = [new() { Value = "billing", Description = "Payments and refunds" }, new() { Value = "technical", Description = "Software defects" }] },
            ],
        }, new DecisionOptions { MaxTokens = 256 }, new RequestOptions { CancellationToken = cancellation.Token });
        Assert.True(result.Answers[0].Probability > 0.5, "明确的重复扣费应识别为真。");
        Assert.Equal("billing", result.Answers[1].Choice);
        Assert.InRange(result.Answers[1].Confidence!.Value, 0, 1);
        Assert.True(result.Usage.InputTokens > 0);
        Assert.All(result.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.ProbabilitySource)));
    }

    [LiveFact]
    public async Task SystemOne_Text_Live()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await fixture.Client.GetDecisionsClient(DeepSeekTestModels.FlashModel).SystemOneAsync(new SystemOneRequest
        {
            State = new { report = "The service is completely unavailable for every customer. There is no workaround." },
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["severity"] = new() { Type = "score", Instructions = "Rate incident severity.", Criteria = new[] { "Cosmetic only", "Partial problem with a workaround", "Complete outage with no workaround" } },
            },
        }, new DecisionOptions { Method = "structured", MaxTokens = 256 }, new RequestOptions { CancellationToken = cancellation.Token });
        Assert.True(result.Answers["severity"].Score > 1.5, "全体用户无法使用且没有绕行方案，应接近最高等级。");
        Assert.Equal(3, result.Answers["severity"].Legend!.Count);
        Assert.Equal("model_reported", result.Diagnostics[0].ProbabilitySource);
    }

    [LiveFact]
    public async Task Decisions_SharedAndQuestionImages_Live()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await fixture.Client.GetDecisionsClient(DeepSeekTestModels.FlashModel).CreateDecisionAsync(new DecisionRequest
        {
            Input = new[] { new DecisionInputMessage { Content = new DecisionContentPart[]
            {
                new() { Text = "This is reference image A." },
                new() { Type = "input_image", ImageUrl = "data:image/png;base64," + Red },
            } } },
            Questions = [new() { Name = "colors", Instructions = new DecisionContentPart[]
            {
                new() { Text = "This is candidate image B. Is image A solid red and image B solid blue? Answer yes only if BOTH images are visible and have those colors." },
                new() { Type = "input_image", ImageUrl = "data:image/png;base64," + Blue },
            } }],
        }, new DecisionOptions { Method = "structured", MaxTokens = 256 }, new RequestOptions { CancellationToken = cancellation.Token });
        Assert.True(result.Answers[0].Probability > 0.5, "模型应实际看见共享输入与题目中的两张图片。");
    }

    [LiveFact]
    public async Task SystemOne_ImageAssets_Live()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await fixture.Client.GetDecisionsClient(DeepSeekTestModels.FlashModel).SystemOneAsync(new SystemOneRequest
        {
            State = new { reference = "asset-reference" },
            Assets = new Dictionary<string, SystemOneAsset> { ["asset-reference"] = new() { DataBase64 = Red }, ["asset-candidate"] = new() { DataBase64 = Blue } },
            Questions = new Dictionary<string, SystemOneQuestion>
            {
                ["colors"] = new() { Instructions = "Candidate: asset-candidate. Is the reference image solid red and the candidate image solid blue? Answer yes only if BOTH images are visible and have those colors." },
            },
        }, new DecisionOptions { Method = "structured", MaxTokens = 256 }, new RequestOptions { CancellationToken = cancellation.Token });
        Assert.True(result.Answers["colors"].Noul > 0.5, "Jev 资产引用必须转换为实际图片证据。");
    }
}
