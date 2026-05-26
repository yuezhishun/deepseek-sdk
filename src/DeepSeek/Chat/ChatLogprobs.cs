namespace DeepSeek.Chat;

public sealed class ChatLogprobs
{
    public IList<LogprobToken>? Content { get; set; }
    public IList<LogprobToken>? ReasoningContent { get; set; }
}
