namespace DeepSeek.Chat;

public sealed class ChatTool
{
    public string Type { get; set; } = "function";
    public ChatToolFunction Function { get; set; } = new();
}
