---
layout: default
title: Configuration Cheatsheet
parent: Configuration
nav_order: 2
---

# Configuration Cheatsheet

Every EverTask configuration option at a glance: one row per option with its default. Details and examples live in the [Configuration Reference](configuration-reference.md); each section below links to the matching one.

## Service Configuration

→ [Reference: Service Configuration](configuration-reference.md#service-configuration)

| Method | Parameters | Default | Notes |
|--------|-----------|---------|-------|
| `SetChannelOptions` | `int` or `BoundedChannelOptions` | `ProcessorCount × 200` (min 1000), `FullMode=Wait` | Max queued tasks + full behavior |
| `SetMaxDegreeOfParallelism` | `int` | `ProcessorCount × 2` (min 4) | Concurrent workers |
| `SetDefaultRetryPolicy` | `IRetryPolicy` | `LinearRetryPolicy(3, 500ms)` | Global retry policy |
| `SetDefaultTimeout` | `TimeSpan?` | `null` (no timeout) | Global per-attempt timeout |
| `SetDefaultAuditLevel` | `AuditLevel` | `Full` | Audit trail verbosity (see table below) |
| `SetMisfireThreshold` | `TimeSpan` | `5 s` | How late a delivery may start before `ITaskExecutionContext.Misfire` reports it, and how old a due slot must be for a durable schedule to stamp the occurrence it materializes as missed work. Observation only: nothing about execution changes, and the 1 s tolerance of the recurring skip path is untouched. A run of more than one missed slot is reported whatever the threshold |
| `SetDefaultScheduleTimeZone` | `TimeZoneInfo` | `null` (UTC) | Zone for **calendar-anchored** schedules and day/date exclusions built without `InTimeZone` (days/weeks/months, `AtTime`, cron, `Except(e => e.OnDays/OnDates)`). Plain cadences without calendar exclusions are never touched. Stamped into the definition at dispatch, so existing rows keep their meaning. Custom zones throw |
| `SetMaterializationConcurrency` | `int` | same as `SetMaxDegreeOfParallelism` | How many durable schedules may materialize occurrences at once. Bounds the storage burst of a large restart backlog; unrelated to how many occurrences RUN concurrently (queue parallelism) or to `MaxPendingOccurrences` (per schedule). Below 1 throws |
| `SetBacklogRetryInterval` | `TimeSpan` | `1 min` | How long a durable schedule waits before trying again when it could not make progress (concurrency budget full, or a compare-and-swapped write that lost its race). The normal way it resumes is the kick each occurrence gives when it ends; this is the guarantee behind it. A **halted** catch-up is not retried at all: only an explicit schedule change releases one (`ResumeSchedule`, `Reschedule`, or dispatching the series again after a cancel). Below 1 s or above 1 day throws |
| `SetOccurrenceProviderRetry` | `Action<OccurrenceProviderRetryOptions>` | `InitialBackoff` 1 min, `MaxBackoff` 15 min | How long a live schedule waits after its occurrence evaluation could not answer: an `INextOccurrenceProvider` failure or exclusion-search budget exhaustion. The wait doubles per consecutive failure of that schedule and one answer resets it. Either bound outside `(0, 1 day]` throws |
| `SetThrowIfUnableToPersist` | `bool` | `true` | Throw on storage save failure |
| `UseShardedScheduler` | `int shardCount = 0` | Off (`PeriodicTimerScheduler`); auto-scale when 0 | High `Schedule()`-call rates (scheduling axis, not task-execution throughput) |
| `SetUseLazyHandlerResolution` | `bool` | `true` (adaptive) | `DisableLazyHandlerResolution()` to opt out |
| `WithPersistentLogger` | `Action<PersistentLoggerOptions>` | Disabled | Persists handler logs to DB; logs ALWAYS go to ILogger regardless |
| `SetRateLimiterOptions` | `Action<RateLimiterOptions>` | See below | Keyed rate limiter global knobs (v3.7+) |
| `RegisterTasksFromAssembly` | `Assembly` | n/a | Scan one assembly for handlers (required) |
| `RegisterTasksFromAssemblies` | `params Assembly[]` | n/a | Scan multiple assemblies |

> **The scheduling clock** is not a builder method either. `AddEverTask` registers `TimeProvider.System` with `TryAddSingleton`, and that one `TimeProvider` answers "now" for dispatch delays, the occurrence grid, both schedulers, startup recovery and the rate limiter with its gate and parking lot. Register your own (`services.AddSingleton<TimeProvider>(clock)`) and the whole pipeline follows it, which is what makes a schedule testable without waiting for real time. Retry delays, audit timestamps and log timestamps stay on the real clock. See [Configuration Reference](configuration-reference.md#the-scheduling-clock-timeprovider).

> **Audit / execution-log retention** is not a builder method: register it with `services.AddAuditCleanup(policy, cleanupIntervalHours: 24)` (IServiceCollection extension, EF Core storage package). Trims audits, execution logs (`ExecutionLogRetentionDays` / `MaxExecutionLogsPerTask`) and the finished occurrences of durable schedules (`OccurrenceRetentionDays`); any knob `<= 0` is treated as disabled. See [Configuration Reference](configuration-reference.md).

## Rate Limiting (v3.7+)

→ [Reference: Rate Limiting Configuration](configuration-reference.md#rate-limiting-configuration) · [Feature docs](rate-limiting.md)

**Global knobs** (`SetRateLimiterOptions`):

| Option | Default | Notes |
|--------|---------|-------|
| `MaxParkedTasks` | `min(5000, 2 × default-queue channel capacity)` | Distinct parked tasks before gated consumers pause (backpressure); ungated traffic keeps flowing |
| `MaxTrackedKeys` | `100,000` | (task type, key) buckets before new keys fail OPEN |
| `MaxKeyLength` | `256` | Longer keys hashed (SHA-256) |
| `EmitDeferralEvents` | `true` | Deferral monitoring events, aggregated at source |

**Per-handler policy** (`RateLimitPolicy` property, `new RateLimitPolicy(permits, period)`):

| Property | Default | Notes |
|----------|---------|-------|
| `Permits` | n/a (ctor, required) | Public get-only; executions allowed per `Period`. Must be > 0 |
| `Period` | n/a (ctor, required) | Public get-only; the window for `Permits`. Must be > 0 |
| `Burst` | `Permits` | ≥ 1; `1` = strict even spacing |
| `ThrottleRetries` | `true` | Retries re-acquire budget through the gate (never erodes the per-attempt timeout). Not an inline wait: a far slot **re-parks** the task and it fires via redelivery, **restarting** retry attempt numbering |
| `StartEmpty` | `false` | `false` = new bucket starts **full** (burst available immediately); `true` = starts at steady rate (caps post-restart burst) |
| `MaxReservationHorizon` | `1 hour` | Farther slots → terminal rejection |
| `MaxInSlotWait` | `1 second` | **No-op (binary-compat only)**: over-budget tasks always re-park to the scheduler; no inline consumer wait |
| `OverflowBehavior` | `WaitForCapacity` | `Discard` rejects over-budget tasks: one-shot → `Failed` + `OnError`; recurring → occurrence skipped (series continues, no callback) |

**Key source**: task implements `IRateLimitedTask`, or handler overrides `GetRateLimitKey(task)`. Null/empty key → task runs **ungated** (warning logged); if the key selector **throws**, it is caught, logged, and the task also runs ungated (fail-open).

## Audit Levels

→ [Reference: SetDefaultAuditLevel](configuration-reference.md#setdefaultauditlevel)

| Level | `StatusAudit` | `RunsAudit` (recurring) | Use Case |
|-------|---------------|-------------------------|----------|
| `Full` (default) | All status transitions | Every run | Critical tasks |
| `Minimal` | Errors only | **Every run** (tracks last run) + `LastExecutionUtc` | High-frequency recurring |
| `ErrorsOnly` | Errors only | Errors only (run with an **exception recorded** *or* status `Failed`) | Fire-and-forget |
| `None` | Never | Never | Extremely high-frequency |

`Minimal` vs `ErrorsOnly` differ **only** in `RunsAudit`: `Minimal` records *every* recurring run (run-frequency history), `ErrorsOnly` only the runs that carry a non-empty exception string or end in status `Failed`. Decisions live in `AuditPolicy.cs`. Per-task override: `dispatcher.Dispatch(task, auditLevel: AuditLevel.Minimal)`.

## Audit Retention / Cleanup

→ [Reference: Storage Configuration](configuration-reference.md#storage-configuration)

Not a builder method; register on `IServiceCollection` (EF Core storage providers only): `services.AddAuditCleanup(policy, cleanupIntervalHours: 24)`. Build the policy with a factory (`AuditRetentionPolicy.WithUniformRetention(days)` or `WithErrorPriority(successDays, errorDays)`) or set properties directly. Any knob `<= 0` is treated as disabled.

| `AuditRetentionPolicy` | Default | Notes |
|------------------------|---------|-------|
| `StatusAuditRetentionDays` | `null` (unlimited) | Days to keep StatusAudit rows |
| `RunsAuditRetentionDays` | `null` | Days to keep RunsAudit rows |
| `ErrorAuditRetentionDays` | `null` | Overrides the two above for error rows (keep errors longer) |
| `ExecutionLogRetentionDays` | `null` | Days to keep TaskExecutionLog rows |
| `MaxExecutionLogsPerTask` | `null` | Cross-run cap per task; oldest deleted first |
| `OccurrenceRetentionDays` | `null` | Days to keep the finished occurrences of a durable recurring schedule. Prunes **every** terminal state (Completed, Failed and Cancelled), unlike `DeleteCompletedTasksAfterRetention`, which only removes completed rows with no audit trail left. If a log-retention window/cap is active, an occurrence that still owns execution logs is **preserved** (same guard as the completed-task purge). Each audit trail has a guard of its own: with `StatusAuditRetentionDays` set, an occurrence whose status rows are still inside that window is preserved too, and `RunsAuditRetentionDays` guards the runs trail the same way. Deleting the row cascades both. The schedule row itself is recurring and is never deleted here. |
| `DeleteCompletedTasksAfterRetention` | `false` | Hard-delete completed non-recurring tasks older than the **longest** configured audit window **and** with no remaining StatusAudit/RunsAudit rows. If a log-retention window/cap is active, a task that still owns execution logs is **preserved** (the purge only cascades once logs age out on their own). No audit window configured → nothing deleted. Recurring/Failed/Cancelled never auto-deleted. |

| `AddAuditCleanup` / `AuditCleanupOptions` | Default | Notes |
|-------------------------------------------|---------|-------|
| `RetentionPolicy` | `null` | The policy supplied via the `AddAuditCleanup(policy, …)` arg; `null` = no cleanup runs |
| `cleanupIntervalHours` arg → `CleanupInterval` | `24h` | Sweep interval |
| `InitialDelay` | `1 min` | Delay before the first sweep |

## Queue Configuration

→ [Reference: Queue Configuration](configuration-reference.md#queue-configuration)

Builder methods: `ConfigureDefaultQueue(...)`, `AddQueue(name, configure?)`, `ConfigureRecurringQueue(...)`, `EnsureRecurringQueue()` (rarely needed: `AddEverTask` auto-creates the recurring queue). The builder also exposes `.Services` (the underlying `IServiceCollection`) so you can register your own singletons mid-chain (e.g. a custom `IKeyedRateLimiter` or `IGuidGenerator`). Note: if a custom/distributed `IKeyedRateLimiter` **throws** (e.g. Redis down), the gate **fails open**: the task runs unthrottled with a warning, never failing over a limiter outage. The well-known names are the constants `QueueNames.Default` (`"default"`) and `QueueNames.Recurring` (`"recurring"`).

Defaults differ between the auto-created `default`/`recurring` queues (inherit the global settings, `QueueFullBehavior.Wait`) and queues created via `AddQueue` (see Default column):

| Method | Parameters | Default for `AddQueue` queues | Notes |
|--------|-----------|-------------------------------|-------|
| `SetMaxDegreeOfParallelism` | `int` | `1` (sequential!) | `default`/`recurring` inherit the global value |
| `SetChannelCapacity` | `int` | `500` | `default`/`recurring` inherit the global capacity |
| `SetChannelOptions` | `BoundedChannelOptions` | n/a | Replaces the queue's whole channel options (FullMode, SingleReader/Writer, ...) |
| `SetFullBehavior` | `QueueFullBehavior` | `FallbackToDefault` | `Wait` / `FallbackToDefault` / `ThrowException`, immediate dispatches only; the auto-created `default` queue uses `Wait`. `FallbackToDefault` = non-blocking try on target, then re-route to the `default` queue with **blocking `Wait` backpressure** (the task then runs on the default queue, so it does **not** honor the target queue's parallelism/isolation; if the target *is* the default queue it degenerates to plain `Wait`) |
| `SetDefaultTimeout` | `TimeSpan?` | unset → global | Chain: handler → queue → global (v3.7+) |
| `SetDefaultRetryPolicy` | `IRetryPolicy?` | unset → global | Chain: handler → queue → global (v3.7+) |

> The "Default for `AddQueue` queues" column above is what `AddQueue` applies. A **raw** `new QueueConfiguration()` object (if you build one directly) defaults differently: `Name = "default"`, `MaxDegreeOfParallelism = 1`, channel capacity **`2000`** with `FullMode = Wait`, `QueueFullBehavior = FallbackToDefault`. (Note the capacity differs from `AddQueue`'s `500`.)

## Storage

→ [Reference: Storage Configuration](configuration-reference.md#storage-configuration)

| Method | Package | Notes |
|--------|---------|-------|
| `AddMemoryStorage()` | core | Dev/test only: tasks lost on restart |
| `AddSqlServerStorage(cs, opt?)` | `EverTask.Storage.SqlServer` | Options: `SqlServerTaskStoreOptions`; DbContext pooling + stored procedures |
| `AddPostgresStorage(cs, opt?)` | `EverTask.Storage.Postgres` | Options: `PostgresTaskStoreOptions`; DbContext pooling + writable-CTE optimizations |
| `AddMySqlStorage(cs, opt?)` | `EverTask.Storage.MySql` | Options: `MySqlTaskStoreOptions`; DbContext pooling + stored-proc hot writes; server-side base (no schema; net9/net10 only) |
| `AddSqliteStorage(cs?, opt?)` | `EverTask.Storage.Sqlite` | Options: `SqliteTaskStoreOptions`; `cs` defaults to `"Data Source=EverTask.db"` |

| Option (all EF Core store option types) | Default | Notes |
|----------------------------------|---------|-------|
| `SchemaName` | SQL Server: `"EverTask"`; PostgreSQL: `"evertask"`; SQLite & MySQL/MariaDB: `""` | PostgreSQL: lowercase only (`null` = `public`); SQL Server: `null` = dbo; SQLite & MySQL/MariaDB: must stay `""` (no schema concept — MySQL rejects a non-empty value) |
| `AutoApplyMigrations` | `true` | Disable for manual migrations in production |
| `ServerVersion` (MySQL/MariaDB only) | `null` → `ServerVersion.AutoDetect` | Set an explicit `MariaDbServerVersion`/`MySqlServerVersion` to skip the startup auto-detect connection |

## Logging

→ [Reference: Logging Configuration](configuration-reference.md#logging-configuration)

`AddSerilog(opt => ...)` (package `EverTask.Logging.Serilog`). Called with no argument, `AddSerilog()` defaults to a `WriteTo.Console()` sink.

`WithPersistentLogger` (`PersistentLoggerOptions`, v3.0+):

| Method | Default | Notes |
|--------|---------|-------|
| `SetMinimumLevel(LogLevel)` | `Information` | Min level persisted to DB (ILogger gets everything) |
| `SetMaxLogsPerTask(int?)` | `1000` | `null` = unlimited (not recommended) |
| `Enable()` / `Disable()` | enabled by `WithPersistentLogger` | Toggle DB persistence (logs still flow to ILogger when disabled) |

## Monitoring

→ [Reference: Monitoring Configuration](configuration-reference.md#monitoring-configuration)

`AddMonitoringApi(opt => ...)` + `app.MapEverTaskApi()` (package `EverTask.Monitor.Api`). `MapEverTaskApi` accepts an optional `Action<HttpConnectionDispatcherOptions>` to tune the SignalR hub connection. Fixed paths: dashboard `/evertask-monitoring`, API `/evertask-monitoring/api`, SignalR hub `/evertask-monitoring/hub`. For setups without the EverTask builder there is `services.AddEverTaskMonitoringApiStandalone(opt => ...)` (you must register `ITaskStorage` yourself). It does **not** wire SignalR monitoring, and there is **no `IServiceCollection` overload of `AddSignalRMonitoring`** (it exists only on `EverTaskServiceBuilder`). `MapEverTaskApi` still maps the hub endpoint, but without the monitor subscription no live events are pushed; for live dashboard updates use the builder path (`AddEverTask(...).AddMonitoringApi(...)`).

| EverTaskApiOptions | Default | Notes |
|--------------------|---------|-------|
| `EnableUI` | `true` | Embedded React dashboard |
| `EnableOpenApiDocument` | `false` | OpenAPI doc at `/evertask-monitoring/openapi/evertask-monitoring.json` (net9+; auto-enabled by `EverTask.Monitor.Api.Scalar`) |
| `EnableSwagger` | `false` | Obsolete no-op since 4.0.0 (use `EnableOpenApiDocument`) |
| `Username` / `Password` | `"admin"` / `"admin"` | CHANGE IN PRODUCTION |
| `EnableAuthentication` | `true` | JWT on API + hub |
| `EnableManagementEndpoints` | `false` | Opens the write surface (`POST /api/management/tasks/{id}/{requeue\|resume\|cancel}`, since 4.0.0). Off = every path under `/api/management` answers 404 |
| `ManagementUsername` / `ManagementPassword` | `null` / `null` | Second, **operate-level** credential (since 4.0.0): logging in with it returns a token that the management endpoints accept. `Username`/`Password` stay read-only. Registration THROWS if only one half is set, or if `ManagementPassword` equals `Password` or `MagicLinkToken` |
| `ManagementAuthorization` | `null` | `Func<HttpContext, Task<bool>>` the host fills in (since 4.0.0). When set it REPLACES the role check for `/api/management`. Runs inside routing, after the host's `UseAuthentication`, so `context.User` is the app's own principal |
| `JwtSecret` | `null` (random 256-bit per instance) | Set explicitly (≥ 32 bytes) for multi-instance deployments |
| `JwtIssuer` / `JwtAudience` | `"EverTask.Monitor.Api"` | n/a |
| `JwtExpirationHours` | `8` | Token TTL |
| `EnableCors` | `true` | Applies the `EverTaskMonitoringApi` CORS policy to requests under `/evertask-monitoring` only (since 4.0.0); the host's CORS setup is untouched |
| `CorsAllowedOrigins` | `[]` (allow all) | Origins for the monitoring CORS policy (non-empty adds `AllowCredentials`). Restrict in production |
| `AllowedIpAddresses` | `[]` (allow all) | IPv4/IPv6/CIDR; checked before auth, dashboard files included. Enforced inside routing since 4.0.0, so it holds under `app.UsePathBase(...)` too. **The address is the connection's: `X-Forwarded-For` is NOT trusted (breaking in 4.0.0)** — behind a proxy configure `UseForwardedHeaders` with `KnownProxies` |
| `MagicLinkToken` | `null` (disabled) | Instant auth via `/magic#token=...` (fragment + `POST /api/auth/magic`, since 4.0.0); `?token=` still works but lands in request logs |
| `EventDebounceMs` | `1000` | Dashboard SignalR refresh debounce |
| `BasePath` | `/evertask-monitoring` | **read-only** computed; fixed |
| `ApiBasePath` | `/evertask-monitoring/api` | **read-only** computed; fixed |
| `UIBasePath` | `/evertask-monitoring` | **read-only** computed; fixed |
| `SignalRHubPath` | `/evertask-monitoring/hub` | **read-only** computed; fixed |

`AddSignalRMonitoring(...)` (package `EverTask.Monitor.AspnetCore.SignalR`): 4 overloads: `AddSignalRMonitoring()`, `(Action<SignalRMonitoringOptions>)`, `(Action<HubOptions>)`, `(Action<HubOptions>, Action<SignalRMonitoringOptions>)`. When used **standalone** (without `AddMonitoringApi`), you MUST also call `app.MapEverTaskMonitorHub()` after `Build()`: it maps the hub **and** subscribes the monitor, so without it no events are broadcast. Overloads: `MapEverTaskMonitorHub()` (default path), `MapEverTaskMonitorHub("/custom")`, `MapEverTaskMonitorHub("/custom", hub => { ... })`. Options:

| Option | Default | Notes |
|--------|---------|-------|
| `IncludeExecutionLogs` | `false` | Streams handler logs in events (bandwidth cost) |

## Handler Properties

→ [Reference: Handler Configuration](configuration-reference.md#handler-configuration)

| Property / Member | Type | Default | Notes |
|----------|------|---------|-------|
| `Timeout` | `TimeSpan?` | Inherits queue/global | Per-attempt timeout |
| `RetryPolicy` | `IRetryPolicy?` | Inherits queue/global | Per-handler retry |
| `QueueName` | `string?` | `"default"` (`"recurring"` for recurring tasks) | Target queue. An **unregistered/unknown** name logs a warning and falls back to the `default` queue, both for routing **and** for the retry/timeout config resolution (the task runs on `default` with `default`'s config). |
| `RateLimitPolicy` | `RateLimitPolicy?` | `null` (no limit) | Per-key throttling (v3.7+) |
| `GetRateLimitKey(task)` | `string?` (override) | reads `IRateLimitedTask.RateLimitKey` | Derive the throttle key without changing the task type |
| `Logger` | `ITaskLogCapture` (read) | injected per delivery | Task-scoped logging; persisted when `WithPersistentLogger` is on |
| `Context` | `ITaskExecutionContext` (read) | injected per delivery | Identity of THIS delivery: `TaskId`, `ScheduleId`, `TaskKey`, `ScheduledAtUtc` (nominal slot, never the rate-limit reserved one), `ScheduledAtLocal`, `TimeZoneId`, `StartedAtUtc`, `Attempt`, `RunNumber` (durable), `ScheduleVersion`, `IsRecurring`, `IsOccurrence`, `Misfire`. Readable in `Handle` and every callback; throws if read from the constructor. Outside the handler: inject `ITaskExecutionContextAccessor` (singleton, ambient, `Current` is null outside a delivery) |

> Obsolete: `CpuBoundOperation` (bool) still exists on the handler but is `[Obsolete]` and has **no effect**: do not set it. For CPU-bound work, use `Task.Run` inside `Handle`.

## Dispatch Parameters

→ [Reference: Task Dispatching](task-dispatching.md)

Optional parameters on every `ITaskDispatcher.Dispatch(...)` overload:

| Parameter | Type | Default | Notes |
|-----------|------|---------|-------|
| `auditLevel` | `AuditLevel?` | `null` → global default (`Full`) | Per-dispatch audit override |
| `taskKey` | `string?` | `null` (no dedup) | Idempotency key (≤ 200 chars). **Non-recurring**: `InProgress` (or an immediate one-shot whose delivery is already in flight) → no-op; `Pending`/`Queued`/`WaitingQueue` → update; terminal → replace. **Recurring**: `InProgress` → no-op; otherwise → **update in place** (never replaced), preserving `NextRunUtc`+`CurrentRunCount` **only if `NextRunUtc.HasValue`** (an exhausted series with no next run recalculates instead); recurring→one-shot re-dispatch is **discarded** |
| `cancellationToken` | `CancellationToken` | `default` | Cancels the dispatch op (not execution) |

## Recurring Schedule Builder

→ [Reference: Recurring Tasks](recurring-tasks.md) · used via `Dispatch(task, Action<IRecurringTaskBuilder>, …)`. All times **UTC** unless the schedule names a zone.

| Stage | Methods |
|-------|---------|
| Start | `Schedule()` (recurring only) · `RunNow()` / `RunDelayed(TimeSpan)` / `RunAt(DateTimeOffset)` → `.Then()` |
| Interval | `Every(n).Seconds()/.Minutes()/.Hours()/.Days()/.Weeks()/.Months()` · `EverySecond/Minute/Hour/Day/Week/Month()` · `OnDays(params DayOfWeek[])` · `OnMonths(params int[])` |
| Refine | hour `.AtMinute(0–59)` · minute `.AtSecond(0–59)` · day `.AtTime(TimeOnly)` / `.AtTimes(…)` · week `.OnDay(s)` · month `.OnDay(1–31)` / `.OnDays(…)` / `.OnFirst(DayOfWeek)` |
| Cron | `UseCron("expr")` (5- or 6-field; **overrides** all other interval calls) |
| Provider | `UseOccurrenceProvider(key, config?)` — the grid comes from an `INextOccurrenceProvider` registered as `AddOccurrenceProvider<T>(key)` on the EverTask builder. **Exclusive** with every interval and with cron |
| Zone | `.InTimeZone(TimeZoneInfo)` / `.InTimeZone(string)` (IANA or Windows id; stored as IANA) |
| Exclude | `.Except(Action<IExclusionBuilder>)` — adds the callback's selectors to the schedule; calls are additive |
| Exclude days | `IExclusionBuilder.OnDays(params DayOfWeek[])` — whole weekdays on the exclusion clock |
| Exclude dates | `IExclusionBuilder.OnDates(params DateOnly[])` — whole dates on the exclusion clock |
| Exclude range | `IExclusionBuilder.Between(DateTimeOffset from, DateTimeOffset to)` — absolute half-open `[from, to)` window |
| Exclude weekends | `.ExceptWeekends()` — sugar for excluding Saturday and Sunday |
| Occurrences | `.WithDurableOccurrences()` · `.OnMisfire(m => m.Skip() / m.FireOnce(FireOnceOptions?) / m.CatchUp(CatchUpOptions))` · `.BackfillFrom(DateTimeOffset)` |
| Limit | `.RunUntil(DateTimeOffset)` · `.MaxRuns(int)` (counts real runs; skipped-after-downtime don't count. On a DURABLE schedule it counts materializations) |

> **Durable occurrences** turn every due slot into its own one-shot row (own status, retries, audit, rate-limit budget) and stop the schedule row from running the handler. `FireOnce` and `CatchUp` imply them; `Skip` (the default) does not replay anything and `WithDurableOccurrences()` gives the rows without the replay. `CatchUpOptions(maxAge, maxOccurrences)` requires both caps: `MaxAge` is how far back a replay may reach (the one ordinary way a slot is lost, always reported), `MaxOccurrences` is how many slots one episode may replay. `OverflowPolicy` = `Halt` (default: nothing is materialized and a durable marker is written; the passage of time never releases it — only an explicit schedule change does: `ITaskScheduleManager.ResumeSchedule`, `Reschedule`, or dispatching the series again after a cancel) or `SkipOldest` (keep the most recent `MaxOccurrences`). `MaxPendingOccurrences` (default `1`) is how many occurrences of the schedule may be alive at once. `FireOnceOptions.MaxAge` (default `null`) drops the collapsed run when even its newest slot is too old. `BackfillFrom` starts the cursor in the past on a new registration. Contracts: at-least-once, and **one active host**. See [Durable Occurrences](recurring-tasks/durable-occurrences.md).

> **Occurrence providers** answer "which occurrence comes after this instant" for the calendars the fluent API cannot express (business days, a holiday table). Register with `AddOccurrenceProvider<T>("key")` on the EverTask builder (scoped unless you registered `T` yourself; the key, and never a type name, is what the row persists) and select with `.UseOccurrenceProvider("key", config?)` — `config` is an opaque string EverTask never reads. The answer must be strictly after `NextOccurrenceRequest.AfterUtc` and in UTC; `null` ends the series; the provider is asked about arbitrary instants, not only the ones it returned. Everything else works over it (misfire policies, durable occurrences, `InTimeZone` — whose id it is handed —, `MaxRuns`/`RunUntil`, skip-forward, `ReevaluateSchedule`) except `CatchUpOverflowPolicy.SkipOldest`, which needs `IsDeterministic => true`, and `RescheduleMode.RebaseFromCursor`, which has no period to carry. An unknown key is a configuration error (`ArgumentException` at dispatch, poison at recovery); a provider that throws is transient (nothing written, backoff re-park, warning event, no poison counter) and reaches the caller as `OccurrenceProviderException` wherever a caller is holding the call: a dispatch, and `ITaskScheduleManager.Reschedule`/`ReevaluateSchedule`, which ask the provider before they can decide the new cursor. See [Occurrence Providers](recurring-tasks/occurrence-providers.md).

> `OnLast(DayOfWeek)` does **not** exist (only `OnFirst`), and neither does an hourly counterpart of `OnDays`/`OnMonths`: `OnHours()` is on the concrete `IntervalSchedulerBuilder`, not on `IIntervalSchedulerBuilder`, so `Schedule().OnHours()` does not compile, and it selects no hours anyway — it is `EveryHour()`'s cadence. Use a stable `taskKey` for idempotent startup registration.

> **`InTimeZone` governs calendar-anchored schedules and day/date exclusions**. On a plain cadence with no calendar exclusion it throws `InvalidOperationException`: an elapsed step is the same set of instants in every zone. A zone on `Every(4).Hours().ExceptWeekends()` governs only the exclusion calendar; an absolute `Between` window needs no zone. See [Time Zones](recurring-tasks/time-zones.md).

## Schedule Management (runtime)

→ [Reference: Managing Recurring Tasks](recurring-tasks/managing-tasks.md) · `ITaskScheduleManager`, registered by `AddEverTask` next to `ITaskDispatcher`. Every method needs a registered storage; what each needs beyond that is in the last column (every built-in storage has both capabilities). Missing one ⇒ `NotSupportedException`.

| Method | Returns | Notes | Storage |
|--------|---------|-------|---------|
| `Reschedule(taskKey, configure, mode = RecalculateFromNow, ct)` | `ScheduleUpdateResult` | New definition, built exactly as at dispatch. Never refused because a run is in flight | `SupportsScheduleVersioning`, plus `SupportsDurableOccurrences` when the new definition calls `WithDurableOccurrences()`/`OnMisfire(FireOnce/CatchUp)` |
| `ReevaluateSchedule(taskKey, ct)` | `ScheduleUpdateResult` | Same definition, cursor recomputed from now — so a durable backlog is **discarded** (use `ResumeSchedule` to keep it) | `SupportsScheduleVersioning` |
| `ResumeSchedule(taskKey, ct)` | `ScheduleUpdateResult` | Releases a durable catch-up `Halt` and **keeps the cursor**, so the backlog is planned again (and halts again if it still overflows) | `SupportsScheduleVersioning` |
| `RequeueFailedOccurrence(occurrenceId, ct)` | `bool` | `Failed`/`Cancelled` occurrence back to `Queued`, same id, history and audit trail. `false` if no longer terminal; a schedule row, or an occurrence of a **cancelled** schedule, throws | `SupportsDurableOccurrences` |
| `CancelSchedule(taskKey, ct)` | — | The full cancel pipeline by key: blacklist, unschedule, and every pending occurrence cancelled with the schedule. Terminal — dispatch again to restart | none |

`RescheduleMode`: `RecalculateFromNow` (default) puts the cursor at the new definition's first occurrence after now, discarding a durable backlog and reporting it (`DiscardedBacklog`, `DiscardedBacklogIsExact`, plus a monitoring event); `RebaseFromCursor` keeps the schedule inside the day/week/month the old cursor was in — the period is read on the OLD definition's clock and the new cursor is the NEW definition's slot at the same POSITION inside it, so only the time of day, the zone, the bounds and the misfire settings may change. Position is what keeps a period holding several slots (`OnDays(Mon, Wed).AtTimes(09:00, 15:00)`) from rewinding onto one that has already run. A cadence with no day selector (`EveryWeek()`, `EveryMonth()`) carries its day on the cursor, and that day is what is kept. A different cadence or selector, a cron schedule, a period with no valid slot (or fewer slots than the cursor had already passed), or bounds the series has already reached throws `InvalidOperationException` and writes nothing.

`ScheduleUpdateResult`: `TaskId`, `ScheduleVersion` / `PreviousScheduleVersion`, `NextRunUtc` / `PreviousNextRunUtc`, `Mode`, `DiscardedBacklog` (+ `IsExact`), `ReleasedHalt`.

> **Linearization**: immediate for occurrences that have not fired; a delivery already in a worker queue may finish under the old definition, and its advance then loses the compare-and-swap and applies the new one. Within the process that rescheduled, such a delivery is dropped instead; after a restart nothing is published, so a recovered delivery always runs.

## Retry Policy & Exception Filtering

→ [Reference: Resilience](resilience.md)

Built-in policies (both share the same fluent filtering via `RetryPolicyBase<TPolicy>`):

| Policy | Constructors | Delays |
|--------|--------------|--------|
| `LinearRetryPolicy` | `(int retryCount, TimeSpan retryDelay)` · `(TimeSpan[] retryDelays)` | Fixed (or explicit per-attempt array) |
| `ExponentialRetryPolicy` | `(int retryCount, TimeSpan initialDelay, double backoffFactor = 2.0, TimeSpan? maxDelay = null, bool useJitter = false)` | `initialDelay × backoffFactor^(n-1)`, capped at `maxDelay` (and always at the `Task.Delay` ceiling, ~49.7 days); `useJitter` adds ±20% per-attempt jitter (still capped). `backoffFactor` >= 1.0; `maxDelay` >= `initialDelay` |

Default global policy: `LinearRetryPolicy(3, 500ms)`, retrying everything except `OperationCanceledException`/`TimeoutException`. Fluent filtering (whitelist and blacklist cannot be mixed):

| Method | Mode | Notes |
|--------|------|-------|
| `.Handle<T>()` / `.Handle(params Type[])` | Whitelist | Retry only these types |
| `.DoNotHandle<T>()` / `.DoNotHandle(params Type[])` | Blacklist | Retry all except these |
| `.HandleWhen(Func<Exception,bool>)` | Predicate | Highest priority |
| `.HandleTransientDatabaseErrors()` | Preset | `DbException` (+ `TimeoutException`, blocked by the hardcoded guard) |
| `.HandleTransientNetworkErrors()` | Preset | `HttpRequestException`, `SocketException`, `WebException`, `TaskCanceledException` (⚠ `TaskCanceledException` derives from `OperationCanceledException`, so it's blocked by the hardcoded fail-fast guard and never actually retried) |
| `.HandleAllTransientErrors()` | Preset | Both presets combined |

## Performance Tuning

→ [Reference: Performance Tuning Guidelines](configuration-reference.md#performance-tuning-guidelines)

| Workload | Max Parallelism | Channel Capacity | Notes |
|----------|----------------|------------------|-------|
| **CPU-bound** | `ProcessorCount` | Small (100–500) | Heavy computation |
| **I/O-bound** | `ProcessorCount × 4` | Large (5000+) | API/DB/file operations |
| **Mixed** | Separate queues | Varies | Different configs per queue |
| **High `Schedule()` rate** | `ProcessorCount × 4+` | 10000+ | Sharded scheduler (scheduling axis only; execution stays storage-bound) |

## See Also

- [Full Configuration Reference](configuration-reference.md): full detail for every option
- [Keyed Rate Limiting](rate-limiting.md): feature documentation
- [Getting Started](getting-started.md) · [Scalability](scalability.md) · [Resilience](resilience.md)
