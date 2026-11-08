using System.Text.Json.Serialization;

namespace DeepSeek.Decisions;

public sealed class SystemOneResponse
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("answers")]
    public IDictionary<string, SystemOneAnswer> Answers { get; set; } = new Dictionary<string, SystemOneAnswer>();

    [JsonPropertyName("usage")]
    public DecisionUsage Usage { get; set; } = new();

    [JsonIgnore]
    public IReadOnlyList<DecisionDiagnostic> Diagnostics { get; internal set; } = [];
}

public sealed class SystemOneAnswer
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("noul"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Noul { get; set; }

    [JsonPropertyName("choice"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Choice { get; set; }

    [JsonPropertyName("score"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Score { get; set; }

    [JsonPropertyName("probabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, double>? Probabilities { get; set; }

    [JsonPropertyName("confidence"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Confidence { get; set; }

    [JsonPropertyName("legend"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, object?>? Legend { get; set; }
}
