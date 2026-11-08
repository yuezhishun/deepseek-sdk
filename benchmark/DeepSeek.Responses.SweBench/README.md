# DeepSeek Responses SWE-Sharp-Bench benchmark

这是一个独立、无需 Docker 的 C#/.NET 软件工程 benchmark，用于验证 DeepSeek Responses adapter 驱动长时间编码 Agent 的能力。任务来自 Microsoft 的 [SWE-Sharp-Bench](https://huggingface.co/datasets/microsoft/SWE-Sharp-Bench)，评测语义沿用 SWE-bench 的 issue、base commit、patch 和回归测试流程，但结果只表示本地 SWE-Sharp-Bench 兼容评测，不代表官方 leaderboard 分数。

数据集包含 150 个真实 .NET 仓库任务。本工具默认固定三个任务，并将数据集 revision 固定为 `41e599424ff967e0f44d69750f9f023a89f7758d`：

- `ardalis__cleanarchitecture-546`
- `dotnet__benchmarkdotnet-2491`
- `messagepack-csharp__messagepack-csharp-2057`

`tasks.json` 只包含 issue、仓库、base commit、版本和 F2P/P2P 测试元数据，不包含 gold patch 或 `test_patch`。评测器运行时从 Hugging Face 获取隐藏测试 patch，并且只将它应用到独立 evaluation workspace，Agent workspace 永远看不到它。

## Run

从仓库根目录提供真实 API key：

```bash
DEEPSEEK_API_KEY=... dotnet run --project benchmark/DeepSeek.Responses.SweBench
```

指定任务或调整限制：

```bash
dotnet run --project benchmark/DeepSeek.Responses.SweBench -- \
  --instances ardalis__cleanarchitecture-546 \
  --max-turns 50 \
  --task-timeout 30m \
  --command-timeout 5m \
  --work-root artifacts/swe-sharp-bench/workspaces \
  --output-dir artifacts/swe-sharp-bench/output
```

默认模型是 `deepseek-v4-flash`。每个任务创建两个干净仓库 clone；启用 setup 时分别执行 `dotnet restore` 和 `dotnet build`。机器上缺少仓库所需 .NET SDK、NuGet 依赖无法恢复、构建失败或 baseline 状态不符合元数据时，任务会标记为 `environment_error`，不计入 resolved rate。使用 `--setup=false` 可以复用机器上已有的 restore/build 环境。

Agent 使用 `ResponsesClient.AsIChatClient()` 进行显式多轮 streaming 循环，工具包括：

- `list_files`
- `read_file`
- `search_repository`
- `run_command`
- `apply_patch`
- `git_diff`
- `run_tests`

工具只能访问任务 workspace；命令在 workspace 内执行，具有超时、输出上限和破坏性操作限制。API key 不写入 trajectory、patch 或日志。

## Evaluation

每个任务按三个阶段执行：

1. 在未应用隐藏测试的干净 base clone 上验证 P2P 通过，再在另一个应用了 `test_patch` 的 clone 上验证 F2P 在 base commit 失败；否则标记为 `environment_error`。这样可以处理 C# 隐藏测试在修复前无法编译的情况。
2. Agent 在独立 base clone 中检查代码、修改 C# 文件并运行 `dotnet test`。
3. 在新的 evaluation clone 上依次应用 `test_patch` 和 Agent patch；只有所有 F2P 通过且 P2P 没有回归，任务才标记为 `resolved`。

SWE-Sharp-Bench 测试名称会转换为 `dotnet test --filter FullyQualifiedName~...`，支持多个测试名称和嵌套类型名称。

## Outputs

默认输出目录包含：

- `predictions.json`：标准预测记录，包括 `instance_id`、模型名称和 unified patch。
- `trajectory/<instance_id>.jsonl`：每轮请求、streaming 响应、tool call、tool result、耗时、token usage 和终止原因。
- `results.json`：baseline、Agent patch、测试结果、错误分类、轮数、耗时、token 和本地 `resolved_rate`。

## Deterministic checks

无需 API call 即可运行安全和解析自测：

```bash
dotnet run --project benchmark/DeepSeek.Responses.SweBench -- --self-test
```

该自测覆盖 CLI、SWE-Sharp-Bench 测试过滤器、路径/命令安全、tool schema、输出限制、进程超时和符号链接逃逸检查。
