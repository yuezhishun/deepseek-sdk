using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeepSeek.Tools;

namespace DeepSeek;

public partial class DeepSeekClient
{
    private const string SpeechPath = "/v1/tools/tts";
    private const int MaximumSpeechCharacters = 10000;
    private const int MaximumSpeechBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SpeechJson = new(DeepSeekJson.SerializerOptions)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ClientResult<OcrResult> Ocr(Stream stream, string fileName, string purpose, RequestOptions? options = null)
        => UploadToolFile<OcrResult>("/v1/tools/ocr", stream, fileName, ValidateOcrPurpose(purpose), 100, options);

    public Task<ClientResult<OcrResult>> OcrAsync(Stream stream, string fileName, string purpose, RequestOptions? options = null)
        => UploadToolFileAsync<OcrResult>("/v1/tools/ocr", stream, fileName, ValidateOcrPurpose(purpose), 100, options);

    public ClientResult<SpeechTranscription> TranscribeSpeech(Stream stream, string fileName, RequestOptions? options = null)
        => UploadToolFile<SpeechTranscription>("/v1/tools/asr", stream, fileName, null, 8, options);

    public Task<ClientResult<SpeechTranscription>> TranscribeSpeechAsync(Stream stream, string fileName, RequestOptions? options = null)
        => UploadToolFileAsync<SpeechTranscription>("/v1/tools/asr", stream, fileName, null, 8, options);

