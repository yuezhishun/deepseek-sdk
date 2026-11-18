# DeepSeek.Agents.AI

Adapter package for using the DeepSeek typed client with `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

## Install

```bash
dotnet add package DeepSeek.Agents.AI --version 1.0.8
```

This package depends on `DeepSeek.Core 1.0.8`.

Model identifiers are supplied by the caller as strings. Neither this adapter nor `DeepSeek.Core` publishes a fixed model enum or constants, so model name changes do not require a package upgrade.

`GetChatClient(model)`, `GetResponsesClient(model)`, and `GetAnthropicClient(model)` bind the model. Create a new protocol client to switch models. `ChatOptions.ModelId` leaves that binding in place.

## Compatibility

Release 1.0.8 passed 199 adapter tests and 34 live integration tests on November 11, 2026 with .NET 11 GA (SDK 11.0.100, .NET and ASP.NET Core runtimes 11.0.0). The package targets `netstandard2.0`.

The adapter is built and tested against:

- `Microsoft.Agents.AI 1.28.0`
- `Microsoft.Extensions.AI 10.11.0`

The 1.0.8 release uses the stable Microsoft AI dependencies verified on November 8, 2026, plus Microsoft.Bcl.AsyncInterfaces 10.0.13.

[Microsoft Agent Framework on NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI/1.28.0) · [Microsoft.Extensions.AI on NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.11.0)

AGUI hosting is optional. The repository samples and integration tests use `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore 1.28.0-preview.261106.1` and target .NET 10. The AGUI console sample uses `Microsoft.Agents.AI.AGUI 1.13.0-preview.260703.1`.

## Usage

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

var client = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-flash")
    .AsIChatClient();

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "Hello")
]);

Console.WriteLine(response.Text);
```

## Application extensions (Unreleased)

Use `deepseek-flash` for the November 13 tools and documents. Set the adapter defaults or override them for one call in `ChatOptions.AdditionalProperties`.

| Adapter default | Per-call key | Effect |
| --- | --- | --- |
| `EnableWebSearch` | `DeepSeekChatOptionKeys.WebSearch` | Adds `deepseek_web_search`. Thinking mode enables deep search. |
| `EnableReadUrl` | `DeepSeekChatOptionKeys.ReadUrl` | Adds `deepseek_read_url`. For explicit URL reading, disable thinking or omit the search tool. |
| `SafetyCheckLevel` | `DeepSeekChatOptionKeys.SafetyCheckLevel` | `"high"` enables enhanced checks. `"low"` uses the default API checks. |
| `GenerateTitle` | `DeepSeekChatOptionKeys.GenerateTitle` | Requests a title, when available, in `AdditionalProperties["title"]`. |

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

var sdk = new DeepSeekClient(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
    ?? throw new InvalidOperationException("Set DEEPSEEK_API_KEY."));
using var client = sdk.GetResponsesClient("deepseek-flash").AsIChatClient(new DeepSeekChatClientOptions
{
    EnableReadUrl = true,
    SafetyCheckLevel = "high",
});
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var history = new List<ChatMessage> { new(ChatRole.User, "Read https://example.com and name the page.") };

var first = await client.GetStreamingResponseAsync(history, new ChatOptions
{
    AdditionalProperties = new AdditionalPropertiesDictionary { [DeepSeekChatOptionKeys.GenerateTitle] = true },
}, cancellation.Token).ToChatResponseAsync();

