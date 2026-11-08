namespace DeepSeek.Chat;

public sealed class LogprobToken
{
    public string? Token { get; set; }
    public double Logprob { get; set; }
    /// <summary>UTF-8 byte values for the token, as returned by DeepSeek.</summary>
    public IList<int>? Bytes { get; set; }
    public IList<LogprobToken>? TopLogprobs { get; set; }
}
