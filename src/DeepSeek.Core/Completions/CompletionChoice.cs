using DeepSeek.Chat;

namespace DeepSeek.Completions;

public sealed class CompletionChoice
{
    public int Index { get; set; }
    public string? Text { get; set; }
    public string? FinishReason { get; set; }
    public ChatLogprobs? Logprobs { get; set; }
}
