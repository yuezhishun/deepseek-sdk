using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;

namespace DeepSeek.Billing;

public sealed class BalanceClient
{
    private const string Path = "/user/balance";
    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;

    public BalanceClient(string apiKey) : this(new ApiKeyCredential(apiKey), new DeepSeekClientOptions()) { }
    public BalanceClient(string apiKey, DeepSeekClientOptions options) : this(new ApiKeyCredential(apiKey), options) { }
    public BalanceClient(ApiKeyCredential credential) : this(credential, new DeepSeekClientOptions()) { }
    public BalanceClient(ApiKeyCredential credential, DeepSeekClientOptions options) : this(DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options) { }
    public BalanceClient(AuthenticationPolicy authenticationPolicy) : this(authenticationPolicy, new DeepSeekClientOptions()) { }

    public BalanceClient(AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        Options = options.CloneAndFreeze();
        _pipeline = DeepSeekClientUtilities.CreatePipeline(Options, authenticationPolicy);
        _endpoint = Options.Endpoint;
    }

    internal BalanceClient(ClientPipeline pipeline, Uri endpoint, DeepSeekClientOptions options)
    {
        _pipeline = pipeline;
        _endpoint = endpoint;
        Options = options;
    }

    public DeepSeekClientOptions Options { get; }

    public ClientResult GetBalanceResponse(RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "GET", null, options);
        return DeepSeekProtocol.Send(_pipeline, message, uri);
    }

    public Task<ClientResult> GetBalanceResponseAsync(RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(_endpoint, Path);
        using var message = DeepSeekProtocol.CreateRequest(_pipeline, uri, "GET", null, options);
        return DeepSeekProtocol.SendAsync(_pipeline, message, uri);
    }

    public ClientResult<BalanceInfo> GetBalance(RequestOptions? options = null)
        => DeepSeekProtocol.CreateResult<BalanceInfo>(GetBalanceResponse(options));

    public async Task<ClientResult<BalanceInfo>> GetBalanceAsync(RequestOptions? options = null)
        => await DeepSeekProtocol.CreateResultAsync<BalanceInfo>(await GetBalanceResponseAsync(options).ConfigureAwait(false)).ConfigureAwait(false);
}

public sealed class BalanceInfo
{
    public bool IsAvailable { get; set; }
    public IList<UserBalance> BalanceInfos { get; set; } = new List<UserBalance>();
}

public sealed class UserBalance
{
    public string Currency { get; set; } = string.Empty;
    public string TotalBalance { get; set; } = string.Empty;
    public string GrantedBalance { get; set; } = string.Empty;
    public string ToppedUpBalance { get; set; } = string.Empty;
}
