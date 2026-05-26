using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DeepSeek;

internal static class DeepSeekStreaming
{
    public static async IAsyncEnumerable<T> ReadServerSentEventsAsync<T>(
        PipelineResponse response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = response.ContentStream ?? throw new InvalidOperationException("The response did not contain a streaming body.");
        using var reader = new StreamReader(stream);
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line is null)
            {
                yield break;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line.Substring(6);
            if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                yield break;
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize<T>(payload, DeepSeekJson.SerializerOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (value is not null)
            {
                yield return value;
            }
        }
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

    public DelegateAsyncCollectionResult(
        Func<CancellationToken, Task<ClientResult>> getPage,
        Func<PipelineResponse, CancellationToken, IAsyncEnumerable<T>> getValues)
    {
        _getPage = getPage;
        _getValues = getValues;
    }

    protected override async IAsyncEnumerable<T> GetValuesFromPageAsync(ClientResult page)
    {
        using var response = page.GetRawResponse();
        await foreach (var item in _getValues(response, CancellationToken.None).ConfigureAwait(false))
        {
            yield return item;
        }
    }

    public override async IAsyncEnumerable<ClientResult> GetRawPagesAsync()
    {
        yield return await _getPage(CancellationToken.None).ConfigureAwait(false);
    }

    public override ContinuationToken? GetContinuationToken(ClientResult page) => null;
}
