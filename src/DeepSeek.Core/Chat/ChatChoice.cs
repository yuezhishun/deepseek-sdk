namespace DeepSeek.Chat;

public sealed class ChatChoice
{
    public int Index { get; set; }
    public ChatMessage? Message { get; set; }
    public ChatMessage? Delta { get; set; }
    public string? FinishReason { get; set; }
    public ChatLogprobs? Logprobs { get; set; }
}
