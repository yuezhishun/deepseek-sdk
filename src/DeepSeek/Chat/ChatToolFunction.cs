using System.Text.Json.Nodes;

namespace DeepSeek.Chat;

public sealed class ChatToolFunction
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonNode Parameters { get; set; } = new JsonObject();
    public bool? Strict { get; set; }
}
