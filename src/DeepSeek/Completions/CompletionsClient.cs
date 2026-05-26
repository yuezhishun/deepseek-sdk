using System.ClientModel;
using System.ClientModel.Primitives;

namespace DeepSeek.Completions;

public sealed class CompletionsClient
{
    private const string Path = "/beta/completions";

    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;

    public CompletionsClient(string model, string apiKey) : this(model, new ApiKeyCredential(apiKey), new DeepSeekClientOptions()) { }
    public CompletionsClient(string model, string apiKey, DeepSeekClientOptions options) : this(model, new ApiKeyCredential(apiKey), options) { }
    public CompletionsClient(string model, ApiKeyCredential credential) : this(model, credential, new DeepSeekClientOptions()) { }
    public CompletionsClient(string model, ApiKeyCredential credential, DeepSeekClientOptions options) : this(model, DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options) { }
    public CompletionsClient(string model, AuthenticationPolicy authenticationPolicy) : this(model, authenticationPolicy, new DeepSeekClientOptions()) { }

    public CompletionsClient(string model, AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(model, nameof(model));
        Model = model;
        Options = options.CloneAndFreeze();
        _pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        _endpoint = Options.Endpoint;
    }

    internal CompletionsClient(ClientPipeline pipeline, string model, Uri endpoint, DeepSeekClientOptions options)
    {
        _pipeline = pipeline;
        Model = model;
        _endpoint = endpoint;
        Options = options;
    }

    public string Model { get; }
    public DeepSeekClientOptions Options { get; }

    public ClientResult CompleteCompletionResponse(CompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;
        ValidateRequest(request);
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "POST", request, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CompleteTextAsync)));
        return DeepSeekProtocol.Send(_pipeline, message, uri);
    }

    public Task<ClientResult> CompleteCompletionResponseAsync(CompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;
        ValidateRequest(request);
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "POST", request, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CompleteTextAsync)));
        return DeepSeekProtocol.SendAsync(_pipeline, message, uri);
    }

    public ClientResult<Completion> CompleteText(CompletionRequest request, RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<Completion>(CompleteCompletionResponse(request, options));

    public async Task<ClientResult<Completion>> CompleteTextAsync(CompletionRequest request, RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<Completion>(await CompleteCompletionResponseAsync(request, options).ConfigureAwait(false)).ConfigureAwait(false);

    public AsyncCollectionResult<Completion> CompleteTextStreaming(CompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = true;
        ValidateRequest(request);

        return new DelegateAsyncCollectionResult<Completion>(
            ct => SendStreamingRequestAsync(request, options, ct),
            (response, ct) => DeepSeekStreaming.ReadServerSentEventsAsync<Completion>(response, ct));
    }

    private async Task<ClientResult> SendStreamingRequestAsync(CompletionRequest request, RequestOptions? options, CancellationToken cancellationToken)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        var requestOptions = DeepSeekProtocol.GetStreamingRequestOptions(options, cancellationToken);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "POST", request, requestOptions, streamResponse: true);
        return await DeepSeekProtocol.SendAsync(_pipeline, message, uri).ConfigureAwait(false);
    }

    private static void ValidateRequest(CompletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(request));
        }
    }
}
