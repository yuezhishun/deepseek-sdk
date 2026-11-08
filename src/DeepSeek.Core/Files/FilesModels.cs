using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Files;

public sealed class DeepSeekFile
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public long Bytes { get; set; }
    public long CreatedAt { get; set; }
    public string? Filename { get; set; }
    public string? MimeType { get; set; }
    public string? Purpose { get; set; }
    public long? ExpiresAt { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DeepSeekFileList
{
    public string? Object { get; set; }
    public IList<DeepSeekFile> Data { get; set; } = [];
    public string? FirstId { get; set; }
    public string? LastId { get; set; }
    public bool HasMore { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class DeepSeekFileDeleteResult
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public bool Deleted { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class FileListOptions
{
    public string? After { get; set; }
    /// <summary>Anthropic Files 的前向游标，不能与 After 同时使用。</summary>
    public string? Before { get; set; }
    public int? Limit { get; set; }
    public string? Order { get; set; }
    public string? Purpose { get; set; }
}

public sealed class FileUploadOptions
{
    public string Purpose { get; set; } = "user_data";
    public string? ExpiresAfterAnchor { get; set; }
    public int? ExpiresAfterSeconds { get; set; }
}
