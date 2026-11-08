using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepSeek.Files;

public sealed class FilesClient
{
    private const string FilesPath = "/files";
    private const int MaximumUploadBytes = 64 * 1024 * 1024;
    private const int MinimumExpirySeconds = 3600;
    private const int MaximumExpirySeconds = 2592000;
    private readonly ClientPipeline _pipeline;
    private readonly Uri _endpoint;
    private readonly DeepSeekClientOptions _options;
    private readonly bool _anthropic;

    public FilesClient(string apiKey)
        : this(new ApiKeyCredential(apiKey), new DeepSeekClientOptions())
    {
    }

    public FilesClient(string apiKey, DeepSeekClientOptions options)
        : this(new ApiKeyCredential(apiKey), options)
    {
    }

    public FilesClient(ApiKeyCredential credential)
        : this(credential, new DeepSeekClientOptions())
    {
    }

    public FilesClient(ApiKeyCredential credential, DeepSeekClientOptions options)
        : this(DeepSeekClientUtilities.CreateAuthenticationPolicy(credential), options)
    {
    }

    public FilesClient(AuthenticationPolicy authenticationPolicy)
        : this(authenticationPolicy, new DeepSeekClientOptions())
    {
    }

    public FilesClient(AuthenticationPolicy authenticationPolicy, DeepSeekClientOptions options)
    {
        ArgumentGuard.ThrowIfNull(authenticationPolicy, nameof(authenticationPolicy));
        ArgumentGuard.ThrowIfNull(options, nameof(options));

        _options = options.CloneAndFreeze();
        _pipeline = DeepSeekClientUtilities.CreatePipeline(_options, authenticationPolicy);
        _endpoint = _options.Endpoint;
    }

