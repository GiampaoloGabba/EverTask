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
| `SetAuditRetentionPolicy` | `AuditRetentionPolicy?` | `null` (unlimited) | Needs `services.AddAuditCleanup(policy, cleanupIntervalHours: 24)` (IServiceCollection extension, EF Core storage package) to take effect |
| `SetThrowIfUnableToPersist` | `bool` | `true` | Throw on storage save failure |
| `UseShardedScheduler` | `int shardCount = 0` | Off (`PeriodicTimerScheduler`); auto-scale when 0 | For >10k `Schedule()`/sec loads |
| `SetUseLazyHandlerResolution` | `bool` | `true` (adaptive) | `DisableLazyHandlerResolution()` to opt out |
| `WithPersistentLogger` | `Action<PersistentLoggerOptions>` | Disabled | Persists handler logs to DB; logs ALWAYS go to ILogger regardless |
| `SetRateLimiterOptions` | `Action<RateLimiterOptions>` | See below | Keyed rate limiter global knobs (v3.7+) |
| `RegisterTasksFromAssembly` | `Assembly` | — | Scan one assembly for handlers (required) |
| `RegisterTasksFromAssemblies` | `params Assembly[]` | — | Scan multiple assemblies |

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
| `Burst` | `Permits` | ≥ 1; `1` = strict even spacing |
| `ThrottleRetries` | `true` | Retries re-acquire budget (never erodes the per-attempt timeout) |
| `StartEmpty` | `false` | Fresh buckets at steady rate (caps post-restart burst) |
| `MaxReservationHorizon` | `1 hour` | Farther slots → terminal rejection |
| `MaxInSlotWait` | `1 second` | Nearer slots → inline wait |
| `OverflowBehavior` | `WaitForCapacity` | Or `Discard` (terminal `Failed` + `OnError`) |

**Key source**: task implements `IRateLimitedTask`, or handler overrides `GetRateLimitKey(task)`.

## Audit Levels

