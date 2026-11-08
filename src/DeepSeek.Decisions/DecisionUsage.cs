using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Decisions;

/// <summary>聚合所有实际尝试的用量；任一尝试未报告的计数为 null。</summary>
public sealed class DecisionUsage
{
    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; set; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long? TotalTokens { get; set; }

    [JsonPropertyName("input_tokens_details")]
    public IDictionary<string, long?> InputTokensDetails { get; set; } = new Dictionary<string, long?>
    {
        ["cached_tokens"] = null,
        ["cache_write_tokens"] = null,
    };

    [JsonPropertyName("output_tokens_details")]
    public IDictionary<string, long?> OutputTokensDetails { get; set; } = new Dictionary<string, long?>
    {
        ["reasoning_tokens"] = null,
    };

    internal static DecisionUsage Read(JsonElement root)
    {
        var result = new DecisionUsage();
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            result.InputTokens = Count(usage, "prompt_tokens");
            result.OutputTokens = Count(usage, "completion_tokens");
            try { result.TotalTokens = checked(result.InputTokens + result.OutputTokens); }
            catch (OverflowException) { result.TotalTokens = null; }
            result.InputTokensDetails["cached_tokens"] = Count(usage, "prompt_cache_hit_tokens") ?? NestedCount(usage, "prompt_tokens_details", "cached_tokens");
            result.OutputTokensDetails["reasoning_tokens"] = NestedCount(usage, "completion_tokens_details", "reasoning_tokens");
        }
        return result;
    }

    internal static DecisionUsage Sum(IEnumerable<DecisionUsage> usages)
    {
        var items = usages.ToArray();
        long? SumCount(Func<DecisionUsage, long?> select)
        {
            if (items.Length == 0 || items.Any(item => select(item) is null)) return null;
            try { return checked(items.Sum(item => select(item)!.Value)); }
            catch (OverflowException) { return null; }
        }
        return new DecisionUsage
        {
            InputTokens = SumCount(item => item.InputTokens),
            OutputTokens = SumCount(item => item.OutputTokens),
            TotalTokens = SumCount(item => item.TotalTokens),
            InputTokensDetails = new Dictionary<string, long?>
            {
                ["cached_tokens"] = SumCount(item => item.InputTokensDetails["cached_tokens"]),
                ["cache_write_tokens"] = null,
            },
            OutputTokensDetails = new Dictionary<string, long?>
            {
                ["reasoning_tokens"] = SumCount(item => item.OutputTokensDetails["reasoning_tokens"]),
            },
        };
    }

    private static long? NestedCount(JsonElement value, string parent, string name)
        => value.TryGetProperty(parent, out var child) && child.ValueKind == JsonValueKind.Object ? Count(child, name) : null;

    private static long? Count(JsonElement value, string name)
        => value.TryGetProperty(name, out var count) && count.ValueKind == JsonValueKind.Number && count.TryGetInt64(out var number) && number >= 0 ? number : null;
}
