# CLAUDE.md

EverTask is a .NET background task execution library inspired by MediatR: persistent, resilient execution of
immediate, delayed and recurring tasks, with retry policies, timeouts, keyed rate limiting and monitoring.
Multi-targets **net8.0, net9.0, net10.0** (`Directory.Build.props`).

## Build & Test

| Task | Command | Notes |
|------|---------|-------|
| **Build** | `dotnet build EverTask.slnx -c Release` | Warnings as errors; `.slnx` needs SDK 9.0.200+ |
| **Test All** | `dotnet test EverTask.slnx -c Release` | Skip SQL Server: `--filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"` |
| **Pack** | `dotnet pack <project.csproj> -c Release -o nupkg` | |

**Prerequisites**: .NET 9 SDK (`global.json`, rollForward latestMajor). Storage tests for SQL Server,
PostgreSQL and MySQL/MariaDB need Docker (Testcontainers).

**Never run a build while the test suite is running**: the test hosts hold the output assemblies, and the
container-backed tests fall over under the contention.

## Coding Standards

- **Tests**: xUnit + Shouldly + Moq (NOT MSTest), named `Should_{expected}_when_{condition}`
- **Warnings as errors** across the solution — code MUST build warning-free
- **Namespaces** match folder paths (e.g. `EverTask.Storage.SqlServer`)

### Write it right the first time

The analyzer ruleset is an explicit rule list at the end of `.editorconfig` (NOT `AnalysisMode=All`). Rules
set to `warning` there are build ERRORS. What follows is what the build canNOT enforce, so it only holds if
applied while writing:

- **`ConfigureAwait(false)` on every awaited call in `src/`** — this is a library; a consumer awaiting from a
  sync context must not deadlock. **Deliberate exception: NOT on `await using` declarations.** Satisfying
  CA2007 there means splitting the declaration in two or, for the `AsyncServiceScope` struct, boxing it into
  a `ConfiguredAsyncDisposable` — one allocation per task on the hot path. That is why CA2007 sits at
  `suggestion`; do not "fix" those sites.
- **Internal logs are source-generated**: every log in `src/` is a `[LoggerMessage]` method on the component's
  `internal static partial class <Component>Log` (extension form, `this ILogger`, explicit EventId from the
  component's range — a test asserts solution-wide uniqueness). Never call `logger.LogX(...)` directly in
  `src/` (CA1848/CA1873/CA2254/CA1727 are build errors); the one exception is the `TaskLogCapture` forward of
  consumer-supplied templates. Per-task lifecycle and storage status-transition lines log at `Debug`; in
  `WorkerExecutor` the log level is independent of the monitoring event's `Severity`.
- **Structured log placeholders in PascalCase** (`{TaskId}`, never `{taskId}`): a sink treats the two casings
  as different properties, so a query on one silently misses the other.
- **Log messages are fragments, no trailing period.** In `WorkerExecutor` they are also the monitoring
  event's `Message`, visible in the dashboard and text-matched by consumers (keep `Rate limit deferred task `,
  `completed`, `cancelled`, `Error occurred` fragments stable).
- **Never flatten an explicit `object[]` into `params` on `ExecuteSqlRawAsync(sql, args, ct)`**: overload
  resolution moves to `params object[]` and the `CancellationToken` silently becomes a SQL parameter. It
  compiles.
- **EF migrations are frozen**: exclude them from every formatting/cleanup pass (`dotnet format` does not
  honor the exclusion used for the ReSharper pass — pass `--exclude "**/Migrations/**"`).

## Architecture

- **Dispatcher** → serializes & persists (`ITaskStorage`) → routes: immediate to a bounded channel,
  scheduled/recurring to the priority queue of `PeriodicTimerScheduler` / `ShardedScheduler`
- **WorkerExecutor** → executes with retry policy, timeout and lifecycle callbacks, in a **scoped service
  scope per task** (DbContext is not thread-safe)
- **RateLimitGate** (handlers declaring a `RateLimitPolicy` only) → per-key GCRA budget at dequeue;
  over-budget tasks re-park into the scheduler with NO storage write
- **ITaskStorage** → SqlServer, Postgres, MySql, Sqlite (all EF Core) + InMemory

The no-loss / no-deadlock invariants of the queue and recovery paths live in `src/EverTask/CLAUDE.md` — read
them before touching the dispatcher, the worker or a recovery filter.

## Critical Design Decisions

- **Serialization**: System.Text.Json via the internal, isolated `EverTaskJson` (private static options).
  Reads legacy Newtonsoft rows leniently, writes the historical numeric form. Payload contract (public
  properties only, fields dropped, Newtonsoft attributes NOT honored): `src/EverTask.Abstractions/CLAUDE.md`.
  Keep payloads simple and carry IDs, not entities.
- **Retry policies**: exception filtering (whitelist/blacklist), predicate filtering, OnRetry callback.
  Default retries everything except `OperationCanceledException` and `TimeoutException`.

## Commit & PR

- Conventional commits, imperative mood, scoped: `feat(storage):`, `fix(scheduler):`
- PRs: link issues, state behavioral impact and breaking changes, list verification steps
- **Public option changes (anti-stale)**: adding or changing any public configuration option/default requires
  updating ALL THREE in the same PR — `docs/configuration-cheatsheet.md` (exhaustive, one row per option),
  `docs/configuration-reference.md`, and `plugins/evertask/skills/integrate-evertask/`

## Ops Quick Facts

- **Central Package Management**: versions go in `Directory.Packages.props`, never in a `.csproj`
- **Version**: `Directory.Build.props`, lockstep across all packages (current 3.11.0)
- **CI**: build on push/PR to master; release is a manual workflow
- **MediatR attribution**: `Dispatcher.cs`, `TaskHandlerExecutor.cs`, `TaskHandlerWrapper.cs`,
  `HandlerRegistrar.cs` are adapted from MediatR (Apache 2.0) — keep the attribution comments

## Module-Specific Guidance

| Module | Local CLAUDE.md |
|--------|-----------------|
| Core (dispatcher, worker, queue/recovery invariants) | `src/EverTask/CLAUDE.md` |
| Abstractions (interfaces, retry, payload contract, analyzers ET0001–ET0008) | `src/EverTask.Abstractions/CLAUDE.md` |
| Rate limiting (hard invariants) | `src/EverTask/RateLimiting/CLAUDE.md` |
| Recurring (cron, builder, skip-forward) | `src/EverTask/Scheduler/Recurring/CLAUDE.md` |
| EF Core base | `src/Storage/EverTask.Storage.EfCore/CLAUDE.md` |
| SQL Server / PostgreSQL / MySQL / SQLite | `src/Storage/EverTask.Storage.<Provider>/CLAUDE.md` |
| Monitoring API + dashboard UI | `src/Monitoring/EverTask.Monitor.Api/CLAUDE.md`, `.../UI/CLAUDE.md` |
| SignalR monitoring | `src/Monitoring/EverTask.Monitor.AspnetCore.SignalR/CLAUDE.md` |
| Serilog | `src/Logging/EverTask.Logging.Serilog/CLAUDE.md` |
| Tests | `test/EverTask.Tests/CLAUDE.md`, `test/EverTask.Tests.Storage/CLAUDE.md`, `test/EverTask.Tests.Logging/CLAUDE.md` |

Create a local CLAUDE.md only for module-specific prerequisites or critical gotchas; keep it 40-100 lines and
do not duplicate what is here.
