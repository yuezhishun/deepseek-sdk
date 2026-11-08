using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeepSeek.Anthropic;
using Microsoft.Extensions.AI;

namespace DeepSeek.Agents.AI;

public sealed class DeepSeekAnthropicChatClient : IChatClient
{
    private readonly AnthropicClient _client;
    private readonly ChatOptions? _defaults;

    internal DeepSeekAnthropicChatClient(AnthropicClient client, DeepSeekChatClientOptions? options)
    {
        _client = client;
        _defaults = options?.ToChatOptions();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(AnthropicClient) || serviceType.IsAssignableFrom(typeof(AnthropicClient)))
        {
            return _client;
        }

        if (serviceType == typeof(DeepSeekAnthropicChatClient))
        {
            return this;
        }

        return null;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var merged = MergeOptions(options);
        var request = DeepSeekAnthropicRequestMapper.MapRequest(messages, merged, stream: false);
        var response = await _client.CreateMessageAsync(request, new System.ClientModel.Primitives.RequestOptions
        {
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        var result = DeepSeekAnthropicRequestMapper.MapResponse(response.Value);
        DeepSeekChatRequestMapper.ValidateJsonResponse(result.Text, merged, response.Value,
            response.Value.StopReason is "end_turn" or "stop_sequence" or "tool_use",
            response.Value.Content.Any(static block => block.Type == "tool_use"));
        return result;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = MergeOptions(options);
        var request = DeepSeekAnthropicRequestMapper.MapRequest(messages, merged, stream: true);
        var blocks = new SortedDictionary<int, AnthropicContentBlock>();
        var arguments = new Dictionary<int, StringBuilder>();
        var active = new HashSet<int>();
        var emittedCalls = new HashSet<string>(StringComparer.Ordinal);
        var response = new AnthropicMessageResponse { Model = _client.Model };
        var usage = new AnthropicUsage();
        var hasUsage = false;
        await foreach (var chunk in _client.CreateMessageStreaming(
            request,
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = cancellationToken }).ConfigureAwait(false))
        {
            if (chunk.Type == "message_start" && chunk.Message is { } start)
            {
                response = start;
                if (start.Usage is not null)
                {
                    MergeUsage(usage, start.Usage);
                    hasUsage = true;
                }
            }

            if (chunk.Usage is not null)
            {
                MergeUsage(usage, chunk.Usage);
                hasUsage = true;
            }

            var index = chunk.Index ?? 0;
            if (chunk.Type == "content_block_start" && chunk.ContentBlock is { } block)
            {
                blocks[index] = block;
                active.Add(index);
                if (block.Type is "tool_use" or "server_tool_use") arguments[index] = new StringBuilder();
            }

            var delta = chunk.Delta;
            if (delta?.StopReason is not null) response.StopReason = delta.StopReason;
            if (delta?.StopSequence is not null) response.StopSequence = delta.StopSequence;
            if (delta?.PartialJson is { } fragment && arguments.TryGetValue(index, out var buffer)) buffer.Append(fragment);
            if (blocks.TryGetValue(index, out var current))
            {
                if (delta?.Thinking is { } thinkingDelta) current.Thinking = (current.Thinking ?? string.Empty) + thinkingDelta;
                if (delta?.Text is { } textDelta) current.Text = (current.Text ?? string.Empty) + textDelta;
                if (delta?.Signature is { } signature) current.Signature = signature;
            }

            var thinking = delta?.Thinking;
            if (!string.IsNullOrWhiteSpace(thinking))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(thinking)])
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["reasoning_content"] = thinking,
                        ["is_reasoning"] = true,
                    },
                    RawRepresentation = chunk,
                };
            }

            var text = delta?.Text;
            if (!string.IsNullOrEmpty(text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    RawRepresentation = chunk,
                };
            }

            if (chunk.Type == "content_block_stop" && active.Remove(index)
                && blocks.TryGetValue(index, out var completed))
            {
                if (arguments.TryGetValue(index, out var json) && json.Length > 0)
                {
                    try
                    {
                        completed.Input = JsonNode.Parse(json.ToString());
                    }
                    catch (JsonException exception)
                    {
                        throw new JsonException("The Anthropic tool arguments are not complete JSON.", exception);
                    }
                }

                if (completed.Type == "tool_use" && emittedCalls.Add(completed.Id ?? index.ToString()))
                {
                    var call = new FunctionCallContent(completed.Id ?? string.Empty, completed.Name ?? string.Empty,
                        JsonSerializer.Deserialize<Dictionary<string, object?>>(completed.Input?.ToJsonString() ?? "{}") ?? [])
                    {
                        RawRepresentation = completed,
                    };
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [call]) { RawRepresentation = chunk };
                }
            }

            if (chunk.Type == "error")
            {
                throw new InvalidDataException("The Anthropic stream returned an error event.");
            }

            if (chunk.Type == "message_stop")
            {
                if (active.Count > 0) throw new InvalidDataException("The Anthropic message ended with incomplete content blocks.");
                if (blocks.Count > 0) response.Content = blocks.Values.ToList();
                response.Usage = hasUsage ? usage : null;
                DeepSeekChatRequestMapper.ValidateJsonResponse(
                    string.Concat(response.Content.Where(static block => block.Type == "text").Select(static block => block.Text)),
                    merged, response, response.StopReason is "end_turn" or "stop_sequence" or "tool_use",
                    response.Content.Any(static block => block.Type == "tool_use"));
                var update = new ChatResponseUpdate(ChatRole.Assistant, (string?)null)
                {
                    RawRepresentation = response,
                    AdditionalProperties = new AdditionalPropertiesDictionary { ["finish_reason"] = response.StopReason },
                };
                if (hasUsage) update.Contents.Add(new UsageContent(DeepSeekAnthropicRequestMapper.MapUsage(usage)));
                if (response.Content.Count > 0) update.Contents.Add(new AIContent { RawRepresentation = response });
                yield return update;
                yield break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidDataException("The Anthropic stream ended before message_stop.");
    }

    public void Dispose()
    {
    }

    private ChatOptions? MergeOptions(ChatOptions? options)
        => _defaults is null ? options : DeepSeekChatRequestMapper.MergeChatOptions(_defaults, options);

    private static void MergeUsage(AnthropicUsage target, AnthropicUsage update)
    {
        target.InputTokens = update.InputTokens ?? target.InputTokens;
        target.OutputTokens = update.OutputTokens ?? target.OutputTokens;
        target.CacheReadInputTokens = update.CacheReadInputTokens ?? target.CacheReadInputTokens;
        target.CacheCreationInputTokens = update.CacheCreationInputTokens ?? target.CacheCreationInputTokens;
        if (update.ServerToolUse is not null)
        {
            target.ServerToolUse ??= new Dictionary<string, long>();
            foreach (var count in update.ServerToolUse) target.ServerToolUse[count.Key] = count.Value;
        }
    }
}
