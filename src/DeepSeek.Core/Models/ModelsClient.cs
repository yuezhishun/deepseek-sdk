using System.ClientModel;
using System.ClientModel.Primitives;

namespace DeepSeek.Models;

public sealed class ModelsClient
{
    private const string Path = "/models";
    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;

    public ModelsClient(string apiKey) : this(new ApiKeyCredential(apiKey), new DeepSeekClientOptions()) { }
    public ModelsClient(string apiKey, DeepSeekClientOptions options) : this(new ApiKeyCredential(apiKey), options) { }
    public ModelsClient(ApiKeyCredential credential) : this(credential, new DeepSeekClientOptions()) { }
    public ModelsClient(ApiKeyCredential credential, DeepSeekClientOptions options) : this(DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options) { }
    public ModelsClient(AuthenticationPolicy authenticationPolicy) : this(authenticationPolicy, new DeepSeekClientOptions()) { }

    public ModelsClient(AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        Options = options.CloneAndFreeze();
        _pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        _endpoint = Options.Endpoint;
    }

    internal ModelsClient(ClientPipeline pipeline, Uri endpoint, DeepSeekClientOptions options)
    {
        _pipeline = pipeline;
        _endpoint = endpoint;
        Options = options;
    }

    public DeepSeekClientOptions Options { get; }

    public ClientResult GetModelsResponse(RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "GET", null, options);
        return DeepSeekProtocol.Send(_pipeline, message, uri);
    }

    public Task<ClientResult> GetModelsResponseAsync(RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "GET", null, options);
        return DeepSeekProtocol.SendAsync(_pipeline, message, uri);
    }

    public ClientResult<ModelList> GetModels(RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<ModelList>(GetModelsResponse(options));

    public async Task<ClientResult<ModelList>> GetModelsAsync(RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<ModelList>(await GetModelsResponseAsync(options).ConfigureAwait(false)).ConfigureAwait(false);
}

public sealed class ModelList
{
    public string? Object { get; set; }
    public IList<ModelInfo> Data { get; set; } = [];
}

public sealed class ModelInfo
{
    public string? Id { get; set; }
    public string? Object { get; set; }
    public string? OwnedBy { get; set; }
}