    internal FilesClient(ClientPipeline pipeline, Uri endpoint, DeepSeekClientOptions options, bool anthropic = false)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _anthropic = anthropic;
    }

    public DeepSeekClientOptions Options => _options;

    public ClientResult<DeepSeekFile> UploadFile(
        Stream stream,
        string fileName,
        FileUploadOptions? options = null,
        RequestOptions? requestOptions = null)
    {
        ArgumentGuard.ThrowIfNull(stream, nameof(stream));
        ArgumentGuard.ThrowIfNullOrWhiteSpace(fileName, nameof(fileName));
        ValidateFileName(fileName);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }
        ValidateUploadOptions(options);

        using var data = CopyToMemory(stream);
        using var form = CreateMultipart(data, fileName, options);
        using var message = CreateMultipartRequest(form, requestOptions);
        return ReadResult<DeepSeekFile>(DeepSeekProtocol.Send(_pipeline, message, GetFilesUri()));
    }

    public async Task<ClientResult<DeepSeekFile>> UploadFileAsync(
        Stream stream,
        string fileName,
        FileUploadOptions? options = null,
        RequestOptions? requestOptions = null)
    {
        ArgumentGuard.ThrowIfNull(stream, nameof(stream));
        ArgumentGuard.ThrowIfNullOrWhiteSpace(fileName, nameof(fileName));
        ValidateFileName(fileName);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }
        ValidateUploadOptions(options);

        using var data = await CopyToMemoryAsync(stream, requestOptions?.CancellationToken ?? CancellationToken.None).ConfigureAwait(false);
        using var form = CreateMultipart(data, fileName, options);
        using var message = CreateMultipartRequest(form, requestOptions);
        return ReadResult<DeepSeekFile>(await DeepSeekProtocol.SendAsync(_pipeline, message, GetFilesUri()).ConfigureAwait(false));
    }

    public ClientResult<DeepSeekFileList> ListFiles(FileListOptions? options = null, RequestOptions? requestOptions = null)
    {
        ValidateListOptions(options);
        var uri = BuildFilesUri(options);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "GET",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(ListFiles)), headers: CreateHeaders());
        return ReadResult<DeepSeekFileList>(DeepSeekProtocol.Send(_pipeline, message, uri));
    }

    public async Task<ClientResult<DeepSeekFileList>> ListFilesAsync(FileListOptions? options = null, RequestOptions? requestOptions = null)
    {
        ValidateListOptions(options);
        var uri = BuildFilesUri(options);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "GET",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(ListFilesAsync)), headers: CreateHeaders());
        return ReadResult<DeepSeekFileList>(await DeepSeekProtocol.SendAsync(_pipeline, message, uri).ConfigureAwait(false));
    }

    public ClientResult<DeepSeekFile> RetrieveFile(string fileId, RequestOptions? requestOptions = null)
    {
        ValidateFileId(fileId);
        var uri = GetFileUri(fileId);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "GET",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(RetrieveFile)), headers: CreateHeaders());
        return ReadResult<DeepSeekFile>(DeepSeekProtocol.Send(_pipeline, message, uri));
    }

    public async Task<ClientResult<DeepSeekFile>> RetrieveFileAsync(string fileId, RequestOptions? requestOptions = null)
    {
        ValidateFileId(fileId);
        var uri = GetFileUri(fileId);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "GET",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(RetrieveFileAsync)), headers: CreateHeaders());
        return ReadResult<DeepSeekFile>(await DeepSeekProtocol.SendAsync(_pipeline, message, uri).ConfigureAwait(false));
    }

    public ClientResult<DeepSeekFileDeleteResult> DeleteFile(string fileId, RequestOptions? requestOptions = null)
    {
        ValidateFileId(fileId);
        var uri = GetFileUri(fileId);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "DELETE",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(DeleteFile)), headers: CreateHeaders());
        return ReadResult<DeepSeekFileDeleteResult>(DeepSeekProtocol.Send(_pipeline, message, uri));
    }

    public async Task<ClientResult<DeepSeekFileDeleteResult>> DeleteFileAsync(string fileId, RequestOptions? requestOptions = null)
    {
        ValidateFileId(fileId);
        var uri = GetFileUri(fileId);
        using var message = DeepSeekProtocol.CreateRequest(
            _pipeline,
            uri,
            "DELETE",
            content: null,
            DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(DeleteFileAsync)), headers: CreateHeaders());
        return ReadResult<DeepSeekFileDeleteResult>(await DeepSeekProtocol.SendAsync(_pipeline, message, uri).ConfigureAwait(false));
    }

    private PipelineMessage CreateMultipartRequest(MultiPartFormContent form, RequestOptions? requestOptions)
    {
        var uri = GetFilesUri();
        var message = _pipeline.CreateMessage(uri, "POST", PipelineMessageClassifier.Default);
        message.Request.Content = form;
        message.Request.Headers.Set("Content-Type", form.MediaType ?? "multipart/form-data");
        if (_anthropic)
        {
            message.Request.Headers.Set("anthropic-beta", "files-api-2025-04-14");
        }
        message.Apply(DeepSeekProtocol.GetBufferedRequestOptions(requestOptions, nameof(UploadFileAsync)));
        return message;
    }

    private MultiPartFormContent CreateMultipart(MemoryStream data, string fileName, FileUploadOptions? options)
    {
        var form = new MultiPartFormContent();
        var file = new FileBinaryContent(data, "application/octet-stream")
        {
            Filename = fileName,
        };
        form.Add("file", file);
        if (!_anthropic)
        {
            form.Add("purpose", options?.Purpose ?? "user_data", "text/plain");
        }

        if (options?.ExpiresAfterSeconds is { } seconds)
        {
            form.Add("expires_after[anchor]", options.ExpiresAfterAnchor ?? "created_at", "text/plain");
            form.Add("expires_after[seconds]", seconds, "text/plain");
        }

        return form;
    }

    private Uri GetFilesUri()
        => DeepSeekClientUtilities.CombinePath(_endpoint, _anthropic ? "/anthropic/v1/files" : FilesPath);

    private Uri GetFileUri(string fileId)
        => new(GetFilesUri().ToString().TrimEnd('/') + "/" + Uri.EscapeDataString(fileId));

    private Uri BuildFilesUri(FileListOptions? options)
    {
        var baseUri = GetFilesUri().ToString();
        if (options is null)
        {
            return new Uri(baseUri);
        }

        var query = new List<string>();
        AddQuery(query, _anthropic ? "after_id" : "after", options.After);
        if (_anthropic)
        {
            AddQuery(query, "before_id", options.Before);
        }
        if (options.Limit is { } limit)
        {
            AddQuery(query, "limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        AddQuery(query, "order", options.Order);
        AddQuery(query, "purpose", options.Purpose);
        return new Uri(query.Count == 0 ? baseUri : baseUri + "?" + string.Join("&", query));
    }

    private static void AddQuery(ICollection<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
        }
    }

    private static MemoryStream CopyToMemory(Stream stream)
    {
        var data = new MemoryStream();
        var buffer = new byte[81920];
        while (data.Length < MaximumUploadBytes)
        {
            var count = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumUploadBytes - (int)data.Length));
            if (count == 0)
            {
                break;
            }

            AppendToMemory(data, buffer, count);
        }

        if (data.Length == MaximumUploadBytes && stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The file exceeds the maximum upload size of 64 MiB.");
        }

        data.Position = 0;
        return data;
    }

    private static async Task<MemoryStream> CopyToMemoryAsync(Stream stream, CancellationToken cancellationToken)
    {
        var data = new MemoryStream();
        var buffer = new byte[81920];
        while (data.Length < MaximumUploadBytes)
        {
            var count = await stream.ReadAsync(
                buffer,
                0,
                Math.Min(buffer.Length, MaximumUploadBytes - (int)data.Length),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            AppendToMemory(data, buffer, count);
        }

        if (data.Length == MaximumUploadBytes)
        {
            var extraByte = new byte[1];
            if (await stream.ReadAsync(extraByte, 0, 1, cancellationToken).ConfigureAwait(false) != 0)
            {
                throw new InvalidDataException("The file exceeds the maximum upload size of 64 MiB.");
            }
        }

        data.Position = 0;
        return data;
    }

    private static void AppendToMemory(MemoryStream data, byte[] buffer, int count)
    {
        var requiredCapacity = checked(data.Length + count);
        if (requiredCapacity > data.Capacity)
        {
            var doubledCapacity = Math.Min((long)data.Capacity * 2, MaximumUploadBytes);
            var capacity = Math.Max(requiredCapacity, Math.Max(doubledCapacity, buffer.Length));
            data.Capacity = (int)Math.Min(capacity, MaximumUploadBytes);
        }

        data.Write(buffer, 0, count);
    }

    private void ValidateUploadOptions(FileUploadOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (!string.Equals(options.Purpose, "user_data", StringComparison.Ordinal))
        {
            throw new ArgumentException("Purpose must be 'user_data'.", nameof(options));
        }

        if (_anthropic && (options.ExpiresAfterSeconds is not null || options.ExpiresAfterAnchor is not null))
        {
            throw new ArgumentException("Expiry options are only supported by the OpenAI-compatible Files endpoint.", nameof(options));
        }

        if (options.ExpiresAfterSeconds is { } seconds && (seconds < MinimumExpirySeconds || seconds > MaximumExpirySeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(options.ExpiresAfterSeconds), seconds, "The expiry must be between 3600 and 2592000 seconds.");
        }

        if (options.ExpiresAfterSeconds is null && options.ExpiresAfterAnchor is not null)
        {
            throw new ArgumentException("ExpiresAfterAnchor requires ExpiresAfterSeconds.", nameof(options));
        }

        if (options.ExpiresAfterAnchor is not null && !string.Equals(options.ExpiresAfterAnchor, "created_at", StringComparison.Ordinal))
        {
            throw new ArgumentException("ExpiresAfterAnchor must be 'created_at'.", nameof(options));
        }
    }

    private void ValidateListOptions(FileListOptions? options)
    {
        if (options is null)
        {
            return;
        }

        if (options.Limit is { } limit && (limit < 1 || limit > 1000))
        {
            throw new ArgumentOutOfRangeException(nameof(options.Limit), limit, "Limit must be between 1 and 1000.");
        }

        if (_anthropic)
        {
            if ((!string.IsNullOrEmpty(options.After) && !string.IsNullOrEmpty(options.Before))
                || options.Order is not null || options.Purpose is not null)
            {
                throw new ArgumentException("Anthropic Files accepts one cursor and does not accept order or purpose filters.", nameof(options));
            }
        }
        else if (options.Before is not null)
        {
            throw new ArgumentException("Before is only supported by Anthropic Files.", nameof(options));
        }

        if (options.Order is not null && options.Order is not ("asc" or "desc"))
        {
            throw new ArgumentException("Order must be 'asc' or 'desc'.", nameof(options));
        }

        if (options.Purpose is not null && !string.Equals(options.Purpose, "user_data", StringComparison.Ordinal))
        {
            throw new ArgumentException("Purpose must be 'user_data'.", nameof(options));
        }
    }

    private static void ValidateFileId(string fileId)
        => ArgumentGuard.ThrowIfNullOrWhiteSpace(fileId, nameof(fileId));

    private static void ValidateFileName(string fileName)
    {
        if (Encoding.UTF8.GetByteCount(fileName) > 512)
        {
            throw new ArgumentException("A file name must not exceed 512 UTF-8 bytes.", nameof(fileName));
        }
    }

    private Dictionary<string, string>? CreateHeaders()
        => _anthropic ? new() { ["anthropic-beta"] = "files-api-2025-04-14" } : null;

    private ClientResult<T> ReadResult<T>(ClientResult result)
    {
        if (!_anthropic)
        {
            return DeepSeekProtocol.CreateResult<T>(result);
        }

        var response = result.GetRawResponse();
        var json = JsonNode.Parse(response.Content.ToString())!.AsObject();
        if (typeof(T) == typeof(DeepSeekFileList))
        {
            foreach (var file in json["data"]!.AsArray())
            {
                NormalizeFile(file!.AsObject());
            }

            json["object"] = "list";
        }
        else if (typeof(T) == typeof(DeepSeekFileDeleteResult))
        {
            json["object"] = json["type"]?.DeepClone();
            json["deleted"] = json["type"]?.GetValue<string>() == "file_deleted";
        }
        else
        {
            NormalizeFile(json);
        }

        var value = json.Deserialize<T>(DeepSeekJson.SerializerOptions)
            ?? throw new JsonException("The file response could not be deserialized.");
        return ClientResult.FromValue(value, response);
    }

    private static void NormalizeFile(JsonObject file)
    {
        file["object"] = file["type"]?.DeepClone();
        file["bytes"] = file["size_bytes"]?.DeepClone();
        if (file["created_at"] is { } createdAt)
        {
            file["created_at"] = DateTimeOffset.Parse(createdAt.GetValue<string>(), CultureInfo.InvariantCulture).ToUnixTimeSeconds();
        }
    }
}
