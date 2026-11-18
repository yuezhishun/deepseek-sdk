using System.ClientModel;
using System.ClientModel.Primitives;

namespace DeepSeek.Responses;

public class ResponsesClient
{
    private const string ResponsesPath = "/responses";

    protected internal ClientPipeline Pipeline { get; }
    protected internal Uri Endpoint { get; }

    public ResponsesClient(string model, string apiKey)
        : this(model, new ApiKeyCredential(apiKey), new DeepSeekClientOptions())
    {
    }

    public ResponsesClient(string model, string apiKey, DeepSeekClientOptions options)
        : this(model, new ApiKeyCredential(apiKey), options)
    {
    }

    public ResponsesClient(string model, ApiKeyCredential credential)
        : this(model, credential, new DeepSeekClientOptions())
    {
    }

    public ResponsesClient(string model, ApiKeyCredential credential, DeepSeekClientOptions options)
        : this(model, DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options)
    {
    }

    public ResponsesClient(string model, AuthenticationPolicy authenticationPolicy)
        : this(model, authenticationPolicy, new DeepSeekClientOptions())
    {
    }

    public ResponsesClient(string model, AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(model, nameof(model));
        ArgumentGuard.ThrowIfNull(authenticationPolicy, nameof(authenticationPolicy));
        ArgumentGuard.ThrowIfNull(options, nameof(options));

        Model = model;
        Options = options.CloneAndFreeze();
        Pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        Endpoint = Options.Endpoint;
    }

    protected internal ResponsesClient(ClientPipeline pipeline, string model, Uri endpoint, DeepSeekClientOptions options)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("Value cannot be null or empty.", nameof(model)) : model;
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Model { get; }

    public DeepSeekClientOptions Options { get; }

    public virtual ClientResult CreateResponseResponse(ResponseRequest request, RequestOptions? options = null)
    {
        PrepareRequest(request, stream: false);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ResponsesPath);
        using var message = CreateRequest(request, uri, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CreateResponseAsync)));
        return DeepSeekProtocol.Send(Pipeline, message, uri);
    }

    /// <summary>Sends a non-streaming request and returns the raw pipeline result.</summary>
    public virtual ClientResult CreateResponseRaw(ResponseRequest request, RequestOptions? options = null)
        => CreateResponseResponse(request, options);

    public virtual Task<ClientResult> CreateResponseResponseAsync(ResponseRequest request, RequestOptions? options = null)
    {
        PrepareRequest(request, stream: false);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ResponsesPath);
        using var message = CreateRequest(request, uri, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CreateResponseAsync)));
        return DeepSeekProtocol.SendAsync(Pipeline, message, uri);
    }

    /// <summary>Sends a non-streaming request asynchronously and returns the raw pipeline result.</summary>
    public virtual Task<ClientResult> CreateResponseRawAsync(ResponseRequest request, RequestOptions? options = null)
        => CreateResponseResponseAsync(request, options);

    public virtual ClientResult<Response> CreateResponse(ResponseRequest request, RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<Response>(CreateResponseResponse(request, options));

    public virtual async Task<ClientResult<Response>> CreateResponseAsync(ResponseRequest request, RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<Response>(await CreateResponseResponseAsync(request, options).ConfigureAwait(false)).ConfigureAwait(false);

    public virtual AsyncCollectionResult<ResponseStreamEvent> CreateResponseStreaming(ResponseRequest request, RequestOptions? options = null)
    {
        PrepareRequest(request, stream: true);
        return new DelegateAsyncCollectionResult<ResponseStreamEvent>(
            ct => SendStreamingRequestAsync(request, options, ct),
            (response, ct) => DeepSeekStreaming.ReadServerSentEventsAsync<ResponseStreamEvent>(response, ct), options?.CancellationToken ?? CancellationToken.None);
    }

    private PipelineMessage CreateRequest(ResponseRequest request, Uri uri, RequestOptions options)
        => DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", request, options);

    private async Task<ClientResult> SendStreamingRequestAsync(ResponseRequest request, RequestOptions? options, CancellationToken cancellationToken)
    {
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ResponsesPath);
        var requestOptions = DeepSeekProtocol.GetStreamingRequestOptions(options, cancellationToken);
        using var message = DeepSeekProtocol.CreateRequest(
            Pipeline,
            uri,
            "POST",
            request,
            requestOptions,
            streamResponse: true,
            headers: new Dictionary<string, string> { ["Accept"] = "text/event-stream" });
        return await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false);
    }

    private void PrepareRequest(ResponseRequest request, bool stream)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        if (request.Input is null && string.IsNullOrWhiteSpace(request.Instructions))
        {
            throw new ArgumentException("At least one of Input and Instructions is required.", nameof(request));
        }

        var extended = ConversationOptions.Validate(request.Tools?.Select(static tool => tool.Type), request.SafetyCheck, request.Title, request.ToolChoice);
        if (extended && (request.Text?.Format?.Type is not (null or "text") || request.TopLogprobs is not null
            || request.AdditionalProperties?.ContainsKey("previous_response_id") == true
            || request.AdditionalProperties?.ContainsKey("conversation") == true))
            throw new ArgumentException("The DeepSeek conversation extensions require text output and stateless input without top_logprobs.", nameof(request));
        if (request.Input is not null)
        {
            foreach (var (type, role) in request.Input.GetItemKinds())
            {
                if (role == "latest_reminder")
                    throw new ArgumentException("Responses does not support latest_reminder messages.", nameof(request));
                if (extended && type is not ("message" or "reasoning" or "function_call" or "function_call_output" or "web_search_call")
                    && !(type is null && role is "user" or "assistant" or "system" or "developer"))
                    throw new ArgumentException("Unsupported input item for the DeepSeek conversation extensions: " + type, nameof(request));
            }
        }

        request.Model = Model;
        request.Stream = stream;
    }
}
