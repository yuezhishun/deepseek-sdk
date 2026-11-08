namespace DeepSeek.Decisions;

/// <summary>每题的执行证据；不含请求正文、图片数据或认证信息。</summary>
public sealed class DecisionDiagnostic
{
    public string QuestionId { get; internal set; } = string.Empty;
    public string Method { get; internal set; } = string.Empty;
    public string ProbabilitySource { get; internal set; } = string.Empty;
    public IReadOnlyList<string> MissingLabels { get; internal set; } = [];
    public string? FallbackReason { get; internal set; }
    public int CallCount { get; internal set; }
    public int MalformedRetries { get; internal set; }
    public IReadOnlyDictionary<string, double>? OriginalProbabilities { get; internal set; }
    public DecisionUsage Usage { get; internal set; } = new();
}
