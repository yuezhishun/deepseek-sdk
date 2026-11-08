# DeepSeek.Decisions

基于 `DeepSeek.Core` 的独立决策类库，同时提供 Jev/System One 和 OpenAI Decisions 风格的请求与结果，支持文本、JSON 和图片。

目标框架为 `netstandard2.0`，仅引用 `DeepSeek.Core`。决策在 SDK 内转换为 DeepSeek Chat Completions 调用；每道题独立调用，默认最多并发 8 道。HTTP 服务托管由使用方负责。

## 引用与入口

源码项目为 `src/DeepSeek.Decisions/DeepSeek.Decisions.csproj`，可直接使用 `ProjectReference`。包版本为 `1.0.7`，依赖 `DeepSeek.Core 1.0.7`；NuGet 发布由仓库 `v1.0.7` tag 流程完成。

```bash
dotnet add package DeepSeek.Decisions --version 1.0.7
```

```csharp
using DeepSeek;
using DeepSeek.Decisions;

var decisions = new DeepSeekClient(apiKey)
    .GetDecisionsClient("deepseek-flash");

// 也可以组合已有的 ChatClient，保留其认证、Endpoint、pipeline 和重试设置。
var existing = new DeepSeekClient(apiKey).GetChatClient("deepseek-flash");
var sameModel = new DecisionsClient(existing);
```

`GetDecisionsClient` 是本包的扩展方法，需要 `using DeepSeek.Decisions`。模型与底层 `ChatClient.Model` 绑定；请求中可省略 `Model`，显式指定时必须一致。模型名称由调用方配置，图片请求需要支持视觉的模型。

## Decisions：分类、真值概率与评分

```csharp
var result = await decisions.CreateDecisionAsync(new DecisionRequest
{
    Input = "同一笔订阅被扣了两次费，请退还重复的扣款。",
    Questions =
    [
        new() { Name = "duplicate", Instructions = "用户是否报告了重复扣费？" },
        new()
        {
            Type = "choice",
            Name = "department",
            Instructions = "哪个部门应处理此请求？",
            Choices =
            [
                new() { Value = "billing", Description = "付款与退款" },
                new() { Value = "technical", Description = "软件故障" },
            ],
        },
        new()
        {
            Type = "score",
            Name = "urgency",
            Instructions = "评估处理优先级。",
            Levels = [new() { Label = "低" }, new() { Label = "中" }, new() { Label = "高" }],
        },
    ],
});

Console.WriteLine(result.Answers[0].Probability);
Console.WriteLine(result.Answers[1].Choice);
Console.WriteLine(result.Answers[2].Score);
```

`Type` 默认是 `predicate`。Choice 的 `Value` 接受字符串和布尔值，`true` 与字符串 `"true"` 是不同选项；同类型的重复值会被拒绝。答案按题目输入顺序排列，`Name` 可省略或重复，不作为内部匹配键。

Input 可以是字符串，也可以是 `DecisionInputMessage` 数组。消息 `Content`、题目 `Instructions` 可以是字符串或 `DecisionContentPart` 数组；反序列化 JSON 后的 `JsonElement` 也可以用于这些联合字段。

## Jev：state、criteria 与命名答案

```csharp
var result = await decisions.SystemOneAsync(new SystemOneRequest
{
    State = new { ticket = "所有用户都无法打开网站，且没有绕行方案。" },
    Questions = new Dictionary<string, SystemOneQuestion>
    {
        ["outage"] = new()
        {
            Instructions = "是否发生全面故障？",
            Criteria = new Dictionary<string, string>
            {
                ["true"] = "服务对所有用户不可用",
                ["false"] = "服务仍可正常使用或只影响部分用户",
            },
        },
        ["severity"] = new()
        {
            Type = "score",
            Instructions = "评估故障严重程度。",
            Criteria = new[] { "外观问题", "部分功能故障且可绕行", "全面中断且不可绕行" },
        },
    },
});

Console.WriteLine(result.Answers["outage"].Noul);
Console.WriteLine(result.Answers["severity"].Score);
Console.WriteLine(result.Answers["severity"].Legend!["2"]);
```

