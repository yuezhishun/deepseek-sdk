using System.Text.Json.Serialization;

namespace DeepSeek.Chat;

public sealed class ChatCompletionRequest
{
    public IList<ChatMessage> Messages { get; set; } = [];
    public string Model { get; set; } = string.Empty;
    public long? MaxTokens { get; set; }
    public ResponseFormat? ResponseFormat { get; set; }
    public IList<string>? Stop { get; set; }
    public bool? Stream { get; set; }
    public IList<ChatTool>? Tools { get; set; }
    public object? ToolChoice { get; set; }
    public StreamOptions? StreamOptions { get; set; }
    public ThinkingMode? Thinking { get; set; }

    [JsonPropertyName("reasoning_effort")]
    public ChatReasoningEffort? ReasoningEffort { get; set; }

    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public bool? Logprobs { get; set; }
    public int? TopLogprobs { get; set; }

    [JsonPropertyName("user")]
    public string? UserId { get; set; }
}
