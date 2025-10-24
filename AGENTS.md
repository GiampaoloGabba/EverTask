# Repository Guidelines

## Project Structure & Module Organization
EverTask.sln orchestrates modular packages under `src/`: `EverTask` core runtime, `EverTask.Abstractions` interfaces, `Storage/*` persistence providers, `Logging/*` integrations, and `Monitoring/*` for SignalR monitoring. Mirror tests live in `test/` (e.g., `EverTask.Tests`, `EverTask.Tests.Storage`), while documentation, assets, runnable samples, and staged packages reside in `docs/`, `assets/`, `samples/`, and `nupkg/`.

## Build, Test, and Development Commands
- `dotnet restore EverTask.sln` — hydrate dependencies with the .NET 9 SDK pinned in `global.json`.
- `dotnet build EverTask.sln -c Release` — compile all target frameworks (net6.0–net9.0) with warnings promoted to errors.
- `dotnet test EverTask.sln --configuration Release` — execute unit, integration, and storage suites; append `--filter FullyQualifiedName!~SqlServer` when SQL Server infrastructure is unavailable.
- `dotnet pack src/EverTask/EverTask.csproj -c Release -o nupkg` — produce NuGet artifacts; repeat for abstractions, storage, logging, and monitoring projects touched.
- `dotnet run --project samples/EverTask.Example.Console/EverTask.Example.Console.csproj` — smoke-test workflow samples before publishing API changes.

## Coding Style & Naming Conventions
`.editorconfig` enforces spaces-only indentation (4 for C#), LF endings, final newline, and implicit `var` usage when the type is obvious. Projects opt into nullable reference types, `LangVersion` 12, and `TreatWarningsAsErrors`, so fixes must land warning-free. Follow the configured naming rules: PascalCase for types and members, camelCase for parameters, `_camelCase` for private fields, and `I` prefixes for interfaces; keep namespaces aligned with folder paths.

## Testing Guidelines
Tests use xUnit attributes and mirror production code; integration flows sit under `test/EverTask.Tests/IntegrationTests` and reuse helpers in `test/EverTask.Tests/TestHelpers`. Extend existing fixtures for logging, GUID generation, and async coordination rather than re-inventing harnesses. Guard new behavior with fast unit coverage plus targeted integration scenarios, document environmental requirements (e.g., SQL Server container), and run `dotnet test` locally before opening a pull request.

## Commit & Pull Request Guidelines
Git history follows conventional prefixes such as `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, often with scoped variants like `feat(storage):`. Craft commit subjects in the imperative mood, summarizing intent and notable constraints. Pull requests should link issues, outline behavioral impact, list verification steps (`dotnet test`, sample runs), and update docs or samples alongside code changes so reviewers can validate quickly.

## Documentation & Samples
The published site pulls from `docs/`; refresh or add Markdown when APIs or defaults shift. Keep `samples/` compilable across all target frameworks, and record required secrets or connection strings in the sample README before shipping demos.