    /// <summary>Returns streaming audio. Dispose the raw response when finished reading.</summary>
    public ClientResult<Stream> SynthesizeSpeech(string text, SpeechSynthesisOptions? synthesisOptions = null, RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, SpeechPath);
        using var message = CreateSpeechRequest(uri, text, synthesisOptions, options);
        return ReadAudio(DeepSeekProtocol.Send(Pipeline, message, uri));
    }

    /// <summary>Returns streaming audio. Dispose the raw response when finished reading.</summary>
    public async Task<ClientResult<Stream>> SynthesizeSpeechAsync(string text, SpeechSynthesisOptions? synthesisOptions = null, RequestOptions? options = null)
    {
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, SpeechPath);
        using var message = CreateSpeechRequest(uri, text, synthesisOptions, options);
        return ReadAudio(await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false));
    }

    /// <summary>Sends text segments and yields complete audio messages as they arrive.</summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeSpeechStreamingAsync(
        IAsyncEnumerable<string> text, SpeechSynthesisOptions? options = null, ApiKeyCredential? credential = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentGuard.ThrowIfNull(text, nameof(text));
        ValidateSpeechOptions(options, webSocket: true);
        if (_credential is not null && credential is not null)
            throw new ArgumentException("A client with an API key credential already uses it for WebSocket speech synthesis.", nameof(credential));
        credential ??= _credential ?? throw new InvalidOperationException("This client requires an explicit credential for WebSocket speech synthesis.");
        credential.Deconstruct(out var key);
        var uri = new UriBuilder(DeepSeekClientUtilities.CombinePath(Endpoint, SpeechPath))
        {
            Scheme = Endpoint.Scheme == "https" ? "wss" : "ws",
            Query = "voice_id=" + (options?.VoiceId ?? "stella")
                + "&format=" + (options?.Format ?? "pcm")
                + "&filter_markdown=" + (options?.FilterMarkdown == false ? "false" : "true"),
        };
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + key);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stop.CancelAfter(TimeSpan.FromMinutes(30));
        await socket.ConnectAsync(uri.Uri, stop.Token).ConfigureAwait(false);
        var sender = SendSpeechTextAsync(socket, text, stop.Token);
        var buffer = new byte[8192];
        try
        {
            while (true)
            {
                using var payload = new MemoryStream();
                WebSocketReceiveResult frame;
                do
                {
                    var receive = socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    if (sender.Status != TaskStatus.RanToCompletion)
                    {
                        var ready = await Task.WhenAny(receive, sender).ConfigureAwait(false);
                        if (ready == sender && sender.Status != TaskStatus.RanToCompletion)
                        {
                            stop.Cancel();
                            _ = receive.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                            await sender.ConfigureAwait(false);
                        }
                    }

                    frame = await receive.ConfigureAwait(false);
                    if (frame.MessageType == WebSocketMessageType.Close)
                        throw new IOException("The speech WebSocket closed before a successful finish event.");
                    payload.Write(buffer, 0, frame.Count);
                } while (!frame.EndOfMessage);

                if (frame.MessageType == WebSocketMessageType.Binary)
                {
                    yield return payload.ToArray();
                    continue;
                }

                using var finish = JsonDocument.Parse(payload.ToArray());
                var root = finish.RootElement;
                if (root.GetProperty("event").GetString() != "finish")
                    throw new InvalidDataException("The speech WebSocket returned an unexpected text event.");
                var code = root.GetProperty("code").GetString();
                if (code != "success")
                {
                    var error = new IOException("Speech synthesis failed: " + code);
                    error.Data["code"] = code;
                    if (root.TryGetProperty("message", out var detail)) error.Data["message"] = detail.GetString();
                    throw error;
                }

                await sender.ConfigureAwait(false);
                yield break;
            }
        }
        finally
        {
            stop.Cancel();
            socket.Dispose();
            // Observe a concurrent sender failure without replacing the original receive outcome.
            _ = sender.ContinueWith(static task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static async Task SendSpeechTextAsync(ClientWebSocket socket, IAsyncEnumerable<string> text, CancellationToken cancellationToken)
    {
        long characters = 0;
        long bytes = 0;
        await foreach (var fragment in text.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(fragment)) continue;
            characters += CountSpeechCharacters(fragment);
            bytes += Encoding.UTF8.GetByteCount(fragment);
            if (characters > MaximumSpeechCharacters || bytes > MaximumSpeechBytes)
                throw new ArgumentException("Speech text exceeds the WebSocket session limit.", nameof(text));
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { @event = "text", text = fragment }, SpeechJson);
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }

        if (characters == 0) throw new ArgumentException("Speech text must contain at least one character.", nameof(text));
        var finish = Encoding.UTF8.GetBytes("{\"event\":\"finish_text\"}");
        await socket.SendAsync(new ArraySegment<byte>(finish), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private PipelineMessage CreateSpeechRequest(Uri uri, string text, SpeechSynthesisOptions? synthesisOptions, RequestOptions? options)
    {
        ArgumentGuard.ThrowIfNullOrWhiteSpace(text, nameof(text));
        ValidateSpeechOptions(synthesisOptions, webSocket: false);
        if (CountSpeechCharacters(text) > MaximumSpeechCharacters)
            throw new ArgumentException("Speech text exceeds 10,000 billing characters.", nameof(text));
        var format = synthesisOptions?.Format ?? "wav";
        var payload = JsonSerializer.Serialize(new
        {
            text,
            voice_id = synthesisOptions?.VoiceId,
            format,
            filter_markdown = synthesisOptions?.FilterMarkdown,
        }, SpeechJson);
        if (Encoding.UTF8.GetByteCount(payload) > MaximumSpeechBytes)
            throw new ArgumentException("The speech request exceeds 64 KiB.", nameof(text));
        var message = DeepSeekProtocol.CreateRequest(Pipeline, uri, "POST", null, options, streamResponse: true,
            headers: new Dictionary<string, string> { ["Accept"] = "audio/" + format, ["Content-Type"] = "application/json" });
        message.Request.Content = BinaryContent.CreateJson(payload, validate: false);
        return message;
    }

    private static ClientResult<Stream> ReadAudio(ClientResult result)
    {
        var response = result.GetRawResponse();
        if (response.ContentStream is { } stream) return ClientResult.FromValue(stream, response);
        response.Dispose();
        throw new InvalidDataException("The speech response did not contain an audio stream.");
    }

    private static void ValidateSpeechOptions(SpeechSynthesisOptions? options, bool webSocket)
    {
        if (options?.VoiceId is not (null or "stella" or "tide"))
            throw new ArgumentException("VoiceId must be 'stella' or 'tide'.", nameof(options));
        if (options?.Format is { } format && format != "pcm" && format != (webSocket ? "opus" : "wav"))
            throw new ArgumentException("The specified audio format is unsupported by this transport.", nameof(options));
    }

    private static int CountSpeechCharacters(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var codePoint = (int)text[i];
            if (char.IsSurrogatePair(text, i)) codePoint = char.ConvertToUtf32(text, i++);
            count += codePoint is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF)
                or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x2FA1F) or (>= 0x30000 and <= 0x323AF) ? 2 : 1;
        }
        return count;
    }

    private static string ValidateOcrPurpose(string purpose)
        => purpose is "text_recognition" or "rich_layout_recognition" ? purpose
            : throw new ArgumentException("Purpose must be 'text_recognition' or 'rich_layout_recognition'.", nameof(purpose));

    private ClientResult<T> UploadToolFile<T>(string path, Stream stream, string fileName, string? purpose, int maxMiB, RequestOptions? options)
    {
        DeepSeekUpload.ValidateFileName(fileName);
        var requestOptions = DeepSeekProtocol.GetBufferedRequestOptions(options, path);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, path);
        using var data = DeepSeekUpload.Read(stream, maxMiB * 1024 * 1024, requestOptions.CancellationToken);
        using var form = CreateToolForm(data, fileName, purpose);
        using var message = DeepSeekProtocol.CreateMultipartRequest(Pipeline, uri, form, requestOptions);
        return DeepSeekProtocol.CreateResult<T>(DeepSeekProtocol.Send(Pipeline, message, uri));
    }

    private async Task<ClientResult<T>> UploadToolFileAsync<T>(string path, Stream stream, string fileName, string? purpose, int maxMiB, RequestOptions? options)
    {
        DeepSeekUpload.ValidateFileName(fileName);
        var requestOptions = DeepSeekProtocol.GetBufferedRequestOptions(options, path);
        var uri = DeepSeekClientUtilities.CombinePath(Endpoint, path);
        using var data = await DeepSeekUpload.ReadAsync(stream, maxMiB * 1024 * 1024, requestOptions.CancellationToken).ConfigureAwait(false);
        using var form = CreateToolForm(data, fileName, purpose);
        using var message = DeepSeekProtocol.CreateMultipartRequest(Pipeline, uri, form, requestOptions);
        return await DeepSeekProtocol.CreateResultAsync<T>(await DeepSeekProtocol.SendAsync(Pipeline, message, uri).ConfigureAwait(false)).ConfigureAwait(false);
    }

    private static MultiPartFormContent CreateToolForm(Stream stream, string fileName, string? purpose)
    {
        var form = new MultiPartFormContent();
        if (purpose is not null) form.Add("purpose", purpose, "text/plain");
        DeepSeekUpload.AddFile(form, stream, fileName);
        return form;
    }
}
