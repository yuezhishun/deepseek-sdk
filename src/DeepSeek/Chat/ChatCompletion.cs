namespace DeepSeek.Chat;

public sealed class ChatCompletion
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public long Created { get; set; }
    public string? Model { get; set; }
    public string? SystemFingerprint { get; set; }
    public IList<ChatChoice> Choices { get; set; } = [];
    public TokenUsage? Usage { get; set; }
}
