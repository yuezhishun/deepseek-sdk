using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DeepSeek.Chat;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace DeepSeek.Agents.AI;

public sealed class DeepSeekChatClient : IChatClient
{
    private const int MaxStreamingToolContinuationTurns = 2;

    private readonly ChatClient _client;
    private readonly ChatOptions? _defaults;

    internal DeepSeekChatClient(ChatClient client, DeepSeekChatClientOptions? options)
    {
        _client = client;
        _defaults = options?.ToChatOptions();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ChatClient) || serviceType.IsAssignableFrom(typeof(ChatClient)))
        {
            return _client;
        }

        if (serviceType == typeof(DeepSeekChatClient))
        {
            return this;
        }

        return null;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = DeepSeekChatRequestMapper.MapToChatRequest(messages, MergeOptions(options), stream: false);
        var response = await _client.CompleteChatAsync(request, new System.ClientModel.Primitives.RequestOptions
        {
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        return DeepSeekChatRequestMapper.MapToChatResponse(response.Value);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<AiChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = MergeOptions(options);
        var conversation = messages.ToList();
        var automaticContinuationTurns = 0;
        var continuedToolCallRounds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            var request = DeepSeekChatRequestMapper.MapToChatRequest(conversation, merged, stream: true);
            var turnState = new StreamingAssistantTurnState();
            ChatCompletion? lastChunk = null;
            string? messageId = null;

            await foreach (var chunk in _client.CompleteChatStreaming(
                request,
                new System.ClientModel.Primitives.RequestOptions { CancellationToken = cancellationToken }).ConfigureAwait(false))
            {
                lastChunk = chunk;
                messageId ??= chunk.Id ?? Guid.NewGuid().ToString("N");
                var choice = chunk.Choices.Count > 0 ? chunk.Choices[0] : null;
                var delta = choice?.Delta;
                var reasoningContent = delta?.ReasoningContent;

                if (reasoningContent is not null && !string.IsNullOrWhiteSpace(reasoningContent))
                {
                    turnState.AppendReasoning(reasoningContent);
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new TextReasoningContent(reasoningContent)])
                    {
                        RawRepresentation = chunk,
                        MessageId = messageId,
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["reasoning_content"] = reasoningContent,
                            ["is_reasoning"] = true,
                        },
                    };
                }

                if (delta?.ToolCalls is { } deltaToolCalls)
                {
                    turnState.ApplyToolCalls(deltaToolCalls);
                }

                var content = delta?.Content;
                if (content is not null && !string.IsNullOrWhiteSpace(content))
                {
                    turnState.AppendContent(content);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, content)
                    {
                        RawRepresentation = chunk,
                        MessageId = messageId,
                    };
                }

                if (chunk.Usage is not null && chunk.Choices.Count == 0)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                    {
                        RawRepresentation = chunk,
                        MessageId = messageId,
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["usage_total_tokens"] = chunk.Usage.TotalTokens,
                            ["usage_prompt_tokens"] = chunk.Usage.PromptTokens,
                            ["usage_completion_tokens"] = chunk.Usage.CompletionTokens,
                        },
                    };
                }
            }

            if (messageId is null || !turnState.TryBuildToolCalls(out var toolCalls))
            {
                yield break;
            }

            var toolCallRoundSignature = CreateToolCallRoundSignature(toolCalls);
            if (ShouldHandleStreamingToolContinuation(merged, toolCalls, automaticContinuationTurns, toolCallRoundSignature, continuedToolCallRounds) &&
                await TryInvokeAllToolsAsync(merged?.Tools, toolCalls, cancellationToken).ConfigureAwait(false) is { } toolResults)
            {
                automaticContinuationTurns++;
                continuedToolCallRounds.Add(toolCallRoundSignature);

                if (turnState.CreateToolCallUpdate(lastChunk, messageId, toolCalls, informationalOnly: true) is { } toolUpdate)
                {
                    yield return toolUpdate;
                }

                foreach (var toolResult in toolResults)
                {
                    yield return new ChatResponseUpdate(ChatRole.Tool, [toolResult])
                    {
                        RawRepresentation = lastChunk,
                        MessageId = messageId,
                    };
                }

                conversation.Add(turnState.CreateAssistantToolCallMessage(toolCalls));
                conversation.AddRange(toolResults.Select(static result => new AiChatMessage(ChatRole.Tool, [result])));
                continue;
            }

            if (turnState.CreateToolCallUpdate(lastChunk, messageId, toolCalls, informationalOnly: false) is { } finalToolUpdate)
            {
                yield return finalToolUpdate;
            }

            yield break;
        }
    }

    public void Dispose()
    {
    }

    private static bool ShouldHandleStreamingToolContinuation(
        ChatOptions? options,
        IReadOnlyList<FunctionCallContent> toolCalls,
        int automaticContinuationTurns,
        string toolCallRoundSignature,
        HashSet<string> continuedToolCallRounds)
        => !(options?.AdditionalProperties?.TryGetValue(DeepSeekChatRequestMapper.DisableStreamingToolContinuationKey, out var value) == true && value is true)
           && toolCalls.Count > 1
           && automaticContinuationTurns < MaxStreamingToolContinuationTurns
           && !continuedToolCallRounds.Contains(toolCallRoundSignature);

    private static string CreateToolCallRoundSignature(IReadOnlyList<FunctionCallContent> toolCalls)
    {
        var builder = new StringBuilder();
        foreach (var toolCall in toolCalls)
        {
            builder.Append(toolCall.Name);
            builder.Append('(');

            if (toolCall.Arguments is not null)
            {
                foreach (var argument in toolCall.Arguments.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append(argument.Key);
                    builder.Append('=');
                    AppendCanonicalValue(builder, argument.Value);
                    builder.Append(';');
                }
            }

            builder.Append(')');
            builder.Append('|');
        }

        return builder.ToString();
    }

    private static void AppendCanonicalValue(StringBuilder builder, object? value)
    {
        value = NormalizeArgumentValue(value);

        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                builder.Append('"');
                builder.Append(text.Replace("\"", "\\\""));
                builder.Append('"');
                break;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;
            case IDictionary<string, object?> dictionary:
                builder.Append('{');
                foreach (var pair in dictionary.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    builder.Append(pair.Key);
                    builder.Append(':');
                    AppendCanonicalValue(builder, pair.Value);
                    builder.Append(';');
                }

                builder.Append('}');
                break;
            case System.Collections.IEnumerable sequence when value is not byte[]:
                builder.Append('[');
                foreach (var item in sequence)
                {
                    AppendCanonicalValue(builder, item);
                    builder.Append(',');
                }

                builder.Append(']');
                break;
            default:
                builder.Append(value);
                break;
        }
    }

    private static async Task<List<FunctionResultContent>?> TryInvokeAllToolsAsync(
        IList<AITool>? tools,
        IReadOnlyList<FunctionCallContent> toolCalls,
        CancellationToken cancellationToken)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var functionLookup = tools
            .Select(static tool => tool.GetService<AIFunction>())
            .OfType<AIFunction>()
            .ToDictionary(static tool => tool.Name, StringComparer.Ordinal);

        if (functionLookup.Count == 0)
        {
            return null;
        }

        var results = new List<FunctionResultContent>(toolCalls.Count);
        foreach (var toolCall in toolCalls)
        {
            if (!functionLookup.TryGetValue(toolCall.Name, out var tool))
            {
                return null;
            }

            var arguments = new AIFunctionArguments();
            foreach (var argument in toolCall.Arguments ?? new Dictionary<string, object?>())
            {
                arguments[argument.Key] = NormalizeArgumentValue(argument.Value);
            }

            var result = await tool.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            results.Add(new FunctionResultContent(toolCall.CallId, result));
        }

        return results;
    }

    private static object? NormalizeArgumentValue(object? value)
        => value is JsonElement element ? NormalizeJsonElement(element) : value;

    private static object? NormalizeJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(static p => p.Name, static p => NormalizeJsonElement(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(NormalizeJsonElement).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var i32) => i32,
            JsonValueKind.Number when element.TryGetInt64(out var i64) => i64,
            JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };
    }

    private ChatOptions? MergeOptions(ChatOptions? options)
        => _defaults is null ? options : DeepSeekChatRequestMapper.MergeChatOptions(_defaults, options);

    private sealed class StreamingAssistantTurnState
    {
        private readonly Dictionary<int, StreamingToolCallState> _toolCallStates = [];
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoning = new();

        public void AppendContent(string content) => _content.Append(content);
        public void AppendReasoning(string reasoning) => _reasoning.Append(reasoning);

        public void ApplyToolCalls(IList<ToolCall> toolCalls)
        {
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var toolCall = toolCalls[i];
                var toolIndex = toolCall.Index ?? i;
                if (!_toolCallStates.TryGetValue(toolIndex, out var state))
                {
                    state = new StreamingToolCallState();
                    _toolCallStates[toolIndex] = state;
                }

                state.Apply(toolCall);
            }
        }

        public bool TryBuildToolCalls(out List<FunctionCallContent> toolCalls)
        {
            toolCalls = [];
            if (_toolCallStates.Count == 0)
            {
                return false;
            }

            foreach (var state in _toolCallStates.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
            {
                var name = state.Name;
                if (name is null || string.IsNullOrWhiteSpace(name) || !state.HasArguments)
                {
                    toolCalls = [];
                    return false;
                }

                Dictionary<string, object?> arguments;
                try
                {
                    arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(state.Arguments.ToString()) ?? [];
                }
                catch (JsonException)
                {
                    toolCalls = [];
                    return false;
                }

                toolCalls.Add(new FunctionCallContent(state.Id ?? string.Empty, name, arguments));
            }

            return true;
        }

        public AiChatMessage CreateAssistantToolCallMessage(IReadOnlyList<FunctionCallContent> toolCalls)
        {
            var assistant = new AiChatMessage(ChatRole.Assistant, _content.ToString());
            if (_reasoning.Length > 0)
            {
                assistant.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_content"] = _reasoning.ToString(),
                };
            }

            foreach (var toolCall in toolCalls)
            {
                assistant.Contents.Add(toolCall);
            }

            return assistant;
        }

        public ChatResponseUpdate? CreateToolCallUpdate(
            ChatCompletion? chunk,
            string messageId,
            IReadOnlyList<FunctionCallContent> toolCalls,
            bool informationalOnly)
        {
            if (toolCalls.Count == 0)
            {
                return null;
            }

            List<AIContent> contents = [];
            foreach (var toolCall in toolCalls)
            {
                contents.Add(new FunctionCallContent(toolCall.CallId, toolCall.Name, toolCall.Arguments)
                {
                    InformationalOnly = informationalOnly,
                });
            }

            var update = new ChatResponseUpdate(ChatRole.Assistant, contents)
            {
                RawRepresentation = chunk,
                MessageId = messageId,
            };

            if (_reasoning.Length > 0)
            {
                update.AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    ["reasoning_content"] = _reasoning.ToString(),
                };
            }

            _toolCallStates.Clear();
            return update;
        }
    }

    private sealed class StreamingToolCallState
    {
        public string? Id { get; private set; }
        public string? Name { get; private set; }
        public bool HasArguments { get; private set; }
        public StringBuilder Arguments { get; } = new();

        public void Apply(ToolCall toolCall)
        {
            if (string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(toolCall.Id))
            {
                Id = toolCall.Id;
            }

            var function = toolCall.Function;
            var name = function?.Name;
            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(name))
            {
                Name = name;
            }

            if (function?.Arguments is { } arguments)
            {
                Arguments.Append(arguments);
                HasArguments = true;
            }
        }
    }
}
