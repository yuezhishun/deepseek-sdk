# DeepSeek.Agents.AI

Adapter package for using the DeepSeek typed client with `Microsoft.Extensions.AI` and `Microsoft.Agents.AI`.

## Install

```bash
dotnet add package DeepSeek.Agents.AI --version 1.0.8
```

This package depends on `DeepSeek.Core 1.0.8`.

Model identifiers are supplied by the caller as strings. Neither this adapter nor `DeepSeek.Core` publishes a fixed model enum or constants, so model name changes do not require a package upgrade.

适配器绑定的模型由 `GetChatClient(model)`、`GetResponsesClient(model)` 或 `GetAnthropicClient(model)` 决定。`ChatOptions.ModelId` 不会重新绑定已创建的协议客户端；切换模型时应创建对应的客户端。

## Compatibility

2026-11-11 已在本机系统安装的 .NET 11 正式版下验证：SDK `11.0.100`、.NET / ASP.NET Core 运行时 `11.0.0`；适配器 199 项测试和全套 34 项在线集成测试通过，无失败或跳过。包目标继续为 `netstandard2.0`，历史发布记录中的 RC3 为当时的测试环境。

The adapter is built and tested against:

- `Microsoft.Agents.AI 1.28.0`
- `Microsoft.Extensions.AI 10.11.0`

以上为 2026-11-08 核对的最新稳定版；AGUI hosting 升级为 `1.28.0-preview.261106.1`。`Microsoft.Agents.AI.AGUI` 的最新版本仍为 `1.13.0-preview.260703.1`，保留该引用。新版依赖要求将 `Microsoft.Bcl.AsyncInterfaces` 同步至 `10.0.13`；SDK 包版本为 `1.0.8`。

版本来源：[Agent Framework 官方 NuGet](https://www.nuget.org/packages/Microsoft.Agents.AI/1.28.0)、[Microsoft.Extensions.AI 官方 NuGet](https://www.nuget.org/packages/Microsoft.Extensions.AI/10.11.0)。

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

## Changelog

### 1.0.8

- 依赖 `DeepSeek.Core 1.0.8`，通过 .NET 11 正式版适配器与在线集成验证；更新当前兼容性说明和随包 README。

### 1.0.7

- 普通 JSON 和 Schema 模式统一使用精简中文提示词，保留调用方对字符串语言的要求，移除固定的无关示例。
- 三条适配路径增加最终 JSON / Schema 校验；Anthropic 不再忽略 `ResponseFormat`。修复 Chat、Anthropic 流式空格片段丢失。
- 新增 MIT 许可的 `JsonSchema.Net 8.0.5` 依赖用于本地 Schema 校验，继续支持 `netstandard2.0`。

### 1.0.6

- 将 `DeepSeek.Core` 依赖、包版本与程序集版本统一为 `1.0.6` / `1.0.6.0`，与新增 Jev/Decisions 决策类库同步发布。

### 1.0.5

- 三条适配路径补齐 `ReasoningEffort.None/Low/High/ExtraHigh`（对应 none/low/high/max）；单次选项覆盖默认值，同次显式 thinking 开关优先。
- 工具结果支持按原顺序混合文本、URL、base64、`HostedFileContent` 图片，并保留调用 ID；Anthropic 流式参数按块索引拼接完整后只输出一次调用，中断和非法 JSON 明确失败。
- Responses、Anthropic 接受 `HostedWebSearchTool`，完整保留并回传搜索历史及未知协议数据，避免重复消息。需使用支持搜索的 Pro 模型。
- `UsageDetails.CachedInputTokenCount` 表示缓存命中，`ReasoningTokenCount` 表示协议提供的推理计数；`AdditionalCounts` 原样保留 `tool_search`、`tool_fetch`、`web_search_requests` 等服务端计数。Anthropic 输入总量包含缓存读取和写入；流式累计用量仅在终止结果计入一次。
- Chat 流式默认请求 usage，允许 `DeepSeekChatOptionKeys.IncludeUsage = false` 显式关闭；标准 `UsageContent` 可由 `ToChatResponseAsync()` 聚合。
- 搜索上下文通过返回消息的 `RawRepresentation` 保留；直接回传原消息或标准流式聚合结果即可续聊。该字段不参与 JSON 序列化，跨进程持久化需要自行保存原生协议数据；修改内容时应构造新消息。
- SDK 保持 `netstandard2.0`，验证 .NET 11 RC3（SDK `11.0.100-rc.3.26522.148`）；GA 发布后仍需复测。包版本 `1.0.5`，程序集 `1.0.5.0`，未增加直接依赖项。

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

`ChatOptions.ResponseFormat` 在三个 `IChatClient` / `AIAgent` 适配器中均生效：

| 协议 | 普通 JSON 模式 | 带 Schema 模式 |
| --- | --- | --- |
| Chat | `response_format: json_object`，附加中文提示词 | 同一 JSON 模式，提示词携带原始 Schema，并在本地校验 |
| Responses | `text.format: json_object`，附加中文提示词 | 常规对象使用原生 `json_schema`；非对象根节点或包含 Schema 类型 `additionalProperties` 时，使用 JSON 模式、Schema 提示词和本地校验回退 |
| Anthropic | 中文提示词和本地校验 | 中文提示词携带原始 Schema，并在本地校验；不发送协议未支持的 `response_format` |

提示词要求只输出 JSON，禁止解释、Markdown 代码块和前后缀；字段名和枚举值不翻译，字符串内容的语言遵循调用方要求。调用方原有 `Instructions` 会保留，选项不会被修改。普通 JSON 模式要求一个对象；Schema 模式按 Schema 决定根类型，可输出数组。

三个适配器均检查最终结果：空白（即使正常结束）、非法 JSON、非完整终止、Schema 不匹配均抛出 `System.Text.Json.JsonException`，不自动修复或重试。取得原始响应时，可读取异常的 `Data["RawRepresentation"]`；流中断时若尚无原始响应，该值可能为空。Schema 校验使用 `JsonSchema.Net 8.0.5`，未声明方言时采用 Draft 2020-12，覆盖必填项、类型、枚举、额外字段、数组、局部 `$ref` 和组合约束；`format` 按 JSON Schema 默认语义作为注解，不额外强制校验。

流式调用仍立即输出增量，只有完整枚举到结束且未抛异常，才能将聚合结果视为已校验。字符串中的空白片段会原样保留。工具调用的中间响应无需输出 JSON 文档，后续最终文本仍需满足格式要求。默认 `ResponseFormat` 与单次选项按现有规则合并，单次指定 `ChatResponseFormat.Text` 可关闭 JSON 提示词和校验。直接使用 `DeepSeek.Core` 原生协议客户端的行为不变。
