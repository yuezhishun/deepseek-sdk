# deepseek-sdk

`deepseek-sdk` is a .NET SDK for DeepSeek with two primary libraries:

- `src/DeepSeek.Core`: built on `System.ClientModel` and the HTTP pipeline model, providing a consistent request handling model, better control over cross-cutting concerns such as logging, retries, and diagnostics, and a stronger foundation for extensible typed SDK clients
- `src/DeepSeek.Agents.AI`: extension and adapter support for Microsoft Agent Framework
- support for DeepSeek's two Beta APIs: chat prefix continuation and FIM completion
- implements thinking mode following DeepSeek's official documentation, including reasoning enablement, reasoning effort control, `reasoning_content` handling, and continued reasoning semantics across multi-turn conversations and tool calls

## Installation

```bash
dotnet add package DeepSeek.Core --version 1.0.0
dotnet add package DeepSeek.Agents.AI --version 1.0.0
```

The core NuGet package is named `DeepSeek.Core`, while the public namespaces remain `DeepSeek` and `DeepSeek.*`.

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

Typical usage:

```csharp
using DeepSeek;
using DeepSeek.Chat;

var client = new DeepSeekClient("your-api-key");
var chat = client.GetChatClient("deepseek-v4-flash");

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
var chat = client.GetChatClient("deepseek-v4-flash");

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
Console.WriteLine(message?.Content);
```

### `src/DeepSeek.Agents.AI`

The `DeepSeek.Agents.AI` package adapts the typed SDK to the abstractions from `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

It includes:

- `AsIChatClient(...)` extension methods for `ChatClient` and `AnthropicClient`
- `DeepSeekChatClientOptions` for adapter-specific configuration
- support for `ChatOptions`, tool calls, streaming updates, and reasoning metadata
- integration helpers for agent scenarios built on `Microsoft.Agents.AI`

Typical usage:

```csharp
using DeepSeek;
using Microsoft.Extensions.AI;
using DeepSeek.Agents.AI;

var client = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-v4-flash")
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
    .GetChatClient("deepseek-v4-flash")
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
builder.Services.AddAGUI();

var app = builder.Build();

var chatClient = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-v4-flash");

var agent = chatClient.AsAIAgent(
    name: "DeepSeekAgUiHostedAgent",
    instructions: "You are a concise assistant.");

app.MapAGUI("/agui", agent);
app.Run();
```

If you want a ready-to-run reference, start from `sample/DeepSeek.Agui.Agent` and connect `sample/DeepSeek.Agui.Web` to that endpoint.

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
var chat = client.GetChatPrefixClient("deepseek-v4-flash");

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

Console.WriteLine(response.Value.Choices[0].Message?.Content);
```

For prefix continuation requests, the final message must be an assistant message with `Prefix = true`.

### FIM completion

The completions API supports fill-in-the-middle style completion by combining `Prompt` and `Suffix`.

```csharp
using DeepSeek;
using DeepSeek.Completions;

var client = new DeepSeekClient("your-api-key");
var completions = client.GetCompletionsClient("deepseek-v4-flash");

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
- `EnableMessageContentLogging = true` includes body content in the logs.
- `MessageContentSizeLimit` controls how much body content is emitted.
- Configure your logger provider such as console, Serilog, or another sink.
- Be careful with sensitive data when enabling full payload logging in production.

## Current caveat

`DeepSeekAnthropicChatClient` and the Anthropic-style adapter path are available in the repository, but they have not been tested as thoroughly as the primary `ChatClient`-based path. Use them with additional validation in production scenarios.

## Notes

- Tests are maintained for `src/DeepSeek.Core` and `src/DeepSeek.Agents.AI`.
- Projects under `sample/` are examples and are not covered by dedicated test projects.
