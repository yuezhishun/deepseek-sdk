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
            var delta = chunk.Delta;
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
            if (!string.IsNullOrWhiteSpace(text))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, text)
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
