# Coding Guidelines

## Project Structure & Module Organization

`src/DeepSeek.Core` 包含核心强类型 SDK，按 API 分为 `Chat/`、`Completions/`、`Models/`、`Billing/`、`Anthropic/` 等目录，共享工具位于 `Internal/`。`src/DeepSeek.Agents.AI` 是 Microsoft.Extensions.AI 与 Agent Framework 适配层。`src/DeepSeek.Decisions` 是依赖 Core 的独立 Jev/Decisions 决策类库，不依赖 Agents。单元测试分别位于 `test/DeepSeek.Tests`、`test/DeepSeek.Agents.AI.UnitTests` 和 `test/DeepSeek.Decisions.UnitTests`，在线覆盖位于 `test/DeepSeek.IntegrationTests`。可运行示例位于 `sample/`。

## Build, Test, and Development Commands
Use the solution file from the repository root:

- `dotnet build DeepSeek.slnx` builds all libraries, tests, and the sample.
- `dotnet test DeepSeek.slnx` 运行 Core、Agents、Decisions 的单元测试与集成测试。
- `dotnet test test/DeepSeek.Tests/DeepSeek.Tests.csproj` runs SDK unit tests only.
- `dotnet run --project sample/Sample/Sample.csproj` runs the sample app locally.
- `dotnet test test/DeepSeek.Decisions.UnitTests/DeepSeek.Decisions.UnitTests.csproj` 运行决策类库的离线测试。
- 对 `src/` 下 Core、Agents 或 Decisions 的项目运行 `dotnet pack`，会在各项目的 `pack/` 目录创建 NuGet 包。

## Release Publishing

- `main` 是开发主线。功能和修复通过短分支及 PR 合并，PR 运行无需凭据的离线 CI。只有需要隔离发布范围或维护旧版本时才创建 `release/<version>`。发布完成后将相关改动合回 `main`。
- GitHub 源码发布使用 `source/vMAJOR.MINOR.PATCH` 标签。发布说明注明 Source Release。
- NuGet 正式发布使用带注释的 `vMAJOR.MINOR.PATCH` 标签。通过 GitHub 手动运行发布工作流，在目标提交上完成默认框架、.NET 11 在线回归和打包验证。标签推送后才执行 NuGet 上传。
- 发布前对齐 Core、Agents、Decisions 的包版本、程序集和文件版本、集中版本、包间依赖和 README 安装命令。CI 核对三个打包结果及其中的程序集与 README。
- 新增兼容 API 升次版本，兼容修复升补丁版本，破坏兼容的公开 API 变更升主版本。已发布的标签保持固定。上传中断时重跑同一提交，内容有变更时使用新版本。
- GitHub Release 标题只用版本号（如 `v1.1.0`）。正文提供英文说明并保留中文内容。NuGet 正式发布后核对三个包均可下载，再创建对应的 GitHub Release。

## Coding Style & Naming Conventions
Follow the existing C# conventions used across `src/` and `test/`: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and `ImplicitUsings` enabled. Use `PascalCase` for public types and members, `camelCase` for locals and parameters, and keep API-area files grouped with the client they belong to, for example `Chat/ChatClient.cs` and `Chat/ChatModels.cs`. Prefer small, focused request/response model files over large mixed modules.

## Testing Guidelines
测试使用 xUnit，仅维护 `src/DeepSeek.Core`、`src/DeepSeek.Agents.AI` 和 `src/DeepSeek.Decisions` 的测试。禁止为 `sample/` 中的示例创建或维护测试项目。文件按被测类型命名，方法沿用 `Method_Scenario_ExpectedResult` 风格，例如 `ChatClient_SendsExpectedHeadersPathAndBody`。单元测试使用内存 handler，保持确定性；决策测试需验证概率含义、证据保留及失败语义。在线凭据优先读取 `DEEPSEEK_API_KEY`，其次为 `deepseek-api-key`，最后回退到本地 `sample/appsettings.json` 的 `apiKey`。在线用例沿用 `LiveFact`；发布 CI 必须配置仓库 Secret `DEEPSEEK_API_KEY`，并设置 `DEEPSEEK_REQUIRE_LIVE_TESTS=true`，缺少凭据时直接失败。手动运行发布工作流验证测试与打包，NuGet 上传仅由 `v*` tag push 触发。

## Commit & Pull Request Guidelines
The visible history is minimal, so keep commits short, imperative, and specific, for example `Add streaming chat error handling`. Keep unrelated changes out of the same commit. Pull requests should describe the API or behavior change, list test coverage, note any config or package-version updates, and include sample output only when the user-facing behavior changes.
