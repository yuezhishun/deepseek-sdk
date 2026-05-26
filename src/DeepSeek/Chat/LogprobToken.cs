namespace DeepSeek.Chat;

public sealed class LogprobToken
{
    public string? Token { get; set; }
    public double Logprob { get; set; }
    public byte[]? Bytes { get; set; }
    public IList<LogprobToken>? TopLogprobs { get; set; }
}