Jev `Type` 默认是 `noul`。Choice 的 `Criteria` 是选项名到描述的字典，Score 是从低到高的等级数组，Noul 可提供 `true/false` 描述。State、instructions 和 criteria 的描述支持 JSON 结构；Score 的 `Legend` 保留原始等级描述。

## JSON 与图片证据

Decisions 图片支持 HTTP(S) URL，以及 PNG、JPEG、GIF、WebP 的 base64 data URL。SDK 将图片内容映射为 Core 的 `ChatImageContentPart`，发送给模型。

```csharp
var result = await decisions.CreateDecisionAsync(new DecisionRequest
{
    Input = new[]
    {
        new DecisionInputMessage
        {
            Content = new DecisionContentPart[]
            {
                new() { Text = "以下是参考照片和订单信息。" },
                new() { Type = "input_image", ImageUrl = referenceImageUrl, Detail = "high" },
                new() { Type = "input_json", Data = new { order = 42, expected = "无破损" } },
            },
        },
    },
    Questions =
    [
        new()
        {
            Name = "damage",
            Instructions = new DecisionContentPart[]
            {
                new() { Text = "候选照片中的包裹是否比参考照片有更多破损？" },
                new() { Type = "input_image", ImageUrl = candidateImageUrl },
            },
        },
    ],
});
```

`input_json.Data` 接受对象或对象数组。数据内部名为 `role`、`type` 或 `image_url` 的字段仍是普通数据，不会创建消息或自动加载图片。

Jev 图片通过 `Assets` 提供 PNG/JPEG 的原始 base64（无 `data:` 前缀），在 State、instructions 或 criteria 的字符串值中引用资产键：

```csharp
var result = await decisions.SystemOneAsync(new SystemOneRequest
{
    State = new { reference = "img-reference" },
    Assets = new Dictionary<string, SystemOneAsset>
    {
        ["img-reference"] = new() { Format = "png", DataBase64 = referencePngBase64 },
        ["img-candidate"] = new() { Format = "png", DataBase64 = candidatePngBase64 },
    },
    Questions = new Dictionary<string, SystemOneQuestion>
    {
        ["same"] = new() { Instructions = "参考照片与候选照片 img-candidate 是否是同一只动物？" },
    },
});
```

引用可位于整个字符串值或句子中；对象键不替换。先匹配整个值，再按从左到右、同位置最长引用优先的规则展开。重复引用重复传入图片，每道题只附带共享证据和本题引用的图片。每个资产必须被引用；建议使用不会出现在普通文本中的唯一键。

## 方法、概率与配置

```csharp
using System.ClientModel.Primitives;

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var result = await decisions.CreateDecisionAsync(request,
    new DecisionOptions { Method = "auto", MaxConcurrency = 4, MaxTokens = 256 },
    new RequestOptions { CancellationToken = cancellation.Token });
```

| 配置 | 默认值 | 含义 |
| --- | --- | --- |
| `Method` | `auto` | `auto`、`logprobs`、`structured` 或 `discrete` |
| `TopLogprobs` | 20 | 2–20，读取标签 token 的候选 |
| `MaxConcurrency` | 8 | 每次决策调用的并发上限，最小为 1 |
| `NormalizeProbabilities` | true | Structured 权重偏离总和 1 超过 `1e-6` 时归一化 |
| `MaxMalformedRetries` | 1 | 可设为 0 或 1；格式纠正次数 |
| `Thinking` | Disabled | 原生 Thinking 开关；不执行两阶段推理 |
| `ReasoningEffort` | null | 可选 Core 推理强度 |
| `MaxTokens`、`Temperature` | null | 由底层服务采用默认值 |

- **Logprobs**：读取首个答案标签 token，在题目选项内归一化。这是选项条件下的 token 分布；未出现在候选中的标签按零参与计算，`MissingLabels` 保留其缺失事实，不能据此认定真实概率严格为零。至少需要一个可比较的竞争选项，Jev 单选项除外。
- **Structured**：使用 `json_object` 和本地结构校验，让模型自报概率。权重必须有限、非负且键完整；默认归一化，全零权重转为均匀分布。关闭归一化时保留原始权重，Score 和 Confidence 的计算仍使用归一化分布。
- **Discrete**：模型返回一个合法选项，生成 one-hot 分布。该分布表示离散选择，不表示模型经过校准的确定性。
- **Auto**：优先 Logprobs，不可用时为本题回退 Structured。明确不支持字段时立即缓存；连续两次缺少有效读数后缓存，成功读数会重置。超过 26 个选项直接使用 Structured。显式指定 Logprobs 时不会替换方法。

