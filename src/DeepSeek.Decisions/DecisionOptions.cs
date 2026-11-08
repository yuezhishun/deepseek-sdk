using DeepSeek.Chat;

namespace DeepSeek.Decisions;

public sealed class DecisionOptions
{
    /// <summary>auto、logprobs、structured 或 discrete。</summary>
    public string Method { get; set; } = "auto";
    public int TopLogprobs { get; set; } = 20;
    public int MaxConcurrency { get; set; } = 8;
    public bool NormalizeProbabilities { get; set; } = true;
    public int MaxMalformedRetries { get; set; } = 1;
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Disabled;
    public ChatReasoningEffort? ReasoningEffort { get; set; }
    public long? MaxTokens { get; set; }
    public double? Temperature { get; set; }

    internal DecisionOptions Snapshot()
    {
        if (Method is not ("auto" or "logprobs" or "structured" or "discrete"))
            throw new ArgumentException("Method 必须是 auto、logprobs、structured 或 discrete。", nameof(Method));
        if (TopLogprobs is < 2 or > 20)
            throw new ArgumentOutOfRangeException(nameof(TopLogprobs), "TopLogprobs 必须在 2 到 20 之间。");
        if (MaxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        if (MaxMalformedRetries is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(MaxMalformedRetries), "格式纠正最多一次。");
        if (MaxTokens is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTokens));
        if (Temperature is { } temperature && (!DecisionMath.IsFinite(temperature) || temperature < 0 || temperature > 2))
            throw new ArgumentOutOfRangeException(nameof(Temperature));
        if (!Enum.IsDefined(typeof(ThinkingMode), Thinking))
            throw new ArgumentOutOfRangeException(nameof(Thinking));
        return (DecisionOptions)MemberwiseClone();
    }
}
