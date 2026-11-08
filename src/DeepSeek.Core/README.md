# DeepSeek.Core

Typed .NET SDK for DeepSeek APIs, built on `System.ClientModel`.

## Install

```bash
dotnet add package DeepSeek.Core --version 1.0.5
```

`DeepSeek.Core 1.0.5` depends on `System.ClientModel 1.14.0` and does not require Microsoft AI or AGUI packages.

Model identifiers are supplied by the caller as strings. `DeepSeek.Core` does not publish a fixed model enum or constants, so model name changes do not require a Core package upgrade.

## Changelog

### 1.0.5

- 新增 `DeepSeekClient.Search/SearchAsync(SearchRequest, RequestOptions?)`，返回 `ClientResult<IReadOnlyList<SearchResult>>`；参数包含 `Query/Count/OriginalQuestion/Localization/NeedContent`。
- 新增 `Fetch/FetchAsync(string, RequestOptions?)`，返回 `ClientResult<FetchResult>`（标题、Markdown 正文）。原始响应统一通过 `GetRawResponse()` 获取。
- 新增 `GetAnthropicFilesClient()`，复用同步、异步文件操作；自动发送 beta header，新增 `Before`、`MimeType`，转换大小、RFC 3339 时间及删除结果。不支持的协议选项明确报错。
- `ChatReasoningEffort` 新增 `Low/None`，保留 `High/Max` 数值；Chat 用户字段改为 `user_id`。Responses 保持 `user`，Anthropic 保持 `metadata.user_id`。
- 修复文件查询/删除路径及嵌套文件图片的 beta header。默认示例模型为 `deepseek-flash` / `deepseek-v4-pro`，仍接受任意模型字符串。
- 包版本 `1.0.5`、程序集 `1.0.5.0`，目标 `netstandard2.0`；兼容性测试增加 .NET 11 RC3（SDK `11.0.100-rc.3.26522.148`），GA 尚待发布后复测。

搜索：`Query` / `OriginalQuestion` 为 1–128 个 Unicode 字符，`Count` 为 1–50。Fetch URL 必须为不带凭据的 HTTP(S) 地址且长度不超过 8192。文件限制为 64 MiB / 文件名 512 UTF-8 字节。Anthropic 列表仅支持互斥 `After/Before` 及 `Limit`；有效期仅用于 OpenAI Files。

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

协议依据：[Search](https://api-docs.deepseek.com/zh-cn/api/search/)、[Fetch](https://api-docs.deepseek.com/zh-cn/api/fetch/)、[Files](https://api-docs.deepseek.com/zh-cn/guides/files_api/)（2026-11-08 复核）。

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

Multimodal Chat content uses `ChatTextContentPart`, `ChatImageContentPart`, and `ChatFileContentPart`. Images may be HTTP(S)/data URLs or `file_id` values returned by `FilesClient`. Responses uses `ResponseInputImageContent`, and Anthropic uses `AnthropicImageSource` with `base64`, `url`, or `file` sources. JPEG, PNG, GIF, and WebP are supported; Files uploads are limited to 64 MiB and configured expiry is 3600–2592000 seconds.

The NuGet package is named `DeepSeek.Core`; the public namespaces remain `DeepSeek` and `DeepSeek.*`.
