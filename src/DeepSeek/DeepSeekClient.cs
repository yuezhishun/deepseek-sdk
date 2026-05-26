using System.ClientModel;
using System.ClientModel.Primitives;
using DeepSeek.Anthropic;
using DeepSeek.Billing;
using DeepSeek.Chat;
using DeepSeek.Completions;
using DeepSeek.Models;

namespace DeepSeek;

public class DeepSeekClient
{
    private readonly DeepSeekClientOptions _options;

    public DeepSeekClient(string apiKey)
        : this(new ApiKeyCredential(apiKey), new DeepSeekClientOptions())
    {
    }

    public DeepSeekClient(string apiKey, DeepSeekClientOptions options)
        : this(new ApiKeyCredential(apiKey), options)
    {
    }

    public DeepSeekClient(ApiKeyCredential credential)
        : this(credential, new DeepSeekClientOptions())
    {
    }

    public DeepSeekClient(ApiKeyCredential credential, DeepSeekClientOptions options)
        : this(DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options)
    {
    }

    public DeepSeekClient(AuthenticationPolicy authenticationPolicy)
        : this(authenticationPolicy, new DeepSeekClientOptions())
    {
    }

    public DeepSeekClient(AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNull(authenticationPolicy, nameof(authenticationPolicy));
        ArgumentGuard.ThrowIfNull(options, nameof(options));

        _options = options.CloneAndFreeze();
        Pipeline = DeepSeekClientUtilities.CreatePipeline(_options, authenticationPolicy);
        Endpoint = _options.Endpoint;
    }

    protected internal DeepSeekClient(ClientPipeline pipeline, Uri endpoint, DeepSeekClientOptions options)
    {
        Pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ClientPipeline Pipeline { get; }

    public Uri Endpoint { get; }

    public DeepSeekClientOptions Options => _options;

    public ChatClient GetChatClient(string model) => new(Pipeline, model, Endpoint, _options);

    public ChatPrefixClient GetChatPrefixClient(string model) => new(Pipeline, model, Endpoint, _options);

    public CompletionsClient GetCompletionsClient(string model) => new(Pipeline, model, Endpoint, _options);

    public ModelsClient GetModelsClient() => new(Pipeline, Endpoint, _options);

    public BalanceClient GetBalanceClient() => new(Pipeline, Endpoint, _options);

    public AnthropicClient GetAnthropicClient(string model) => new(Pipeline, model, Endpoint, _options);
}