→ [Reference: SetDefaultAuditLevel](configuration-reference.md#setdefaultauditlevel)

| Level | Successful executions | Failed executions | Use Case |
|-------|----------------------|-------------------|----------|
| `Full` (default) | Full audit trail | Full audit trail | Critical tasks |
| `Minimal` | No audit rows (only `LastExecutionUtc`) | Full audit trail | High-frequency recurring |
| `ErrorsOnly` | No audit rows | Audited | Fire-and-forget |
| `None` | Never | Never | Extremely high-frequency |

Per-task override: `dispatcher.Dispatch(task, auditLevel: AuditLevel.Minimal)`.

## Queue Configuration

→ [Reference: Queue Configuration](configuration-reference.md#queue-configuration)

Builder methods: `ConfigureDefaultQueue(...)`, `AddQueue(name, configure?)`, `ConfigureRecurringQueue(...)`, `EnsureRecurringQueue()` (rarely needed: `AddEverTask` auto-creates the recurring queue).

Defaults differ between the auto-created `default`/`recurring` queues (inherit the global settings, `QueueFullBehavior.Wait`) and queues created via `AddQueue` (see Default column):

| Method | Parameters | Default for `AddQueue` queues | Notes |
|--------|-----------|-------------------------------|-------|
| `SetMaxDegreeOfParallelism` | `int` | `1` (sequential!) | `default`/`recurring` inherit the global value |
| `SetChannelCapacity` | `int` | `500` | `default`/`recurring` inherit the global capacity |
| `SetChannelOptions` | `BoundedChannelOptions` | — | Replaces the queue's whole channel options (FullMode, SingleReader/Writer, ...) |
| `SetFullBehavior` | `QueueFullBehavior` | `FallbackToDefault` (spills to default queue) | `Wait` / `FallbackToDefault` / `ThrowException`, immediate dispatches only; the auto-created `default` queue uses `Wait` |
| `SetDefaultTimeout` | `TimeSpan?` | unset → global | Chain: handler → queue → global (v3.7+) |
| `SetDefaultRetryPolicy` | `IRetryPolicy?` | unset → global | Chain: handler → queue → global (v3.7+) |

## Storage

→ [Reference: Storage Configuration](configuration-reference.md#storage-configuration)

| Method | Package | Notes |
|--------|---------|-------|
| `AddMemoryStorage()` | core | Dev/test only: tasks lost on restart |
| `AddSqlServerStorage(cs, opt?)` | `EverTask.Storage.SqlServer` | Options: `SqlServerTaskStoreOptions`; DbContext pooling + stored procedures |
| `AddSqliteStorage(cs?, opt?)` | `EverTask.Storage.Sqlite` | Options: `SqliteTaskStoreOptions`; `cs` defaults to `"Data Source=EverTask.db"` |

| Option (both store option types) | Default | Notes |
|----------------------------------|---------|-------|
| `SchemaName` | SQL Server: `"EverTask"`; SQLite: `""` | SQL Server: `null` = dbo; SQLite: must stay `""` (no schema concept) |
| `AutoApplyMigrations` | `true` | Disable for manual migrations in production |

## Logging

→ [Reference: Logging Configuration](configuration-reference.md#logging-configuration)

`AddSerilog(opt => ...)` (package `EverTask.Logging.Serilog`).

`WithPersistentLogger` (`PersistentLoggerOptions`, v3.0+):

| Method | Default | Notes |
|--------|---------|-------|
| `SetMinimumLevel(LogLevel)` | `Information` | Min level persisted to DB (ILogger gets everything) |
| `SetMaxLogsPerTask(int?)` | `1000` | `null` = unlimited (not recommended) |
| `Disable()` | — | Stop DB persistence (logs still flow to ILogger) |

## Monitoring

→ [Reference: Monitoring Configuration](configuration-reference.md#monitoring-configuration)

`AddMonitoringApi(opt => ...)` + `app.MapEverTaskApi()` (package `EverTask.Monitor.Api`). Fixed paths: dashboard `/evertask-monitoring`, API `/evertask-monitoring/api`, SignalR hub `/evertask-monitoring/hub`.

| EverTaskApiOptions | Default | Notes |
|--------------------|---------|-------|
| `EnableUI` | `true` | Embedded React dashboard |
| `EnableSwagger` | `false` | Separate Swagger document `evertask-monitoring` |
| `Username` / `Password` | `"admin"` / `"admin"` | CHANGE IN PRODUCTION |
| `EnableAuthentication` | `true` | JWT on API + hub |
| `JwtSecret` | `null` (random 256-bit per instance) | Set explicitly (≥ 32 bytes) for multi-instance deployments |
| `JwtIssuer` / `JwtAudience` | `"EverTask.Monitor.Api"` | — |
| `JwtExpirationHours` | `8` | Token TTL |
| `EnableCors` | `true` | — |
| `CorsAllowedOrigins` | `[]` (allow all) | Restrict in production |
| `AllowedIpAddresses` | `[]` (allow all) | IPv4/IPv6/CIDR; checked before auth |
| `MagicLinkToken` | `null` (disabled) | Instant auth via `/magic?token=...` |
| `EventDebounceMs` | `1000` | Dashboard SignalR refresh debounce |

`AddSignalRMonitoring(opt => ...)` (package `EverTask.Monitor.AspnetCore.SignalR`):

| Option | Default | Notes |
|--------|---------|-------|
| `IncludeExecutionLogs` | `false` | Streams handler logs in events (bandwidth cost) |

## Handler Properties

→ [Reference: Handler Configuration](configuration-reference.md#handler-configuration)

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `Timeout` | `TimeSpan?` | Inherits queue/global | Per-attempt timeout |
| `RetryPolicy` | `IRetryPolicy?` | Inherits queue/global | Per-handler retry |
| `QueueName` | `string?` | `"default"` (`"recurring"` for recurring tasks) | Target queue |
| `RateLimitPolicy` | `RateLimitPolicy?` | `null` (no limit) | Per-key throttling (v3.7+) |

## Performance Tuning

→ [Reference: Performance Tuning Guidelines](configuration-reference.md#performance-tuning-guidelines)

| Workload | Max Parallelism | Channel Capacity | Notes |
|----------|----------------|------------------|-------|
| **CPU-bound** | `ProcessorCount` | Small (100–500) | Heavy computation |
| **I/O-bound** | `ProcessorCount × 4` | Large (5000+) | API/DB/file operations |
| **Mixed** | Separate queues | Varies | Different configs per queue |
| **Extreme load** | `ProcessorCount × 4+` | 10000+ | Enable sharded scheduler |

## See Also

- [Full Configuration Reference](configuration-reference.md): full detail for every option
- [Keyed Rate Limiting](rate-limiting.md): feature documentation
- [Getting Started](getting-started.md) · [Scalability](scalability.md) · [Resilience](resilience.md)
