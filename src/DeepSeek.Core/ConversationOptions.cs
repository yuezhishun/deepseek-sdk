using System.Text.Json;

namespace DeepSeek;

public sealed class SafetyCheckOptions
{
    public string Level { get; set; } = "low";
}

public sealed class ConversationTitleOptions
{
    public bool Enabled { get; set; }
}

internal static class ConversationOptions
{
    internal static bool IsWebTool(string? type) => type is "deepseek_web_search" or "deepseek_read_url";

    internal static bool Validate(IEnumerable<string?>? tools, SafetyCheckOptions? safety, ConversationTitleOptions? title, object? choice)
    {
        if (safety is not null && safety.Level is not ("low" or "high"))
            throw new ArgumentException("Safety check level must be 'low' or 'high'.", nameof(safety));

        var types = tools?.ToArray() ?? [];
        var webTools = types.Where(IsWebTool).ToArray();
        if (webTools.Distinct(StringComparer.Ordinal).Count() != webTools.Length)
            throw new ArgumentException("Each DeepSeek web tool may be declared only once.", nameof(tools));

        var enabled = webTools.Length > 0 || safety?.Level == "high" || title?.Enabled == true;
        if (!enabled) return false;
        if (types.Any(type => type is not (null or "function") && !IsWebTool(type)))
            throw new ArgumentException("The DeepSeek conversation extensions cannot be combined with other hosted or custom tools.", nameof(tools));

        if (choice is not null)
        {
            var wire = JsonSerializer.SerializeToElement(choice, DeepSeekJson.SerializerOptions);
            var mode = wire.ValueKind == JsonValueKind.String ? wire.GetString()
                : wire.ValueKind == JsonValueKind.Object && wire.TryGetProperty("type", out var kind) ? kind.GetString() : null;
            if (webTools.Length == 0 && safety?.Level == "high" && mode is not ("auto" or "none"))
                throw new ArgumentException("A standalone high safety check allows only auto or none tool choice.", nameof(choice));
            if (wire.ValueKind == JsonValueKind.Object && (IsWebTool(mode)
                || wire.TryGetProperty("name", out var name) && IsWebTool(name.GetString())
                || wire.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
                    && function.TryGetProperty("name", out name) && IsWebTool(name.GetString())))
                throw new ArgumentException("DeepSeek hosted web tools cannot be forced.", nameof(choice));
        }
        return true;
    }

    internal static void ValidateReminder(IList<string> roles, int index, string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || index + 1 >= roles.Count || roles[index + 1] != "user")
            throw new ArgumentException("A latest_reminder message must have text and immediately precede its user message.");
    }
}
