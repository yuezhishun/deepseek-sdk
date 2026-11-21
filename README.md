# deepseek-sdk

The SDK version is **1.1.0**. The Microsoft AI adapter uses Microsoft Agent Framework **1.28.0**. [Simplified Chinese README](README_CN.md)

Release 1.0.8 passed all 387 tests on the .NET 11 GA SDK (11.0.100) and runtime (11.0.0). Both libraries target `netstandard2.0`.

`deepseek-sdk` is a .NET SDK for DeepSeek with two primary libraries:

- `src/DeepSeek.Core`: built on `System.ClientModel` and the HTTP pipeline model, providing a consistent request handling model, better control over cross-cutting concerns such as logging, retries, and diagnostics, and a stronger foundation for extensible typed SDK clients
- `src/DeepSeek.Agents.AI`: extension and adapter support for Microsoft Agent Framework
- accepts model identifiers supplied by the caller, so applications can follow current and future DeepSeek model names without a Core package upgrade
- support for DeepSeek's two Beta APIs: chat prefix continuation and FIM completion
- implements thinking mode following DeepSeek's official documentation, including reasoning enablement, reasoning effort control, `reasoning_content` handling, and continued reasoning semantics across multi-turn conversations and tool calls

`DeepSeek.Decisions` provides Jev and Decisions APIs. Its development and releases moved to a separate repository. [Decisions SDK](https://github.com/yuezhishun/decisions-sdk)

## API coverage

| DeepSeek API | SDK entry point | Availability |
| --- | --- | --- |
| Chat Completions | `GetChatClient(model)` | 1.0.8 |
| Chat prefix continuation (Beta) | `GetChatPrefixClient(model)` | 1.0.8 |
| Responses | `GetResponsesClient(model)` | 1.0.8 |
| Anthropic Messages | `GetAnthropicClient(model)` | 1.0.8 |
| FIM completion (Beta) | `GetCompletionsClient(model)` | 1.0.8 |
| Models and Balance | `GetModelsClient()` and `GetBalanceClient()` | 1.0.8 |
| Search and Fetch | `Search[Async]` and `Fetch[Async]` | 1.0.8 |
| File upload, list, retrieve, and delete | `GetFilesClient()` and `GetAnthropicFilesClient()` | 1.0.8 |
| Parsed file uploads and references across chat protocols | Files upload purposes and protocol-specific document content | 1.1.0 |
| Web, deep, and image search, URL reading, safety, and titles | Chat, Responses, and Messages request options | 1.1.0 |
| OCR and speech recognition | `Ocr[Async]` and `TranscribeSpeech[Async]` | 1.1.0 |
| HTTP and WebSocket speech synthesis | `SynthesizeSpeech[Async]` and `SynthesizeSpeechStreamingAsync` | 1.1.0 |

Use `deepseek-flash` for the new conversation extensions and file inputs. Standalone tools have no model parameter. Check the model documentation for features specific to other models. [Official API directory](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion/) · [November 13 announcement](https://api-docs.deepseek.com/zh-cn/news/news261113/)

### Use the conversation extensions

Install DeepSeek.Agents.AI 1.1.0 to run this example. The three native clients expose the same opt-in features through their typed requests. [Core API examples](src/DeepSeek.Core/README.md#110) · [Microsoft AI options and multi-turn example](src/DeepSeek.Agents.AI/README.md#application-extensions)

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

var sdk = new DeepSeekClient(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
    ?? throw new InvalidOperationException("Set DEEPSEEK_API_KEY."));
using var chat = sdk.GetChatClient("deepseek-flash").AsIChatClient(new DeepSeekChatClientOptions
{
    EnableReadUrl = true,
    SafetyCheckLevel = "high",
    GenerateTitle = true,
});

var answer = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Read https://example.com and summarize it.")]);
Console.WriteLine(answer.Text);
```

## Changelog

### 1.1.0

- Added OCR, ASR, and HTTP and WebSocket TTS.
- Added parsed file uploads, cross-protocol document references, and inline plain-text documents in Messages.
- Added opt-in hosted search, URL reading, safety checks, and titles across three chat protocols. Chat and Messages also support `latest_reminder`.
- Preserved signed tool history, citations, refusals, in-band generation errors, and final usage.
- Added WorkflowEvals benchmarks for Decisions. [Benchmark guide](https://github.com/yuezhishun/decisions-sdk/tree/main/benchmark/DeepSeek.Decisions.Benchmarks)
- Updated the central Agent Framework Azure Functions and DurableTask pins to stable `1.16.0`.

### 1.0.8

- Verified .NET 11 GA on SDK 11.0.100 and runtime 11.0.0. All 387 tests passed.
- Pinned the GA SDK in release CI and added full `net11.0` tests with separate build and TRX output.
- Updated the installation instructions and package readmes. The packages use version 1.0.8 and assembly version 1.0.8.0 and target `netstandard2.0`.

### 1.0.7

- Unified JSON object and schema prompts in concise Chinese. String values follow the caller's requested language.
- Validated final JSON and schemas in all three adapters. Anthropic implements `ResponseFormat` through prompting and local validation. Responses uses the same approach for schemas with non-object roots or schema-valued `additionalProperties`.
- Preserved whitespace-only streaming fragments in Chat and Messages. Added 138 offline regression cases and live coverage on .NET 11 RC3.
- Empty, invalid, truncated, or schema-invalid JSON produces a `JsonException`. Read the entire stream before using the validated result. [JSON output details](src/DeepSeek.Agents.AI/README.md#json-output)
- Set package and assembly versions to 1.0.7 and 1.0.7.0. Added MIT-licensed JsonSchema.Net 8.0.5 to the adapter.

### 1.0.6

- Added DeepSeek.Decisions for Jev and Decisions workflows, with image evidence, four probability methods, automated fallback, and diagnostics.
- Added decision unit tests and live text and image cases. Published the 1.0.6 packages from tag `v1.0.6` using NuGet Trusted Publishing.

### 1.0.5

- Added standalone Search and Fetch and Anthropic Files. Fixed file paths, images in tool results, and Chat `user_id`.
- Added None, Low, High, and Max reasoning effort, option precedence, Messages tool argument assembly, and server search history replay.
- Mapped cache, search, and fetch usage. Added .NET 11 RC3 verification for the `netstandard2.0` packages.
- [Compatibility and .NET 11 test commands](README_CN.md#net-11-%E4%B8%8E%E9%AA%8C%E6%94%B6%E5%91%BD%E4%BB%A4)

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
dotnet add package DeepSeek.Core --version 1.1.0
dotnet add package DeepSeek.Agents.AI --version 1.1.0
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

Supported image formats are JPEG, PNG, GIF, and WebP. Hosted image uploads accept up to 64 MiB. Configure OpenAI Files expiry between 3,600 and 2,592,000 seconds.

For streaming, enumerate `CreateResponseStreaming(...)`. Events expose their semantic `Type`, `SequenceNumber`, typed `Item`/`ContentPart`, and terminal `Response`; the stream ends with `response.completed`, `response.incomplete`, or `response.failed`.

`ChatClient` uses `response_format: { "type": "json_object" }` for Chat JSON Output. Strict function schemas are a Beta capability. `GetChatPrefixClient(...)` targets `/beta/chat/completions` and validates the final assistant message with `Prefix = true`; `CompletionsClient` targets `/beta/completions` and accepts `Prompt`, `Suffix`, `Stop`, and `Stream`.

The Chat, Responses, and Anthropic adapters support `ChatOptions.ResponseFormat`. JSON object and schema prompts use concise Chinese while retaining the caller's language requirements. Anthropic uses prompting and validation. Responses uses the same fallback for non-object roots and schema-valued `additionalProperties`. Read the full stream to validate the result. [JSON output details](src/DeepSeek.Agents.AI/README.md#json-output)

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
- `test/DeepSeek.Tests`: unit tests for the typed SDK
- `test/DeepSeek.Agents.AI.UnitTests`: unit tests for the adapter
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

- Tests cover Core and Agents.
- Projects under `sample/` are examples and are not covered by dedicated test projects.
