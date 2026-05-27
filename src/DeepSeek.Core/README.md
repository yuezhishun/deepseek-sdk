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

## Included clients

- `ChatClient` for chat completions
- `CompletionsClient` for FIM completions
- `ModelsClient` for model listing
- `BalanceClient` for billing and balance queries
- `AnthropicClient` for the Anthropic-style messages API exposed by DeepSeek

The NuGet package is named `DeepSeek.Core`; the public namespaces remain `DeepSeek` and `DeepSeek.*`.
