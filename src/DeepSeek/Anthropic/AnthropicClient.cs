using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Anthropic;

public sealed class AnthropicClient
{
    private const string Path = "/anthropic/v1/messages";
    private static readonly HashSet<string> SupportedInputBlockTypes = ["text", "thinking", "tool_use", "tool_result"];
    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;

    public AnthropicClient(string model, string apiKey) : this(model, new ApiKeyCredential(apiKey), new DeepSeekClientOptions()) { }
    public AnthropicClient(string model, string apiKey, DeepSeekClientOptions options) : this(model, new ApiKeyCredential(apiKey), options) { }
    public AnthropicClient(string model, ApiKeyCredential credential) : this(model, credential, new DeepSeekClientOptions()) { }
    public AnthropicClient(string model, ApiKeyCredential credential, DeepSeekClientOptions options) : this(model, DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options) { }
    public AnthropicClient(string model, AuthenticationPolicy authenticationPolicy) : this(model, authenticationPolicy, new DeepSeekClientOptions()) { }

    public AnthropicClient(string model, AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(model, nameof(model));
        Model = model;
        Options = options.CloneAndFreeze();
        _pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        _endpoint = Options.Endpoint;
    }

    internal AnthropicClient(ClientPipeline pipeline, string model, Uri endpoint, DeepSeekClientOptions options)
    {
        _pipeline = pipeline;
        Model = model;
        _endpoint = endpoint;
        Options = options;
    }

    public string Model { get; }
    public DeepSeekClientOptions Options { get; }

    public ClientResult CreateMessageResponse(AnthropicMessageRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;
        ValidateRequest(request);
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "POST",
            request,
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CreateMessageAsync)),
            headers: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" });
        return DeepSeekProtocol.Send(_pipeline, message, uri);
    }

    public Task<ClientResult> CreateMessageResponseAsync(AnthropicMessageRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = false;
        ValidateRequest(request);
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "POST",
            request,
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(CreateMessageAsync)),
            headers: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" });
        return DeepSeekProtocol.SendAsync(_pipeline, message, uri);
    }

    public ClientResult<AnthropicMessageResponse> CreateMessage(AnthropicMessageRequest request, RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<AnthropicMessageResponse>(CreateMessageResponse(request, options));

    public async Task<ClientResult<AnthropicMessageResponse>> CreateMessageAsync(AnthropicMessageRequest request, RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<AnthropicMessageResponse>(await CreateMessageResponseAsync(request, options).ConfigureAwait(false)).ConfigureAwait(false);

    public AsyncCollectionResult<AnthropicStreamEvent> CreateMessageStreaming(AnthropicMessageRequest request, RequestOptions? options = null)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        request.Model = Model;
        request.Stream = true;
        ValidateRequest(request);
        return new DelegateAsyncCollectionResult<AnthropicStreamEvent>(
            ct => SendStreamingRequestAsync(request, options, ct),
            (response, ct) => DeepSeekStreaming.ReadServerSentEventsAsync<AnthropicStreamEvent>(response, ct));
    }

    private async Task<ClientResult> SendStreamingRequestAsync(AnthropicMessageRequest request, RequestOptions? options, CancellationToken cancellationToken)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        var requestOptions = DeepSeekProtocol.GetStreamingRequestOptions(options, cancellationToken);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "POST",
            request,
            requestOptions,
            streamResponse: true,
            headers: new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" });
        return await DeepSeekProtocol.SendAsync(_pipeline, message, uri).ConfigureAwait(false);
    }

    private static void ValidateRequest(AnthropicMessageRequest request)
    {
        foreach (var message in request.Messages)
        {
            foreach (var block in message.Content)
            {
                if (!SupportedInputBlockTypes.Contains(block.Type))
                {
                    throw new ArgumentException("Unsupported Anthropic content block type: " + block.Type, nameof(request));
                }
            }
        }
    }
}

public sealed class AnthropicMessageRequest
{
    public string Model { get; set; } = string.Empty;
    public long? MaxTokens { get; set; }
    public string? System { get; set; }
    public IList<AnthropicMessage> Messages { get; set; } = [];
    public IList<string>? StopSequences { get; set; }
    public bool? Stream { get; set; }
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public AnthropicThinkingConfig? Thinking { get; set; }
    public AnthropicOutputConfig? OutputConfig { get; set; }
    public IList<AnthropicTool>? Tools { get; set; }
    public object? ToolChoice { get; set; }
    public IDictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class AnthropicMessage
{
    public string Role { get; set; } = "user";
    public IList<AnthropicContentBlock> Content { get; set; } = [];
}

public sealed class AnthropicContentBlock
{
    public string Type { get; set; } = "text";
    public string? Text { get; set; }
    public string? Thinking { get; set; }
    public string? Signature { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonNode? Input { get; set; }
    public string? ToolUseId { get; set; }
    public JsonNode? Content { get; set; }
}

public sealed class AnthropicThinkingConfig
{
    public string Type { get; set; } = "enabled";
}

public sealed class AnthropicOutputConfig
{
    public string? Effort { get; set; }
}

public sealed class AnthropicTool
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public JsonNode InputSchema { get; set; } = new JsonObject();
}

public sealed class AnthropicToolChoice
{
    public string Type { get; set; } = "auto";
    public string? Name { get; set; }
}

public sealed class AnthropicMessageResponse
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Role { get; set; }
    public string? Model { get; set; }
    public string? StopReason { get; set; }
    public string? StopSequence { get; set; }
    public IList<AnthropicContentBlock> Content { get; set; } = [];
    public AnthropicUsage? Usage { get; set; }
}

public sealed class AnthropicUsage
{
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? CacheCreationInputTokens { get; set; }
    public int? CacheReadInputTokens { get; set; }
}

public sealed class AnthropicStreamEvent
{
    public string? Type { get; set; }
    public AnthropicMessageResponse? Message { get; set; }
    public AnthropicContentBlock? Delta { get; set; }
    public AnthropicContentBlock? ContentBlock { get; set; }
    public int? Index { get; set; }
    public AnthropicUsage? Usage { get; set; }
}
