using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using DeepSeek.Files;
using DeepSeek.Tools;
using Xunit;

namespace DeepSeek.Tests;

public sealed class DeepSeekApplicationToolsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ocr_PreservesPageOrderOriginalTextAndTruncation(bool synchronous)
    {
        using var handler = new ToolHandler("/v1/tools/ocr", """
            {"id":"ocr-1","purpose":"text_recognition","created":1762790000,"page_count":2,"pages":[
            {"index":0,"text":"<ref>First</ref>","finish_reason":"stop"},{"index":1,"text":"Second","finish_reason":"length"}],
            "usage":{"input_tokens":7,"output_tokens":5,"total_tokens":12}}
            """);
        using var input = new MemoryStream("document"u8.ToArray());
        var client = CreateClient(handler);
        var result = synchronous ? client.Ocr(input, "report.pdf", "text_recognition")
            : await client.OcrAsync(input, "report.pdf", "text_recognition");

        using var raw = result.GetRawResponse();
        Assert.Equal(200, raw.Status);
        Assert.Equal(2, result.Value.PageCount);
        Assert.Collection(result.Value.Pages,
            first => { Assert.Equal(0, first.Index); Assert.Equal("<ref>First</ref>", first.Text); },
            second => { Assert.Equal(1, second.Index); Assert.Equal("length", second.FinishReason); });
        Assert.Equal(12, result.Value.Usage?.TotalTokens);
        Assert.True(input.CanRead);
        handler.AssertUpload("report.pdf", "text_recognition");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscribeSpeech_OnlyUploadsFileAndReturnsRequestId(bool synchronous)
    {
        using var handler = new ToolHandler("/v1/tools/asr", """{"request_id":"asr-1","text":"Hello","duration_ms":850}""");
        using var input = new MemoryStream("audio"u8.ToArray());
        var client = CreateClient(handler);
        var result = synchronous ? client.TranscribeSpeech(input, "speech.wav")
            : await client.TranscribeSpeechAsync(input, "speech.wav");

        using var raw = result.GetRawResponse();
        Assert.Equal("asr-1", result.Value.RequestId);
        Assert.Equal("Hello", result.Value.Text);
        Assert.Equal(850, result.Value.DurationMs);
        handler.AssertUpload("speech.wav", purpose: null);
    }

    [Theory]
    [InlineData(false, "wav")]
    [InlineData(true, "pcm")]
    public async Task SynthesizeSpeech_ReturnsUnbufferedAudioOwnedByRawResponse(bool synchronous, string format)
    {
        using var audio = new AudioStream();
        using var handler = new ToolHandler("/v1/tools/tts", audio, format);
        var client = CreateClient(handler);
        var options = new SpeechSynthesisOptions { Format = format, VoiceId = "tide", FilterMarkdown = false };
        var result = synchronous ? client.SynthesizeSpeech("Hello **there**", options)
            : await client.SynthesizeSpeechAsync("Hello **there**", options);

        Assert.Equal(0, audio.Position);
        using (var raw = result.GetRawResponse())
        {
            Assert.Same(raw.ContentStream, result.Value);
            Assert.Equal(200, raw.Status);
            Assert.Equal((int)'R', result.Value.ReadByte());
        }
        Assert.False(audio.CanRead);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(format, body.RootElement.GetProperty("format").GetString());
        Assert.Equal("tide", body.RootElement.GetProperty("voice_id").GetString());
        Assert.False(body.RootElement.GetProperty("filter_markdown").GetBoolean());
        Assert.Equal("Hello **there**", body.RootElement.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 100)]
    public async Task Tools_RequestsAboveSizeLimitFailBeforeNetwork(bool ocr, int maxMiB)
    {
        using var handler = new ToolHandler("/unused", "{}");
        using var input = new LengthOnlyStream((long)maxMiB * 1024 * 1024 + 1);
        var client = CreateClient(handler);

        var error = ocr ? await Assert.ThrowsAsync<InvalidDataException>(() => client.OcrAsync(input, "report.pdf", "text_recognition"))
            : Assert.Throws<InvalidDataException>(() => client.TranscribeSpeech(input, "speech.wav"));
        Assert.Contains(maxMiB + " MiB", error.Message);
        Assert.Null(handler.Body);
    }

    [Fact]
    public async Task TranscribeSpeech_ExactlyEightMiBFromNonSeekableInputIsAccepted()
    {
        using var input = new NonSeekableInput(8 * 1024 * 1024);
        using var handler = new ToolHandler("/v1/tools/asr", """{"request_id":"asr-boundary"}""");
        var result = await CreateClient(handler).TranscribeSpeechAsync(input, "speech.ogg");
        using var raw = result.GetRawResponse();
        Assert.Equal("asr-boundary", result.Value.RequestId);
        Assert.Equal(8 * 1024 * 1024, input.BytesRead);
        handler.AssertUpload("speech.ogg", purpose: null);
    }

    [Fact]
    public async Task Tools_CancellationAndInvalidFormatStopBeforeUpload()
    {
        using var handler = new ToolHandler("/unused", "{}");
        using var input = new MemoryStream("document"u8.ToArray());
        var client = CreateClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.OcrAsync(input, "doc.pdf", "rich_layout_recognition",
            new RequestOptions { CancellationToken = new CancellationToken(true) }));
        Assert.Throws<ArgumentException>(() => client.Ocr(input, "doc.pdf", "user_data"));
        Assert.Throws<ArgumentException>(() => client.SynthesizeSpeech("text", new SpeechSynthesisOptions { Format = "opus" }));
        Assert.Throws<ArgumentException>(() => client.SynthesizeSpeech(new string('中', 5001)));
        Assert.Null(handler.Body);
    }

    [Fact]
    public void Ocr_Http200ErrorRetainsFailureDetails()
    {
        using var handler = new ToolHandler("/v1/tools/ocr", """{"error":{"message":"OCR timed out","code":"gateway_timeout_error"}}""");
        using var input = new MemoryStream("document"u8.ToArray());
        var response = CreateClient(handler).Ocr(input, "doc.pdf", "text_recognition");
        using var raw = response.GetRawResponse();
        Assert.Equal(200, raw.Status);
        Assert.Equal("gateway_timeout_error", response.Value.Error?.Code);
        Assert.Equal("OCR timed out", response.Value.Error?.Message);
    }

    [Fact]
    public async Task TranscribeSpeech_HttpErrorPreservesStatusAndMessage()
    {
        using var handler = new ToolHandler("/v1/tools/asr", """{"error":{"message":"audio format rejected"}}""", HttpStatusCode.BadRequest);
        using var input = new MemoryStream("audio"u8.ToArray());
        var error = await Assert.ThrowsAsync<DeepSeekException>(() => CreateClient(handler).TranscribeSpeechAsync(input, "speech.wav"));
        Assert.Equal(400, error.Status);
        Assert.Contains("audio format rejected", error.Message);
    }

    [Theory]
    [InlineData(false, "text_recognition")]
    [InlineData(true, "rich_layout_recognition")]
    public async Task Files_ParsesDocumentsUsingPurposeBeforeFile(bool anthropic, string purpose)
    {
        using var handler = new ToolHandler(anthropic ? "/anthropic/v1/files" : "/files",
            anthropic ? """{"id":"file-doc","type":"file","size_bytes":3,"created_at":"2026-11-14T10:00:00Z","mime_type":"application/pdf"}"""
                : """{"id":"file-doc","object":"file","bytes":3,"purpose":"text_recognition"}""",
            anthropic: anthropic);
        using var input = new MemoryStream("doc"u8.ToArray());
        var client = CreateClient(handler);
        var files = anthropic ? client.GetAnthropicFilesClient() : client.GetFilesClient();
        var result = await files.UploadFileAsync(input, "report.pdf", new FileUploadOptions { Purpose = purpose });

        using var raw = result.GetRawResponse();
        Assert.Equal("file-doc", result.Value.Id);
        Assert.Equal(3, result.Value.Bytes);
        if (anthropic) Assert.Equal("application/pdf", result.Value.MimeType);
        handler.AssertUpload("report.pdf", purpose);
    }

    [Theory]
    [InlineData("report.pdf", 100)]
    [InlineData("photo.png", 64)]
    [InlineData("notes.txt", 32)]
    [InlineData("data.xlsx", 32)]
    [InlineData("book.epub", 32)]
    public async Task Files_EnforcesDocumentImageAndTextSizeBoundaries(string fileName, int maxMiB)
    {
        using var handler = new ToolHandler("/unused", "{}");
        using var input = new LengthOnlyStream((long)maxMiB * 1024 * 1024 + 1);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => CreateClient(handler).GetFilesClient().UploadFileAsync(input, fileName));
        Assert.Contains(maxMiB + " MiB", exception.Message);
        Assert.Null(handler.Body);
    }

    private static DeepSeekClient CreateClient(ToolHandler handler) => new("test-key", new DeepSeekClientOptions
    {
        Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
        RetryPolicy = new ClientRetryPolicy(0),
    });

    private sealed class ToolHandler : HttpMessageHandler
    {
        private readonly string _path;
        private readonly HttpContent _content;
        private readonly HttpStatusCode _status;
        private readonly bool _anthropic;

        public ToolHandler(string path, string json, HttpStatusCode status = HttpStatusCode.OK, bool anthropic = false)
        {
            _path = path;
            _content = new StringContent(json, Encoding.UTF8, "application/json");
            _status = status;
            _anthropic = anthropic;
        }

        public ToolHandler(string path, Stream audio, string format)
        {
            _path = path;
            _status = HttpStatusCode.OK;
            _content = new StreamContent(audio);
            _content.Headers.ContentType = new("audio/" + format);
        }

        public string? Body { get; private set; }

        public void AssertUpload(string file, string? purpose)
        {
            var fileIndex = Body!.IndexOf("name=file", StringComparison.Ordinal);
            Assert.True(fileIndex >= 0);
            Assert.Contains(file, Body);
            var purposeIndex = Body.IndexOf("name=purpose", StringComparison.Ordinal);
            if (purpose is null) Assert.Equal(-1, purposeIndex);
            else
            {
                Assert.InRange(purposeIndex, 0, fileIndex - 1);
                Assert.Contains(purpose, Body);
            }
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(_path, request.RequestUri!.AbsolutePath);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            if (_anthropic)
                Assert.Equal("files-api-2025-04-14", Assert.Single(request.Headers.GetValues("anthropic-beta")));
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Assert.Equal(_path == "/v1/tools/tts" ? "application/json" : "multipart/form-data", request.Content.Headers.ContentType!.MediaType);
            return new HttpResponseMessage(_status) { Content = _content };
        }
    }

    private sealed class AudioStream() : MemoryStream("RIFF"u8.ToArray());

    private sealed class LengthOnlyStream(long length) : MemoryStream
    {
        public override long Length => length;
        public override int Read(byte[] buffer, int offset, int count) => throw new Xunit.Sdk.XunitException("Oversize seekable input must be rejected before reading.");
    }

    private sealed class NonSeekableInput(int length) : MemoryStream
    {
        public int BytesRead { get; private set; }
        public override bool CanSeek => false;
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(count, length - BytesRead);
            Array.Clear(buffer, offset, read);
            BytesRead += read;
            return Task.FromResult(read);
        }
    }
}
