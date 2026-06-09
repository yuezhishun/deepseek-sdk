# DeepSeek.Agents.AI

Adapter package for using the DeepSeek typed client with `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

## Install

```bash
dotnet add package DeepSeek.Agents.AI --version 1.0.0
```

This package depends on `DeepSeek.Core`.

## Usage

```csharp
using DeepSeek;
using DeepSeek.Agents.AI;
using Microsoft.Extensions.AI;

var client = new DeepSeekClient("your-api-key")
    .GetChatClient("deepseek-v4-flash")
    .AsIChatClient();

var response = await client.GetResponseAsync(
[
    new ChatMessage(ChatRole.User, "Hello")
]);

Console.WriteLine(response.Text);
```

## Features

- `AsIChatClient(...)` extension methods for DeepSeek chat clients
- `ChatOptions` mapping for model options, tools, streaming, and reasoning
- Agent Framework integration helpers for DeepSeek-backed agents

## JSON Output

DeepSeek currently exposes JSON Output through `response_format: { "type": "json_object" }`.

When you set `ChatOptions.ResponseFormat` to `ChatResponseFormat.Json` or `ChatResponseFormat.ForJsonSchema<T>()`, this provider sends `json_object` on the wire and augments the system prompt with json/schema guidance for DeepSeek models. `ForJsonSchema<T>()` should be treated as prompt augmentation, not native server-side schema enforcement.
