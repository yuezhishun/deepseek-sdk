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
NuGet publishing must be triggered only by pushing a `v*` tag such as `v1.0.2`. `release/*` branches are for release preparation and must not be treated as the package version source. Before tagging, confirm the target commit contains the intended SDK, test, and workflow fixes.

Publish checklist and pitfalls:

- 更新 `src/` 下 Core、Agents、Decisions 三个包项目的 `Version`、`AssemblyVersion`、`FileVersion` 和发布说明，使其与目标版本一致，例如 `1.0.3` / `1.0.3.0`。
- Keep the workflow packaging step aligned with the pushed tag version. The publish workflow resolves `PACKAGE_VERSION` from the `v*` tag and passes it into `dotnet pack`.
- Do not use a `v*` branch name as the release trigger. A branch like `v1.0.3` will fail publish validation because the workflow requires `refs/tags/v*`.
- If a branch and tag share the same name, push the tag with an explicit ref such as `git push origin refs/tags/v1.0.3` to avoid refspec ambiguity.
- If a tag was pushed before the release commit was ready, move the local tag to the corrected commit, delete the remote tag with `git push origin :refs/tags/v1.0.3`, then push the corrected tag again.

## Coding Style & Naming Conventions
Follow the existing C# conventions used across `src/` and `test/`: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and `ImplicitUsings` enabled. Use `PascalCase` for public types and members, `camelCase` for locals and parameters, and keep API-area files grouped with the client they belong to, for example `Chat/ChatClient.cs` and `Chat/ChatModels.cs`. Prefer small, focused request/response model files over large mixed modules.

## Testing Guidelines
测试使用 xUnit，仅维护 `src/DeepSeek.Core`、`src/DeepSeek.Agents.AI` 和 `src/DeepSeek.Decisions` 的测试。禁止为 `sample/` 中的示例创建或维护测试项目。文件按被测类型命名，方法沿用 `Method_Scenario_ExpectedResult` 风格，例如 `ChatClient_SendsExpectedHeadersPathAndBody`。单元测试使用内存 handler，保持确定性；决策测试需验证概率含义、证据保留及失败语义。在线用例沿用现有 `LiveFact`，要求 `sample/appsettings.json` 包含 `apiKey`；完整在线验收可设置 `DEEPSEEK_REQUIRE_LIVE_TESTS=true`。

## Commit & Pull Request Guidelines
The visible history is minimal, so keep commits short, imperative, and specific, for example `Add streaming chat error handling`. Keep unrelated changes out of the same commit. Pull requests should describe the API or behavior change, list test coverage, note any config or package-version updates, and include sample output only when the user-facing behavior changes.
