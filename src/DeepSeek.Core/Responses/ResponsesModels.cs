using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DeepSeek.Responses;

/// <summary>Request body for DeepSeek's stateless Responses API.</summary>
public sealed class ResponseRequest
{
    public string Model { get; set; } = string.Empty;
    public ResponseInput? Input { get; set; }
    public string? Instructions { get; set; }
    public ResponseReasoningOptions? Reasoning { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? Stream { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public ResponseTextOptions? Text { get; set; }
    public IList<ResponseTool>? Tools { get; set; }

    /// <summary>
    /// A string (<c>none</c>, <c>auto</c>, or <c>required</c>) or a
    /// <see cref="ResponseToolChoice"/> object selecting one tool.
    /// </summary>
    /// <remarks>
    /// This remains an object for source compatibility with the wire API's
    /// string-or-object union. Use <see cref="ResponseToolChoice"/> for the
    /// typed object form.
    /// </remarks>
    public object? ToolChoice { get; set; }

    public int? TopLogprobs { get; set; }
    public string? User { get; set; }

    /// <summary>Preserves request fields not modeled by DeepSeek.</summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>String, item-list, or raw JSON input accepted by Responses.</summary>
public sealed class ResponseInput : Collection<ResponseInputItem>
{
    private readonly string? _text;
    private readonly JsonElement? _jsonElement;
    private readonly JsonNode? _jsonNode;

    public ResponseInput()
    {
    }

    public ResponseInput(IEnumerable<ResponseInputItem> items)
    {
        if (items is null)
        {
            throw new ArgumentNullException(nameof(items));
        }
        foreach (var item in items)
        {
            Add(item);
        }
    }

    private ResponseInput(string text) => _text = text;

    private ResponseInput(JsonElement jsonElement) => _jsonElement = jsonElement.Clone();

    private ResponseInput(JsonNode jsonNode) => _jsonNode = jsonNode;

    public static implicit operator ResponseInput(string value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseInput(value);

    public static implicit operator ResponseInput(JsonElement value) => new(value);

    public static implicit operator ResponseInput(JsonObject value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseInput(value);

    public static implicit operator ResponseInput(List<ResponseInputItem> value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseInput(value);

    public static implicit operator ResponseInput(ResponseInputItem[] value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseInput(value);

    internal void WriteJson(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_text is not null)
        {
            writer.WriteStringValue(_text);
        }
        else if (_jsonElement is JsonElement element)
        {
            element.WriteTo(writer);
        }
        else if (_jsonNode is not null)
        {
            _jsonNode.WriteTo(writer, options);
        }
        else
        {
            JsonSerializer.Serialize(writer, (IList<ResponseInputItem>)this, options);
        }
    }
}

/// <summary>Base class for Responses input items. Unknown item types are retained here.</summary>
public class ResponseInputItem
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Role { get; set; }
    public ResponseContentValue? Content { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
    public string? Input { get; set; }
    public ResponseContentValue? Output { get; set; }
    public string? Status { get; set; }
    public IList<ResponseReasoningSummary>? Summary { get; set; }
    public string? EncryptedContent { get; set; }
    public ResponseWebSearchAction? Action { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class ResponseInputMessage : ResponseInputItem
{
    public ResponseInputMessage()
    {
        Type = "message";
        Role = "user";
    }
}

public sealed class ResponseInputFunctionCall : ResponseInputItem
{
    public ResponseInputFunctionCall() => Type = "function_call";
}

public sealed class ResponseInputFunctionCallOutput : ResponseInputItem
{
    public ResponseInputFunctionCallOutput() => Type = "function_call_output";
}

public sealed class ResponseInputReasoning : ResponseInputItem
{
    public ResponseInputReasoning() => Type = "reasoning";
}

public sealed class ResponseInputWebSearchCall : ResponseInputItem
{
    public ResponseInputWebSearchCall() => Type = "web_search_call";
}

/// <summary>Base class for input and output content blocks.</summary>
public class ResponseContentPart
{
    public string? Type { get; set; }
    public string? Text { get; set; }
    public IList<ResponseAnnotation>? Annotations { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>String or typed content parts accepted by a Responses input item.</summary>
public sealed class ResponseContentValue
{
    private readonly string? _text;
    private readonly IList<ResponseContentPart>? _parts;

    public ResponseContentValue()
    {
    }

    public ResponseContentValue(string text)
        => _text = text ?? throw new ArgumentNullException(nameof(text));

    public ResponseContentValue(IEnumerable<ResponseContentPart> parts)
    {
        if (parts is null)
        {
            throw new ArgumentNullException(nameof(parts));
        }

        _parts = parts.ToList();
    }

    public string? Text => _text;

    public IList<ResponseContentPart>? Parts => _parts;

    public static implicit operator ResponseContentValue(string? value)
        => value is null ? null! : new ResponseContentValue(value);

    public static implicit operator ResponseContentValue(List<ResponseContentPart> value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseContentValue(value);

    public static implicit operator ResponseContentValue(ResponseContentPart[] value)
        => value is null ? throw new ArgumentNullException(nameof(value)) : new ResponseContentValue(value);

    internal void WriteJson(Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        if (_text is not null)
        {
            writer.WriteStringValue(_text);
            return;
        }

        JsonSerializer.Serialize(writer, _parts ?? [], options);
    }
}

public class ResponseInputTextContent : ResponseContentPart
{
    public ResponseInputTextContent() => Type = "input_text";
}

public sealed class ResponseInputText : ResponseInputTextContent
{
}

public class ResponseOutputTextContent : ResponseContentPart
{
    public ResponseOutputTextContent() => Type = "output_text";
}

public sealed class ResponseOutputText : ResponseOutputTextContent
{
}

public class ResponseReasoningTextContent : ResponseContentPart
{
    public ResponseReasoningTextContent() => Type = "reasoning_text";
}

/// <summary>An image input content part. Images may be supplied by URL/data URL or file ID.</summary>
public sealed class ResponseInputImageContent : ResponseContentPart
{
    public ResponseInputImageContent() => Type = "input_image";

    public string? ImageUrl { get; set; }

    public string? Detail { get; set; }

    public string? FileId { get; set; }
}

public sealed class ResponseReasoningText : ResponseReasoningTextContent
{
}

public class ResponseAnnotation
{
    public string? Type { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseUrlCitationAnnotation : ResponseAnnotation
{
    public ResponseUrlCitationAnnotation() => Type = "url_citation";

    public int? StartIndex { get; set; }
    public int? EndIndex { get; set; }
    public string? Url { get; set; }
    public string? Title { get; set; }
}

public sealed class ResponseReasoningOptions
{
    public string? Effort { get; set; }
    public object? Summary { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseTextOptions
{
    public ResponseTextFormat? Format { get; set; }
    public string? Verbosity { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseTextFormat
{
    public string Type { get; set; } = "text";
    public string? Name { get; set; }
    public object? Schema { get; set; }
    public bool? Strict { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }

    public static ResponseTextFormat Text() => new() { Type = "text" };

    public static ResponseTextFormat JsonObject() => new() { Type = "json_object" };

    public static ResponseTextFormat JsonSchema(string name, object schema, bool strict = true)
        => new() { Type = "json_schema", Name = name, Schema = schema, Strict = strict };
}

/// <summary>Base class for a Responses tool.</summary>
public class ResponseTool
{
    public string Type { get; set; } = "function";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public object? Parameters { get; set; }
    public bool? Strict { get; set; }
    public string? SearchContextSize { get; set; }
    public ResponseUserLocation? UserLocation { get; set; }
    public ResponseCustomToolFormat? Format { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseFunctionTool : ResponseTool
{
    public ResponseFunctionTool() => Type = "function";
}

public sealed class ResponseWebSearchTool : ResponseTool
{
    public ResponseWebSearchTool() => Type = "web_search";
}

public sealed class ResponseCustomTool : ResponseTool
{
    public ResponseCustomTool() => Type = "custom";
}

public sealed class ResponseCustomToolFormat
{
    public string? Type { get; set; }
    public string? Syntax { get; set; }
    public string? Definition { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseUserLocation
{
    public string? Type { get; set; }
    public string? Country { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? Timezone { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>String and named-tool forms accepted by <c>tool_choice</c>.</summary>
public sealed class ResponseToolChoice
{
    public const string None = "none";
    public const string Auto = "auto";
    public const string Required = "required";

    public string Type { get; set; } = "function";
    public string? Name { get; set; }

    public ResponseToolChoice()
    {
    }

    public ResponseToolChoice(string type, string? name = null)
    {
        Type = type;
        Name = name;
    }

    public static ResponseToolChoice Function(string name)
        => new() { Type = "function", Name = name };

    public static ResponseToolChoice WebSearch(string type = "web_search")
        => new() { Type = type };

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>Base class for a Responses output item.</summary>
public class ResponseOutputItem
{
    public string? Type { get; set; }
    public string? Id { get; set; }
    public string? Status { get; set; }
    public string? Role { get; set; }
    public IList<ResponseContentPart> Content { get; set; } = [];
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Arguments { get; set; }
    public string? Output { get; set; }
    public string? Input { get; set; }
    public IList<ResponseReasoningSummary>? Summary { get; set; }
    public string? EncryptedContent { get; set; }
    public ResponseWebSearchAction? Action { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public class ResponseMessageOutputItem : ResponseOutputItem
{
    public ResponseMessageOutputItem()
    {
        Type = "message";
        Role = "assistant";
    }
}

public sealed class ResponseMessageOutput : ResponseMessageOutputItem
{
}

public class ResponseReasoningOutputItem : ResponseOutputItem
{
    public ResponseReasoningOutputItem() => Type = "reasoning";
}

public sealed class ResponseReasoningOutput : ResponseReasoningOutputItem
{
}

public class ResponseFunctionCallOutputItem : ResponseOutputItem
{
    public ResponseFunctionCallOutputItem() => Type = "function_call";
}

public sealed class ResponseFunctionCallOutput : ResponseFunctionCallOutputItem
{
}

public class ResponseWebSearchCallOutputItem : ResponseOutputItem
{
    public ResponseWebSearchCallOutputItem() => Type = "web_search_call";
}

public sealed class ResponseWebSearchCallOutput : ResponseWebSearchCallOutputItem
{
}

public class ResponseCustomToolCallOutputItem : ResponseOutputItem
{
    public ResponseCustomToolCallOutputItem() => Type = "custom_tool_call";
}

public sealed class ResponseCustomToolCallOutput : ResponseCustomToolCallOutputItem
{
}

public sealed class ResponseReasoningSummary
{
    public string? Type { get; set; }
    public string? Text { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseWebSearchAction
{
    public string? Type { get; set; }
    public string? Query { get; set; }
    public string? Url { get; set; }
    public string? Pattern { get; set; }
    public IList<ResponseWebSearchSource>? Sources { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseWebSearchSource
{
    public string? Url { get; set; }
    public string? Title { get; set; }
    public string? Snippet { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseError
{
    public string? Type { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Param { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseIncompleteDetails
{
    public string? Reason { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseUsage
{
    public int InputTokens { get; set; }
    public ResponseInputTokenDetails? InputTokensDetails { get; set; }
    public int OutputTokens { get; set; }
    public ResponseOutputTokenDetails? OutputTokensDetails { get; set; }
    public int TotalTokens { get; set; }
    public IDictionary<string, long>? ServerToolUse { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseInputTokenDetails
{
    public int CachedTokens { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class ResponseOutputTokenDetails
{
    public int ReasoningTokens { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class Response
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public long CreatedAt { get; set; }
    public string? Status { get; set; }
    public ResponseError? Error { get; set; }
    public ResponseIncompleteDetails? IncompleteDetails { get; set; }
    public string? Model { get; set; }
    public IList<ResponseOutputItem> Output { get; set; } = [];
    public ResponseUsage? Usage { get; set; }
    public bool? Store { get; set; }
    public bool? ParallelToolCalls { get; set; }
    public string? PreviousResponseId { get; set; }

    [JsonIgnore]
    public string OutputText
        => string.Concat(Output
            .Where(static item => item.Type == "message")
            .SelectMany(static item => item.Content)
            .Where(static content => content.Type == "output_text")
            .Select(static content => content.Text));

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>A semantic SSE event emitted by the Responses API.</summary>
public sealed class ResponseStreamEvent
{
    public string? Type { get; set; }
    public int? SequenceNumber { get; set; }
    public int? OutputIndex { get; set; }
    public int? ContentIndex { get; set; }
    public string? ItemId { get; set; }
    public string? Delta { get; set; }
    public string? Text { get; set; }
    public string? Arguments { get; set; }
    public string? Input { get; set; }
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public ResponseOutputItem? Item { get; set; }

    [JsonPropertyName("part")]
    public ResponseContentPart? ContentPart { get; set; }

    public Response? Response { get; set; }
    public ResponseWebSearchAction? Action { get; set; }

    /// <summary>The SSE event name, if one was present in the stream.</summary>
    [JsonIgnore]
    public string? Event { get; internal set; }

    /// <summary>The complete JSON data payload received for this event.</summary>
    [JsonIgnore]
    public JsonElement? RawData { get; internal set; }

    [JsonExtensionData]
    public IDictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
