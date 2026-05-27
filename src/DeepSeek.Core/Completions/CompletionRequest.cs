using DeepSeek.Chat;

namespace DeepSeek.Completions;

public sealed class CompletionRequest
{
    public string Prompt { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool? Echo { get; set; }
    public long? MaxTokens { get; set; }
    public IList<string>? Stop { get; set; }
    public bool? Stream { get; set; }
    public string? Suffix { get; set; }
    public StreamOptions? StreamOptions { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? Logprobs { get; set; }
}
