namespace DeepSeek.Chat;

public sealed class ChatToolChoice
{
    public string Type { get; set; } = "function";
    public ChatToolChoiceFunction Function { get; set; } = new();
}
