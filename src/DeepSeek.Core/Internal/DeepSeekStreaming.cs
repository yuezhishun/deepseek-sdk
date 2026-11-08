using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepSeek.Responses;
using DeepSeek.Anthropic;

namespace DeepSeek;

internal static class DeepSeekStreaming
{
    public static async IAsyncEnumerable<T> ReadServerSentEventsAsync<T>(
        PipelineResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = response.ContentStream ?? throw new InvalidOperationException("The response did not contain a streaming body.");
        using var reader = new StreamReader(stream);
        using var cancellation = cancellationToken.Register(static state => ((Stream)state!).Dispose(), stream);
        string? eventName = null;
        var data = new System.Text.StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line;
            try
            {
                line = await reader.ReadLineAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (line is null)
            {
                if (data.Length > 0)
                {
                    var last = DeserializeEvent<T>(eventName, data.ToString());
                    if (last.HasValue)
                    {
                        yield return last.Value!;
                    }
                }

                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (line.Length == 0 || string.IsNullOrWhiteSpace(line))
            {
                if (data.Length > 0)
                {
                    var current = DeserializeEvent<T>(eventName, data.ToString());
                    data.Clear();
                    eventName = null;
                    if (current.IsTerminal)
                    {
                        if (current.HasValue)
                        {
                            yield return current.Value!;
                        }

                        yield break;
                    }

                    if (current.HasValue)
                    {
                        yield return current.Value!;
                    }
                }

                continue;
            }

            if (line.StartsWith(":", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line.Substring(6).Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            if (data.Length > 0)
            {
                data.Append('\n');
            }

            data.Append(line.Length > 5 && line[5] == ' ' ? line.Substring(6) : line.Substring(5));
        }
    }

    private static ParsedEvent<T> DeserializeEvent<T>(string? eventName, string payload)
    {
        if (string.Equals(payload.Trim(), "[DONE]", StringComparison.Ordinal))
        {
            return new ParsedEvent<T>(default, true, hasValue: false);
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(payload, DeepSeekJson.SerializerOptions);
            if (value is null)
            {
                return new ParsedEvent<T>(default, false, hasValue: false);
            }

            var terminal = false;
            if (value is ResponseStreamEvent responseEvent)
            {
                responseEvent.Event = eventName;
                using var document = JsonDocument.Parse(payload);
                responseEvent.RawData = document.RootElement.Clone();
                if (string.IsNullOrWhiteSpace(responseEvent.Type))
                {
                    responseEvent.Type = eventName;
                }

                terminal = responseEvent.Type is "response.completed" or "response.incomplete" or "response.failed";
            }
            else if (value is AnthropicStreamEvent anthropicEvent)
            {
                if (string.IsNullOrWhiteSpace(anthropicEvent.Type)) anthropicEvent.Type = eventName;
                terminal = anthropicEvent.Type == "message_stop";
            }

            return new ParsedEvent<T>(value, terminal, hasValue: true);
        }
        catch (JsonException)
        {
            return new ParsedEvent<T>(default, false, hasValue: false);
        }
    }

    private readonly struct ParsedEvent<T>
    {
        public ParsedEvent(T? value, bool isTerminal, bool hasValue)
        {
            Value = value;
            IsTerminal = isTerminal;
            HasValue = hasValue;
        }

        public T? Value { get; }
        public bool IsTerminal { get; }
        public bool HasValue { get; }

        public static implicit operator bool(ParsedEvent<T> value) => value.HasValue;
    }
}

internal sealed class DelegateCollectionResult<T> : CollectionResult<T>
{
    private readonly ClientResult _page;

    public DelegateCollectionResult(ClientResult page)
    {
        _page = page;
    }

    protected override IEnumerable<T> GetValuesFromPage(ClientResult page) => throw new NotSupportedException();

    public override IEnumerable<ClientResult> GetRawPages()
    {
        yield return _page;
    }

    public override ContinuationToken? GetContinuationToken(ClientResult page) => null;
}

internal sealed class DelegateAsyncCollectionResult<T> : AsyncCollectionResult<T>
{
    private readonly Func<CancellationToken, Task<ClientResult>> _getPage;
    private readonly Func<PipelineResponse, CancellationToken, IAsyncEnumerable<T>> _getValues;
    private readonly CancellationToken _cancellationToken;

    public DelegateAsyncCollectionResult(
        Func<CancellationToken, Task<ClientResult>> getPage,
        Func<PipelineResponse, CancellationToken, IAsyncEnumerable<T>> getValues,
        CancellationToken cancellationToken = default)
    {
        _getPage = getPage;
        _getValues = getValues;
        _cancellationToken = cancellationToken;
    }

    protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
    {
        using var response = page.GetRawResponse();
        await foreach (var item in _getValues(response, _cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
    {
        yield return await _getPage(_cancellationToken).ConfigureAwait(false);
    }

    public override ContinuationToken? GetContinuationToken(ClientResult page) => null;
}
