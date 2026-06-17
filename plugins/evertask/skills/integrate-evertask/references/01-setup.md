# 01 — Setup & DI configuration

The entry point and every option. Defaults are auto-sized and good; set an option only when
the user has a concrete reason.

## Entry point

```csharp
// IServiceCollection extension — returns EverTaskServiceBuilder for chaining
public static EverTaskServiceBuilder AddEverTask(
    this IServiceCollection services,
    Action<EverTaskServiceConfiguration>? configure = null)
```

- Throws `ArgumentException` if `configure` is null **or no assembly is registered** inside it.
- **Must be followed by exactly one storage call** (`.AddMemoryStorage()` / `.AddSqlServerStorage(...)` /
  `.AddSqliteStorage(...)` / `.AddPostgresStorage(...)`).

### Minimal

```csharp
builder.Services.AddEverTask(opt =>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage();
```

## `EverTaskServiceConfiguration` — every option

### Required: assembly registration

| Method | Notes |
|---|---|
| `RegisterTasksFromAssembly(Assembly)` | Add one assembly to scan for handlers. Call ≥ once. |
| `RegisterTasksFromAssemblies(params Assembly[])` | Add several at once. |

### Concurrency & default-queue channel

| Method | Default | Notes |
|---|---|---|
| `SetMaxDegreeOfParallelism(int)` | `Math.Max(4, ProcessorCount*2)` (≈16 on 8-core) | Concurrent workers on the **default** queue. `1` logs a startup warning; any value `< 1` is clamped to 1 consumer (with a warning) — there is no "unlimited" mode. |
| `SetChannelOptions(int capacity)` | `Math.Max(1000, ProcessorCount*200)` (≈1600 on 8-core) | Bounded channel capacity for the default queue; full-mode stays `Wait`. |
| `SetChannelOptions(BoundedChannelOptions)` | as above, `FullMode=Wait` | Full replacement (FullMode, SingleReader/Writer, AllowSynchronousContinuations). **Keep `FullMode=Wait`** — `Drop*` modes bypass the `QueueFull` signal/backpressure (a dropped item is reverted to `WaitingQueue` and recovered at startup, but won't run in-process). |

Guidance: CPU-bound → `ProcessorCount`; I/O-bound → `ProcessorCount × 2–4`. Capacity: low 500–1000,
moderate 2000–5000, high 10000+.

### Resilience defaults (overridable per-queue and per-handler)

| Method | Default | Notes |
|---|---|---|
| `SetDefaultRetryPolicy(IRetryPolicy)` | `new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500))` | 3 retries (4 total). Both ctor args must be > 0. See `04-resilience.md`. |
| `SetDefaultTimeout(TimeSpan?)` | `null` (no timeout) | Cooperative via `CancellationToken`. `TimeoutException` is not retried by default. |

### Persistence & audit

| Method | Default | Notes |
|---|---|---|
| `SetThrowIfUnableToPersist(bool)` | `true` | `true` = dispatch fails loudly on storage failure (recommended). `false` = silent, task may be lost. |
| `SetDefaultAuditLevel(AuditLevel)` | `AuditLevel.Full` | `Full`/`Minimal`/`ErrorsOnly`/`None`. Override per-dispatch. See `03-storage.md`. |

### Handler resolution

| Method | Default | Notes |
|---|---|---|
| `SetUseLazyHandlerResolution(bool)` | `true` | Adaptive: immediate / recurring ≥5min / delayed ≥30min use lazy mode; shorter intervals eager. |
| `DisableLazyHandlerResolution()` | — | Convenience for `SetUseLazyHandlerResolution(false)`. |

### Persistent logger (handler logs → DB)

`WithPersistentLogger(Action<PersistentLoggerOptions>)` — auto-enables DB persistence; logs are
always also forwarded to `ILogger`.

| `PersistentLoggerOptions` | Default | Notes |
|---|---|---|
| `SetMinimumLevel(LogLevel)` | `Information` | Min level stored in DB (ILogger still gets all). |
| `SetMaxLogsPerTask(int?)` | `1000` | Per-execution cap; `null` = unlimited (discouraged). Single bulk INSERT after completion. |

Pair with `AddAuditCleanup(...)` (see `03-storage.md`) to bound table growth.

### Sharded scheduler (high scheduling load only)

`UseShardedScheduler(int shardCount = 0)` — `0` auto-scales to `Math.Max(4, ProcessorCount)`.
Use only when sustained `Schedule()` rate is very high (>~10k/sec) or 100k+ tasks scheduled at
once, and profiling shows scheduler lock contention. Does **not** raise execution throughput.
See `06-rate-limiting-queues.md`.

### Rate-limiter global knobs (v3.7+)

`SetRateLimiterOptions(Action<RateLimiterOptions>)`:

| `RateLimiterOptions` | Default | Notes |
|---|---|---|
| `MaxParkedTasks` | `min(5000, 2 × default-queue capacity)` | Distinct rate-limited tasks parked waiting for budget; at cap, consumers pause (backpressure). |
| `MaxTrackedKeys` | `100_000` | (task-type, key) buckets; overflow fails OPEN with a warning + monitoring event. |
| `MaxKeyLength` | `256` | Longer keys are SHA-256 hashed. |
| `EmitDeferralEvents` | `true` | Aggregated deferral monitoring events. |

## `EverTaskServiceBuilder` — chaining (post-`AddEverTask`)

```csharp
.ConfigureDefaultQueue(Action<QueueConfiguration>)   // tune the auto-created "default" queue
.AddQueue(string name, Action<QueueConfiguration>?)  // create a named queue
.ConfigureRecurringQueue(Action<QueueConfiguration>) // tune the auto-created "recurring" queue
.EnsureRecurringQueue()                              // idempotently create the recurring queue
.Services                                            // escape hatch: the underlying IServiceCollection — register your own singletons mid-chain (e.g. a TaskMonitoringService, a custom IKeyedRateLimiter, IGuidGenerator)
// + storage / logging / monitoring extension methods (see their reference files)
```

Well-known names: `QueueNames.Default = "default"` (always created), `QueueNames.Recurring = "recurring"`
(both auto-created during service registration — `RegisterQueueManager` — if not configured). Full
queue semantics in `06-rate-limiting-queues.md`.

## Fully-loaded example (every knob — copy only what is needed)

```csharp
var everTask = builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssemblies(typeof(CoreTask).Assembly, typeof(ApiTask).Assembly)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(5000)
    .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
    .SetDefaultTimeout(TimeSpan.FromMinutes(5))
    .SetThrowIfUnableToPersist(true)
    .SetDefaultAuditLevel(AuditLevel.Full)
    .SetUseLazyHandlerResolution(true)
    .WithPersistentLogger(log => log.SetMinimumLevel(LogLevel.Information).SetMaxLogsPerTask(1000))
    .UseShardedScheduler(shardCount: 0)
    .SetRateLimiterOptions(o => { o.MaxParkedTasks = 5000; o.MaxTrackedKeys = 100_000; }))
  .ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10).SetChannelCapacity(2000).SetFullBehavior(QueueFullBehavior.Wait))
  .AddQueue("critical", q => q.SetMaxDegreeOfParallelism(20).SetChannelCapacity(500))
  .AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!,
      o => { o.SchemaName = "EverTask"; o.AutoApplyMigrations = false; });

// Audit retention is on IServiceCollection, NOT the builder:
builder.Services.AddAuditCleanup(
    AuditRetentionPolicy.WithErrorPriority(successRetentionDays: 30, errorRetentionDays: 90),
    cleanupIntervalHours: 24);

// Web apps only — after builder.Build():
app.MapEverTaskApi();
```

## Wizard decision points (only ask what the prompt didn't already answer)

- Storage backend (drives persistence + packages) → `03-storage.md`.
- Workload type → `MaxDegreeOfParallelism`; expected burst size → channel capacity.
- Need retry/timeout tuning, rate limiting, multi-queue, monitoring, Serilog, persistent logs?
  Each maps to a reference file; wire only what's chosen.
- Throw on persistence failure (default true) — flip to false only for best-effort/test.
- High `Schedule()` rate → sharded scheduler (rare).
