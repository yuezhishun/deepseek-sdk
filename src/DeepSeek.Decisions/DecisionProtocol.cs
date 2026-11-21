using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeepSeek.Decisions;

internal static class DecisionProtocol
{
    internal static DecisionProtocolException Invalid(int status, string field) =>
        new(status, status == 422 ? "validation_error" : "invalid_request", "请求字段缺失或格式无效。", field);

    internal static DecisionProtocolException Unsupported(string field) =>
        new(400, "unsupported_capability", "当前后端无法提供该协议能力。", field);

    internal static void Object(JsonElement value, string field, int status, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid(status, field);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || allowed.Length != 0 && !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw Invalid(status, field);
        }
    }

    internal static JsonElement Field(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) ? property : default;

    internal static bool Present(JsonElement value) => value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    internal static string Text(JsonElement value, int status, string field, int maximum, int minimum = 0)
    {
        if (value.ValueKind != JsonValueKind.String) throw Invalid(status, field);
        var text = value.GetString()!;
        return text.Length >= minimum && text.Length <= maximum ? text : throw Invalid(status, field);
    }

    internal static string? OptionalText(JsonElement value, int status, string field, int maximum, int minimum = 0) =>
        Present(value) ? Text(value, status, field, maximum, minimum) : null;

    internal static void Model(JsonElement value, string bound, int status)
    {
        if (!string.Equals(Text(value, status, "model", 1048576, 1), bound, StringComparison.Ordinal))
            throw new DecisionProtocolException(status, "model_not_supported", "请求模型与此决策客户端绑定的模型不一致。", "model");
    }

    private static readonly JsonSerializerOptions CompactJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 128 };

    internal static int CompactBytes(JsonElement value) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, CompactJson));

    internal static void Structure(JsonElement value, string field, int maxDepth, int maxValues = int.MaxValue)
    {
        var count = 0;
        Inspect(value, 0);
        void Inspect(JsonElement item, int depth)
        {
            if (++count > maxValues) throw Invalid(400, field);
            if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                if (++depth > maxDepth) throw Invalid(400, field);
                if (item.ValueKind == JsonValueKind.Object)
                {
                    Object(item, field, 400);
                    foreach (var child in item.EnumerateObject()) Inspect(child.Value, depth);
                }
                else
                    foreach (var child in item.EnumerateArray()) Inspect(child, depth);
            }
            else if (item.ValueKind == JsonValueKind.Number &&
                     (!item.TryGetDouble(out var number) || !DecisionMath.IsFinite(number)))
                throw Invalid(400, field);
        }
    }

    internal static void Vision(string model, string field)
    {
        // deepseek-flash and its historical aliases are the supported DeepSeek vision models.
        if (model is not ("deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp"))
            throw Unsupported(field);
    }
}
