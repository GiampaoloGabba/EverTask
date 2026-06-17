---
name: integrate-evertask
version: 1.0.0
description: |
  Interactive playbook for integrating the EverTask .NET background-task library
  into an application: install the right packages, wire up `AddEverTask(...)` with
  the correct storage/resilience/scheduling/rate-limiting/monitoring options, and
  scaffold task records + handlers that satisfy the System.Text.Json payload
  contract (analyzers ET0001–ET0007). Use when the user asks to "add/integrate/set
  up EverTask", "create a background task / recurring task / scheduled job with
  EverTask", "configure EverTask storage/retry/rate-limiting/monitoring", or to
  pick between the SQL Server / PostgreSQL / SQLite / In-Memory storage providers.
  The skill ASKS the user the decisions it cannot infer (via the AskUserQuestion
  tool) and reads the matching references/*.md only for the features actually
  chosen. Do NOT use it to ADD A NEW storage provider package (that is the
  `new-relational-storage-provider` skill) or to author EverTask's own internals.
---

# Integrate EverTask into a project

This skill turns a vague "add background tasks with EverTask" request into a correct,
minimal, working integration. It is **interactive and progressive**: you ask the user
only the decisions you cannot infer, then read only the reference files for the features
they actually want. Never dump every option at once: that is what the references are for.

EverTask is a MediatR-inspired, **persistent** background-task library: a task is a
`record … : IEverTask` (the message), a handler is `EverTaskHandler<TTask>` (the logic),
and `ITaskDispatcher.Dispatch(...)` persists + queues it. Tasks survive restarts, support
delayed/scheduled/recurring execution, retry policies, timeouts, keyed rate limiting,
multi-queue isolation, and monitoring. Multi-targets net8.0/net9.0/net10.0.

## Golden rules (read before doing anything)

1. **One storage call is mandatory.** `AddEverTask(...)` must be followed by exactly one of
   `.AddMemoryStorage()`, `.AddSqlServerStorage(...)`, `.AddPostgresStorage(...)`,
   `.AddSqliteStorage(...)`. Without it the app fails at startup.
2. **At least one assembly must be registered** inside the options lambda
   (`RegisterTasksFromAssembly(...)`), or `AddEverTask` throws `ArgumentException`.
3. **The payload contract is enforced by analyzers** shipped inside `EverTask.Abstractions`
   (no install needed). Every task you scaffold must use **public properties only**, no
   public fields, **IDs not entities**. See `references/08-payload-contract.md`: getting
   this wrong corrupts recovery, the one moment persistence matters.
4. **Defaults are good.** Do not set `MaxDegreeOfParallelism`, channel capacity, retry,
   timeout, or rate-limiter knobs unless the user has a reason. The library auto-sizes
   sensibly (e.g. `MaxDegreeOfParallelism = max(4, CPU×2)`).
5. **Propose, then apply.** Show the user the package list and the `Program.cs` diff before
   writing. Confirm before editing existing files.
6. **C# 12 primary constructors** for handlers with DI (per repo style). Records for tasks.

## Reference map: read on demand, not up front

| Feature the user wants | Read this |
|---|---|
| Registration entry point, all `AddEverTask` options, concurrency/channel sizing, queues | `references/01-setup.md` |
| Defining task records + handlers, dispatch overloads, lifecycle callbacks, `taskKey` idempotency, cancel | `references/02-tasks-and-handlers.md` |
| Choosing & configuring a storage provider, audit levels, retention/cleanup, custom `ITaskStorage` | `references/03-storage.md` |
| Retry policies, exception filtering (whitelist/blacklist/predicate), timeout, cancellation, graceful shutdown | `references/04-resilience.md` |
| Delayed / scheduled / recurring tasks, fluent builder, cron, idempotent startup registration, drift | `references/05-scheduling.md` |
| Keyed (per-tenant) rate limiting, named multi-queues, scalability, sharded scheduler | `references/06-rate-limiting-queues.md` |
| Monitoring events, SignalR, dashboard + REST API (JWT), Serilog, persistent execution logs | `references/07-monitoring-logging.md` |
| System.Text.Json payload contract + every analyzer rule ET0001–ET0007 + payload checklist | `references/08-payload-contract.md` |
| Chaining/orchestrating tasks (continuations, compensation, sagas) via lifecycle callbacks | `references/09-orchestration.md` |
| Which NuGet package ships which feature | `references/packages.md` |
| Copy-paste code (registration, task+handler, recurring registrar) | `templates/*.md` |

## Interactive flow

Run these phases in order. Phases 3–6 only happen if the user wants them.

### Phase 0: Detect context (no questions yet)

Look before you ask. Inspect the project to avoid asking what you can read:

- Find the startup project's `.csproj` and `Program.cs` (or `Startup.cs`). Determine the
  host type: **ASP.NET Core web app**, **Worker Service** (`Microsoft.NET.Sdk.Worker` /
  `AddHostedService`), or **plain console / Generic Host**. This decides whether monitoring
  (which needs ASP.NET Core endpoint mapping) is even applicable.
- Check the target framework(s): EverTask needs **net8.0+**. All EverTask packages (core, storage,
  monitoring, SignalR, Serilog) multi-target net8.0/net9.0/net10.0, so there is no version mismatch
  to worry about within that range.
- Grep for an existing `AddEverTask`: if present, this is a *modify*, not a *fresh setup*;
  read the current config and only add what is missing.
- Note whether a DB is already configured (connection string in `appsettings.json` /
  `Aspire` / env) so you can reuse it for storage.

Summarize what you found in one or two lines before moving on.

### Phase 1: Gather requirements (AskUserQuestion)

Ask only what the prompt did not already specify. If the user's prompt already says e.g.
"recurring job that hits the Stripe API per tenant, store in Postgres", infer storage =
Postgres, scheduling = recurring, rate-limiting = yes, and skip those questions.

Use **one `AskUserQuestion` call** with the still-open questions below (max 4 per call; if
more remain, do a second call). Recommended option goes first, labelled "(Recommended)".

- **Storage backend** (single-select): drives persistence and packages.
  Options: `SQL Server`, `PostgreSQL`, `SQLite (single-server / edge)`,
  `In-Memory (dev/test only, lost on restart)`. Recommend based on Phase-0 findings
  (existing DB wins; otherwise PostgreSQL for greenfield server apps, SQLite for
  desktop/edge, In-Memory only if they said "just trying it out"). See `03-storage.md`
  for the full decision matrix and the SQLite write-concurrency / large-backlog caveats.

- **Which capabilities?** (multi-select): only wire what is chosen.
  Options: `Scheduled / recurring tasks`, `Custom retry / timeout policies`,
  `Per-key rate limiting (per tenant/account/resource)`,
  `Real-time monitoring dashboard`, `Dedicated Serilog pipeline`,
  `Persist handler logs to DB`. (Immediate fire-and-forget dispatch always works with no
  extra wiring, so it is not an option here.)

- **Concurrency / throughput profile** (single-select): only ask if the user signals
  scale concerns or asks; otherwise SKIP and keep defaults.
  Options: `Use defaults (Recommended)`, `I/O-bound, expect bursts (raise parallelism +
  capacity)`, `Need workload isolation (separate critical vs background queues)`.

Phrase questions in the user's own domain terms when their prompt gave them (e.g. "How many
Stripe calls per tenant per minute?" rather than abstract "permits/period").

### Phase 2: Install packages & wire `AddEverTask`

1. Resolve the package set from `references/packages.md` for the chosen storage + capabilities.
2. Add them via the project's package manager. Check whether the repo uses **Central Package
   Management** (a `Directory.Packages.props`): if so, add `<PackageVersion>` there and a
   versionless `<PackageReference>` in the `.csproj`; otherwise a normal `dotnet add package`.
3. Build the registration block from `templates/Program.registration.md` + `01-setup.md`,
   including ONLY the options the user opted into. Show it as a diff against `Program.cs`.
4. On confirmation, apply the edit. Place storage connection strings in configuration, never
   inline literals (read existing config style first).

### Phase 3: Scaffold a task + handler

If the user described a concrete task ("send welcome email", "nightly cleanup"), scaffold it;
otherwise scaffold one representative example so they have a working pattern.

- Use `templates/Task.and.Handler.md`. Task = `record … : IEverTask` with public properties,
  primitives/Guid/DateTimeOffset, **IDs not entities**.
- **Validate the payload against `08-payload-contract.md` before writing**: no public fields
  (ET0001), reachable setters or matching ctor params (ET0002), no Newtonsoft attributes
  (ET0003), polymorphic props annotated (ET0004), avoid `object`/`Dictionary<string,object>`
  (ET0005), no `Stream`/`DbContext`/`ValueTuple`/delegates (ET0006), class payloads ctor-
  resolvable (ET0007).
- Handler uses a **primary constructor** for DI. Each task runs in its own DI scope, so
  injecting scoped services (e.g. `DbContext`) is safe.

### Phase 4: Wire chosen capabilities

For each capability selected in Phase 1, read the matching reference and apply:

- **Scheduling/recurring** (`05-scheduling.md`): pick `Dispatch(task, TimeSpan)` /
  `Dispatch(task, DateTimeOffset)` for one-shots, or the fluent recurring builder. For
  recurring, register at startup via an `IHostedService` with a stable `taskKey` for
  idempotency (`templates/RecurringRegistrar.md`). Warn: `UseCron(...)` overrides all other
  interval calls; all schedules are UTC.
- **Retry/timeout** (`04-resilience.md`): override `RetryPolicy` / `Timeout` on the handler,
  or set queue/global defaults. Default is `LinearRetryPolicy(3, 500ms)` retrying everything
  except `OperationCanceledException`/`TimeoutException`.
- **Rate limiting** (`06-rate-limiting-queues.md`): override `RateLimitPolicy` on the handler;
  carry the key via `IRateLimitedTask` or `GetRateLimitKey`. Note rate limiting is
  **per-instance**: divide external budgets across instances in multi-instance deployments.
- **Monitoring** (`07-monitoring-logging.md`): `AddMonitoringApi(...)` for the full dashboard
  (web apps only) + `app.MapEverTaskApi()`, or `AddSignalRMonitoring()` + `MapEverTaskMonitorHub()`
  for events only, or subscribe to `IEverTaskWorkerExecutor.TaskEventOccurredAsync` in code.
  **Change the default `admin`/`admin` credentials.**
- **Serilog** (`07-monitoring-logging.md`): `.AddSerilog(...)` for a dedicated pipeline.
- **Persistent logs** (`07-monitoring-logging.md`): `.WithPersistentLogger(...)`; pair with
  `AddAuditCleanup(...)` retention to prevent unbounded table growth.
- **Orchestration / chaining** (`09-orchestration.md`): if the task is one step in a larger flow
  (continuation, compensation/rollback, fan-out, saga), there is no dedicated workflow API; chain
  from `OnCompleted`/`OnError` by dispatching the next task, passing handoff data as IDs in its payload.

### Phase 5: Verify

Run `dotnet build` (Release; the repo treats warnings as errors and the payload analyzers run
at compile time). Fix any ET0001–ET0007 diagnostics on scaffolded payloads before declaring
done. If a DB provider was chosen, mention that the first run auto-applies migrations
(`AutoApplyMigrations = true` by default) unless they opted to manage migrations manually.

## Common end-to-end shapes (sanity anchors)

- **Minimal dev**: `AddEverTask(o => o.RegisterTasksFromAssembly(typeof(Program).Assembly)).AddMemoryStorage();`
- **Production web app**: Postgres/SQL storage + recurring registrar + monitoring dashboard
  (`MapEverTaskApi`) + retention cleanup.
- **Worker service**: SQL/Postgres storage + recurring registrar; no monitoring dashboard
  (no HTTP pipeline); use event subscription or SignalR-to-an-external-hub instead.
- **Multi-tenant API caller**: storage + `IRateLimitedTask` per tenant + retry policy.

When unsure which detail applies, open the specific reference file rather than guessing; the
references are exact (cross-checked against source, with file:line) and call out the known
docs-vs-code gotchas (`OnLast` is not implemented; `GetAllRecurringTasksAsync` is illustrative).
