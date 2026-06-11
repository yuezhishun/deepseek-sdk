# DeepSeek.Core

Typed .NET SDK for DeepSeek APIs, built on `System.ClientModel`.

## Install

```bash
dotnet add package DeepSeek.Core --version 1.0.0
```

## Usage

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

Console.WriteLine(response.Value.Choices[0].Message?.Content);
```

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

The NuGet package is named `DeepSeek.Core`; the public namespaces remain `DeepSeek` and `DeepSeek.*`.
