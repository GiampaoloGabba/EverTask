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
| `SetThrowIfUnableToPersist` | `bool` | `true` | Throw on storage save failure |
| `UseShardedScheduler` | `int shardCount = 0` | Off (`PeriodicTimerScheduler`); auto-scale when 0 | High `Schedule()`-call rates (scheduling axis, not task-execution throughput) |
| `SetUseLazyHandlerResolution` | `bool` | `true` (adaptive) | `DisableLazyHandlerResolution()` to opt out |
| `WithPersistentLogger` | `Action<PersistentLoggerOptions>` | Disabled | Persists handler logs to DB; logs ALWAYS go to ILogger regardless |
| `SetRateLimiterOptions` | `Action<RateLimiterOptions>` | See below | Keyed rate limiter global knobs (v3.7+) |
| `RegisterTasksFromAssembly` | `Assembly` | — | Scan one assembly for handlers (required) |
| `RegisterTasksFromAssemblies` | `params Assembly[]` | — | Scan multiple assemblies |

> **Audit / execution-log retention** is not a builder method: register it with `services.AddAuditCleanup(policy, cleanupIntervalHours: 24)` (IServiceCollection extension, EF Core storage package). Trims audits and execution logs (`ExecutionLogRetentionDays` / `MaxExecutionLogsPerTask`); any knob `<= 0` is treated as disabled. See [Configuration Reference](configuration-reference.md).

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
| `Permits` | — (ctor, required) | Public get-only; executions allowed per `Period`. Must be > 0 |
| `Period` | — (ctor, required) | Public get-only; the window for `Permits`. Must be > 0 |
| `Burst` | `Permits` | ≥ 1; `1` = strict even spacing |
| `ThrottleRetries` | `true` | Retries re-acquire budget through the gate (never erodes the per-attempt timeout). Not an inline wait: a far slot **re-parks** the task and it fires via redelivery, **restarting** retry attempt numbering |
| `StartEmpty` | `false` | `false` = new bucket starts **full** (burst available immediately); `true` = starts at steady rate (caps post-restart burst) |
| `MaxReservationHorizon` | `1 hour` | Farther slots → terminal rejection |
| `MaxInSlotWait` | `1 second` | **No-op (binary-compat only)** — over-budget tasks always re-park to the scheduler; no inline consumer wait |
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

Not a builder method — register on `IServiceCollection` (EF Core storage providers only): `services.AddAuditCleanup(policy, cleanupIntervalHours: 24)`. Build the policy with a factory (`AuditRetentionPolicy.WithUniformRetention(days)` or `WithErrorPriority(successDays, errorDays)`) or set properties directly. Any knob `<= 0` is treated as disabled.

| `AuditRetentionPolicy` | Default | Notes |
|------------------------|---------|-------|
| `StatusAuditRetentionDays` | `null` (unlimited) | Days to keep StatusAudit rows |
| `RunsAuditRetentionDays` | `null` | Days to keep RunsAudit rows |
| `ErrorAuditRetentionDays` | `null` | Overrides the two above for error rows (keep errors longer) |
| `ExecutionLogRetentionDays` | `null` | Days to keep TaskExecutionLog rows |
| `MaxExecutionLogsPerTask` | `null` | Cross-run cap per task; oldest deleted first |
| `DeleteCompletedTasksAfterRetention` | `false` | Hard-delete completed non-recurring tasks older than the **longest** configured audit window **and** with no remaining StatusAudit/RunsAudit rows. If a log-retention window/cap is active, a task that still owns execution logs is **preserved** (the purge only cascades once logs age out on their own). No audit window configured → nothing deleted. Recurring/Failed/Cancelled never auto-deleted. |

| `AddAuditCleanup` / `AuditCleanupOptions` | Default | Notes |
|-------------------------------------------|---------|-------|
| `RetentionPolicy` | `null` | The policy supplied via the `AddAuditCleanup(policy, …)` arg; `null` = no cleanup runs |
| `cleanupIntervalHours` arg → `CleanupInterval` | `24h` | Sweep interval |
| `InitialDelay` | `1 min` | Delay before the first sweep |

## Queue Configuration

