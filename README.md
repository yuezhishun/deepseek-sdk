# deepseek-sdk

当前 SDK 版本为 **1.0.7**，安装命令见下方；Microsoft Agent Framework 引用已更新至 **1.28.0**。

`deepseek-sdk` is a .NET SDK for DeepSeek with three primary libraries:

- `src/DeepSeek.Core`: built on `System.ClientModel` and the HTTP pipeline model, providing a consistent request handling model, better control over cross-cutting concerns such as logging, retries, and diagnostics, and a stronger foundation for extensible typed SDK clients
- `src/DeepSeek.Agents.AI`: extension and adapter support for Microsoft Agent Framework
- `src/DeepSeek.Decisions`：从 `1.0.6` 起提供独立的 Jev/Decisions 决策类库，支持文本、JSON、图片和概率来源诊断；[中文使用说明](src/DeepSeek.Decisions/README.md)。
- accepts model identifiers supplied by the caller, so applications can follow current and future DeepSeek model names without a Core package upgrade
- support for DeepSeek's two Beta APIs: chat prefix continuation and FIM completion
- implements thinking mode following DeepSeek's official documentation, including reasoning enablement, reasoning effort control, `reasoning_content` handling, and continued reasoning semantics across multi-turn conversations and tool calls

## Changelog

### 1.0.7

