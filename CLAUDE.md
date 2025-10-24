# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

EverTask is a .NET background task execution library inspired by MediatR. It provides persistent, resilient task execution with support for scheduled, delayed, and recurring tasks. Multi-targets **net6.0, net7.0, net8.0, net9.0**.

**Key Features**: Request/handler pattern, persistent storage (SQL Server, SQLite, In-Memory), retry policies with exception filtering, scheduled/recurring tasks (cron + fluent API), lifecycle callbacks, timeout handling, monitoring integrations.

## File Organization Principle

- **Root CLAUDE.md** (this file) = Global rules, architecture, standards
- **Local CLAUDE.md** = Module-specific gotchas, prerequisites, operational notes only
- **IMPORTANT**: Avoid duplicating content between root and local files. Root = general guidance, local = specific operational details.

## Solution Structure

- **src/EverTask**: Core library (dispatcher, worker executor, scheduler, in-memory storage)
- **src/EverTask.Abstractions**: Lightweight interfaces package (IEverTask, ITaskDispatcher, IEverTaskHandler)
- **src/Storage/**: Persistence providers (EfCore, SqlServer, Sqlite)
- **src/Logging/**: Logging integrations (Serilog)
- **src/Monitoring/**: Monitoring integrations (SignalR)
- **samples/**: Example implementations
- **test/**: Test projects mirroring src/

## Build & Test Commands

| Task | Command | Notes |
|------|---------|-------|
| **Build** | `dotnet build EverTask.sln -c Release` | Warnings as errors |
| **Test All** | `dotnet test EverTask.sln -c Release` | Exclude SQL Server: add `--filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"` |
| **Pack** | `dotnet pack <project.csproj> -c Release -o nupkg` | For all projects in src/ |
| **Run Sample** | `dotnet run --project samples/<project>/<project>.csproj` | AspnetCore or Console |

**Prerequisites**: .NET 9 SDK (pinned in `global.json`), SQL Server/LocalDB optional for storage tests.

## Coding Standards

**IMPORTANT**: Strict code quality enforced.

- **Test Framework**: xUnit + Shouldly + Moq (NOT MSTest)
- **Nullable**: Enabled project-wide (`<Nullable>enable</Nullable>`)
- **Warnings as Errors**: `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` — code MUST build warning-free
- **EditorConfig**: `.editorconfig` enforces 4-space indent, LF line endings, implicit `var`
- **Naming**: PascalCase (types/public), camelCase (params/locals), _camelCase (private fields), I prefix (interfaces)
- **Namespaces**: Match folder paths (e.g., `EverTask.Storage.SqlServer`)

## Architecture Overview

EverTask uses MediatR-inspired request/handler pattern adapted for persistent background execution:

- **Dispatcher** → Serializes & persists tasks (ITaskStorage) → Routes to queues:
  - Immediate tasks: BoundedQueue (System.Threading.Channels)
  - Scheduled/recurring: ConcurrentPriorityQueue (TimerScheduler)
- **WorkerExecutor** → Executes with retry policies, timeouts, lifecycle callbacks (OnStarted, OnCompleted, OnError, OnRetry)
- **TaskHandlerExecutor** → Task execution metadata, converts to/from QueuedTask for persistence
- **ITaskStorage** → Abstract persistence (SqlServer, Sqlite, InMemory implementations in src/Storage/)
- **Scheduler** → Cron + fluent API for recurring tasks

**Key Pattern**:
```csharp
// Request: public record MyTask(...) : IEverTask;
// Handler: public class MyHandler : EverTaskHandler<MyTask> { ... }
// Register: services.AddEverTask(opt => opt.RegisterTasksFromAssembly(...)).AddSqlServerStorage(...);
```

See local CLAUDE.md files for implementation details.

## Operative Checklists

### Critical Design Decisions
- **MediatR Inspiration**: `IEverTask` ≈ `INotification`, `IEverTaskHandler<T>` ≈ `INotificationHandler<T>`, `ITaskDispatcher` ≈ `IMediator`
  - **Key Differences**: Persistent (survives restarts), scheduling/recurring, retry policies, timeouts, monitoring
- **Serialization**: Uses **Newtonsoft.Json** (NOT System.Text.Json) for polymorphism support
  - **Best Practice**: Keep tasks simple (primitives, Guid, DateTimeOffset), use IDs not entities (e.g., `Guid OrderId` not `Order Order`)
- **DI Scoping**: WorkerExecutor creates **scoped service scope per task** (safe DbContext usage, no shared state)
- **Retry Policies (v1.6.0+)**: Exception filtering (whitelist/blacklist), predicate filtering, OnRetry callback
  - **Default**: Retry all except `OperationCanceledException` and `TimeoutException`
  - See `src/EverTask.Abstractions/CLAUDE.md` for details

### Testing
- **Framework**: xUnit + Shouldly + Moq
- **Naming**: `Should_{expected_behavior}_when_{condition}` or `Should_{expected_behavior}`
- **Organization**: Tests mirror src/ structure
- **Helpers**: `test/EverTask.Tests/TestHelpers/` (TaskWaitHelper, TestTaskStateManager)
- **SQL Server Tests**: Require Docker (see `src/Storage/EverTask.Storage.SqlServer/CLAUDE.md`)
- See `test/EverTask.Tests/CLAUDE.md` for patterns

### Commit & PR
- **Format**: Conventional commits (imperative mood) — `feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `build:`
- **Scoped**: `feat(storage):`, `fix(scheduler):`, etc.
- **PRs**: Link issues, outline behavioral impact/breaking changes, list verification steps, update docs/samples

## Ops Quick Facts

- **Package Management**: Central Package Management (`Directory.Packages.props`) — do NOT add `<PackageReference>` versions in .csproj, add to Directory.Packages.props
- **Version**: Current 1.5.4 (defined in `Directory.Build.props`)
- **CI/CD**: Build on push/PR to master (`.github/workflows/build.yml`), manual release workflow (`.github/workflows/release.yml`)
- **MediatR Attribution**: Core files adapted from MediatR (Apache 2.0) — see attribution comments in Dispatcher.cs, TaskHandlerExecutor.cs, TaskHandlerWrapper.cs, HandlerRegistrar.cs

## Module-Specific Guidance

| Module | Local CLAUDE.md | Focus |
|--------|-----------------|-------|
| **Core** | `src/EverTask/CLAUDE.md` | Dispatcher/worker implementation, MediatR attribution, async guidance |
| **Abstractions** | `src/EverTask.Abstractions/CLAUDE.md` | Interfaces, retry policy details, serialization gotchas |
| **Recurring** | `src/EverTask/Scheduler/Recurring/CLAUDE.md` | Cron scheduling, builder flow, calculation gotchas |
| **SQL Server** | `src/Storage/EverTask.Storage.SqlServer/CLAUDE.md` | Setup, schema-aware migrations, Docker testing |
| **SQLite** | `src/Storage/EverTask.Storage.Sqlite/CLAUDE.md` | Setup, connection strings |
| **EF Core** | `src/Storage/EverTask.Storage.EfCore/CLAUDE.md` | Base EF Core implementation |
| **Serilog** | `src/Logging/EverTask.Logging.Serilog/CLAUDE.md` | Serilog integration |
| **SignalR** | `src/Monitoring/EverTask.Monitor.AspnetCore.SignalR/CLAUDE.md` | SignalR monitoring |
| **Tests** | `test/EverTask.Tests/CLAUDE.md` | Test organization, naming conventions, helpers |
| **Storage Tests** | `test/EverTask.Tests.Storage/CLAUDE.md` | Storage integration tests |
| **Logging Tests** | `test/EverTask.Tests.Logging/CLAUDE.md` | Logging integration tests |

**Adding New Modules**: Create local CLAUDE.md only for module-specific prerequisites or critical gotchas. Link to external docs for extended explanations. Follow 40-100 line guideline.
