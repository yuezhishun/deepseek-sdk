using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DeepSeek.Chat;
using DeepSeek.Responses;
using static DeepSeek.ResponseJsonConverterHelpers;

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
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        options.Converters.Add(new OneOrManyStringsConverter());
        options.Converters.Add(new ObjectOrStringConverter());
        options.Converters.Add(new ChatMessageContentJsonConverter());
        options.Converters.Add(new ChatContentPartJsonConverter());
        options.Converters.Add(new ResponseContentValueJsonConverter());
        options.Converters.Add(new ThinkingModeJsonConverter());
        options.Converters.Add(new ChatReasoningEffortJsonConverter());
        options.Converters.Add(new ResponseOutputItemJsonConverter());
        options.Converters.Add(new ResponseContentPartJsonConverter());
        options.Converters.Add(new ResponseAnnotationJsonConverter());
        options.Converters.Add(new ResponseInputItemJsonConverter());
        options.Converters.Add(new ResponseToolJsonConverter());
        options.Converters.Add(new ResponseInputJsonConverter());
        return options;
    }
}

internal sealed class ChatMessageContentJsonConverter : JsonConverter<ChatMessageContent>
{
    public override ChatMessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new ChatMessageContent(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected chat content to be a string or an array.");
        }

        var parts = JsonSerializer.Deserialize<List<ChatContentPart>>(ref reader, options) ?? [];
        return new ChatMessageContent(parts);
    }

    public override void Write(Utf8JsonWriter writer, ChatMessageContent value, JsonSerializerOptions options)
        => value.WriteJson(writer, options);
}

internal sealed class ChatContentPartJsonConverter : JsonConverter<ChatContentPart>
{
    public override ChatContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var raw = root.GetRawText();
        return ResponseJsonConverterHelpers.GetString(root, "type") switch
        {
            "text" => JsonSerializer.Deserialize<ChatTextContentPart>(raw, options),
            "image_url" => JsonSerializer.Deserialize<ChatImageContentPart>(raw, options),
            "file" => JsonSerializer.Deserialize<ChatFileContentPart>(raw, options),
            _ => JsonSerializer.Deserialize<ChatContentPart>(raw, ResponseJsonConverterHelpers.Without<ChatContentPartJsonConverter>(options)),
        };
    }

    public override void Write(Utf8JsonWriter writer, ChatContentPart value, JsonSerializerOptions options)
    {
        if (value is ChatFileContentPart file)
        {
            file.Validate();
        }

        JsonSerializer.Serialize(writer, value, value.GetType(), ResponseJsonConverterHelpers.Without<ChatContentPartJsonConverter>(options));
    }
}

internal sealed class ResponseContentValueJsonConverter : JsonConverter<ResponseContentValue>
{
    public override ResponseContentValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new ResponseContentValue(reader.GetString() ?? string.Empty);
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected Responses content to be a string or an array.");
        }

        var parts = JsonSerializer.Deserialize<List<ResponseContentPart>>(ref reader, options) ?? [];
        return new ResponseContentValue(parts);
    }

    public override void Write(Utf8JsonWriter writer, ResponseContentValue value, JsonSerializerOptions options)
        => value.WriteJson(writer, options);
}

internal sealed class ResponseInputJsonConverter : JsonConverter<ResponseInput>
{
    public override ResponseInput? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return (ResponseInput)(reader.GetString() ?? string.Empty);
        }

        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var items = JsonSerializer.Deserialize<List<ResponseInputItem>>(document.RootElement.GetRawText(), options) ?? [];
            return new ResponseInput(items);
        }

        return document.RootElement.Clone();
    }

    public override void Write(Utf8JsonWriter writer, ResponseInput value, JsonSerializerOptions options)
        => value.WriteJson(writer, options);
}

internal sealed class ResponseOutputItemJsonConverter : JsonConverter<ResponseOutputItem>
{
    public override ResponseOutputItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = GetString(root, "type");
        var raw = root.GetRawText();

        return type switch
        {
            "message" => JsonSerializer.Deserialize<ResponseMessageOutputItem>(raw, options),
            "reasoning" => JsonSerializer.Deserialize<ResponseReasoningOutputItem>(raw, options),
            "function_call" => JsonSerializer.Deserialize<ResponseFunctionCallOutputItem>(raw, options),
            "web_search_call" => JsonSerializer.Deserialize<ResponseWebSearchCallOutputItem>(raw, options),
            "custom_tool_call" => JsonSerializer.Deserialize<ResponseCustomToolCallOutputItem>(raw, options),
            _ => JsonSerializer.Deserialize<ResponseOutputItem>(raw, CreateOptionsWithoutResponseConverters(options)),
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseOutputItem value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(
            writer,
            value,
            value.GetType(),
            value.GetType() == typeof(ResponseOutputItem)
                ? CreateOptionsWithoutResponseConverters(options)
                : options);

    private static JsonSerializerOptions CreateOptionsWithoutResponseConverters(JsonSerializerOptions options)
    {
        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
        {
            if (clone.Converters[i] is ResponseOutputItemJsonConverter)
            {
                clone.Converters.RemoveAt(i);
            }
        }

        return clone;
    }
}

internal sealed class ResponseInputItemJsonConverter : JsonConverter<ResponseInputItem>
{
    public override ResponseInputItem? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var raw = root.GetRawText();
        var type = GetString(root, "type");
        var item = type switch
        {
            "message" => JsonSerializer.Deserialize<ResponseInputMessage>(raw, options),
            "function_call" => JsonSerializer.Deserialize<ResponseInputFunctionCall>(raw, options),
            "function_call_output" => JsonSerializer.Deserialize<ResponseInputFunctionCallOutput>(raw, options),
            "reasoning" => JsonSerializer.Deserialize<ResponseInputReasoning>(raw, options),
            "web_search_call" => JsonSerializer.Deserialize<ResponseInputWebSearchCall>(raw, options),
            _ => JsonSerializer.Deserialize<ResponseInputItem>(raw, Without<ResponseInputItemJsonConverter>(options)),
        };

