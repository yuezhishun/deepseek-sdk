# DeepSeek.Core

Typed .NET SDK for DeepSeek APIs, built on `System.ClientModel`.

## Install

```bash
dotnet add package DeepSeek.Core --version 1.0.8
```

`DeepSeek.Core 1.0.8` depends on `System.ClientModel 1.14.0` and does not require Microsoft AI or AGUI packages.

Model identifiers are supplied by the caller as strings. `DeepSeek.Core` does not publish a fixed model enum or constants, so model name changes do not require a Core package upgrade.

## Changelog

Release 1.0.8 passed 95 Core tests on .NET 11 GA (SDK 11.0.100 and runtime 11.0.0) on November 11, 2026. The package targets `netstandard2.0`.

### Unreleased

The November 13 application APIs are available in the source tree. The install command above still targets the current published version.

The WebSocket streaming signature exposes `IAsyncEnumerable<T>`. The Core package declares `Microsoft.Bcl.AsyncInterfaces 10.0.13` so projects can compile against that signature with Core alone.

| API | Methods or request fields |
| --- | --- |
| `POST /v1/tools/ocr` | `Ocr` and `OcrAsync` take a stream, file name, and recognition purpose |
| `POST /v1/tools/asr` | `TranscribeSpeech` and `TranscribeSpeechAsync` take a WAV or Ogg Opus stream and name |
| `POST /v1/tools/tts` | `SynthesizeSpeech` and `SynthesizeSpeechAsync` return a WAV or PCM stream |
| `WebSocket /v1/tools/tts` | `SynthesizeSpeechStreamingAsync` sends text fragments and returns PCM messages or raw Opus packets |
| `/files` and `/anthropic/v1/files` | `UploadFile[Async]` supports `text_recognition` and `rich_layout_recognition` |
| Chat, Responses, and Messages | `deepseek_web_search` and `deepseek_read_url` tools, `SafetyCheck`, and `Title` |

