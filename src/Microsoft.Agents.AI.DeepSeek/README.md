# Microsoft.Agents.AI.DeepSeek

Adapter package for using the DeepSeek typed client with `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

## Install

```bash
dotnet add package Microsoft.Agents.AI.DeepSeek --version 1.0.0
```

This package depends on `DeepSeek.Core`.

## Usage

```csharp
using DeepSeek;
using Microsoft.Agents.AI.DeepSeek;
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
