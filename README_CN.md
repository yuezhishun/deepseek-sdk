# deepseek-sdk

当前 SDK 版本为 **1.0.7**，安装命令见下方；Microsoft Agent Framework 引用已更新至 **1.28.0**。

`deepseek-sdk` 是一个适用于 DeepSeek 的 .NET SDK，包含三个主要库：

- `src/DeepSeek.Core`：基于 `System.ClientModel` 与 HTTP pipeline 构建，可获得一致的请求处理模型、对日志、重试、诊断等横切能力更好的控制，也更适合构建可扩展的强类型 SDK 客户端
- `src/DeepSeek.Agents.AI`：面向 Microsoft Agent Framework 的扩展与适配支持
- `src/DeepSeek.Decisions`：独立的 Jev 与 Decisions 决策类库，依赖 Core，支持文本、JSON、图片及概率来源诊断；[用法与兼容边界](src/DeepSeek.Decisions/README.md)
- 模型标识由调用方传入，应用可以跟随 DeepSeek 当前及未来的模型名称，而无需因模型名称变化升级 Core 包
- 包含 DeepSeek 的两个 Beta 接口支持：对话前缀续写（chat prefix continuation）和 FIM 补全（FIM completion）
- 按照 DeepSeek 官方思考模式文档实现了 thinking mode，包括推理开关、推理强度控制、`reasoning_content` 处理，以及多轮对话和工具调用下的续推理语义

## 变更日志

### 1.0.7

