using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIDojoServer;

internal sealed class CustomStreamingAgent : AIAgent
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public CustomStreamingAgent(JsonSerializerOptions jsonSerializerOptions)
    {
        _jsonSerializerOptions = jsonSerializerOptions;
    }

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(new CustomStreamingSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session.StateBag, jsonSerializerOptions ?? _jsonSerializerOptions));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
    {
        AgentSessionStateBag stateBag = serializedState.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? new AgentSessionStateBag()
            : serializedState.Deserialize<AgentSessionStateBag>(jsonSerializerOptions ?? _jsonSerializerOptions) ?? new AgentSessionStateBag();

        return ValueTask.FromResult<AgentSession>(new CustomStreamingSession(stateBag));
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string responseId = "response_" + Guid.NewGuid().ToString("N");
        string introMessageId = "message_" + Guid.NewGuid().ToString("N");
        string toolSummaryMessageId = "message_" + Guid.NewGuid().ToString("N");
        string toolResultMessageId = "message_" + Guid.NewGuid().ToString("N");
        string stateMessageId = "state_" + Guid.NewGuid().ToString("N");
        string finalMessageId = "message_" + Guid.NewGuid().ToString("N");
        string toolCallId = "tool_" + Guid.NewGuid().ToString("N");

        bool simulateError = messages
            .LastOrDefault(static message => message.Role == ChatRole.User)?
            .Text?.Contains("/error", StringComparison.OrdinalIgnoreCase) == true;

        yield return CreateTextUpdate(
            introMessageId,
            responseId,
            "CustomStreamingAgent demo started. ",
            ChatRole.Assistant);

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        yield return CreateTextUpdate(
            introMessageId,
            responseId,
            "This run emits text, tool-call, tool-result, state snapshot, and state delta events.",
            ChatRole.Assistant);

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        yield return new AgentResponseUpdate(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    toolCallId,
                    "render_status_panel",
                    new Dictionary<string, object?>
                    {
                        ["title"] = "Build deployment",
                        ["status"] = "running",
                        ["progress"] = 35,
                        ["items"] = new[] { "restore dependencies", "compile project", "publish artifact" },
                    }),
            ])
        {
            MessageId = toolSummaryMessageId,
            ResponseId = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = Id,
        };

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        var snapshot = new CustomStreamingState
        {
            Phase = "tool-called",
            Progress = 35,
            ActiveStep = "compile project",
            Items =
            [
                "restore dependencies",
                "compile project",
                "publish artifact",
            ],
        };

        yield return CreateDataUpdate(
            stateMessageId,
            responseId,
            snapshot,
            "application/json");

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        var patch = new object[]
        {
            new Dictionary<string, object?>
            {
                ["op"] = "replace",
                ["path"] = "/phase",
                ["value"] = "tool-result-received",
            },
            new Dictionary<string, object?>
            {
                ["op"] = "replace",
                ["path"] = "/progress",
                ["value"] = 100,
            },
            new Dictionary<string, object?>
            {
                ["op"] = "replace",
                ["path"] = "/activeStep",
                ["value"] = "publish artifact",
            },
        };

        yield return CreateDataUpdate(
            stateMessageId,
            responseId,
            patch,
            "application/json-patch+json");

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        yield return new AgentResponseUpdate(
            ChatRole.Tool,
            [
                new FunctionResultContent(
                    toolCallId,
                    new Dictionary<string, object?>
                    {
                        ["ok"] = true,
                        ["panelId"] = "deploy-status-panel",
                        ["renderedAt"] = DateTimeOffset.UtcNow,
                    }),
            ])
        {
            MessageId = toolResultMessageId,
            ResponseId = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = Id,
        };

        if (simulateError)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("CustomStreamingAgent simulated failure. Remove `/error` from the last user message to complete the run normally.");
        }

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        yield return CreateTextUpdate(
            finalMessageId,
            responseId,
            "Tool execution completed and frontend state is synchronized. ",
            ChatRole.Assistant);

        await Task.Delay(150, cancellationToken).ConfigureAwait(false);

        yield return CreateTextUpdate(
            finalMessageId,
            responseId,
            "Send `/error` as the last user message if you also want to observe a RUN_ERROR event.",
            ChatRole.Assistant);
    }

    private AgentResponseUpdate CreateTextUpdate(string messageId, string responseId, string text, ChatRole role)
        => new(role, text)
        {
            MessageId = messageId,
            ResponseId = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = Id,
        };

    private AgentResponseUpdate CreateDataUpdate(string messageId, string responseId, object payload, string mediaType)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, _jsonSerializerOptions);

        return new AgentResponseUpdate(
            ChatRole.System,
            [new DataContent(bytes, mediaType)])
        {
            MessageId = messageId,
            ResponseId = responseId,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentId = Id,
        };
    }

    internal sealed class CustomStreamingState
    {
        public required string Phase { get; set; }

        public required int Progress { get; set; }

        public required string ActiveStep { get; set; }

        public required List<string> Items { get; set; }
    }

    private sealed class CustomStreamingSession : AgentSession
    {
        public CustomStreamingSession()
        {
        }

        public CustomStreamingSession(AgentSessionStateBag stateBag)
            : base(stateBag)
        {
        }
    }
}
