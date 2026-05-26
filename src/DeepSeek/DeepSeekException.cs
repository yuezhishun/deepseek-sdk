using System.ClientModel.Primitives;

namespace DeepSeek;

public sealed class DeepSeekException : Exception
{
    public DeepSeekException(
        string message,
        int status,
        Uri requestUri,
        string? responseContent,
        object? errorPayload,
        PipelineResponse response)
        : base(message)
    {
        Status = status;
        RequestUri = requestUri;
        ResponseContent = responseContent;
        ErrorPayload = errorPayload;
        Response = response;
    }

    public int Status { get; }

    public Uri RequestUri { get; }

    public string? ResponseContent { get; }

    public object? ErrorPayload { get; }

    public PipelineResponse Response { get; }
}
