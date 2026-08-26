# CLAUDE.md

EverTask is a .NET background task execution library inspired by MediatR: persistent, resilient execution of
immediate, delayed and recurring tasks, with retry policies, timeouts, keyed rate limiting and monitoring.
Multi-targets **net8.0, net9.0, net10.0** (`Directory.Build.props`).

## Build & Test

- **Build** `dotnet build EverTask.slnx -c Release` (warnings are errors; `.slnx` needs SDK 9.0.200+).
  **Test** `dotnet test EverTask.slnx -c Release`. Docker-free run — six test classes start a container, so it
  takes all four filters:
  `--filter "FullyQualifiedName!~SqlServer&FullyQualifiedName!~Postgres&FullyQualifiedName!~MySql&FullyQualifiedName!~AuditLevel"`.
- **Prerequisites**: .NET 10 SDK — it builds all three TFMs (`global.json` pins 9.0.0 with `rollForward:
  latestMajor`). SQL Server, PostgreSQL and MySQL/MariaDB storage tests need Docker (Testcontainers).
- **Never run a build while the test suite is running**: the test hosts hold the output assemblies and the
  container-backed tests fall over under the contention.
- **Tests**: xUnit + Shouldly + Moq (NOT MSTest), named `Should_{expected}_when_{condition}`.

## Write it right the first time

The analyzer ruleset is an explicit rule list at the end of `.editorconfig` (NOT `AnalysisMode=All`); rules at
`warning` there are build ERRORS. What follows is what the build canNOT enforce:

- **`ConfigureAwait(false)` on every awaited call in `src/`** — CA2007 deliberately sits at `suggestion`
  (rationale at `.editorconfig:472`), so a miss compiles. Exception: never on `await using` declarations.
- **A new log is a `[LoggerMessage]` method in the component's `<Component>Log` class** (next EventId of the
  range in its header; direct `logger.LogX` calls are build errors, `TaskLogCapture`'s consumer-template
  forward is the one exception). Per-task lifecycle and storage status-transition lines are `Debug`; in
  `WorkerExecutor` the level is chosen independently of the monitoring event's `Severity`.
- **Log messages are fragments, no trailing period** — in `WorkerExecutor` they are also the monitoring
  event's `Message`, shown in the dashboard and text-matched by consumers (keep `Rate limit deferred task `,
  `completed`, `cancelled`, `Error occurred` stable).
- **Never flatten an explicit `object[]` into `params` on `ExecuteSqlRawAsync(sql, args, ct)`**: overload
  resolution moves to `params object[]`, the `CancellationToken` silently becomes a SQL parameter, it compiles.
- **Comments say WHY, never HOW — and only a non-obvious why**: a special case, a fixed race, an order
  that must not change, in 1-3 tight lines. No essays (long rationale goes in the commit message), no
  plan/review codes (`M7`, `CU13`, `F6`, ...) — a GitHub issue reference is fine. XML docs only on public
  surface: contract and exceptions, not history. Never comment to persuade a reviewer.
- **EF migrations are frozen**: exclude them from every formatting/cleanup pass (`dotnet format` ignores the
  ReSharper pass's exclusion — pass `--exclude "**/Migrations/**"`).

## Architecture

- **Dispatcher** → serializes & persists (`ITaskStorage`) → routes: immediate to a bounded channel,
  scheduled/recurring to the priority queue of `PeriodicTimerScheduler` / `ShardedScheduler`
- **WorkerExecutor** → retry policy, timeout and lifecycle callbacks, in a scoped service scope per task
- **OccurrenceMaterializer** (durable schedules only, opt-in) → one child row per due slot, misfire policies
- **RateLimitGate** (handlers declaring a `RateLimitPolicy` only) → per-key GCRA budget at dequeue
- **ITaskStorage** → SqlServer, Postgres, MySql, Sqlite (all EF Core) + InMemory

The no-loss / no-deadlock invariants of the queue and recovery paths live in `src/EverTask/CLAUDE.md` — read
them before touching the dispatcher, the worker or a recovery filter.

## Critical Design Decisions

- **Serialization**: System.Text.Json via `src/EverTask/Serialization/EverTaskJson.cs` — process-isolated
  static options a host's global ASP.NET JSON config (or a Newtonsoft `JsonConvert.DefaultSettings`) cannot
  reach; reads legacy Newtonsoft rows leniently, writes the historical numeric form. Payload contract:
  `src/EverTask.Abstractions/CLAUDE.md`. Keep payloads simple and carry IDs, not entities.
- **Retry policies**: `LinearRetryPolicy` / `ExponentialRetryPolicy` over a shared `RetryPolicyBase<TPolicy>`;
  exception filtering (whitelist/blacklist/predicate), OnRetry callback. Default retries everything except
  `OperationCanceledException` and `TimeoutException`.
- **Public option changes (anti-stale)**: adding or changing any public configuration option/default requires
  updating ALL THREE in the same PR — `docs/configuration-cheatsheet.md` (exhaustive, one row per option),
  `docs/configuration-reference.md`, and `plugins/evertask/skills/integrate-evertask/`.

## Ops Quick Facts

- **Central Package Management**: versions go in `Directory.Packages.props`, never in a `.csproj`
- **Version**: `Directory.Build.props`, lockstep across all packages (current 4.0.0, unreleased — bumped early
  so the consumer-compatibility fixture really loads a 3.11-compiled assembly against a 4.0 one)
- **MediatR attribution**: `Dispatcher.cs`, `TaskHandlerExecutor.cs`, `TaskHandlerWrapper.cs`,
  `HandlerRegistrar.cs` are adapted from MediatR (Apache 2.0) — keep the attribution comments

## Module-Specific Guidance

| Module | Local CLAUDE.md |
|--------|-----------------|
| Core (dispatcher, worker, queue/recovery invariants) | `src/EverTask/CLAUDE.md` |
| Abstractions (payload contract, retry, analyzers ET0001–ET0009) | `src/EverTask.Abstractions/CLAUDE.md` |
| Rate limiting (hard invariants) | `src/EverTask/RateLimiting/CLAUDE.md` |
| Recurring (cron, builder, skip-forward) | `src/EverTask/Scheduler/Recurring/CLAUDE.md` |
| EF Core base + the four providers | `src/Storage/EverTask.Storage.{EfCore,SqlServer,Postgres,MySql,Sqlite}/CLAUDE.md` |
| Monitoring API, dashboard UI, SignalR | `src/Monitoring/EverTask.Monitor.Api/CLAUDE.md`, `.../UI/CLAUDE.md`, `.../EverTask.Monitor.AspnetCore.SignalR/CLAUDE.md` |
| Serilog | `src/Logging/EverTask.Logging.Serilog/CLAUDE.md` |
| Tests | `test/EverTask.Tests{,.Storage,.Logging}/CLAUDE.md` |

Create a local CLAUDE.md only for module-specific prerequisites or critical gotchas; keep it 40-100 lines and
do not duplicate what is here.