        if (item is null)
        {
            return null;
        }

        if (TryGetProperty(root, "content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            item.Content = JsonSerializer.Deserialize<List<ResponseContentPart>>(content.GetRawText(), options) ?? [];
        }

        if (item is ResponseInputWebSearchCall && TryGetProperty(root, "action", out var action)
            && action.ValueKind == JsonValueKind.Object)
        {
            item.Action = JsonSerializer.Deserialize<ResponseWebSearchAction>(action.GetRawText(), options);
        }

        return item;
    }

    public override void Write(Utf8JsonWriter writer, ResponseInputItem value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), Without<ResponseInputItemJsonConverter>(options));
}

internal sealed class ResponseContentPartJsonConverter : JsonConverter<ResponseContentPart>
{
    public override ResponseContentPart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var type = GetString(root, "type");
        var raw = root.GetRawText();

        return type switch
        {
            "input_text" => JsonSerializer.Deserialize<ResponseInputTextContent>(raw, options),
            "input_image" => JsonSerializer.Deserialize<ResponseInputImageContent>(raw, options),
            "output_text" => JsonSerializer.Deserialize<ResponseOutputTextContent>(raw, options),
            "reasoning_text" => JsonSerializer.Deserialize<ResponseReasoningTextContent>(raw, options),
            _ => JsonSerializer.Deserialize<ResponseContentPart>(raw, Without<ResponseContentPartJsonConverter>(options)),
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseContentPart value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(
            writer,
            value,
            value.GetType(),
            value.GetType() == typeof(ResponseContentPart)
                ? Without<ResponseContentPartJsonConverter>(options)
                : options);

}

internal sealed class ResponseAnnotationJsonConverter : JsonConverter<ResponseAnnotation>
{
    public override ResponseAnnotation? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var raw = root.GetRawText();
        return GetString(root, "type") switch
        {
            "url_citation" => JsonSerializer.Deserialize<ResponseUrlCitationAnnotation>(raw, options),
            _ => JsonSerializer.Deserialize<ResponseAnnotation>(raw, Without<ResponseAnnotationJsonConverter>(options)),
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseAnnotation value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), Without<ResponseAnnotationJsonConverter>(options));
}

internal sealed class ResponseToolJsonConverter : JsonConverter<ResponseTool>
{
    public override ResponseTool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var raw = root.GetRawText();
        return GetString(root, "type") switch
        {
            "function" => JsonSerializer.Deserialize<ResponseFunctionTool>(raw, options),
            "web_search" or "web_search_2025_08_26" => JsonSerializer.Deserialize<ResponseWebSearchTool>(raw, options),
            "custom" => JsonSerializer.Deserialize<ResponseCustomTool>(raw, options),
            _ => JsonSerializer.Deserialize<ResponseTool>(raw, Without<ResponseToolJsonConverter>(options)),
        };
    }

    public override void Write(Utf8JsonWriter writer, ResponseTool value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, value.GetType(), Without<ResponseToolJsonConverter>(options));
}

internal static class ResponseJsonConverterHelpers
{
    public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static JsonSerializerOptions Without<TConverter>(JsonSerializerOptions options)
        where TConverter : JsonConverter
    {
        var clone = new JsonSerializerOptions(options);
        for (var i = clone.Converters.Count - 1; i >= 0; i--)
        {
            if (clone.Converters[i] is TConverter)
            {
                clone.Converters.RemoveAt(i);
            }
        }

        return clone;
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
                // JSON Schema property names are user-defined and must not be transformed by
                // the SDK's wire-level snake_case naming policy. SDK model instances in other
                // object-valued fields (for example tool_choice) still use the wire policy.
                var serializationOptions = value.GetType().Assembly == typeof(DeepSeekJson).Assembly
                    ? options
                    : new JsonSerializerOptions(options)
                    {
                        PropertyNamingPolicy = null,
                    };
                JsonSerializer.Serialize(writer, value, value.GetType(), serializationOptions);
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
            "low" or "minimal" => DeepSeek.Chat.ChatReasoningEffort.Low,
            "none" => DeepSeek.Chat.ChatReasoningEffort.None,
            "medium" or "xhigh" => DeepSeek.Chat.ChatReasoningEffort.High,
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

        writer.WriteStringValue(value.Value switch
        {
            DeepSeek.Chat.ChatReasoningEffort.Max => "max",
            DeepSeek.Chat.ChatReasoningEffort.Low => "low",
            DeepSeek.Chat.ChatReasoningEffort.None => "none",
            _ => "high",
        });
    }
}