- 普通 JSON 与 Schema 模式统一使用精简中文提示词，保留调用方的语言要求并移除无关示例。
- Chat、Responses、Anthropic 补齐最终 JSON / Schema 校验；Anthropic 不再忽略 `ResponseFormat`，Responses 对原生服务端不接受的数组根节点与字典 Schema 使用提示词和本地校验回退。
- 修复 Chat、Anthropic 流式空格片段丢失；新增 138 个离线回归用例及在线覆盖，验证 .NET 11 RC3。
- 启用 JSON 格式时，空白、非法 JSON、截断或 Schema 不匹配会抛出 `JsonException`；流式结果须完整枚举且未抛异常才算校验成功。详见 [JSON 输出说明](src/DeepSeek.Agents.AI/README.md#json-output)。
- 三个包统一为 `1.0.7` / `1.0.7.0`；Agents 新增 MIT 许可依赖 `JsonSchema.Net 8.0.5`。

### 1.0.6

- 新增独立 `DeepSeek.Decisions` 类库，提供 Jev/System One 与 Decisions 双接口，支持文本、JSON、图片、概率读取、自动回退和诊断。
- 新增决策单元测试和文本/图片在线用例；三个 NuGet 包统一为 `1.0.6`，程序集版本为 `1.0.6.0`。
- `v1.0.6` tag 触发三个包的打包、校验与 NuGet trusted publishing。

### 1.0.5
- 补齐独立 `Search` / `SearchAsync`、`Fetch` / `FetchAsync`，原始响应通过 `ClientResult.GetRawResponse()` 获取。
- 新增 `GetAnthropicFilesClient()`，复用文件操作并转换原生分页、文件大小、时间和删除结果；新增 `Before`、`MimeType`。
- 补齐 `None/Low/High/Max` 推理、单次覆盖及显式 thinking 优先级，Chat 用户标识改用官方 `user_id`。
- 修复工具结果中的混合图片、文件查询/删除路径、Anthropic 流式工具参数重组；服务端搜索历史完整回传且不重复。
- `UsageDetails` 提供缓存计数，`AdditionalCounts` 保留搜索/抓取等服务端计数，流式累计用量只计一次。
- 保持包版本 `1.0.5` / 程序集 `1.0.5.0`；SDK 目标为 `netstandard2.0`，增加 .NET 11 RC3 兼容验证。

- Chat Completions、Responses `input_image` 和 Anthropic image source 新增统一强类型多模态内容。
- 新增兼容 OpenAI 的 Files API 客户端，支持上传、列出、查询和删除。
- 三类 `Microsoft.Extensions.AI` 适配器支持 URL、base64/data URL 与 `file_id`。
- 统一强类型内容模型属于公共 API 破坏性升级；图片模型标识仍由调用方传入字符串，SDK 不固定枚举模型。

- 重新设计 Responses 公共强类型模型，支持输入/输出多态 item、内容块、工具、tool choice、usage、annotations、错误详情和未知字段保留。
- Responses 支持 JSON object/JSON Schema、function、服务端 web search、`apply_patch` custom tool、thinking 和完整语义化 SSE 事件。
- 新增同步、异步、原始结果访问和流式接口，以及独立的 `Microsoft.Extensions.AI` / `Microsoft.Agents.AI` 适配器。
- 审计 Chat JSON Output、strict tool schema、logprobs、用户标识、前缀续写、FIM 和 Anthropic thinking/tool 流式兼容性。
- 两个发布包及仓库集中版本升级为 `1.0.5`；Responses 模型重设计属于公共 API 破坏性变更。
- 将依赖基线更新为 `Microsoft.Agents.AI 1.28.0` 和 `Microsoft.Extensions.AI 10.11.0`，并更新 AGUI hosting 预览包以兼容 .NET 10 示例与集成测试。

## 安装

```bash
dotnet add package DeepSeek.Core --version 1.0.7
dotnet add package DeepSeek.Agents.AI --version 1.0.7
dotnet add package DeepSeek.Decisions --version 1.0.7
```

核心 NuGet 包名为 `DeepSeek.Core`，公共命名空间仍保持为 `DeepSeek` 与 `DeepSeek.*`。

### 搜索、抓取与两种 Files 协议

```csharp
using DeepSeek;
using DeepSeek.Search;

var client = new DeepSeekClient("your-api-key");
var results = await client.SearchAsync(new SearchRequest
{
    Query = "DeepSeek API",
    Count = 5,
    OriginalQuestion = "如何调用 DeepSeek API？",
    NeedContent = true,
});
var page = await client.FetchAsync(results.Value[0].Url);
Console.WriteLine(page.Value.Content); // Markdown
var files = client.GetFilesClient();
var anthropicFiles = client.GetAnthropicFilesClient();
```

搜索词与原问题限制为 1–128 个 Unicode 字符，`Count` 为 1–50；Fetch 接受不带凭据且长度不超过 8192 的 HTTP(S) URL。空搜索结果为合法空列表，调用方应先检查数量。

两种 Files 客户端操作同一账号的文件；Anthropic 模式自动带 beta header，`After` / `Before` 映射为互斥的 `after_id` / `before_id`，不接受 `Order`、`Purpose` 或有效期选项。文件上限为 64 MiB，文件名上限为 512 UTF-8 字节；OpenAI 有效期为 3600–2592000 秒。原生响应仍通过 `GetRawResponse()` 获取。

协议依据（2026-11-08 复核）：[Search](https://api-docs.deepseek.com/zh-cn/api/search/)、[Fetch](https://api-docs.deepseek.com/zh-cn/api/fetch/)、[Files](https://api-docs.deepseek.com/zh-cn/guides/files_api/)。

### .NET 11 与验收命令

两个 NuGet 包继续使用 `netstandard2.0`，调用方可使用 .NET 11。此次验证环境为 SDK `11.0.100-rc.3.26522.148`、运行时 `11.0.0-rc.3.26522.148`；正式版发布后仍需复测，不能视为已经完成 GA 验证。默认离线测试使用 .NET 8，集成测试使用 .NET 10。

安装 .NET 11 SDK 后，可通过测试项目的可选属性运行同一组测试（不改变 SDK 包的目标框架）：

```powershell
dotnet test test/DeepSeek.Tests/DeepSeek.Tests.csproj -c Release -p:CompatibilityTargetFramework=net11.0
dotnet test test/DeepSeek.Agents.AI.UnitTests/DeepSeek.Agents.AI.UnitTests.csproj -c Release -p:CompatibilityTargetFramework=net11.0
$env:DEEPSEEK_REQUIRE_LIVE_TESTS = 'true'
dotnet test test/DeepSeek.IntegrationTests/DeepSeek.IntegrationTests.csproj -c Release -p:CompatibilityTargetFramework=net11.0
```

在线凭据优先来自环境变量 `DEEPSEEK_API_KEY`，其次为 `deepseek-api-key`，最后回退到本地 `sample/appsettings.json`。开发环境未配置时显示 xUnit 跳过；完整验收设置上述环境变量，缺少凭据直接失败。在线测试串行运行，只清理本轮创建的文件，不运行 benchmark。

### Microsoft AI 兼容版本

`DeepSeek.Agents.AI` 基于并测试于 `Microsoft.Agents.AI 1.28.0` 和 `Microsoft.Extensions.AI 10.11.0`。

AGUI 示例与 AGUI 集成测试使用 `Microsoft.Agents.AI.Hosting.AGUI.AspNetCore 1.28.0-preview.261106.1`，目标框架为 .NET 10。AGUI 包属于预览依赖；普通 Chat 和 Responses 适配器不要求安装该包。

## 项目

### `src/DeepSeek.Core`

`DeepSeek.Core` 包是一个底层强类型 SDK，用于在 .NET 中调用 DeepSeek API。

模型名称由调用方配置并以字符串传入。SDK 不发布固定的模型枚举或常量，因此官方模型名称变化时无需升级 Core 包。

它包含：

- `DeepSeekClient`：主入口客户端
- `ChatClient`：用于聊天补全
- `CompletionsClient`：用于FIM （Fill In the Middle）补全
- `ModelsClient`：用于列出模型
- `BalanceClient`：用于账单与余额查询
- `AnthropicClient`：用于调用 DeepSeek 提供的 Anthropic 风格 messages API
- `ResponsesClient`：用于无状态 Responses API 和语义化 SSE 流式响应
- `FilesClient`：用于兼容 OpenAI 的 Files API

典型用法：

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

#### 多模态与 Files API

图片模型标识由调用方传入，例如 `deepseek-flash`，SDK 不固定模型枚举。Chat 支持文本、URL/data URL 图片和 Files API `file_id`；Responses 使用 `ResponseInputImageContent`，Anthropic 使用 `AnthropicImageSource`：

```csharp
var response = await chat.CompleteChatAsync(new ChatCompletionRequest
{
    Messages =
    [new ChatMessage
    {
        Role = "user",
        Content = new ChatContentPart[]
        {
            new ChatTextContentPart("请描述图片。"),
            new ChatImageContentPart("https://example.com/image.png", "high"),
            new ChatFileContentPart { FileId = "file-api-..." },
        },
    }],
});
```

Files API 用法：

```csharp
var files = new DeepSeekClient("your-api-key").GetFilesClient();
await using var stream = File.OpenRead("image.png");
var file = await files.UploadFileAsync(stream, "image.png");
await files.ListFilesAsync(new FileListOptions { Limit = 100, Order = "desc" });
await files.RetrieveFileAsync(file.Value.Id!);
await files.DeleteFileAsync(file.Value.Id!);
```

支持 JPEG、PNG、GIF、WebP；Files 单文件最大 64 MiB，设置有效期时范围为 3600–2592000 秒。服务端还会限制内联图片、请求体和上下文大小。

#### Responses API

```csharp
using DeepSeek;
using DeepSeek.Responses;

var client = new DeepSeekClient("your-api-key");
var responses = client.GetResponsesClient("deepseek-flash");
var response = await responses.CreateResponseAsync(new ResponseRequest
{
    Instructions = "You are a helpful assistant.",
    Input = "Hello",
});

var text = response.Value.Output
    .SelectMany(static item => item.Content)
    .FirstOrDefault(static content => content.Type == "output_text")?.Text;
Console.WriteLine(text);
```

Responses API 是无状态的，每次请求都应在 `Input` 中携带完整历史；`Input` 与 `Instructions` 至少提供一个。它支持 `ResponseTextFormat.JsonObject()`、`JsonSchema(...)`、`ResponseFunctionTool`、服务端 `ResponseWebSearchTool` 和仅用于 Codex 兼容的 `ResponseCustomTool { Name = "apply_patch" }`。

流式调用使用 `CreateResponseStreaming(...)`，每个 `ResponseStreamEvent` 都包含语义化 `Type`、递增的 `SequenceNumber` 以及可能存在的 `Item`、`ContentPart`、`Delta` 或完整 `Response`。流以 `response.completed`、`response.incomplete` 或 `response.failed` 结束，不会发送 `data: [DONE]`。

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

`DeepSeek.Agents.AI` 包将该强类型 SDK 适配到 `Microsoft.Extensions.AI` 和 `Microsoft.Agents.AI` 的抽象层之上。

它包含：

- `ChatClient` 和 `AnthropicClient` 的 `AsIChatClient(...)` 扩展方法
- `ResponsesClient` 独立的 `AsIChatClient(...)` 和 `AsAIAgent(...)` 扩展方法
- 适配器专用配置 `DeepSeekChatClientOptions`
- 对 `ChatOptions`、工具调用、流式更新和推理元数据的支持
- 基于 `Microsoft.Agents.AI` 的 Agent 场景集成辅助能力

典型用法：

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

#### 通过 `Microsoft.Extensions.AI` 使用 thinking 模式

该适配器会把 `ChatOptions.Reasoning` 映射为 DeepSeek 的推理设置，并将推理内容以 `TextReasoningContent` 以及 `AdditionalProperties["reasoning_content"]` 的形式返回。

当你在 thinking 模式下构建带工具调用的多轮对话时，需要在下一轮中将推理链与 assistant 的工具调用消息一并保留。在本仓库中，适配器通过 `TextReasoningContent` 和 `AdditionalProperties["reasoning_content"]` 表示这部分信息，并在继续推理时将其写回 DeepSeek 所使用的对应字段。

示例：

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

如果你需要一个可直接运行的参考实现，可以从 `sample/DeepSeek.Agui.Agent` 开始，再将 `sample/DeepSeek.Agui.Web` 连接到该端点。

首次在本机运行示例或在线集成测试时，请创建一次被 Git 忽略的本地配置文件：

```bash
cp sample/appsettings.dev.json sample/appsettings.json
```

然后只修改被忽略的 `sample/appsettings.json`：

```json
{
  "apiKey": "你的真实 API Key"
}
```

没有该文件时，测试会继续使用 `sample/appsettings.dev.json` 中的 `test-key`，并自动跳过在线网络测试。

## 仓库结构

- `src/DeepSeek.Core`：强类型 SDK
- `src/DeepSeek.Agents.AI`：AI 抽象层适配器
- `src/DeepSeek.Decisions`：Jev/Decisions 决策类库
- `test/DeepSeek.Tests`：强类型 SDK 的单元测试
- `test/DeepSeek.Agents.AI.UnitTests`：适配器的单元测试
- `test/DeepSeek.Decisions.UnitTests`：决策类库的单元测试
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

对于前缀续写请求，最后一条消息必须是带有 `Prefix = true` 的 assistant 消息。

### FIM 补全

补全 API 支持通过组合 `Prompt` 和 `Suffix` 实现 fill-in-the-middle 风格补全。

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

说明：

- `EnableMessageLogging = true`：启用请求与响应消息日志
- `DeepSeekClientOptions.AllowMessageContentLogging = true`：显式允许 SDK 输出消息体日志
- `ClientLoggingOptions.EnableMessageContentLogging = true`：只有在 SDK 级允许开关打开时才会生效
- 默认情况下消息体日志是关闭的，以避免流式响应被提前消费
- `MessageContentSizeLimit`：控制输出的消息体内容大小
- 请根据需要配置日志提供程序，例如 console、Serilog 或其他 sink
- 在生产环境开启完整 payload 日志时，请谨慎处理敏感数据

## NuGet 发布

发布由推送 `v*` 标签触发，标签 `v1.0.6` 对应包版本 `1.0.6`。工作流 `.github/workflows/publish-nuget.yml` 使用 [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) 和 `NuGet/login@v1`，在推送包之前通过 GitHub OIDC 获取临时 API Key，不再读取固定的 `NUGET_API_KEY` 仓库 Secret。

NuGet 策略应绑定以下值：

- NuGet 用户名（Package owner）：`yueyuan`，使用用户名而非邮箱。
- Repository Owner：`yuezhishun`。
- Repository：`deepseek-sdk`。
- Workflow：`publish-nuget.yml`，仅填写文件名。
- 包范围：`DeepSeek.Core`、`DeepSeek.Agents.AI`、`DeepSeek.Decisions`。

新策略可能显示 7 天临时激活窗口；窗口内首次成功发布后会永久激活。CI 每次发布自动取得短期密钥，无需手动复制密钥或定期更新仓库 Secret。

发布 CI 必须另行配置仓库 Secret `DEEPSEEK_API_KEY`，仅在测试步骤映射为同名环境变量；`DEEPSEEK_REQUIRE_LIVE_TESTS=true` 确保缺少凭据时失败，在线测试不会被静默跳过。密钥不写入仓库或输出日志。

手动运行 `publish-nuget.yml` 会执行完整测试、三个包的打包和文件校验，版本来自三个项目一致的 `Version`；NuGet 登录与上传仅在 `v*` tag push 时执行。这样可以验证 CI 修复，并保持已发布 tag 的提交不变。工作流使用 `actions/setup-dotnet@v6.1.0`，替换旧版的 Node `punycode` 弃用依赖链。

## 说明

- 当前维护测试的范围包括 `src/DeepSeek.Core`、`src/DeepSeek.Agents.AI` 和 `src/DeepSeek.Decisions`
- `sample/` 下的项目仅用于示例演示，不包含专门的测试项目
