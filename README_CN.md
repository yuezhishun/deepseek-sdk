# deepseek-sdk

`deepseek-sdk` 是一个适用于 DeepSeek 的 .NET SDK，包含两个主要库：

- `src/DeepSeek.Core`：基于 `System.ClientModel` 与 HTTP pipeline 构建，可获得一致的请求处理模型、对日志、重试、诊断等横切能力更好的控制，也更适合构建可扩展的强类型 SDK 客户端
- `src/DeepSeek.Agents.AI`：面向 Microsoft Agent Framework 的扩展与适配支持
- 包含 DeepSeek 的两个 Beta 接口支持：对话前缀续写（chat prefix continuation）和 FIM 补全（FIM completion）
- 按照 DeepSeek 官方思考模式文档实现了 thinking mode，包括推理开关、推理强度控制、`reasoning_content` 处理，以及多轮对话和工具调用下的续推理语义

## 安装

```bash
dotnet add package DeepSeek.Core --version 1.0.0
dotnet add package DeepSeek.Agents.AI --version 1.0.0
```

核心 NuGet 包名为 `DeepSeek.Core`，公共命名空间仍保持为 `DeepSeek` 与 `DeepSeek.*`。

## 项目

### `src/DeepSeek.Core`

`DeepSeek.Core` 包是一个底层强类型 SDK，用于在 .NET 中调用 DeepSeek API。

它包含：

- `DeepSeekClient`：主入口客户端
- `ChatClient`：用于聊天补全
- `CompletionsClient`：用于FIM （Fill In the Middle）补全
- `ModelsClient`：用于列出模型
- `BalanceClient`：用于账单与余额查询
- `AnthropicClient`：用于调用 DeepSeek 提供的 Anthropic 风格 messages API

典型用法：

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

#### Thinking 与推理

该强类型 SDK 直接支持在聊天请求中启用 DeepSeek thinking 模式。

可用方式：

- `Thinking = ThinkingMode.Enabled`：启用推理输出
- `ReasoningEffort = ChatReasoningEffort.High` 或 `ChatReasoningEffort.Max`：控制推理强度
- `StreamOptions.IncludeUsage`：当你希望流式响应中返回 usage 信息时使用

示例：

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

`DeepSeek.Agents.AI` 包将该强类型 SDK 适配到 `Microsoft.Extensions.AI` 和 `Microsoft.Agents.AI` 的抽象层之上。

它包含：

- `ChatClient` 和 `AnthropicClient` 的 `AsIChatClient(...)` 扩展方法
- 适配器专用配置 `DeepSeekChatClientOptions`
- 对 `ChatOptions`、工具调用、流式更新和推理元数据的支持
- 基于 `Microsoft.Agents.AI` 的 Agent 场景集成辅助能力

典型用法：

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

#### 通过 `Microsoft.Extensions.AI` 使用 thinking 模式

该适配器会把 `ChatOptions.Reasoning` 映射为 DeepSeek 的推理设置，并将推理内容以 `TextReasoningContent` 以及 `AdditionalProperties["reasoning_content"]` 的形式返回。

当你在 thinking 模式下构建带工具调用的多轮对话时，需要在下一轮中将推理链与 assistant 的工具调用消息一并保留。在本仓库中，适配器通过 `TextReasoningContent` 和 `AdditionalProperties["reasoning_content"]` 表示这部分信息，并在继续推理时将其写回 DeepSeek 所使用的对应字段。

示例：

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

对于多轮工具调用场景，请保留：

- assistant 的工具调用消息
- 对应的工具执行结果
- 上一轮 assistant 的推理内容

当模型需要在工具调用后继续推理时，这是必须的。

#### AGUI 示例

仓库在 `sample/` 下也包含面向 AGUI 的示例项目，包括：

- `sample/DeepSeek.Agui.Agent`：ASP.NET Core 托管的 AGUI 端点
- `sample/DeepSeek.Agui.Console`：控制台客户端示例
- `sample/DeepSeek.Agui.CustomClient`：自定义 AGUI 事件流客户端
- `sample/DeepSeek.Agui.Web`：Web UI 示例

最小托管 AGUI 示例：

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

如果你需要一个可直接运行的参考实现，可以从 `sample/DeepSeek.Agui.Agent` 开始，再将 `sample/DeepSeek.Agui.Web` 连接到该端点。

运行示例前，请将 `sample/appsettings.dev.json` 重命名为 `sample/appsettings.json`，并把其中的 `apiKey` 替换为你的真实 DeepSeek API key。

## 仓库结构

- `src/DeepSeek.Core`：强类型 SDK
- `src/DeepSeek.Agents.AI`：AI 抽象层适配器
- `test/DeepSeek.Tests`：强类型 SDK 的单元测试
- `test/DeepSeek.Agents.AI.UnitTests`：适配器的单元测试
- `test/DeepSeek.IntegrationTests`：在线集成测试
- `sample/`：仅示例项目

## 更多 SDK 示例

### 获取模型列表

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

### 获取余额

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

### Chat 前缀续写（Beta）

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

对于前缀续写请求，最后一条消息必须是带有 `Prefix = true` 的 assistant 消息。

### FIM 补全

补全 API 支持通过组合 `Prompt` 和 `Suffix` 实现 fill-in-the-middle 风格补全。

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

## 构建与测试

在仓库根目录构建整个解决方案：

```bash
dotnet build DeepSeek.slnx
```

运行受支持库的测试：

```bash
dotnet test DeepSeek.slnx
```

针对性的测试命令：

```bash
dotnet test test/DeepSeek.Tests/DeepSeek.Tests.csproj
dotnet test test/DeepSeek.Agents.AI.UnitTests/DeepSeek.Agents.AI.UnitTests.csproj
```

## 请求与响应日志

如果你在调试时需要完整记录请求与响应报文，可以配置 `DeepSeekClientOptions.ClientLoggingOptions`。

典型配置：

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

说明：

- `EnableMessageLogging = true`：启用请求与响应消息日志
- `EnableMessageContentLogging = true`：在日志中包含消息体内容
- `MessageContentSizeLimit`：控制输出的消息体内容大小
- 请根据需要配置日志提供程序，例如 console、Serilog 或其他 sink
- 在生产环境开启完整 payload 日志时，请谨慎处理敏感数据

## 当前注意事项

`DeepSeekAnthropicChatClient` 以及 Anthropic 风格适配路径已经在仓库中提供，但其测试覆盖程度尚不如主 `ChatClient` 路径。在生产场景中使用时，请额外进行验证。

## 说明

- 当前维护测试的范围包括 `src/DeepSeek.Core` 和 `src/DeepSeek.Agents.AI`
- `sample/` 下的项目仅用于示例演示，不包含专门的测试项目
