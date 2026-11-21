using System.ClientModel.Primitives;
using System.Text.Json;

namespace DeepSeek.Decisions;

public sealed partial class DecisionsClient
{
    /// <summary>Evaluates the OpenAI Decisions request shape with the bound DeepSeek model.</summary>
    public async Task<JsonElement> CreateOpenAiDecisionAsync(JsonElement request, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var prepared = OpenAiDecisionInput.Prepare(request, Model);
        var response = await RunAsync(prepared, null, new RequestOptions { CancellationToken = ct }, allowRefusal: true).ConfigureAwait(false);
        var result = MakeDecision(prepared, response);
        var usage = result.Usage;

        return JsonSerializer.SerializeToElement(new
        {
            model = result.Model,
            answers = result.Answers,
            usage = new
            {
                input_tokens = Known(usage.InputTokens),
                input_tokens_details = new { cached_tokens = Known(usage.InputTokensDetails["cached_tokens"]), cache_write_tokens = 0 },
                output_tokens = Known(usage.OutputTokens),
                output_tokens_details = new { reasoning_tokens = Known(usage.OutputTokensDetails["reasoning_tokens"]) },
                total_tokens = Known(usage.TotalTokens),
            },
        });
    }

    /// <summary>Evaluates a System One request with inline image assets and explicit experimental feature flags.</summary>
    public async Task<JsonElement> CreateJevSystemOneAsync(JsonElement request, bool enableImages, bool enableLongContext, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var prepared = JevDecisionInput.Prepare(request, Model, enableImages, enableLongContext);
        var result = await RunAsync(prepared, null, new RequestOptions { CancellationToken = ct }).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            model = result.Model,
            answers = result.Answers.ToDictionary(pair => pair.Key, pair => pair.Value.Type == "box"
                ? (object)new { type = "box", box = pair.Value.Box, confidence = pair.Value.Confidence }
                : pair.Value, StringComparer.Ordinal),
            usage = new { input_tokens = Known(result.Usage.InputTokens), output_tokens = Known(result.Usage.OutputTokens) },
        });
    }

    private static long Known(long? count) => count ??
        throw new DecisionProtocolException(502, "upstream_usage_missing", "模型响应缺少协议所需的用量信息。");
}
