using System.ClientModel;
using System.ClientModel.Primitives;
using DeepSeek.Anthropic;
using DeepSeek.Billing;
using DeepSeek.Chat;
using DeepSeek.Completions;
using DeepSeek.Files;
using DeepSeek.Models;
using DeepSeek.Responses;
using DeepSeek.Search;
using System.Text.Json;

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

    public ResponsesClient GetResponsesClient(string model) => new(Pipeline, model, Endpoint, _options);

    public FilesClient GetFilesClient() => new(Pipeline, Endpoint, _options);

    /// <summary>通过 Anthropic Files 协议操作同一账号的上传文件。</summary>
    public FilesClient GetAnthropicFilesClient() => new(Pipeline, Endpoint, _options, anthropic: true);

    /// <summary>使用独立 Search API 搜索网页。</summary>
    public ClientResult<IReadOnlyList<SearchResult>> Search(SearchRequest request, RequestOptions? options = null)
    {
        ValidateSearch(request);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, "/v1/tools/search");
        using var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", request,
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(Search)));
        return ReadSearchResult(DeepSeekProtocol.Send(Pipeline, message, uri));
    }

    /// <summary>异步调用独立 Search API。</summary>
    public async Task<ClientResult<IReadOnlyList<SearchResult>>> SearchAsync(SearchRequest request, RequestOptions? options = null)
    {
        ValidateSearch(request);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, "/v1/tools/search");
        using var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", request,
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(SearchAsync)));
        return ReadSearchResult(await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false));
    }

    /// <summary>抓取 HTTP(S) 网页并返回 Markdown 正文。</summary>
    public ClientResult<FetchResult> Fetch(string url, RequestOptions? options = null)
    {
        ValidateFetchUrl(url);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, "/v1/tools/fetch");
        using var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", new { url },
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(Fetch)));
        return DeepSeekProtocol.CreateResult<FetchResult>(DeepSeekProtocol.Send(Pipeline, message, uri));
    }

    /// <summary>异步抓取 HTTP(S) 网页。</summary>
    public async Task<ClientResult<FetchResult>> FetchAsync(string url, RequestOptions? options = null)
    {
        ValidateFetchUrl(url);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, "/v1/tools/fetch");
        using var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", new { url },
            DeepSeekProtocol.GetBufferedRequestOptions(options, nameof(FetchAsync)));
        return await DeepSeekProtocol.CreateResultAsync<FetchResult>(
            await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false)).ConfigureAwait(false);
    }

    public ChatPrefixClient GetChatPrefixClient(string model) => new(Pipeline, model, Endpoint, _options);

    public CompletionsClient GetCompletionsClient(string model) => new(Pipeline, model, Endpoint, _options);

    public ModelsClient GetModelsClient() => new(Pipeline, Endpoint, _options);

    public BalanceClient GetBalanceClient() => new(Pipeline, Endpoint, _options);

    public AnthropicClient GetAnthropicClient(string model) => new(Pipeline, model, Endpoint, _options);

    private static ClientResult<IReadOnlyList<SearchResult>> ReadSearchResult(ClientResult result)
    {
        var response = result.GetRawResponse();
        using var document = JsonDocument.Parse(response.Content.ToString());
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The search response must contain a results array.");
        }

        IReadOnlyList<SearchResult> items = JsonSerializer.Deserialize<List<SearchResult>>(
            results.GetRawText(), DeepSeekJson.SerializerOptions) ?? [];
        return ClientResult.FromValue(items, response);
    }

    private static void ValidateSearch(SearchRequest request)
    {
        ArgumentGuard.ThrowIfNull(request, nameof(request));
        ValidateSearchText(request.Query, nameof(request.Query));
        if (request.OriginalQuestion is not null)
        {
            ValidateSearchText(request.OriginalQuestion, nameof(request.OriginalQuestion));
        }

        if (request.Count is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(request.Count), "Count must be between 1 and 50.");
        }
    }

    private static void ValidateSearchText(string text, string name)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(text, name);
        var characters = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            characters++;
        }

        if (characters > 128)
        {
            throw new ArgumentException("Search text must contain at most 128 Unicode characters.", name);
        }
    }

    private static void ValidateFetchUrl(string url)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(url, nameof(url));
        if (url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("A Fetch URL must be an HTTP(S) URL without credentials and at most 8192 characters.", nameof(url));
        }
    }
}
