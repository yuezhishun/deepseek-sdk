using System.Runtime.CompilerServices;
using DeepSeek.Responses;
using Microsoft.Extensions.AI;

namespace DeepSeek.Agents.AI;

/// <summary>Adapts the independent DeepSeek Responses API client to Microsoft.Extensions.AI.</summary>
public sealed class DeepSeekResponsesChatClient : IChatClient
{
    private readonly ResponsesClient _client;
    private readonly ChatOptions? _defaults;

    internal DeepSeekResponsesChatClient(ResponsesClient client, DeepSeekChatClientOptions? options)
    {
        _client = client;
        _defaults = options?.ToChatOptions();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(ResponsesClient) || serviceType.IsAssignableFrom(typeof(ResponsesClient)))
        {
            return _client;
        }

        if (serviceType == typeof(DeepSeekResponsesChatClient))
        {
            return this;
        }

        return null;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var request = DeepSeekResponsesRequestMapper.MapRequest(messages, MergeOptions(options), stream: false);
        var response = await _client.CreateResponseAsync(request, new System.ClientModel.Primitives.RequestOptions
        {
            CancellationToken = cancellationToken,
        }).ConfigureAwait(false);
        return DeepSeekResponsesRequestMapper.MapResponse(response.Value);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = DeepSeekResponsesRequestMapper.MapRequest(messages, MergeOptions(options), stream: true);
        var functionCalls = new Dictionary<string, ResponseOutputItem>(StringComparer.Ordinal);
        var emittedFunctionCalls = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var eventData in _client.CreateResponseStreaming(
            request,
            new System.ClientModel.Primitives.RequestOptions { CancellationToken = cancellationToken }).ConfigureAwait(false))
        {
            var type = eventData.Type ?? eventData.Event;
            if (type == "response.output_text.delta" && !string.IsNullOrEmpty(eventData.Delta))
            {
                yield return DeepSeekResponsesRequestMapper.MapTextUpdate(eventData, eventData.Delta!);
                continue;
            }

            if (type == "response.reasoning_text.delta" && !string.IsNullOrEmpty(eventData.Delta))
            {
                yield return DeepSeekResponsesRequestMapper.MapReasoningUpdate(eventData, eventData.Delta!);
                continue;
            }

            if (type == "response.output_item.added" && eventData.Item is { } addedItem)
            {
                var itemKey = eventData.ItemId ?? addedItem.Id ?? Guid.NewGuid().ToString("N");
                functionCalls[itemKey] = addedItem;
                if (addedItem.Type is not "message" and not "reasoning" and not "function_call")
                {
                    yield return DeepSeekResponsesRequestMapper.MapRawEvent(eventData);
                }

                continue;
            }

            if (type == "response.function_call_arguments.delta")
            {
                if (eventData.ItemId is { } itemId && functionCalls.TryGetValue(itemId, out var call))
                {
                    call.Arguments = (call.Arguments ?? string.Empty) + (eventData.Delta ?? string.Empty);
                }

                continue;
            }

            if (type == "response.function_call_arguments.done" || type == "response.output_item.done")
            {
                var item = eventData.Item;
                if (item is null && eventData.ItemId is { } itemId)
                {
                    functionCalls.TryGetValue(itemId, out item);
                }

                if (item is null && type == "response.function_call_arguments.done")
                {
                    item = new ResponseFunctionCallOutputItem
                    {
                        Type = "function_call",
                        Id = eventData.ItemId,
                        CallId = eventData.CallId,
                        Name = eventData.Name,
                        Arguments = eventData.Arguments ?? eventData.Delta,
                    };
                }

                if (item?.Type == "function_call")
                {
                    item.Arguments = eventData.Arguments ?? item.Arguments ?? eventData.Delta;
                    var callId = item.CallId ?? item.Id ?? eventData.ItemId;
                    if (callId is null || emittedFunctionCalls.Add(callId))
                    {
                        yield return DeepSeekResponsesRequestMapper.MapFunctionCallUpdate(eventData, item);
                    }
                }
                else if (item?.Type == "function_call_output")
                {
                    yield return DeepSeekResponsesRequestMapper.MapFunctionResultUpdate(eventData, item);
                }

                continue;
            }

            if (type is "response.completed" or "response.incomplete" or "response.failed")
            {
                yield return DeepSeekResponsesRequestMapper.MapFinalUpdate(eventData);
                continue;
            }

            yield return DeepSeekResponsesRequestMapper.MapRawEvent(eventData);
        }
    }

    public void Dispose()
    {
    }

    private ChatOptions? MergeOptions(ChatOptions? options)
        => _defaults is null ? options : DeepSeekChatRequestMapper.MergeChatOptions(_defaults, options);
}
