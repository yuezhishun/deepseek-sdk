using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace DeepSeek;

internal static class DeepSeekJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = SnakeCaseJsonNamingPolicy.Instance,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        };

        options.Converters.Add(new OneOrManyStringsConverter());
        options.Converters.Add(new ObjectOrStringConverter());
        options.Converters.Add(new ThinkingModeJsonConverter());
        options.Converters.Add(new ChatReasoningEffortJsonConverter());
        return options;
    }
}

internal sealed class SnakeCaseJsonNamingPolicy : JsonNamingPolicy
{
    public static SnakeCaseJsonNamingPolicy Instance { get; } = new();

    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var current = name[i];
            if (char.IsUpper(current))
            {
                if (i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1])))
                {
                    builder.Append('_');
                }
                else if (i > 0 && i + 1 < name.Length && char.IsLower(name[i + 1]))
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(current));
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }
}

internal sealed class OneOrManyStringsConverter : JsonConverter<IList<string>?>
{
    public override IList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return [reader.GetString() ?? string.Empty];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a string or array.");
        }

        var values = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                return values;
            }

            values.Add(reader.GetString() ?? string.Empty);
        }

        throw new JsonException("Unterminated string array.");
    }

    public override void Write(Utf8JsonWriter writer, IList<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Count == 1)
        {
            writer.WriteStringValue(value[0]);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}

internal sealed class ObjectOrStringConverter : JsonConverter<object?>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            _ => JsonNode.Parse(JsonDocument.ParseValue(ref reader).RootElement.GetRawText()),
        };
    }

    public override void Write(Utf8JsonWriter writer, object? value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case JsonNode node:
                node.WriteTo(writer, options);
                break;
            case JsonElement element:
                element.WriteTo(writer);
                break;
            default:
                JsonSerializer.Serialize(writer, value, value.GetType(), options);
                break;
        }
    }
}

internal sealed class ThinkingModeJsonConverter : JsonConverter<DeepSeek.Chat.ThinkingMode?>
{
    public override DeepSeek.Chat.ThinkingMode? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return Parse(reader.GetString());
        }

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.TryGetProperty("type", out var type))
            {
                return Parse(type.GetString());
            }
        }

        throw new JsonException("Unsupported thinking payload.");
    }

    public override void Write(Utf8JsonWriter writer, DeepSeek.Chat.ThinkingMode? value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("type", value.Value == DeepSeek.Chat.ThinkingMode.Enabled ? "enabled" : "disabled");
        writer.WriteEndObject();
    }

    private static DeepSeek.Chat.ThinkingMode? Parse(string? value)
    {
        return value switch
        {
            "enabled" => DeepSeek.Chat.ThinkingMode.Enabled,
            "disabled" => DeepSeek.Chat.ThinkingMode.Disabled,
            _ => null,
        };
    }
}

internal sealed class ChatReasoningEffortJsonConverter : JsonConverter<DeepSeek.Chat.ChatReasoningEffort?>
{
    public override DeepSeek.Chat.ChatReasoningEffort? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Unsupported reasoning_effort payload.");
        }

        return reader.GetString() switch
        {
            "max" => DeepSeek.Chat.ChatReasoningEffort.Max,
            "high" => DeepSeek.Chat.ChatReasoningEffort.High,
            _ => null,
        };
    }

    public override void Write(Utf8JsonWriter writer, DeepSeek.Chat.ChatReasoningEffort? value, JsonSerializerOptions options)
    {
        if (!value.HasValue)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value == DeepSeek.Chat.ChatReasoningEffort.Max ? "max" : "high");
    }
}
