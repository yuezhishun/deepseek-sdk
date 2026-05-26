# Coding Guidelines

## Project Structure & Module Organization

`src/DeepSeek` contains the core typed SDK, split by API area under `Chat/`, `Completions/`, `Models/`, `Billing/`, `Anthropic/`, and shared helpers in `Internal/`. `src/Microsoft.Agents.AI.DeepSeek` contains the Microsoft.Extensions.AI and Agent Framework adapter layer. Tests live under `test/`, with unit tests in `test/DeepSeek.Tests` and `test/Microsoft.Agents.AI.DeepSeek.UnitTests`, plus live API coverage in `test/DeepSeek.IntegrationTests`. The runnable samples live under `sample/`.

## Build, Test, and Development Commands
Use the solution file from the repository root:

- `dotnet build DeepSeek.slnx` builds all libraries, tests, and the sample.
- `dotnet test DeepSeek.slnx` runs the supported unit and integration test projects for `src/DeepSeek` and `src/Microsoft.Agents.AI.DeepSeek`.
- `dotnet test test/DeepSeek.Tests/DeepSeek.Tests.csproj` runs SDK unit tests only.
- `dotnet run --project sample/Sample/Sample.csproj` runs the sample app locally.
- `dotnet pack src/DeepSeek/DeepSeek.csproj` or `dotnet pack src/AgentFramework.AI.DeepSeek/AgentFramework.AI.DeepSeek.csproj` creates NuGet packages in each project's `pack/` folder.

## Coding Style & Naming Conventions
Follow the existing C# conventions used across `src/` and `test/`: 4-space indentation, file-scoped namespaces, nullable reference types enabled, and `ImplicitUsings` enabled. Use `PascalCase` for public types and members, `camelCase` for locals and parameters, and keep API-area files grouped with the client they belong to, for example `Chat/ChatClient.cs` and `Chat/ChatModels.cs`. Prefer small, focused request/response model files over large mixed modules.

## Testing Guidelines
Tests use xUnit. Add or update tests only for `src/DeepSeek` and `src/Microsoft.Agents.AI.DeepSeek`. Do not create or maintain test projects for anything under `sample/`; sample projects are examples only. Name test files after the type under test and use method names in the `Method_Scenario_ExpectedResult` style already present, such as `ChatClient_SendsExpectedHeadersPathAndBody`. Keep unit tests deterministic by using in-memory handlers instead of live network calls. Live tests require `sample/appsettings.json` to contain `apiKey`; guard new live cases the same way as `LiveTestGuard.RequireConfigured(...)`.

## Commit & Pull Request Guidelines
The visible history is minimal, so keep commits short, imperative, and specific, for example `Add streaming chat error handling`. Keep unrelated changes out of the same commit. Pull requests should describe the API or behavior change, list test coverage, note any config or package-version updates, and include sample output only when the user-facing behavior changes.
