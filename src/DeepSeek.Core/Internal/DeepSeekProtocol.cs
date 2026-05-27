using System.ClientModel;
using System.ClientModel.Primitives;
using System.Reflection;
using System.Text.Json;

namespace DeepSeek;

internal static class DeepSeekProtocol
{
    private static readonly FieldInfo? HeadersUpdatesField = typeof(RequestOptions).GetField("_headersUpdates", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PerCallPoliciesField = typeof(RequestOptions).GetField("_perCallPolicies", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? PerTryPoliciesField = typeof(RequestOptions).GetField("_perTryPolicies", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? BeforeTransportPoliciesField = typeof(RequestOptions).GetField("_beforeTransportPolicies", BindingFlags.Instance | BindingFlags.NonPublic);

    public static PipelineMessage CreateRequest(
        ClientPipeline pipeline,
        Uri uri,
        string method,
        object? content,
        RequestOptions? options,
        bool streamResponse = false,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var message = pipeline.CreateMessage(uri, method, PipelineMessageClassifier.Default);
        if (content is not null)
        {
            message.Request.Content = BinaryContent.CreateJson(content, DeepSeekJson.SerializerOptions);
            message.Request.Headers.Set("Content-Type", "application/json");
        }

        if (headers is not null)
        {
            foreach (var pair in headers)
            {
                message.Request.Headers.Set(pair.Key, pair.Value);
            }
        }

        var appliedOptions = CloneRequestOptions(options);
        appliedOptions.ErrorOptions = ClientErrorBehaviors.NoThrow;
        if (streamResponse)
        {
            appliedOptions.BufferResponse = false;
        }

        message.Apply(appliedOptions);
        return message;
    }

    public static RequestOptions GetBufferedRequestOptions(RequestOptions? options, string operationName)
    {
        var appliedOptions = CloneRequestOptions(options);
        if (appliedOptions.BufferResponse is false)
        {
            throw new InvalidOperationException($"'RequestOptions.BufferResponse' must be 'true' when calling '{operationName}'.");
        }

        return appliedOptions;
    }

    public static RequestOptions GetStreamingRequestOptions(RequestOptions? options, CancellationToken cancellationToken)
    {
        var appliedOptions = CloneRequestOptions(options);
        appliedOptions.BufferResponse = false;
        if (cancellationToken.CanBeCanceled)
        {
            appliedOptions.CancellationToken = cancellationToken;
        }
        return appliedOptions;
    }

    private static RequestOptions CloneRequestOptions(RequestOptions? options)
    {
        if (options is null)
        {
            return new RequestOptions();
        }

        var clone = new RequestOptions
        {
            CancellationToken = options.CancellationToken,
            ErrorOptions = options.ErrorOptions,
            BufferResponse = options.BufferResponse,
        };

        CopyFieldValue(HeadersUpdatesField, options, clone);
        CopyFieldValue(PerCallPoliciesField, options, clone);
        CopyFieldValue(PerTryPoliciesField, options, clone);
        CopyFieldValue(BeforeTransportPoliciesField, options, clone);
        return clone;
    }

    private static void CopyFieldValue(FieldInfo? field, RequestOptions source, RequestOptions target)
    {
        if (field?.GetValue(source) is not { } value)
        {
            return;
        }

        object clonedValue = value switch
        {
            Array array => array.Clone(),
            System.Collections.IList list => Activator.CreateInstance(field.FieldType, list) ?? value,
            _ => value,
        };

        field.SetValue(target, clonedValue);
    }

    public static ClientResult Send(ClientPipeline pipeline, PipelineMessage message, Uri requestUri)
    {
        pipeline.Send(message);
        var response = message.ExtractResponse() ?? throw new InvalidOperationException("The pipeline did not return a response.");
        EnsureSuccess(response, requestUri);
        return ClientResult.FromResponse(response);
    }

    public static async Task<ClientResult> SendAsync(ClientPipeline pipeline, PipelineMessage message, Uri requestUri)
    {
        await pipeline.SendAsync(message).ConfigureAwait(false);
        var response = message.ExtractResponse() ?? throw new InvalidOperationException("The pipeline did not return a response.");
        await EnsureSuccessAsync(response, requestUri).ConfigureAwait(false);
        return ClientResult.FromResponse(response);
    }

    public static ClientResult<T> CreateResult<T>(ClientResult responseResult)
    {
        var response = responseResult.GetRawResponse();
        var content = response.Content;
        if (content is null)
        {
            throw new InvalidOperationException("The API response body was empty.");
        }

        var value = JsonSerializer.Deserialize<T>(content.ToString(), DeepSeekJson.SerializerOptions);
        if (value is null)
        {
            throw new InvalidOperationException("The API response could not be deserialized.");
        }

        return ClientResult.FromValue(value, response);
    }

    public static async Task<ClientResult<T>> CreateResultAsync<T>(ClientResult responseResult)
    {
        var response = responseResult.GetRawResponse();
        if (response.Content is null)
        {
            await response.BufferContentAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return CreateResult<T>(responseResult);
    }

    private static void EnsureSuccess(PipelineResponse response, Uri requestUri)
    {
        if (!response.IsError)
        {
            return;
        }

        throw CreateException(response, requestUri);
    }

    private static async Task EnsureSuccessAsync(PipelineResponse response, Uri requestUri)
    {
        if (!response.IsError)
        {
            return;
        }

        if (response.Content is null)
        {
            await response.BufferContentAsync(CancellationToken.None).ConfigureAwait(false);
        }

        throw CreateException(response, requestUri);
    }

    private static DeepSeekException CreateException(PipelineResponse response, Uri requestUri)
    {
        var body = response.Content?.ToString();
        DeepSeekErrorPayload? payload = null;
        try
        {
            if (body is { } errorBody && !string.IsNullOrWhiteSpace(errorBody))
            {
                payload = JsonSerializer.Deserialize<DeepSeekErrorPayload>(errorBody, DeepSeekJson.SerializerOptions);
            }
        }
        catch
        {
        }

        var message = payload?.Error?.Message ?? $"DeepSeek API request failed with status {response.Status}.";
        return new DeepSeekException(message, response.Status, requestUri, body, payload, response);
    }
}

internal sealed class DeepSeekErrorPayload
{
    public string? Type { get; set; }

    public DeepSeekErrorBody? Error { get; set; }
}

internal sealed class DeepSeekErrorBody
{
    public string? Message { get; set; }

    public string? Type { get; set; }

    public string? Param { get; set; }

    public string? Code { get; set; }
}
