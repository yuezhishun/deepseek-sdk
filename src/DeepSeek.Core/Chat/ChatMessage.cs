namespace DeepSeek.Chat;

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public ChatMessageContent? Content { get; set; }
    public string? Name { get; set; }
    public bool? Prefix { get; set; }
    public string? ReasoningContent { get; set; }
    public string? ToolCallId { get; set; }
    public IList<ToolCall>? ToolCalls { get; set; }
}
