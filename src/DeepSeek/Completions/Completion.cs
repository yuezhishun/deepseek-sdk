using DeepSeek.Chat;

namespace DeepSeek.Completions;

public sealed class Completion
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public long Created { get; set; }
    public string? Model { get; set; }
    public IList<CompletionChoice> Choices { get; set; } = [];
    public TokenUsage? Usage { get; set; }
}
