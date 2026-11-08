using System.Text.Json.Serialization;

namespace DeepSeek.Decisions;

public sealed class Decision
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("answers")]
    public IList<DecisionAnswer> Answers { get; set; } = [];

    [JsonPropertyName("usage")]
    public DecisionUsage Usage { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<DecisionDiagnostic> Diagnostics { get; internal set; } = [];
}

public sealed class DecisionAnswer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("probability"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Probability { get; set; }

    [JsonPropertyName("choice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Choice { get; set; }

    [JsonPropertyName("score"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; set; }

    [JsonPropertyName("probabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<DecisionProbability>? Probabilities { get; set; }

    [JsonPropertyName("confidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; set; }
}

public sealed class DecisionProbability
{
    [JsonPropertyName("value")]
    public object Value { get; set; } = string.Empty;

    [JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Label { get; set; }

    [JsonPropertyName("probability")]
    public double Probability { get; set; }
}
