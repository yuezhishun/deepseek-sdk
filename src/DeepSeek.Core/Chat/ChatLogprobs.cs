namespace DeepSeek.Chat;

public sealed class ChatLogprobs
{
    public IList<LogprobToken>? Content { get; set; }
    public IList<LogprobToken>? ReasoningContent { get; set; }

    // FIM completions use the OpenAI text-completion logprobs shape.
    public IList<int>? TextOffset { get; set; }
    public IList<double>? TokenLogprobs { get; set; }
    public IList<string>? Tokens { get; set; }
    public IList<IDictionary<string, double>>? TopLogprobs { get; set; }
}
