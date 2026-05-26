using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DeepSeek.Chat;
using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Microsoft.Agents.AI.DeepSeek;

public sealed class DeepSeekChatClient : IChatClient
{
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

                if (!string.IsNullOrWhiteSpace(choice?.Delta?.ReasoningContent))
                {
                    turnState.AppendReasoning(choice.Delta.ReasoningContent!);
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new TextReasoningContent(choice.Delta.ReasoningContent!)])
                    {
                        RawRepresentation = chunk,
                        MessageId = messageId,
                        AdditionalProperties = new AdditionalPropertiesDictionary
                        {
                            ["reasoning_content"] = choice.Delta.ReasoningContent,
                            ["is_reasoning"] = true,
                        },
                    };
                }

                if (choice?.Delta?.ToolCalls is not null)
                {
                    turnState.ApplyToolCalls(choice.Delta.ToolCalls);
                }

                if (!string.IsNullOrWhiteSpace(choice?.Delta?.Content))
                {
                    turnState.AppendContent(choice.Delta.Content!);
                    yield return new ChatResponseUpdate(ChatRole.Assistant, choice.Delta.Content!)
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

            if (toolCalls.Count > 1 &&
                ShouldHandleStreamingToolContinuation(merged) &&
                await TryInvokeAllToolsAsync(merged?.Tools, toolCalls, cancellationToken).ConfigureAwait(false) is { } toolResults)
            {
                foreach (var toolUpdate in turnState.CreateToolCallUpdates(lastChunk, messageId, toolCalls, informationalOnly: true))
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

            foreach (var toolUpdate in turnState.CreateToolCallUpdates(lastChunk, messageId, toolCalls, informationalOnly: false))
            {
                yield return toolUpdate;
            }

            yield break;
        }
    }

    public void Dispose()
    {
    }

    private static bool ShouldHandleStreamingToolContinuation(ChatOptions? options)
        => !(options?.AdditionalProperties?.TryGetValue(DeepSeekChatRequestMapper.DisableStreamingToolContinuationKey, out var value) == true && value is true);

    private static async Task<List<FunctionResultContent>?> TryInvokeAllToolsAsync(
        IList<AITool>? tools,
        IReadOnlyList<FunctionCallContent> toolCalls,
        CancellationToken cancellationToken)
    {
        if (tools is null || tools.Count == 0)
        {
            return null;
        }

        var functionLookup = tools.OfType<AIFunction>().ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
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
                if (string.IsNullOrWhiteSpace(state.Name) || !state.HasArguments)
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

                toolCalls.Add(new FunctionCallContent(state.Id ?? string.Empty, state.Name!, arguments));
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

        public IReadOnlyList<ChatResponseUpdate> CreateToolCallUpdates(
            ChatCompletion? chunk,
            string messageId,
            IReadOnlyList<FunctionCallContent> toolCalls,
            bool informationalOnly)
        {
            if (toolCalls.Count == 0)
            {
                return [];
            }

            var updates = new List<ChatResponseUpdate>(toolCalls.Count);
            for (var i = 0; i < toolCalls.Count; i++)
            {
                var functionCall = new FunctionCallContent(toolCalls[i].CallId, toolCalls[i].Name, toolCalls[i].Arguments)
                {
                    InformationalOnly = informationalOnly,
                };

                var update = new ChatResponseUpdate(ChatRole.Assistant, [functionCall])
                {
                    RawRepresentation = chunk,
                    MessageId = messageId,
                };

                if (i == toolCalls.Count - 1 && _reasoning.Length > 0)
                {
                    update.AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["reasoning_content"] = _reasoning.ToString(),
                    };
                }

                updates.Add(update);
            }

            _toolCallStates.Clear();
            return updates;
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

            if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(toolCall.Function?.Name))
            {
                Name = toolCall.Function.Name;
            }

            if (toolCall.Function?.Arguments is not null)
            {
                Arguments.Append(toolCall.Function.Arguments);
                HasArguments = true;
            }
        }
    }
}
