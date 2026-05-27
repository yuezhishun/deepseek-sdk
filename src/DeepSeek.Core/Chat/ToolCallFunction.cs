namespace DeepSeek.Chat;

public sealed class ToolCallFunction
{
    public string? Name { get; set; }
    public string Arguments { get; set; } = string.Empty;
}
