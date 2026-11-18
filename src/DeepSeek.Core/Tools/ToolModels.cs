using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeek.Tools;

public sealed class OcrResult
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public long Created { get; set; }
    public string? Purpose { get; set; }
    public int PageCount { get; set; }
    public IList<OcrPage> Pages { get; set; } = [];
    public OcrUsage? Usage { get; set; }
    public Responses.ResponseError? Error { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class OcrPage
{
    public int Index { get; set; }
    public string? Text { get; set; }
    public string? FinishReason { get; set; }
}

public sealed class OcrUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
}

public sealed class SpeechTranscription
{
    public string? RequestId { get; set; }
    public string? Text { get; set; }
    public long DurationMs { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class SpeechSynthesisOptions
{
    public string? VoiceId { get; set; }
    /// <summary>WAV or PCM for HTTP. PCM or Opus for WebSocket. Use lower-case wire values.</summary>
    public string? Format { get; set; }
    public bool? FilterMarkdown { get; set; }
}
