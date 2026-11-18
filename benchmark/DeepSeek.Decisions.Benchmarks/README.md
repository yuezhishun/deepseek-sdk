# DeepSeek.Decisions 官方 WorkflowEvals 评测

使用 TypeSafe 官方 [WorkflowEvals](https://github.com/typesafe-ai/WorkflowEvals) 评测 `DeepSeek.Decisions` 与 `deepseek-flash` 的组合，和公开的 `jev-1.13.0` 逐例结果比较。运行时仅需要 .NET 10 或更新版本；SDK 公共 API 不受影响。

| 工作流 | 案例 | 策略决策 | Jev 公开路径题目数 |
| --- | ---: | ---: | ---: |
| invoice_processing | 150 | 450 | 6,874 |
| customer_service | 204 | 612 | 3,270 |
| agent_trace_observability | 111 | 222 | 1,106 |
| security_incidents | 240 | 240 | 830 |
| 合计 | 705 | 1,524 | 12,080 |

表中题目数来自选定 Jev 运行实际经过的路径。公开参考题目表还有其他路径，因此题目数与参考表行数不同；DeepSeek 的新答案也可能选择不同路径。

## 数据与实现

官方代码固定为 `0ac3b8ad845429f0d8e064ecfb2430a47c5a25cb`。四份 Hugging Face 快照固定为：

| 数据集（`typesafeAI/`） | revision |
| --- | --- |
| evalsafe-invoice-processing | `6beeb2d2acd65c086c835022f5f4d7434114cafc` |
| evalsafe-customer-service | `b1342f5a704587dbc465867c38c2694348ff86e4` |
| evalsafe-agent-trace-observability | `8635540973910a92465fe2bc53e195375aa6e1a8` |
| evalsafe-security-incidents | `fbe1ea5c69cf494157fd23f2002a0d9d9a418443` |

`prepare` 下载每份数据的 `dataset.json`、README 和三个 Parquet 文件。`sources.json` 保存下载地址、revision、字节数及 SHA-256；每次加载重新检查校验值。下载的源文件保存在 git 忽略的 `artifacts/workflowevals`，不会提交原始数据。

模型调用前已经确定的节点输入直接使用官方 Parquet 中的 `state_json` 与 `question_json`：发票的 semantic 输入及初始 exact_facts、Agent 轨迹的固定节点输入。它们仅包含该节点的证据和问题，不包含参考答案。发票的答案相关事实解析及全部 A–E 策略阶段、客服规则及节点门控、Agent 分类与安全规则、安全事件 playbook 均在 C# 中执行。新分支由当前模型答案选择；Agent 未记录的 attribution/clarity 输入由固定证据按官方规则构造。

`questions.json` 保存固定源码的问题定义，动态 intent-pair、消息完整性及工具步骤选项在 C# 中生成。`customer-policies.json` 是官方声明式规则表的 JSON 表达，包含原规则顺序、阈值、停止条件和动作；C# 直接求值这些条件。更新任一规则时都必须重新执行完整 Jev 回放。

## 先准备、验证与估价

从仓库根目录运行：

```powershell
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- prepare
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- --self-test
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- replay
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- estimate
```

以上命令均不调用 DeepSeek 或 TypeSafe 模型 API。`prepare` 及首次 `estimate` 会下载公开数据或 tokenizer。自测在本地运行，要求已缓存 tokenizer；其 HTTP 检查使用内存 handler。

- `validation.json`：705 个案例及 Jev 关联验证，对 consensus、OpenAI、Anthropic 重算参考动作一致率；consensus 分母和分数必须与公开结果一致，误差不超过 `1e-9`。
- `replay.json`：C# 工作流读取公开 Jev 答案，重新计算每个分支和最终动作；每个经过节点的问题、输入、完整路径、动作集合和主要动作必须与 Jev 记录一致。
- `estimate.html`、`estimate.json`：中文费用预估和计算依据。SDK 请求由内存 handler 捕获，分词使用 DeepSeek 官方 tokenizer 的词表、BPE merges、预分词配置和特殊 token。

正常情景假定 Logprobs 可用；超过 26 个选项的 Choice 使用 Structured。保守情景计入每题 Structured、一次格式纠正和适用的 Logprobs 探测，额外增加 20% 输入余量与每次 20 token 包装余量。Structured 输出按 Noul 16 token、其他题目每选项 16 token 加 64 token 估计。两种情景均按缓存未命中、高峰与低峰分别计价，并单列 MaxTokens=4096 的输出上限情景。

价格快照为 **2026-11-11**，来源为 [DeepSeek 官方定价](https://api-docs.deepseek.com/quick_start/pricing/)。离线 tokenizer 使用官方公开 V4 配置；服务端 Flash 版本、包装、生成长度及工作流路径仍会影响实际账单。**预估区间不是账单上限。** 应先审阅 estimate.html 并确认预算，再运行模型评测。

## 显式运行付费评测

API Key 按 `DEEPSEEK_API_KEY`、`deepseek-api-key`、仓库根目录下 `sample/appsettings.json` 的 `apiKey` 顺序读取，不写入日志。配置凭据后，将下面的占位符替换为已经确认的 USD 预算：

```powershell
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- run --name flash-auto --budget-usd <已确认的美元预算>
```

`run` 没有默认预算。未知参数会失败，不能用未支持的 `--dry-run` 等参数启动运行。默认配置：

- `deepseek-flash`、`Method=auto`、Thinking Disabled、Temperature=0、MaxTokens=4096。
- 案例并发 8、每次决策题目并发 8；每次决策超时 600 秒。
- HTTP 自动重试设为 0，SDK 格式纠正保留默认最多 1 次；每次真实 HTTP 尝试独立记账。

`run` 会先验证源文件、重算 Jev 指标并回放完整路径。输出目录锁阻止两个进程同时写入；manifest 绑定数据、估价、模型参数、实现、SDK 二进制、元数据和运行时版本。不一致时必须使用新运行名称。

成功案例在同一运行中复用。默认保留失败案例；显式添加 `--retry-errors true` 才重跑。旧检查点存入 `attempts`，旧 HTTP 日志及费用始终保留。预算停止的案例允许续跑。不要在续跑过程中重新生成或替换该运行绑定的 estimate.json。

预算按已知累计费用加在途预留判断。预留使用本地输入估算、余量和 4096 输出 token，结算采用响应 usage，区分缓存命中并统一按高峰价格保守估计。费用未知或响应模型改变时停止新增调用。意外终止留下的预留、未知用量不会被归零；必须先核对账单并保留审计记录。预算控制仍受 token 估算误差影响，不能替代服务商账户的消费限制。

## 结果与失败语义

```powershell
dotnet run --project benchmark/DeepSeek.Decisions.Benchmarks -c Release -- report --name flash-auto
```

运行输出位于 `artifacts/workflowevals/runs/<name>/`：

在首次模型运行之前生成报告时，预览保存至 `artifacts/workflowevals/reports/<name>/`，报告会明确显示未运行状态。

- `report.html`、`comparison.csv`、`results.json`：中文报告、可导入表格的对照、全部参考和逐策略分数。
- `cases/`：案例状态、输入哈希、动作、概率诊断、耗时及节点日志引用。
- `node-calls/`：每个节点的原始证据、问题及 SDK 答案。
- `calls/`：每次真实 HTTP 尝试的请求体、响应、实际型号、usage、耗时及估算费用；不保存认证头。
- `ledger.json`：预算、累计费用、在途预留、未知尝试及停止原因。

主参考为 consensus。动作集合比较包含参数，忽略顺序和重复；主要动作比较同样包含参数。预测错误计为不一致。未执行或预算停止的案例保留缺失状态，报告完整覆盖数；存在失败、缺失或未知费用时，状态为 `incomplete`，运行命令返回非零。未完成时不输出 DeepSeek 的完整总体分数。完整总体分数按四个工作流的主指标等权平均。

Jev 的成本和延迟来自 2026-09-28 公开历史记录，单独呈现。网络、并发和模型版本不同，不能把两列直接解释为同期速度提升。标签由模型生成，报告使用“参考动作一致率”，而非人工真实正确率。

## 本地回归

```powershell
dotnet build DeepSeek.slnx -c Release
dotnet test test/DeepSeek.Decisions.UnitTests/DeepSeek.Decisions.UnitTests.csproj -c Release
```

benchmark 不会在常规构建、单元测试或发布流程中自动运行。上游派生内容和许可见 NOTICE.md 与 UPSTREAM-LICENSE。