Choice 取最大概率项，平局按原始顺序；Score 为等级索引的概率加权平均，可落在两级之间。Choice Confidence 比较峰值与均匀分布，Score Confidence 还考虑概率距众数等级的距离。Noul 不额外返回 Confidence。这些输出不保证具有专用 Jev 或 OpenAI 决策模型的校准精度与延迟表现。

## 诊断、错误与用量

```csharp
var diagnostic = result.Diagnostics[0];
Console.WriteLine($"{diagnostic.QuestionId}: {diagnostic.Method}/{diagnostic.ProbabilitySource}");
Console.WriteLine(diagnostic.FallbackReason);
Console.WriteLine(string.Join(",", diagnostic.MissingLabels));

// Diagnostics 不进入协议 JSON；顶层只有 model、answers、usage。
var json = System.Text.Json.JsonSerializer.Serialize(result);
```

诊断包含调用次数、格式纠正次数、缺失标签、回退原因，以及发生归一化时的原始权重；不保留请求正文、图片数据或密钥。`CallCount` 统计对 Core 的调用，Core pipeline 内部的 HTTP 重发由 Core 管理。能力缓存仅属于当前决策客户端实例，不在不同模型或不同客户端之间共享。

HTTP 重试复用 Core。Auto 遇到重试耗尽的 5xx 会为本题回退，但不缓存能力判断；鉴权、限流、普通参数错误、截断和拒绝回答明确失败。格式错误最多纠正一次。任一题最终失败，会取消在途请求并停止启动排队题目；异常保留原始类型，并在 `Data` 中附带 `QuestionId`、`DecisionDiagnostics` 和 `DecisionUsage`。

所有题目在首个请求前完成校验。请求模型、选项和传输设置在调用时取快照，调用不会修改传入的模型或冻结的 `RequestOptions`。同步方法与异步方法共享引擎；同步入口为 `CreateDecision` 和 `SystemOne`。

Usage 聚合 SDK 可见的所有尝试；任一尝试未报告的计数保留 `null`，真实的零值保留为零。错误响应未报告用量时，合计可能未知。`cache_write_tokens` 为 `null`，因为 Chat 不提供该计数。

首版范围：每次 1–400 道题，Decisions Choice 为 2–255 项，Jev Choice 为 1–255 项，Score 为 2–10 级。Decisions 仅接受 user 输入消息；完整对话角色、工具历史、文件引用、音频、Box、multipart、grammar、few-shot、多供应商路由及两阶段推理均明确拒绝或不提供入口。

## 构建与验证

从仓库根目录执行：

```powershell
dotnet build DeepSeek.slnx -c Release
dotnet test test/DeepSeek.Decisions.UnitTests/DeepSeek.Decisions.UnitTests.csproj -c Release
dotnet test test/DeepSeek.IntegrationTests/DeepSeek.IntegrationTests.csproj -c Release --filter FullyQualifiedName~DecisionsIntegrationTests
dotnet pack src/DeepSeek.Decisions/DeepSeek.Decisions.csproj -c Release
```

在线用例沿用 `LiveFact`，优先读取 `DEEPSEEK_API_KEY`，其次为 `deepseek-api-key`，最后回退到本地 `sample/appsettings.json`；开发环境缺少凭据时跳过。发布 CI 通过仓库 Secret `DEEPSEEK_API_KEY` 提供凭据，并设置 `DEEPSEEK_REQUIRE_LIVE_TESTS=true`，缺少凭据直接失败。

算法与协议参考：[jevper 0.7.12 / 4fdc054](https://github.com/zhulinchng/jevper/tree/4fdc054082e6328f2d42bc3750d7ebbf9380d3b4)、[Decisions API](https://developers.openai.com/api/docs/guides/decisions)、[Jev 图片](https://docs.typesafe.ai/images)、[Confidence 公式](https://docs.typesafe.ai/confidence)。本包是独立实现，不隶属于 OpenAI 或 TypeSafe。