- 普通 JSON 与 Schema 模式统一使用精简中文提示词，保留调用方的语言要求并移除无关示例。
- Chat、Responses、Anthropic 补齐最终 JSON / Schema 校验；Anthropic 不再忽略 `ResponseFormat`，Responses 对原生服务端不接受的数组根节点与字典 Schema 使用提示词和本地校验回退。
- 修复 Chat、Anthropic 流式空格片段丢失；新增 138 个离线回归用例及在线覆盖，验证 .NET 11 RC3。
- 启用 JSON 格式时，空白、非法 JSON、截断或 Schema 不匹配会抛出 `JsonException`；流式结果须完整枚举且未抛异常才算校验成功。详见 [JSON 输出说明](src/DeepSeek.Agents.AI/README.md#json-output)。
- 三个包统一为 `1.0.7` / `1.0.7.0`；Agents 新增 MIT 许可依赖 `JsonSchema.Net 8.0.5`。

### 1.0.6

- 新增 `DeepSeek.Decisions`，提供 Jev/System One 和 Decisions 双接口、图片证据、四种概率读取方法、自动回退与诊断。
- 新增决策单元测试及文本/图片在线用例；三个 NuGet 包统一升级到 `1.0.6` / `1.0.6.0`，通过 `v1.0.6` tag 和 NuGet trusted publishing 发布。

### 1.0.5

- 补齐独立 Search/Fetch 和 Anthropic Files，修复文件路径、图片工具结果及 Chat `user_id`。
- 补齐 `None/Low/High/Max` 推理、选项优先级、Anthropic 流式工具参数和服务端搜索历史。
- 统一缓存、搜索及抓取用量；保持 `netstandard2.0`，增加 .NET 11 RC3 验证，包版本仍为 `1.0.5`。
- [新增接口、兼容性边界与 .NET 11 验收命令](README_CN.md#net-11-与验收命令)。

- Added typed multimodal content for Chat Completions, Responses `input_image`, and Anthropic image sources.
- Added the OpenAI-compatible Files API client with upload, list, retrieve, and delete operations.
- Added URL, base64/data URL, and hosted `file_id` mappings to all Microsoft AI adapters.
- The unified typed content model is a public API breaking change. Image-capable model identifiers remain caller-supplied strings.

- Redesigned the public Responses models as typed polymorphic request, tool, output-item, content-part, and semantic SSE event models.
- Added Responses support for JSON object/schema output, function tools, server-side web search, the `apply_patch` custom tool, reasoning, annotations, usage details, and unknown-field preservation.
- Added synchronous, asynchronous, raw-result, and semantic SSE APIs plus independent `Microsoft.Extensions.AI`/`Microsoft.Agents.AI` adapters.
- Audited Chat JSON Output, strict tools, logprobs, user identifiers, prefix continuation, FIM, and Anthropic thinking/tool streaming compatibility.
- Updated both release packages and centralized package versions to `1.0.5`; the Responses model redesign is a public API breaking change.
- Updated the dependency baseline to `Microsoft.Agents.AI 1.28.0` and `Microsoft.Extensions.AI 10.11.0`, including AGUI hosting preview compatibility for .NET 10 samples and integration tests.

## Installation

```bash
dotnet add package DeepSeek.Core --version 1.0.7
dotnet add package DeepSeek.Agents.AI --version 1.0.7
dotnet add package DeepSeek.Decisions --version 1.0.7
```

The core NuGet package is named `DeepSeek.Core`, while the public namespaces remain `DeepSeek` and `DeepSeek.*`.

### Microsoft AI compatibility

`DeepSeek.Agents.AI` is built and tested against `Microsoft.Agents.AI 1.28.0` and `Microsoft.Extensions.AI 10.11.0`.

The AGUI samples and AGUI integration tests use `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore 1.28.0-preview.261106.1` and target .NET 10. The AGUI package is a preview dependency; the regular Chat and Responses adapters do not require it.

## Projects

### `src/DeepSeek.Core`

The `DeepSeek.Core` package is the low-level typed SDK for calling DeepSeek APIs from .NET.

It includes:

- `DeepSeekClient` as the main entry point
- `ChatClient` for chat completions
- `CompletionsClient` for FIM （Fill In the Middle）completions
- `ModelsClient` for model listing
- `BalanceClient` for billing and balance queries
- `AnthropicClient` for the Anthropic-style messages API exposed by DeepSeek
- `ResponsesClient` for the stateless Responses API and semantic SSE streaming
- `FilesClient` for the OpenAI-compatible Files API

Responses requests are stateless: send the complete `Input` history on every call. `Input` and `Instructions` must not both be absent.

Model identifiers are caller configuration. The SDK accepts them as strings and does not publish a fixed model enum or constants, so a model name change does not require upgrading the Core package.

```csharp
using DeepSeek;
using DeepSeek.Responses;

var responses = new DeepSeekClient("your-api-key")
    .GetResponsesClient("your-model-id");

var response = await responses.CreateResponseAsync(new ResponseRequest
{
    Instructions = "You are a concise assistant.",
    Input = "Return the answer as JSON.",
    Text = new ResponseTextOptions
    {
        Format = ResponseTextFormat.JsonSchema(
            "answer",
            new { type = "object", properties = new { answer = new { type = "string" } }, required = new[] { "answer" } }),
    },
    Tools =
    [
        new ResponseFunctionTool
        {
            Name = "lookup_weather",
            Description = "Look up the weather for a city.",
            Parameters = new { type = "object", properties = new { city = new { type = "string" } } },
            Strict = true,
        },
        new ResponseWebSearchTool(),
        new ResponseCustomTool { Name = "apply_patch" },
    ],
});

Console.WriteLine(response.Value.OutputText);
```

#### Multimodal requests and Files API

Image-capable models are selected by the caller, for example `deepseek-flash`; the SDK does not validate or enumerate model names. Chat accepts text, URL/data-URL images, and Files API references:

```csharp
using DeepSeek.Chat;

var chat = new DeepSeekClient("your-api-key").GetChatClient("deepseek-flash");
var response = await chat.CompleteChatAsync(new ChatCompletionRequest
{
    Messages =
    [new ChatMessage
    {
        Role = "user",
        Content = new ChatContentPart[]
        {
            new ChatTextContentPart("What is in these images?"),
            new ChatImageContentPart("https://example.com/image.png", "high"),
            new ChatImageContentPart("data:image/png;base64,...", "auto"),
            new ChatFileContentPart { FileId = "file-api-..." },
        },
    }],
});
```

Responses uses `ResponseInputImageContent` (`ImageUrl`, `Detail`, or `FileId`) inside a `ResponseContentValue`; Anthropic uses `AnthropicContentBlock { Type = "image", Source = new AnthropicImageSource { ... } }`. The Microsoft AI adapter maps `DataContent`, `UriContent`, and `HostedFileContent` automatically; set `DeepSeekChatOptionKeys.ImageDetail` in `ChatOptions.AdditionalProperties` to choose image detail.

Files can be uploaded and then referenced by ID:

```csharp
var files = new DeepSeekClient("your-api-key").GetFilesClient();
await using var stream = File.OpenRead("image.png");
var file = await files.UploadFileAsync(stream, "image.png");
var page = await files.ListFilesAsync(new FileListOptions { Limit = 100, Order = "desc" });
var details = await files.RetrieveFileAsync(file.Value.Id!);
await files.DeleteFileAsync(file.Value.Id!);
```

Supported image formats are JPEG, PNG, GIF, and WebP. Inline image URLs/data URLs, request body limits, and the 64 MiB Files upload limit are service limits; Files expiry is 3,600–2,592,000 seconds when configured.

For streaming, enumerate `CreateResponseStreaming(...)`. Events expose their semantic `Type`, `SequenceNumber`, typed `Item`/`ContentPart`, and terminal `Response`; the stream ends with `response.completed`, `response.incomplete`, or `response.failed`.

`ChatClient` uses `response_format: { "type": "json_object" }` for Chat JSON Output. Strict function schemas are a Beta capability. `GetChatPrefixClient(...)` targets `/beta/chat/completions` and validates the final assistant message with `Prefix = true`; `CompletionsClient` targets `/beta/completions` and accepts `Prompt`, `Suffix`, `Stop`, and `Stream`.

`DeepSeek.Agents.AI` 的 Chat、Responses、Anthropic 适配器均支持 `ChatOptions.ResponseFormat`，普通 JSON 与 Schema 提示词统一使用精简中文，并校验最终 JSON / Schema。Anthropic 使用提示词和本地校验实现格式约束；Responses 对服务端不接受的非对象根节点和字典 Schema 使用相同回退。流式结果需完整枚举且未抛异常后才能视为已校验，详见 [JSON 输出说明](src/DeepSeek.Agents.AI/README.md#json-output)。

`AnthropicClient` targets `/anthropic/v1/messages` and supports system prompts, stop sequences, thinking, output effort, tool use/results, and the documented streaming events. DeepSeek ignores unsupported Anthropic fields such as `top_k`, while `metadata.user_id` is supported.

Typical usage:

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
```

#### Thinking and reasoning

The typed SDK supports DeepSeek thinking mode directly on chat requests.

Use:

- `Thinking = ThinkingMode.Enabled` to enable reasoning output
- `ReasoningEffort = ChatReasoningEffort.High` or `ChatReasoningEffort.Max` to control effort
- `StreamOptions.IncludeUsage` when you want usage to be returned on streaming responses

Example:

```csharp
using DeepSeek;
using DeepSeek.Chat;

var client = new DeepSeekClient("your-api-key");
var chat = client.GetChatClient("deepseek-flash");

var response = await chat.CompleteChatAsync(new ChatCompletionRequest
{
    Thinking = ThinkingMode.Enabled,
    ReasoningEffort = ChatReasoningEffort.High,
    Messages =
    [
        new ChatMessage
        {
            Role = "user",
            Content = "Solve 17 * 19 and explain the reasoning briefly."
        }
    ]
});

var message = response.Value.Choices[0].Message;
Console.WriteLine(message?.ReasoningContent);
Console.WriteLine(message?.Content?.Text);
```

### `src/DeepSeek.Agents.AI`

The `DeepSeek.Agents.AI` package adapts the typed SDK to the abstractions from `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

It includes:

- `AsIChatClient(...)` extension methods for `ChatClient` and `AnthropicClient`
- independent `AsIChatClient(...)` and `AsAIAgent(...)` extensions for `ResponsesClient`
- `DeepSeekChatClientOptions` for adapter-specific configuration
- support for `ChatOptions`, tool calls, streaming updates, and reasoning metadata
- integration helpers for agent scenarios built on `Microsoft.Agents.AI`

Typical usage:

```csharp
using DeepSeek;
using Microsoft.Extensions.AI;
using DeepSeek.Agents.AI;

var client = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-flash")
    .AsIChatClient();

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "Hello")
]);
```

#### Thinking mode through `Microsoft.Extensions.AI`

The adapter maps `ChatOptions.Reasoning` to DeepSeek reasoning settings and surfaces reasoning content back as `TextReasoningContent` plus `AdditionalProperties["reasoning_content"]`.

When you build a multi-turn conversation with tool calls under thinking mode, keep the reasoning chain together with the assistant tool-call message in the next round. In this repository, the adapter maps that through `TextReasoningContent` and `AdditionalProperties["reasoning_content"]`, and writes it back to the wire field used by DeepSeek for continued reasoning.

Example:

```csharp
using DeepSeek;
using Microsoft.Extensions.AI;
using DeepSeek.Agents.AI;

var client = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-flash")
    .AsIChatClient();

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "Compare TCP and UDP.")
],
new ChatOptions
{
    Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High }
});

var message = response.Messages[0];
var reasoning = message.Contents.OfType<TextReasoningContent>().FirstOrDefault()?.Text;
Console.WriteLine(reasoning);
Console.WriteLine(message.Text);
```

For multi-turn tool scenarios, preserve:

- the assistant tool call
- the corresponding tool result
- the assistant reasoning content from the previous turn

This is required when the model must continue reasoning after a tool invocation.

#### AGUI example

The repository also includes AGUI-oriented samples under `sample/`, including:

- `sample/DeepSeek.Agui.Agent`: ASP.NET Core hosted AGUI endpoint
- `sample/DeepSeek.Agui.Console`: console client sample
- `sample/DeepSeek.Agui.CustomClient`: custom AGUI event-stream client
- `sample/DeepSeek.Agui.Web`: web UI sample

Minimal hosted AGUI example:

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUIServer();

var app = builder.Build();

var chatClient = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-flash");

var agent = chatClient.AsAIAgent(
    name: "DeepSeekAgUiHostedAgent",
    instructions: "You are a concise assistant.");

app.MapAGUIServer("/agui", agent);
app.Run();
```

If you want a ready-to-run reference, start from `sample/DeepSeek.Agui.Agent` and connect `sample/DeepSeek.Agui.Web` to that endpoint.

Before running the sample or live integration tests locally, create the ignored local configuration file once:

```bash
cp sample/appsettings.dev.json sample/appsettings.json
```

Then modify only the ignored `sample/appsettings.json`:

```json
{
  "apiKey": "your-real-deepseek-api-key"
}
```

Without this file, tests use `test-key` from `sample/appsettings.dev.json` and automatically skip live network tests.

## Repository Layout

- `src/DeepSeek.Core`: typed SDK
- `src/DeepSeek.Agents.AI`: AI abstraction adapter
- `src/DeepSeek.Decisions`：Jev/Decisions 决策类库
- `test/DeepSeek.Tests`: unit tests for the typed SDK
- `test/DeepSeek.Agents.AI.UnitTests`: unit tests for the adapter
- `test/DeepSeek.Decisions.UnitTests`：决策类库的单元测试
- `test/DeepSeek.IntegrationTests`: live integration tests
- `sample/`: example projects only

## More SDK examples

### Get model list

```csharp
using DeepSeek;

var client = new DeepSeekClient("your-api-key");
var modelsClient = client.GetModelsClient();
var response = await modelsClient.GetModelsAsync();

foreach (var model in response.Value.Data)
{
    Console.WriteLine(model.Id);
}
```

### Get balance

```csharp
using DeepSeek;

var client = new DeepSeekClient("your-api-key");
var balanceClient = client.GetBalanceClient();
var response = await balanceClient.GetBalanceAsync();

foreach (var balance in response.Value.BalanceInfos)
{
    Console.WriteLine($"{balance.Currency}: {balance.TotalBalance}");
}
```

### Chat prefix continuation (Beta)

```csharp
using DeepSeek;
using DeepSeek.Chat;

var client = new DeepSeekClient("your-api-key");
var chat = client.GetChatPrefixClient("deepseek-flash");

var response = await chat.CompleteChatAsync(new ChatCompletionRequest
{
    Messages =
    [
        new ChatMessage { Role = "user", Content = "Write a short greeting for a new user." },
        new ChatMessage
        {
            Role = "assistant",
            Content = "Hello and welcome",
            Prefix = true
        }
    ]
});

Console.WriteLine(response.Value.Choices[0].Message?.Content?.Text);
```

For prefix continuation requests, the final message must be an assistant message with `Prefix = true`.

### FIM completion

The completions API supports fill-in-the-middle style completion by combining `Prompt` and `Suffix`.

```csharp
using DeepSeek;
using DeepSeek.Completions;

var client = new DeepSeekClient("your-api-key");
var completions = client.GetCompletionsClient("deepseek-flash");

var response = await completions.CompleteTextAsync(new CompletionRequest
{
    Prompt = "public static int Add(int a, int b)\n{\n    ",
    Suffix = "\n}",
    Temperature = 0,
    MaxTokens = 32,
});

Console.WriteLine(response.Value.Choices[0].Text);
```

## Build and Test

Build the solution from the repository root:

```bash
dotnet build DeepSeek.slnx
```

Run tests for the supported libraries:

```bash
dotnet test DeepSeek.slnx
```

Targeted test commands:

```bash
dotnet test test/DeepSeek.Tests/DeepSeek.Tests.csproj
dotnet test test/DeepSeek.Agents.AI.UnitTests/DeepSeek.Agents.AI.UnitTests.csproj
```

## Request and response logging

If you need complete request and response packet logging for debugging, configure `DeepSeekClientOptions.ClientLoggingOptions`.

Typical configuration:

```csharp
using DeepSeek;
using Microsoft.Extensions.Logging;
using System.ClientModel.Primitives;

var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.SetMinimumLevel(LogLevel.Trace);
    builder.AddConsole();
});

var clientOptions = new DeepSeekClientOptions
{
    AllowMessageContentLogging = true,
    ClientLoggingOptions = new ClientLoggingOptions
    {
        LoggerFactory = loggerFactory,
        EnableLogging = true,
        EnableMessageLogging = true,
        EnableMessageContentLogging = true,
        MessageContentSizeLimit = 1024 * 1024
    }
};

var client = new DeepSeekClient("your-api-key", clientOptions);
```

Notes:

- `EnableMessageLogging = true` enables request and response message logging.
- `DeepSeekClientOptions.AllowMessageContentLogging = true` is the explicit SDK opt-in for body logging.
- `ClientLoggingOptions.EnableMessageContentLogging = true` only takes effect when the SDK-level opt-in is also enabled.
- Message-content logging is disabled by default to keep streaming responses single-consumption-safe.
- `MessageContentSizeLimit` controls how much body content is emitted.
- Configure your logger provider such as console, Serilog, or another sink.
- Streaming scenarios should keep body logging disabled unless payload inspection is strictly required for debugging.
- Be careful with sensitive data when enabling full payload logging in production.

## Notes

- 测试覆盖 `src/DeepSeek.Core`、`src/DeepSeek.Agents.AI` 和 `src/DeepSeek.Decisions`。
- Projects under `sample/` are examples and are not covered by dedicated test projects.
