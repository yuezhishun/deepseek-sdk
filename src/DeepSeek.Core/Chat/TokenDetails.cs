namespace DeepSeek.Chat;

public sealed class TokenDetails
{
    public int? ReasoningTokens { get; set; }
    public int? CachedTokens { get; set; }
    public int? SafetyTokens { get; set; }
    public int? ThinkingTokens { get; set; }
}
