using System.Runtime.CompilerServices;
using DeepSeek.Anthropic;
using Microsoft.Extensions.AI;

namespace Microsoft.Agents.AI.DeepSeek;

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
        var request = DeepSeekAnthropicRequestMapper.MapRequest(messages, MergeOptions(options), stream: false);
        var response = await _client.CreateMessageAsync(request, new System.ClientModel.Primitives.RequestOptions
        {
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        return DeepSeekAnthropicRequestMapper.MapResponse(response.Value);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = DeepSeekAnthropicRequestMapper.MapRequest(messages, MergeOptions(options), stream: true);
        await foreach (var chunk in _client.CreateMessageStreaming(
            request,
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = cancellationToken }).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Delta?.Thinking))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(chunk.Delta.Thinking)])
                {
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["reasoning_content"] = chunk.Delta.Thinking,
                        ["is_reasoning"] = true,
                    },
                    RawRepresentation = chunk,
                };
            }

            if (!string.IsNullOrWhiteSpace(chunk.Delta?.Text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, chunk.Delta.Text)
                {
                    RawRepresentation = chunk,
                };
            }
        }
    }

    public void Dispose()
    {
    }

    private ChatOptions? MergeOptions(ChatOptions? options)
        => _defaults is null ? options : DeepSeekChatRequestMapper.MergeChatOptions(_defaults, options);
}
