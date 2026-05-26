namespace DeepSeek.Chat;

public sealed class ToolCall
{
    public int? Index { get; set; }
    public string? Id { get; set; }
    public string Type { get; set; } = "function";
    public ToolCallFunction Function { get; set; } = new();
}
