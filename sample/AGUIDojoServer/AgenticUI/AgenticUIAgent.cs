using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AGUIDojoServer.AgenticUI;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instantiated by ChatClientAgentFactory.CreateAgenticUI")]
internal sealed class AgenticUIAgent : DelegatingAIAgent
{
    private readonly JsonSerializerOptions _jsonSerializerOptions;

    public AgenticUIAgent(AIAgent innerAgent, JsonSerializerOptions jsonSerializerOptions)
        : base(innerAgent)
    {
        this._jsonSerializerOptions = jsonSerializerOptions;
    }

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        return this.RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var trackedFunctionCalls = new Dictionary<string, FunctionCallContent>();
        var bufferedAssistantMessages = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var bufferedAssistantMessageOrder = new List<string>();
        bool sawPlanToolActivity = false;
        string? lastResponseId = null;
        string? lastAuthorName = null;
        string? lastAgentId = null;

        await foreach (var update in this.InnerAgent.RunStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
        {
            lastResponseId = update.ResponseId ?? lastResponseId;
            lastAuthorName = update.AuthorName ?? lastAuthorName;
            lastAgentId = update.AgentId ?? lastAgentId;

            List<AIContent> forwardedContents = new();
            List<AIContent> stateEventsToEmit = new();
            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent callContent)
                {
                    if (callContent.Name == "create_plan" || callContent.Name == "update_plan_step")
                    {
                        trackedFunctionCalls[callContent.CallId] = callContent;
                        sawPlanToolActivity = true;
                    }

                    forwardedContents.Add(content);
                }
                else if (content is FunctionResultContent resultContent)
                {
                    forwardedContents.Add(content);

                    // Check if this result matches a tracked function call
                    if (trackedFunctionCalls.Remove(resultContent.CallId, out var matchedCall))
                    {
                        sawPlanToolActivity = true;
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(resultContent.Result, this._jsonSerializerOptions);

                        // Determine event type based on the function name
                        if (matchedCall.Name == "create_plan")
                        {
                            stateEventsToEmit.Add(new DataContent(bytes, "application/json"));
                        }
                        else if (matchedCall.Name == "update_plan_step")
                        {
                            stateEventsToEmit.Add(new DataContent(bytes, "application/json-patch+json"));
                        }
                    }
                }
                else if (update.Role == ChatRole.Assistant && content is TextContent textContent)
                {
                    BufferAssistantText(update.MessageId, textContent.Text, bufferedAssistantMessages, bufferedAssistantMessageOrder);
                }
                else
                {
                    forwardedContents.Add(content);
                }
            }

            if (forwardedContents.Count > 0)
            {
                yield return CloneUpdate(update, forwardedContents);
            }

            if (stateEventsToEmit.Count == 0)
            {
                continue;
            }

            yield return new AgentResponseUpdate(ChatRole.System, stateEventsToEmit)
            {
                MessageId = "delta_" + Guid.NewGuid().ToString("N"),
                CreatedAt = update.CreatedAt,
                ResponseId = update.ResponseId,
                AuthorName = update.AuthorName,
                AdditionalProperties = update.AdditionalProperties,
                AgentId = update.AgentId,
            };
        }

        if (!sawPlanToolActivity)
        {
            yield break;
        }

        string finalAssistantText = BuildFinalAssistantText(bufferedAssistantMessages, bufferedAssistantMessageOrder);
        yield return new AgentResponseUpdate(ChatRole.Assistant, finalAssistantText)
        {
            MessageId = "message_" + Guid.NewGuid().ToString("N"),
            CreatedAt = DateTimeOffset.UtcNow,
            ResponseId = lastResponseId,
            AuthorName = lastAuthorName,
            AgentId = lastAgentId ?? this.Id,
        };
    }

    private static AgentResponseUpdate CloneUpdate(AgentResponseUpdate update, IReadOnlyList<AIContent> contents)
        => new(update.Role, [.. contents])
        {
            MessageId = update.MessageId,
            CreatedAt = update.CreatedAt,
            ResponseId = update.ResponseId,
            AuthorName = update.AuthorName,
            AdditionalProperties = update.AdditionalProperties,
            AgentId = update.AgentId,
        };

    private static void BufferAssistantText(
        string? messageId,
        string? text,
        IDictionary<string, StringBuilder> bufferedAssistantMessages,
        ICollection<string> bufferedAssistantMessageOrder)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        string key = string.IsNullOrWhiteSpace(messageId) ? $"assistant_{bufferedAssistantMessageOrder.Count}" : messageId;
        if (!bufferedAssistantMessages.TryGetValue(key, out var builder))
        {
            builder = new StringBuilder();
            bufferedAssistantMessages[key] = builder;
            bufferedAssistantMessageOrder.Add(key);
        }

        builder.Append(text);
    }

    private static string BuildFinalAssistantText(
        IReadOnlyDictionary<string, StringBuilder> bufferedAssistantMessages,
        IReadOnlyList<string> bufferedAssistantMessageOrder)
    {
        for (int i = bufferedAssistantMessageOrder.Count - 1; i >= 0; i--)
        {
            if (!bufferedAssistantMessages.TryGetValue(bufferedAssistantMessageOrder[i], out var builder))
            {
                continue;
            }

            string candidate = NormalizeAssistantText(builder.ToString());
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }

        return "The plan has been fully completed.";
    }

    private static string NormalizeAssistantText(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || LooksLikePlanSummary(trimmed))
        {
            return string.Empty;
        }

        string collapsed = string.Join(" ", trimmed
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return collapsed.Length <= 160
            ? collapsed
            : "The plan has been fully completed.";
    }

    private static bool LooksLikePlanSummary(string text)
    {
        string[] lines = text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int tableLineCount = lines.Count(static line => line.Contains('|'));
        int listLineCount = lines.Count(static line => line.StartsWith("- ", StringComparison.Ordinal)
            || line.StartsWith("* ", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+\.\s+"));
        int statusTokenCount = System.Text.RegularExpressions.Regex.Matches(
            text,
            @"\b(pending|completed)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

        return tableLineCount >= 2
            || (listLineCount >= 2 && statusTokenCount >= 2)
            || (lines.Length >= 4 && statusTokenCount >= 3);
    }
}