→ [Reference: Queue Configuration](configuration-reference.md#queue-configuration)

Builder methods: `ConfigureDefaultQueue(...)`, `AddQueue(name, configure?)`, `ConfigureRecurringQueue(...)`, `EnsureRecurringQueue()` (rarely needed: `AddEverTask` auto-creates the recurring queue). The builder also exposes `.Services` (the underlying `IServiceCollection`) so you can register your own singletons mid-chain (e.g. a custom `IKeyedRateLimiter` or `IGuidGenerator`). Note: if a custom/distributed `IKeyedRateLimiter` **throws** (e.g. Redis down), the gate **fails open** — the task runs unthrottled with a warning, never failing over a limiter outage. The well-known names are the constants `QueueNames.Default` (`"default"`) and `QueueNames.Recurring` (`"recurring"`).

Defaults differ between the auto-created `default`/`recurring` queues (inherit the global settings, `QueueFullBehavior.Wait`) and queues created via `AddQueue` (see Default column):

| Method | Parameters | Default for `AddQueue` queues | Notes |
|--------|-----------|-------------------------------|-------|
| `SetMaxDegreeOfParallelism` | `int` | `1` (sequential!) | `default`/`recurring` inherit the global value |
| `SetChannelCapacity` | `int` | `500` | `default`/`recurring` inherit the global capacity |
| `SetChannelOptions` | `BoundedChannelOptions` | — | Replaces the queue's whole channel options (FullMode, SingleReader/Writer, ...) |
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
| `AddSqliteStorage(cs?, opt?)` | `EverTask.Storage.Sqlite` | Options: `SqliteTaskStoreOptions`; `cs` defaults to `"Data Source=EverTask.db"` |

| Option (all EF Core store option types) | Default | Notes |
|----------------------------------|---------|-------|
| `SchemaName` | SQL Server: `"EverTask"`; PostgreSQL: `"evertask"`; SQLite: `""` | PostgreSQL: lowercase only (`null` = `public`); SQL Server: `null` = dbo; SQLite: must stay `""` (no schema concept) |
| `AutoApplyMigrations` | `true` | Disable for manual migrations in production |

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

`AddMonitoringApi(opt => ...)` + `app.MapEverTaskApi()` (package `EverTask.Monitor.Api`). `MapEverTaskApi` accepts an optional `Action<HttpConnectionDispatcherOptions>` to tune the SignalR hub connection. Fixed paths: dashboard `/evertask-monitoring`, API `/evertask-monitoring/api`, SignalR hub `/evertask-monitoring/hub`. For setups without the EverTask builder there is `services.AddEverTaskMonitoringApiStandalone(opt => ...)` (you must register `ITaskStorage` yourself). It does **not** wire SignalR monitoring, and there is **no `IServiceCollection` overload of `AddSignalRMonitoring`** (it exists only on `EverTaskServiceBuilder`). `MapEverTaskApi` still maps the hub endpoint, but without the monitor subscription no live events are pushed — for live dashboard updates use the builder path (`AddEverTask(...).AddMonitoringApi(...)`).

| EverTaskApiOptions | Default | Notes |
|--------------------|---------|-------|
| `EnableUI` | `true` | Embedded React dashboard |
| `EnableSwagger` | `false` | Separate Swagger document `evertask-monitoring` |
| `Username` / `Password` | `"admin"` / `"admin"` | CHANGE IN PRODUCTION |
| `EnableAuthentication` | `true` | JWT on API + hub |
| `JwtSecret` | `null` (random 256-bit per instance) | Set explicitly (≥ 32 bytes) for multi-instance deployments |
| `JwtIssuer` / `JwtAudience` | `"EverTask.Monitor.Api"` | — |
| `JwtExpirationHours` | `8` | Token TTL |
| `EnableCors` | `true` | ⚠ Only **registers** a named CORS policy (`EverTaskMonitoringApi`); EverTask does **not** apply it (no `UseCors`/`RequireCors`). To enforce CORS you must apply it yourself in your app pipeline |
| `CorsAllowedOrigins` | `[]` (allow all) | Origins for the registered policy (used only when non-empty + the policy is actually applied). Restrict in production |
| `AllowedIpAddresses` | `[]` (allow all) | IPv4/IPv6/CIDR; checked before auth |
| `MagicLinkToken` | `null` (disabled) | Instant auth via `/magic?token=...` |
| `EventDebounceMs` | `1000` | Dashboard SignalR refresh debounce |
| `BasePath` | `/evertask-monitoring` | **read-only** computed; fixed |
| `ApiBasePath` | `/evertask-monitoring/api` | **read-only** computed; fixed |
| `UIBasePath` | `/evertask-monitoring` | **read-only** computed; fixed |
| `SignalRHubPath` | `/evertask-monitoring/hub` | **read-only** computed; fixed |

`AddSignalRMonitoring(...)` (package `EverTask.Monitor.AspnetCore.SignalR`) — 4 overloads: `AddSignalRMonitoring()`, `(Action<SignalRMonitoringOptions>)`, `(Action<HubOptions>)`, `(Action<HubOptions>, Action<SignalRMonitoringOptions>)`. When used **standalone** (without `AddMonitoringApi`), you MUST also call `app.MapEverTaskMonitorHub()` after `Build()` — it maps the hub **and** subscribes the monitor, so without it no events are broadcast. Overloads: `MapEverTaskMonitorHub()` (default path), `MapEverTaskMonitorHub("/custom")`, `MapEverTaskMonitorHub("/custom", hub => { ... })`. Options:

| Option | Default | Notes |
|--------|---------|-------|
| `IncludeExecutionLogs` | `false` | Streams handler logs in events (bandwidth cost) |

## Handler Properties

→ [Reference: Handler Configuration](configuration-reference.md#handler-configuration)

| Property / Member | Type | Default | Notes |
|----------|------|---------|-------|
| `Timeout` | `TimeSpan?` | Inherits queue/global | Per-attempt timeout |
| `RetryPolicy` | `IRetryPolicy?` | Inherits queue/global | Per-handler retry |
| `QueueName` | `string?` | `"default"` (`"recurring"` for recurring tasks) | Target queue. An **unregistered/unknown** name logs a warning and falls back to the `default` queue — both for routing **and** for the retry/timeout config resolution (the task runs on `default` with `default`'s config). |
| `RateLimitPolicy` | `RateLimitPolicy?` | `null` (no limit) | Per-key throttling (v3.7+) |
| `GetRateLimitKey(task)` | `string?` (override) | reads `IRateLimitedTask.RateLimitKey` | Derive the throttle key without changing the task type |

> Obsolete: `CpuBoundOperation` (bool) still exists on the handler but is `[Obsolete]` and has **no effect** — do not set it. For CPU-bound work, use `Task.Run` inside `Handle`.

## Dispatch Parameters

→ [Reference: Task Dispatching](task-dispatching.md)

Optional parameters on every `ITaskDispatcher.Dispatch(...)` overload:

| Parameter | Type | Default | Notes |
|-----------|------|---------|-------|
| `auditLevel` | `AuditLevel?` | `null` → global default (`Full`) | Per-dispatch audit override |
| `taskKey` | `string?` | `null` (no dedup) | Idempotency key (≤ 200 chars). **Non-recurring**: `InProgress` (or an immediate one-shot whose delivery is already in flight) → no-op; `Pending`/`Queued`/`WaitingQueue` → update; terminal → replace. **Recurring**: `InProgress` → no-op; otherwise → **update in place** (never replaced), preserving `NextRunUtc`+`CurrentRunCount` **only if `NextRunUtc.HasValue`** (an exhausted series with no next run recalculates instead); recurring→one-shot re-dispatch is **discarded** |
| `cancellationToken` | `CancellationToken` | `default` | Cancels the dispatch op (not execution) |

## Recurring Schedule Builder

→ [Reference: Recurring Tasks](recurring-tasks.md) · used via `Dispatch(task, Action<IRecurringTaskBuilder>, …)`. All times **UTC**.

| Stage | Methods |
|-------|---------|
| Start | `Schedule()` (recurring only) · `RunNow()` / `RunDelayed(TimeSpan)` / `RunAt(DateTimeOffset)` → `.Then()` |
| Interval | `Every(n).Seconds()/.Minutes()/.Hours()/.Days()/.Weeks()/.Months()` · `EverySecond/Minute/Hour/Day/Week/Month()` · `OnHours()` · `OnDays(params DayOfWeek[])` · `OnMonths(params int[])` |
| Refine | hour `.AtMinute(0–59)` · minute `.AtSecond(0–59)` · day `.AtTime(TimeOnly)` / `.AtTimes(…)` · week `.OnDay(s)` · month `.OnDay(1–31)` / `.OnDays(…)` / `.OnFirst(DayOfWeek)` |
| Cron | `UseCron("expr")` (5- or 6-field; **overrides** all other interval calls) |
| Limit | `.RunUntil(DateTimeOffset)` · `.MaxRuns(int)` (counts real runs; skipped-after-downtime don't count) |

> `OnLast(DayOfWeek)` does **not** exist (only `OnFirst`). Use a stable `taskKey` for idempotent startup registration.

## Retry Policy & Exception Filtering

→ [Reference: Resilience](resilience.md)

`LinearRetryPolicy(int retryCount, TimeSpan retryDelay)` or `LinearRetryPolicy(TimeSpan[] retryDelays)`. Default global policy: `LinearRetryPolicy(3, 500ms)`, retrying everything except `OperationCanceledException`/`TimeoutException`. Fluent filtering (whitelist and blacklist cannot be mixed):

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
