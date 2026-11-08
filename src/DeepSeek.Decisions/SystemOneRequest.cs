using System.Text.Json.Serialization;

namespace DeepSeek.Decisions;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SystemOneRequest
{
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }

    [JsonPropertyName("state")]
    public object? State { get; set; }

    [JsonPropertyName("questions")]
    public IDictionary<string, SystemOneQuestion> Questions { get; set; } = new Dictionary<string, SystemOneQuestion>();

    [JsonPropertyName("assets"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IDictionary<string, SystemOneAsset>? Assets { get; set; }
}

/// <summary>noul 的 Criteria 为 true/false 描述，choice 为字典，score 为有序等级集合。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SystemOneQuestion
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "noul";

    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Instructions { get; set; }

    [JsonPropertyName("criteria"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Criteria { get; set; }
}

/// <summary>Jev 内联图片资产；不接受 multipart part 字段。</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SystemOneAsset
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "image";

    [JsonPropertyName("format")]
    public string Format { get; set; } = "png";

    [JsonPropertyName("data_base64")]
    public string DataBase64 { get; set; } = string.Empty;
}
