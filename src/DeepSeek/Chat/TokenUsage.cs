namespace DeepSeek.Chat;

public sealed class TokenUsage
{
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public int TotalTokens { get; set; }
    public int? PromptCacheHitTokens { get; set; }
    public int? PromptCacheMissTokens { get; set; }
    public TokenDetails? PromptTokensDetails { get; set; }
    public TokenDetails? CompletionTokensDetails { get; set; }
}
