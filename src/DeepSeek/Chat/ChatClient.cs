using System.ClientModel;
using System.ClientModel.Primitives;

namespace DeepSeek.Chat;

public class ChatClient
{
    private const string ChatPath = "/chat/completions";

    protected internal ClientPipeline Pipeline { get; }
    protected internal Uri Endpoint { get; }

    public ChatClient(string model, string apiKey)
        : this(model, new ApiKeyCredential(apiKey), new DeepSeekClientOptions())
    {
    }

    public ChatClient(string model, string apiKey, DeepSeekClientOptions options)
        : this(model, new ApiKeyCredential(apiKey), options)
    {
    }

    public ChatClient(string model, ApiKeyCredential credential)
        : this(model, credential, new DeepSeekClientOptions())
    {
    }

    public ChatClient(string model, ApiKeyCredential credential, DeepSeekClientOptions options)
        : this(model, DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options)
    {
    }

    public ChatClient(string model, AuthenticationPolicy authenticationPolicy)
        : this(model, authenticationPolicy, new DeepSeekClientOptions())
    {
    }

    public ChatClient(string model, AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(model, nameof(model));
        ArgumentGuard.ThrowIfNull(authenticationPolicy, nameof(authenticationPolicy));
        ArgumentGuard.ThrowIfNull(options, nameof(options));

        Model = model;
        Options = options.CloneAndFreeze();
        Pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        Endpoint = Options.Endpoint;
    }

    protected internal ChatClient(ClientPipeline pipeline, string model, Uri endpoint, DeepSeekClientOptions options)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Model = string.IsNullOrWhiteSpace(model) ? throw new ArgumentException("Value cannot be null or empty.", nameof(model)) : model;
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Model { get; }

    public DeepSeekClientOptions Options { get; }

    public virtual ClientResult Complete(RequestOptions? options = null)
        => CompleteChatResponse(new ChatCompletionRequest(), options);

    public virtual Task<ClientResult> CompleteAsync(RequestOptions? options = null)
        => CompleteChatResponseAsync(new ChatCompletionRequest(), options);

    public virtual ClientResult CompleteChatResponse(ChatCompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;

        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ChatPath);
        using var message = CreateCompleteRequest(request, options);
        return DeepSeekProtocol.Send(Pipeline, message, uri);
    }

    public virtual Task<ClientResult> CompleteChatResponseAsync(ChatCompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;

        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ChatPath);
        using var message = CreateCompleteRequest(request, options);
        return DeepSeekProtocol.SendAsync(Pipeline, message, uri);
    }

    public virtual ClientResult<ChatCompletion> CompleteChat(ChatCompletionRequest request, RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<ChatCompletion>(CompleteChatResponse(request, options));

    public virtual async Task<ClientResult<ChatCompletion>> CompleteChatAsync(ChatCompletionRequest request, RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<ChatCompletion>(await CompleteChatResponseAsync(request, options).ConfigureAwait(false)).ConfigureAwait(false);

    public virtual AsyncCollectionResult<ChatCompletion> CompleteChatStreaming(ChatCompletionRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = true;

        return new DelegateAsyncCollectionResult<ChatCompletion>(
            ct => SendStreamingRequestAsync(request, options, ct),
            (response, ct) => DeepSeekStreaming.ReadServerSentEventsAsync<ChatCompletion>(response, ct));
    }

    protected internal virtual PipelineMessage CreateCompleteRequest(ChatCompletionRequest request, RequestOptions? options)
    {
        ValidateRequest(request);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ChatPath);
        return DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", request, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CompleteChatAsync)));
    }

    private async Task<ClientResult> SendStreamingRequestAsync(ChatCompletionRequest request, RequestOptions? options, CancellationToken cancellationToken)
    {
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, ChatPath);
        var requestOptions = DeepSeekProtocol.GetStreamingRequestOptions(options, cancellationToken);
        using var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", request, requestOptions, streamResponse: true);
        return await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false);
    }

    protected static void ValidateRequest(ChatCompletionRequest request)
    {
        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one message is required.", nameof(request));
        }
    }
}

public sealed class ChatPrefixClient : ChatClient
{
    private const string BetaChatPath = "/beta/chat/completions";

    public ChatPrefixClient(string model, string apiKey) : base(model, apiKey) { }
    public ChatPrefixClient(string model, string apiKey, DeepSeekClientOptions options) : base(model, apiKey, options) { }
    public ChatPrefixClient(string model, ApiKeyCredential credential) : base(model, credential) { }
    public ChatPrefixClient(string model, ApiKeyCredential credential, DeepSeekClientOptions options) : base(model, credential, options) { }
    public ChatPrefixClient(string model, AuthenticationPolicy authenticationPolicy) : base(model, authenticationPolicy) { }
    public ChatPrefixClient(string model, AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options) : base(model, authenticationPolicy, options) { }
    internal ChatPrefixClient(ClientPipeline pipeline, string model, Uri endpoint, DeepSeekClientOptions options)
        : base(pipeline, model, endpoint, options) { }

    protected internal override PipelineMessage CreateCompleteRequest(ChatCompletionRequest request, RequestOptions? options)
    {
        ValidateRequest(request);
        ValidatePrefixRequest(request);
        var endpoint = DeepSeekClientUtilities.CombinePath(Endpoint, BetaChatPath);
        return DeepSeekProtocol.CreateRequest(Pipeline, endpoint, "POST", request, DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CompleteChatAsync)));
    }

    private static void ValidatePrefixRequest(ChatCompletionRequest request)
    {
        var lastMessage = request.Messages.LastOrDefault();
        if (lastMessage is null ||
            !string.Equals(lastMessage.Role, "assistant", StringComparison.Ordinal) ||
            lastMessage.Prefix != true)
        {
            throw new ArgumentException("Chat prefix requests require the final assistant message to set Prefix = true.", nameof(request));
        }
    }
}
