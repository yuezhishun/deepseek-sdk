using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Chat;

/// <summary>Text or typed content parts accepted by a chat message.</summary>
public sealed class ChatMessageContent
{
    private readonly string? _text;
    private readonly IList<ChatContentPart>? _parts;

    public ChatMessageContent()
    {
    }

    public ChatMessageContent(string text)
        => _text = text ?? throw new ArgumentNullException(nameof(text));

    public ChatMessageContent(IEnumerable<ChatContentPart> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        _parts = parts.ToList();
    }

    public string? Text => _text;

    public IList<ChatContentPart>? Parts => _parts;

    public static implicit operator ChatMessageContent(string? value)
        => value is null ? null! : new ChatMessageContent(value);

    public static implicit operator ChatMessageContent(List<ChatContentPart> value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ChatMessageContent(value);

    public static implicit operator ChatMessageContent(ChatContentPart[] value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ChatMessageContent(value);

    internal void WriteJson(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_text is not null)
        {
            writer.WriteStringValue(_text);
            return;
        }

        JsonSerializer.Serialize(writer, _parts ?? [], options);
    }
}

/// <summary>Base type for a Chat Completions content part.</summary>
public class ChatContentPart
{
    public string Type { get; set; } = string.Empty;

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ChatTextContentPart : ChatContentPart
{
    public ChatTextContentPart() => Type = "text";

    public ChatTextContentPart(string text)
        : this()
        => Text = text;

    public string? Text { get; set; }
}

public sealed class ChatImageContentPart : ChatContentPart
{
    public ChatImageContentPart()
        => Type = "image_url";

    public ChatImageContentPart(string url, string? detail = null)
        : this()
        => ImageUrl = new ChatImageUrl(url, detail);

    public ChatImageUrl ImageUrl { get; set; } = new();
}

public sealed class ChatImageUrl
{
    public ChatImageUrl()
    {
    }

    public ChatImageUrl(string url, string? detail = null)
    {
        Url = url;
        Detail = detail;
    }

    public string? Url { get; set; }

    public string? Detail { get; set; }
}

public sealed class ChatFileContentPart : ChatContentPart
{
    public ChatFileContentPart()
        => Type = "file";

    public ChatFileContentPart(string fileId)
        : this()
        => FileId = fileId;

    public string? FileId { get; set; }

    public string? FileData { get; set; }

    public string? Filename { get; set; }

    internal void Validate()
    {
        var hasFileId = !string.IsNullOrWhiteSpace(FileId);
        var hasFileData = !string.IsNullOrWhiteSpace(FileData);
        if (hasFileId == hasFileData)
        {
            throw new ArgumentException("A file content part must specify exactly one of FileId or FileData.", nameof(ChatFileContentPart));
        }

        if (!hasFileData && !string.IsNullOrWhiteSpace(Filename))
        {
            throw new ArgumentException("Filename can only be used with FileData.", nameof(Filename));
        }
    }
}
