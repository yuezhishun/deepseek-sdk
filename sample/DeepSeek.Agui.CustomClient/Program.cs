using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var serverUrl = args.FirstOrDefault(arg => arg.StartsWith("--server=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
    ?? "http://localhost:5099/agui";

Console.WriteLine($"AG-UI server: {serverUrl}");
Console.WriteLine("Type /quit to exit.");

using var httpClient = new HttpClient
{
    Timeout = Timeout.InfiniteTimeSpan,
};

httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

var threadId = Guid.NewGuid().ToString();
var conversation = new List<AgUiMessage>
{
    new("system", "You are a protocol debugging assistant for AG-UI demos."),
};
var jsonOptions = CreateJsonOptions();

while (true)
{
    Console.Write("\nUser > ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
    {
        continue;
    }

    if (string.Equals(input, "/quit", StringComparison.OrdinalIgnoreCase))
    {
        break;
    }

    conversation.Add(new AgUiMessage("user", input));

    var runId = Guid.NewGuid().ToString();
    var request = new AgUiRunRequest(threadId, runId, conversation);
    var renderer = new AgUiEventRenderer();
    var assistantMessages = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
    var assistantMessageOrder = new List<string>();

    using var httpRequest = new HttpRequestMessage(HttpMethod.Post, serverUrl)
    {
        Content = new StringContent(JsonSerializer.Serialize(request, jsonOptions), Encoding.UTF8, "application/json"),
    };

    try
    {
        using var response = await httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        string? eventName = null;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                await ProcessFrameAsync(
                    eventName,
                    line["data:".Length..].TrimStart(),
                    renderer,
                    assistantMessages,
                    assistantMessageOrder,
                    jsonOptions);
                eventName = null;
            }
        }
        renderer.CloseActiveSegment();
    }
    catch (Exception ex)
    {
        renderer.CloseActiveSegment();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[REQUEST_ERROR] {ex.Message}");
        Console.ResetColor();
    }

    var assistantText = string.Join(
        Environment.NewLine + Environment.NewLine,
        assistantMessageOrder
            .Select(id => assistantMessages[id].ToString())
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    if (!string.IsNullOrWhiteSpace(assistantText))
    {
        conversation.Add(new AgUiMessage("assistant", assistantText));
    }
}

static JsonSerializerOptions CreateJsonOptions()
{
    return new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

static async Task ProcessFrameAsync(
    string? eventName,
    string data,
    AgUiEventRenderer renderer,
    Dictionary<string, StringBuilder> assistantMessages,
    List<string> assistantMessageOrder,
    JsonSerializerOptions jsonOptions)
{
    if (string.IsNullOrWhiteSpace(data) || string.Equals(data, "[DONE]", StringComparison.Ordinal))
    {
        return;
    }

    AgUiEvent? agUiEvent;
    try
    {
        agUiEvent = JsonSerializer.Deserialize<AgUiEvent>(data, jsonOptions);
    }
    catch (JsonException ex)
    {
        renderer.CloseActiveSegment();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[INVALID_EVENT_JSON] {ex.Message}");
        Console.ResetColor();
        return;
    }

    if (agUiEvent is null)
    {
        return;
    }

    if (string.IsNullOrWhiteSpace(agUiEvent.Type) && !string.IsNullOrWhiteSpace(eventName))
    {
        agUiEvent.Type = eventName;
    }

    renderer.Render(agUiEvent);

    if (string.Equals(agUiEvent.Type, AgUiEventTypes.TextMessageContent, StringComparison.Ordinal)
        && !string.IsNullOrEmpty(agUiEvent.MessageId)
        && agUiEvent.Delta is { Length: > 0 } delta)
    {
        if (!assistantMessages.TryGetValue(agUiEvent.MessageId, out var builder))
        {
            builder = new StringBuilder();
            assistantMessages.Add(agUiEvent.MessageId, builder);
            assistantMessageOrder.Add(agUiEvent.MessageId);
        }

        builder.Append(delta);
    }

    await Task.CompletedTask;
}

internal sealed record AgUiRunRequest(string ThreadId, string RunId, IReadOnlyList<AgUiMessage> Messages);

internal sealed record AgUiMessage(string Role, string Content);

internal sealed class AgUiEvent
{
    public string? Type { get; set; }
    public string? ThreadId { get; set; }
    public string? RunId { get; set; }
    public string? MessageId { get; set; }
    public string? ParentMessageId { get; set; }
    public string? Role { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolCallName { get; set; }
    public JsonElement? Content { get; set; }
    public JsonElement? Result { get; set; }
    public JsonElement? Snapshot { get; set; }
    public JsonElement? Value { get; set; }
    public JsonElement? Input { get; set; }
    public JsonElement? State { get; set; }
    public JsonElement? Context { get; set; }
    public JsonElement? ForwardedProps { get; set; }
    public JsonElement? DeltaPayload { get; set; }
    public string? Code { get; set; }
    public string? Message { get; set; }
    public string? Subtype { get; set; }
    public string? EntityId { get; set; }
    public string? EncryptedValue { get; set; }

    [JsonPropertyName("delta")]
    public JsonElement? DeltaElement
    {
        set
        {
            if (value is null)
            {
                Delta = null;
                DeltaPayload = null;
                return;
            }

            if (value.Value.ValueKind == JsonValueKind.String)
            {
                Delta = value.Value.GetString();
                DeltaPayload = null;
                return;
            }

            Delta = null;
            DeltaPayload = value;
        }
    }

    [JsonIgnore]
    public string? Delta { get; private set; }
}

internal static class AgUiEventTypes
{
    public const string RunStarted = "RUN_STARTED";
    public const string RunFinished = "RUN_FINISHED";
    public const string RunError = "RUN_ERROR";
    public const string TextMessageStart = "TEXT_MESSAGE_START";
    public const string TextMessageContent = "TEXT_MESSAGE_CONTENT";
    public const string TextMessageEnd = "TEXT_MESSAGE_END";
    public const string ToolCallStart = "TOOL_CALL_START";
    public const string ToolCallArgs = "TOOL_CALL_ARGS";
    public const string ToolCallEnd = "TOOL_CALL_END";
    public const string ToolCallResult = "TOOL_CALL_RESULT";
    public const string StateSnapshot = "STATE_SNAPSHOT";
    public const string StateDelta = "STATE_DELTA";
    public const string ReasoningStart = "REASONING_START";
    public const string ReasoningMessageStart = "REASONING_MESSAGE_START";
    public const string ReasoningMessageContent = "REASONING_MESSAGE_CONTENT";
    public const string ReasoningMessageEnd = "REASONING_MESSAGE_END";
    public const string ReasoningEnd = "REASONING_END";
    public const string ReasoningMessageChunk = "REASONING_MESSAGE_CHUNK";
    public const string ReasoningEncryptedValue = "REASONING_ENCRYPTED_VALUE";
}

internal sealed class AgUiEventRenderer
{
    private StreamSegment _activeSegment = StreamSegment.None;

    public void Render(AgUiEvent agUiEvent)
    {
        switch (agUiEvent.Type)
        {
            case AgUiEventTypes.TextMessageContent:
                WriteStream(StreamSegment.Text, "[TEXT] ", agUiEvent.Delta);
                return;

            case AgUiEventTypes.ReasoningMessageContent:
            case AgUiEventTypes.ReasoningMessageChunk:
                WriteStream(StreamSegment.Reasoning, "[REASONING] ", agUiEvent.Delta);
                return;

            case AgUiEventTypes.RunStarted:
            case AgUiEventTypes.RunFinished:
            case AgUiEventTypes.RunError:
            case AgUiEventTypes.TextMessageStart:
            case AgUiEventTypes.TextMessageEnd:
            case AgUiEventTypes.ToolCallStart:
            case AgUiEventTypes.ToolCallArgs:
            case AgUiEventTypes.ToolCallEnd:
            case AgUiEventTypes.ToolCallResult:
            case AgUiEventTypes.StateSnapshot:
            case AgUiEventTypes.StateDelta:
            case AgUiEventTypes.ReasoningStart:
            case AgUiEventTypes.ReasoningMessageStart:
            case AgUiEventTypes.ReasoningMessageEnd:
            case AgUiEventTypes.ReasoningEnd:
            case AgUiEventTypes.ReasoningEncryptedValue:
                CloseActiveSegment();
                WriteSummary(agUiEvent.Type!, BuildSummary(agUiEvent));
                return;

            default:
                CloseActiveSegment();
                WriteSummary("UNKNOWN_EVENT", $"{agUiEvent.Type ?? "<missing type>"} {BuildSummary(agUiEvent)}".Trim());
                return;
        }
    }

    public void CloseActiveSegment()
    {
        if (_activeSegment == StreamSegment.None)
        {
            return;
        }

        Console.WriteLine();
        _activeSegment = StreamSegment.None;
    }

    private void WriteStream(StreamSegment segment, string prefix, string? delta)
    {
        if (_activeSegment != segment)
        {
            CloseActiveSegment();
            Console.ForegroundColor = segment == StreamSegment.Text ? ConsoleColor.Cyan : ConsoleColor.DarkYellow;
            Console.Write(prefix);
            Console.ResetColor();
            _activeSegment = segment;
        }

        if (!string.IsNullOrEmpty(delta))
        {
            Console.Write(delta);
            Console.Out.Flush();
        }
    }

    private static void WriteSummary(string label, string summary)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{label}]");
        Console.ResetColor();

        if (!string.IsNullOrWhiteSpace(summary))
        {
            Console.Write(' ');
            Console.Write(summary);
        }

        Console.WriteLine();
    }

    private static string BuildSummary(AgUiEvent agUiEvent)
    {
        var parts = new List<string>();

        Append(parts, "threadId", agUiEvent.ThreadId);
        Append(parts, "runId", agUiEvent.RunId);
        Append(parts, "messageId", agUiEvent.MessageId);
        Append(parts, "role", agUiEvent.Role);
        Append(parts, "toolCallId", agUiEvent.ToolCallId);
        Append(parts, "toolCallName", agUiEvent.ToolCallName);
        Append(parts, "code", agUiEvent.Code);
        Append(parts, "message", agUiEvent.Message);
        Append(parts, "delta", SummarizeString(agUiEvent.Delta));
        Append(parts, "content", SummarizeJson(agUiEvent.Content));
        Append(parts, "result", SummarizeJson(agUiEvent.Result));
        Append(parts, "snapshot", SummarizeJson(agUiEvent.Snapshot));
        Append(parts, "stateDelta", SummarizeJson(agUiEvent.DeltaPayload));
        Append(parts, "subtype", agUiEvent.Subtype);
        Append(parts, "entityId", agUiEvent.EntityId);

        if (!string.IsNullOrWhiteSpace(agUiEvent.EncryptedValue))
        {
            parts.Add("encryptedValue=<redacted>");
        }

        return string.Join(", ", parts);
    }

    private static void Append(List<string> parts, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{name}={value}");
        }
    }

    private static void Append(List<string> parts, string name, JsonElement? value)
    {
        var summary = SummarizeJson(value);
        if (!string.IsNullOrWhiteSpace(summary))
        {
            parts.Add($"{name}={summary}");
        }
    }

    private static string? SummarizeJson(JsonElement? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => SummarizeString(value.Value.GetString()),
            _ => SummarizeString(value.Value.GetRawText()),
        };
    }

    private static string? SummarizeString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        const int maxLength = 120;
        var singleLine = value.ReplaceLineEndings(" ").Trim();
        return singleLine.Length <= maxLength
            ? singleLine
            : $"{singleLine[..maxLength]}...";
    }

    private enum StreamSegment
    {
        None,
        Text,
        Reasoning,
    }
}