if (first.AdditionalProperties?.TryGetValue("is_refusal", out var refused) != true || refused is not true)
{
    history.AddRange(first.Messages);
    history.Add(new ChatMessage(ChatRole.User, "What does that same page say the domain is for?"));
    var second = await client.GetResponseAsync(history, cancellationToken: cancellation.Token);
    Console.WriteLine(second.Text);
}
```

Keep the returned assistant messages and their `RawRepresentation` for the next turn. Copying only `Text` drops the native tool history. For persistence, store the native protocol data separately. Tool progress, citations, and signed results use native representations. Only client function calls enter the function invocation pipeline. [Web tools](https://api-docs.deepseek.com/zh-cn/guides/web_tools/)

`HostedFileContent` with a document MIME type maps to native file input. An image MIME type or an omitted MIME type retains the published image mapping. Use `DataContent` with media type `text/plain` for an inline plain-text document in Messages. For Chat or Messages, put `new ChatMessage(new ChatRole("latest_reminder"), "2026-11-18,Wednesday,,Web,English")` immediately before its user message. Responses rejects that role. [Document parsing](https://api-docs.deepseek.com/zh-cn/guides/document_parsing/) · [User context](https://api-docs.deepseek.com/zh-cn/guides/latest_reminder/)

Use text format for the Chat and Responses extensions. Responses also requires stateless inputs and rejects incompatible tools. The legacy `HostedWebSearchTool` maps to the existing Responses and Messages tools and should be used in a separate request from the new extensions.

Streamed text remains provisional until the terminal update. On `is_refusal`, discard the provisional text and use the supplied `refusal` explanation when present. Generation errors raise `InvalidDataException` with the native response in `Data["RawRepresentation"]`. A refusal bypasses JSON validation. `UsageDetails.AdditionalCounts` exposes `tool_search`, `tool_fetch`, `tool_image_search`, and `safety_tokens` when reported. Safety tokens are already included in output totals. [Content safety](https://api-docs.deepseek.com/zh-cn/guides/safety/)

## Changelog

### Unreleased

- Added Flash conversation options, native tool and citation streaming, multi-turn signed history, and document MIME mappings.
- Exposed final titles, refusal metadata, and billed tool and safety counters.

### 1.0.8

- Updated the Core dependency to 1.0.8 and verified adapter and live integration tests on the .NET 11 GA SDK.

### 1.0.7

- Unified JSON object and schema prompts in concise Chinese while retaining the requested language for string values.
- Validated final JSON and schemas in all three adapters, including Anthropic. Preserved whitespace-only streaming fragments in Chat and Messages.
- Added MIT-licensed JsonSchema.Net 8.0.5 for local schema validation on `netstandard2.0`.

### 1.0.6

- Aligned the Core dependency and package version at 1.0.6 with assembly version 1.0.6.0 alongside DeepSeek.Decisions.

### 1.0.5

- Mapped None, Low, High, and ExtraHigh to none, low, high, and max. Per-call options override defaults. An explicit thinking switch takes precedence.
- Preserved the order and call IDs of tool results containing text and URL, base64, or hosted images. Anthropic assembles arguments by block index and emits one function call after complete, valid JSON.
- Responses and Anthropic accept the legacy `HostedWebSearchTool` on a supported Pro model. Search history and unknown fields travel with the returned messages.
- `UsageDetails` exposes cached input and reasoning tokens. AdditionalCounts carries counters such as `tool_search`, `tool_fetch`, and `web_search_requests`. Messages includes cache read and write tokens in input totals. A stream reports cumulative usage once.
- Chat requests streaming usage by default. Set `DeepSeekChatOptionKeys.IncludeUsage` to false to override. `ToChatResponseAsync()` aggregates standard `UsageContent`.
- Replay the returned assistant messages, including messages from stream aggregation, to retain search context. Save native protocol data explicitly for cross-process persistence because JSON serialization omits `RawRepresentation`.
- Version 1.0.5 targets `netstandard2.0` with assembly version 1.0.5.0 and was verified on .NET 11 RC3 without new direct dependencies.

- Added `DataContent`, `UriContent`, and `HostedFileContent` image mapping for Chat, Responses, and Anthropic adapters.
- Added image detail mapping through `DeepSeekChatOptionKeys.ImageDetail`.
- Updated the package for the Core 1.0.5 typed-content breaking change.

- Added the independent Responses `IChatClient` and `AIAgent` adapters for the redesigned Responses models.
- Preserved Responses output, reasoning, usage, function calls, web-search/custom items, streaming events, and unknown protocol data through the Microsoft AI abstractions.
- Added live Responses and AGUI integration coverage and synchronized the package release to `1.0.5`.
- Updated AGUI hosting compatibility for the .NET 10 preview package used by the repository samples and integration tests.

The Responses API has an independent adapter. It keeps Responses input history stateless and exposes tool calls to the caller without invoking tools automatically:

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

var client = new DeepSeekClient("your-api-key")
    .GetResponsesClient("deepseek-flash")
    .AsIChatClient();

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "Hello")
]);

Console.WriteLine(response.Text);
```

The Responses adapter can also be exposed through AGUI:

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Agents.AI.Hosting.AGUI.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAGUIServer();

var app = builder.Build();
var agent = new DeepSeekClient("your-api-key")
    .GetResponsesClient("deepseek-flash")
    .AsAIAgent(
        name: "deepseek-responses-agent",
        instructions: "You are a concise assistant.");

app.MapAGUIServer("/agui", agent);
app.Run();
```

## Features

- `AsIChatClient(...)` extension methods for DeepSeek chat clients
- `AsIChatClient(...)` and `AsAIAgent(...)` extension methods for `ResponsesClient`
- `ChatOptions` mapping for model options, tools, streaming, and reasoning
- Agent Framework integration helpers for DeepSeek-backed agents

The Responses adapter is stateless: callers provide the complete input history for each request. Function calls and function results are returned to the caller and are not executed or automatically continued by this adapter.

## JSON Output

Each `IChatClient` and `AIAgent` adapter supports `ChatOptions.ResponseFormat`.

| Protocol | JSON object | JSON Schema |
| --- | --- | --- |
| Chat | `response_format: json_object` and a Chinese prompt | JSON object mode with the original schema in the prompt and local validation |
| Responses | `text.format: json_object` and a Chinese prompt | Native `json_schema` for typical objects. Other roots and schema-valued `additionalProperties` use JSON mode with prompting and local validation. |
| Anthropic | Chinese prompt and local validation | Chinese prompt with the original schema and local validation |

The prompt asks for a single JSON result without explanations, code fences, or other text. It preserves field and enum names and follows the caller's language choice for string values. Original instructions and options are preserved. JSON object mode requires an object. Schema mode permits any root accepted by the schema.

Empty, invalid, incomplete, or schema-invalid JSON raises `System.Text.Json.JsonException`. If available, the raw response is in `Data["RawRepresentation"]`. JsonSchema.Net validates Draft 2020-12 by default, including required fields, types, enums, additional properties, arrays, local references, and composition. `format` is an annotation.

Streaming preserves whitespace and emits deltas immediately. Read through successful completion before using the validated result. Intermediate function calls can have empty text. Use a per-call `ChatResponseFormat.Text` to override a default JSON format. Core exposes native protocol responses.
