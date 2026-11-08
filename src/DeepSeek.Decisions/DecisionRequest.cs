using System.Text.Json.Serialization;

namespace DeepSeek.Decisions;

/// <summary>Decisions 请求；Input 接受字符串或 DecisionInputMessage 集合。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionRequest
{
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("input")]
    public object? Input { get; set; }

    [JsonPropertyName("questions")]
    public IList<DecisionQuestion> Questions { get; set; } = [];
}

/// <summary>题型为 predicate、choice 或 score；入口检查题型与字段是否匹配。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionQuestion
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "predicate";

    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>字符串或 DecisionContentPart 集合。</summary>
    [JsonPropertyName("instructions")]
    public object? Instructions { get; set; }

    [JsonPropertyName("choices"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<DecisionChoice>? Choices { get; set; }

    [JsonPropertyName("levels"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IList<DecisionLevel>? Levels { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionChoice
{
    /// <summary>字符串或布尔值；两种类型不会合并。</summary>
    [JsonPropertyName("value")]
    public object? Value { get; set; }

    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionLevel
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionInputMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    /// <summary>字符串或 DecisionContentPart 集合。</summary>
    [JsonPropertyName("content")]
    public object? Content { get; set; }
}

/// <summary>文本、图片或 JSON 证据。图片在传输时映射到 Core 的 ChatImageContentPart。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DecisionContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "input_text";

    [JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }

    [JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }
}