OCR accepts files up to 100 MiB and returns original page text, zero-based page indices, per-page finish reasons, and usage. Check `OcrResult.Error` for errors that arrive after HTTP 200. ASR accepts uploads up to 8 MiB and returns text, duration in milliseconds, and a request ID. Keep ownership of the input stream. [OCR](https://api-docs.deepseek.com/zh-cn/guides/ocr/) · [ASR](https://api-docs.deepseek.com/zh-cn/guides/asr/)

```csharp
using DeepSeek;
using DeepSeek.Tools;
using System.ClientModel.Primitives;

var sdk = new DeepSeekClient(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
    ?? throw new InvalidOperationException("Set DEEPSEEK_API_KEY."));
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var request = new RequestOptions { CancellationToken = cancellation.Token };

using (var pdf = File.OpenRead("report.pdf"))
{
    var ocr = await sdk.OcrAsync(pdf, "report.pdf", "text_recognition", request);
    using var raw = ocr.GetRawResponse();
    if (ocr.Value.Error is { } error) throw new InvalidDataException(error.Message);
    foreach (var page in ocr.Value.Pages) Console.WriteLine(page.Text);
}

var speech = await sdk.SynthesizeSpeechAsync("Hello from DeepSeek.", new SpeechSynthesisOptions { Format = "wav" }, request);
using (var raw = speech.GetRawResponse())
using (var wav = File.Create("speech.wav"))
{
    await speech.Value.CopyToAsync(wav, cancellation.Token);
}

using (var wav = File.OpenRead("speech.wav"))
{
    var transcript = await sdk.TranscribeSpeechAsync(wav, "speech.wav", request);
    using var raw = transcript.GetRawResponse();
    Console.WriteLine(transcript.Value.Text);
}

using (var pcm = File.Create("speech.pcm"))
{
    await foreach (var packet in sdk.SynthesizeSpeechStreamingAsync(Segments(), cancellationToken: cancellation.Token))
        await pcm.WriteAsync(packet, cancellation.Token);
}

static async IAsyncEnumerable<string> Segments()
{
    yield return "Hello ";
    await Task.Yield();
    yield return "from DeepSeek.";
}
```

Dispose `GetRawResponse()` after reading HTTP audio. HTTP defaults to WAV and accepts `pcm`. WebSocket defaults to 24 kHz, 16-bit mono PCM and also accepts `opus`, which returns packets without an Ogg container. Complete the WebSocket enumeration to receive the service's final status. A client constructed only with a custom authentication policy accepts an explicit `ApiKeyCredential` for WebSocket speech. [TTS protocol and limits](https://api-docs.deepseek.com/zh-cn/guides/tts/)

For file inputs use `deepseek-flash`. Upload PDFs, Word, and PowerPoint files with a recognition purpose. A hosted file ID can be referenced in Chat via `ChatFileContentPart`, in Responses via `ResponseInputFileContent`, and in Messages via a `document` block with `Source.Type = "file"`. Messages also accepts a `document` source with type `text`, media type `text/plain`, and literal text in `Data`. Upload limits are 100 MiB for documents such as PDFs, 64 MiB for images, and 32 MiB for plain text, spreadsheets, and ebooks. The OpenAI client supports purpose filtering and expiry. Anthropic uses its own cursors and automatically sets the Files beta header. [Files](https://api-docs.deepseek.com/zh-cn/guides/files_api/) · [Document parsing](https://api-docs.deepseek.com/zh-cn/guides/document_parsing/)

For native multi-turn requests, keep the assistant's signed `server_tool_calls` in Chat, replay Responses `output` as input items, or reuse the Messages assistant content blocks. A final `content_filter`, `incomplete_details.reason = "content_filter"`, or `stop_reason = "refusal"` means provisional streamed text must be discarded. Inspect `Error` and the terminal event for failures after HTTP 200. [Web tools and replay](https://api-docs.deepseek.com/zh-cn/guides/web_tools/) · [Model safety](https://api-docs.deepseek.com/zh-cn/guides/safety/)

### 1.0.8

- Verified the .NET 11 GA build, tests, and packages. Updated the package readme at version 1.0.8 and assembly version 1.0.8.0.

### 1.0.7

- Published version 1.0.7 with assembly version 1.0.7.0 alongside the Microsoft AI adapter JSON output fix.

### 1.0.6

- Published version 1.0.6 with assembly version 1.0.6.0 alongside Agents and Decisions.
- DeepSeek.Decisions builds on the public Chat client, image content, authentication, and HTTP pipeline. [Decisions package guide](https://github.com/yuezhishun/deepseek-sdk/tree/main/src/DeepSeek.Decisions)

### 1.0.5

- Added `Search` and `SearchAsync` returning `ClientResult<IReadOnlyList<SearchResult>>`. Options include Query, Count, OriginalQuestion, Localization, and NeedContent.
- Added `Fetch` and `FetchAsync` returning the page title and Markdown body. Use `GetRawResponse()` for the underlying HTTP response.
- Added `GetAnthropicFilesClient()`, automatic Files beta headers, the Before cursor, and MimeType. Unsupported protocol options raise argument errors.
- Added Low and None reasoning effort and retained High and Max values. Chat uses `user_id`, Responses uses `user`, and Anthropic uses `metadata.user_id`.
- Fixed file retrieval and deletion paths and the beta header for nested file images. Examples use `deepseek-flash` and `deepseek-v4-pro`.
- Version 1.0.5 targets `netstandard2.0` with assembly version 1.0.5.0 and was verified on .NET 11 RC3.

Search accepts 1–128 Unicode characters in Query and OriginalQuestion and a Count of 1–50. Fetch requires an HTTP(S) URL of at most 8192 characters without credentials. File names are limited to 512 UTF-8 bytes. Anthropic Files listing accepts either After or Before and a Limit. File expiration is available on OpenAI Files.

```csharp
var sdk = new DeepSeek.DeepSeekClient("your-api-key");
var search = await sdk.SearchAsync(new DeepSeek.Search.SearchRequest { Query = "DeepSeek API", Count = 1 });
if (search.Value.Count > 0)
{
    var page = await sdk.FetchAsync(search.Value[0].Url);
    Console.WriteLine(page.Value.Content);
}
var anthropicFiles = sdk.GetAnthropicFilesClient();
```

Protocol references reviewed on November 8, 2026. [Search](https://api-docs.deepseek.com/zh-cn/api/search/) · [Fetch](https://api-docs.deepseek.com/zh-cn/api/fetch/) · [Files](https://api-docs.deepseek.com/zh-cn/guides/files_api/)

- Added typed Chat image/text/file content, Responses `input_image`, Anthropic image sources, and `FilesClient`.
- The Chat content property is now a typed string-or-parts union, which is a public API breaking change.

- Redesigned typed Responses models with polymorphic input/output items and content parts.
- Added function, web-search, `apply_patch`, tool-choice, JSON object/schema, annotations, usage, error, and unknown-field support.
- Added raw-result and semantic SSE streaming APIs; terminal events are `response.completed`, `response.incomplete`, and `response.failed`.
- Audited Chat Completions, prefix continuation, FIM, and Anthropic API support for the v4 flash/pro models.

## Usage

Version 1.0.5 adds typed multimodal Chat/Responses/Anthropic content and the Files API. Model identifiers, including `deepseek-flash`, are supplied by the caller.

```csharp
using DeepSeek;
using DeepSeek.Chat;

var client = new DeepSeekClient("your-api-key");
var chat = client.GetChatClient("deepseek-flash");

var response = await chat.CompleteChatAsync(new ChatCompletionRequest
{
    Messages =
    [
        new ChatMessage { Role = "user", Content = "Hello" }
    ]
});

Console.WriteLine(response.Value.Choices[0].Message?.Content?.Text);
```

### Responses API

```csharp
using DeepSeek;
using DeepSeek.Responses;

var client = new DeepSeekClient("your-api-key");
var responses = client.GetResponsesClient("deepseek-flash");
var response = await responses.CreateResponseAsync(new ResponseRequest
{
    Instructions = "You are a helpful assistant.",
    Input = "Hello",
});

var text = response.Value.Output
    .SelectMany(static item => item.Content)
    .FirstOrDefault(static content => content.Type == "output_text")?.Text;
Console.WriteLine(text);
```

`Response.Value.OutputText` is a convenience for concatenating output text. Responses is stateless, so pass the full input history on each request. Use `CreateResponseStreaming` for typed semantic events and `CreateResponseRaw` when the raw `ClientResult` is needed.

## Logging

Request/response metadata logging remains available through `ClientLoggingOptions`.
Request/response body logging is disabled by default and must be explicitly enabled on `DeepSeekClientOptions`:

```csharp
using DeepSeek;
using Microsoft.Extensions.Logging;
using System.ClientModel.Primitives;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddConsole();
});

var client = new DeepSeekClient("your-api-key", new DeepSeekClientOptions
{
    AllowMessageContentLogging = true,
    ClientLoggingOptions = new ClientLoggingOptions
    {
        LoggerFactory = loggerFactory,
        EnableLogging = true,
        EnableMessageLogging = true,
        EnableMessageContentLogging = true,
        MessageContentSizeLimit = 1024 * 1024,
    }
});
```

Keep `AllowMessageContentLogging` disabled for streaming scenarios unless body logging is strictly required for debugging.

## Included clients

- `ChatClient` for chat completions
- `CompletionsClient` for FIM completions
- `ModelsClient` for model listing
- `BalanceClient` for billing and balance queries
- `AnthropicClient` for the Anthropic-style messages API exposed by DeepSeek
- `ResponsesClient` for the stateless Responses API, including semantic SSE streaming
- `FilesClient` for upload, listing, retrieval, and deletion of vision files

Multimodal Chat content uses `ChatTextContentPart`, `ChatImageContentPart`, and `ChatFileContentPart`. Images may be HTTP(S)/data URLs or `file_id` values returned by `FilesClient`. Responses uses `ResponseInputImageContent`, and Anthropic uses `AnthropicImageSource` with `base64`, `url`, or `file` sources. JPEG, PNG, GIF, and WebP are supported. Hosted image uploads accept up to 64 MiB. Configure OpenAI Files expiry between 3,600 and 2,592,000 seconds.

The NuGet package is named `DeepSeek.Core`; the public namespaces remain `DeepSeek` and `DeepSeek.*`.
