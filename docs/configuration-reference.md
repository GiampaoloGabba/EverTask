---
layout: default
title: Configuration Reference
parent: Configuration
nav_order: 1
---

# Configuration Reference

This is a complete reference for all EverTask configuration options.

## Table of Contents

- [Service Configuration](#service-configuration)
- [Queue Configuration](#queue-configuration)
- [Rate Limiting Configuration](#rate-limiting-configuration)
- [Storage Configuration](#storage-configuration)
- [Logging Configuration](#logging-configuration)
- [Monitoring Configuration](#monitoring-configuration)
- [Storage Provider Details](#storage-provider-details)
- [Handler Configuration](#handler-configuration)
- [Complete Examples](#complete-examples)
- [Configuration Validation](#configuration-validation)
- [Performance Tuning Guidelines](#performance-tuning-guidelines)

## Service Configuration

Use the fluent API in `AddEverTask()` to configure EverTask's core behavior.

### SetChannelOptions

Controls how many tasks can be queued and what happens when the queue fills up.

**Signatures:**
```csharp
SetChannelOptions(int capacity)
SetChannelOptions(BoundedChannelOptions options)
```

**Parameters:**
- `capacity` (int): Maximum number of tasks that can be queued
- `options` (BoundedChannelOptions): Fully configured channel options instance

**Default:** `Environment.ProcessorCount * 200` (minimum 1000)

**Examples:**
```csharp
// Simple capacity
opt.SetChannelOptions(5000)

// Custom configuration
opt.SetChannelOptions(new BoundedChannelOptions(5000)
{
    FullMode = BoundedChannelFullMode.Wait // or DropWrite, DropOldest
})
```

**FullMode Options:**
- `Wait`: Block until space is available (default)
- `DropWrite`: Drop the new item if full
- `DropOldest`: Drop the oldest item and add the new one

### SetMaxDegreeOfParallelism

Controls how many tasks can run at the same time.

**Signature:**
```csharp
SetMaxDegreeOfParallelism(int parallelism)
```

**Parameters:**
- `parallelism` (int): Number of concurrent workers

**Default:** `Environment.ProcessorCount * 2` (minimum 4)

**Examples:**
```csharp
// Fixed parallelism
opt.SetMaxDegreeOfParallelism(16)

// Scale with CPUs
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
```

**Notes:**
- Use higher values for I/O-bound tasks like API calls or database operations
- Use lower values for CPU-intensive tasks
- Setting to 1 will log a warning since it's generally a bad idea in production

### SetDefaultRetryPolicy

Sets how tasks should retry when they fail (applies to all tasks unless overridden).

**Signature:**
```csharp
SetDefaultRetryPolicy(IRetryPolicy policy)
```

**Parameters:**
- `policy` (IRetryPolicy): Retry policy implementation

**Default:** `LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500))`

**Examples:**
```csharp
// Linear retry with fixed delay
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1)))

// Linear retry with custom delays
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(new[]
{
    TimeSpan.FromMilliseconds(100),
    TimeSpan.FromMilliseconds(500),
    TimeSpan.FromSeconds(2)
}))

// Custom retry policy (your own IRetryPolicy implementation; see
// resilience/retry-policies.md for an exponential backoff example)
opt.SetDefaultRetryPolicy(new MyExponentialBackoffPolicy())
```

**Notes:**
- `LinearRetryPolicy` is the only built-in policy; `retryCount` is the number of retries AFTER the initial attempt (e.g. `LinearRetryPolicy(3, ...)` = up to 4 executions), and both `retryCount` and `retryDelay` must be greater than zero.
- Retries cannot be disabled via `LinearRetryPolicy`: to disable them, implement a trivial `IRetryPolicy` that invokes the action once; see [Custom Retry Policies](resilience/retry-policies.md#custom-retry-policies).

### SetDefaultTimeout

Sets a maximum execution time for tasks (applies globally unless overridden).

**Signature:**
```csharp
SetDefaultTimeout(TimeSpan? timeout)
```

**Parameters:**
- `timeout` (TimeSpan?): Maximum execution time, or `null` for no timeout

**Default:** `null` (no timeout)

**Examples:**
```csharp
// 5 minute timeout
opt.SetDefaultTimeout(TimeSpan.FromMinutes(5))

// 30 second timeout
opt.SetDefaultTimeout(TimeSpan.FromSeconds(30))

// No timeout (explicit)
opt.SetDefaultTimeout(null)
```

**Notes:**
- When the timeout is reached, the `CancellationToken` gets cancelled
- Your handler needs to check the token for this to work (cooperative cancellation)
- You can override this per handler or per queue

### SetDefaultAuditLevel

Sets the default audit trail level for all tasks (controls database bloat from high-frequency tasks).

**Signature:**
```csharp
SetDefaultAuditLevel(AuditLevel auditLevel)
```

**Parameters:**
- `auditLevel` (AuditLevel): Audit verbosity level
  - `Full` (default): Complete audit trail (all status transitions and executions)
  - `Minimal`: Errors get the full audit trail; successful executions write NO audit rows (only `QueuedTask.LastExecutionUtc` is updated)
  - `ErrorsOnly`: Only failed executions are audited
  - `None`: No audit trail

**Default:** `AuditLevel.Full`

**Examples:**
```csharp
// Full audit (default)
opt.SetDefaultAuditLevel(AuditLevel.Full)

// Minimal audit for high-frequency tasks
opt.SetDefaultAuditLevel(AuditLevel.Minimal)

// Only audit errors
opt.SetDefaultAuditLevel(AuditLevel.ErrorsOnly)

// No audit trail
opt.SetDefaultAuditLevel(AuditLevel.None)
```

**Notes:**
- For a task running every 5 minutes: Full ≈ 1,152 audit records/day; Minimal = 0 when executions succeed
- You can override this per task when dispatching
- Use lower levels (Minimal/ErrorsOnly/None) for high-frequency recurring tasks
- See [Audit Configuration](storage/audit-configuration.md) for detailed usage guide

### Audit & Execution-Log Retention (`AddAuditCleanup`)

Configure automatic retention to prevent unbounded growth of the audit and execution-log tables. Retention is enforced by the optional `AuditCleanupHostedService`, registered with **`AddAuditCleanup(policy, cleanupIntervalHours)`** — the single entry-point that actually applies the policy.

> **Note:** retention is applied **only** by `AddAuditCleanup(policy, cleanupIntervalHours)`. An earlier `SetAuditRetentionPolicy(...)` on the builder never took effect (the service reads its policy only from `AuditCleanupOptions`) and has been removed; if you used it, pass the policy to `AddAuditCleanup` instead.

> **Non-positive knobs are disabled.** Every day/count knob uses the same convention: `null` = unlimited/disabled, and any value `<= 0` is **also** treated as disabled (a no-op, logged as a warning), never as a "now"/future cutoff. This keeps a typo or a missing `IConfiguration` binding (an absent env var binds to `0`) from turning a cleanup cycle into a mass deletion.

**Default:** `null` (unlimited retention)

**Factory Methods:**
```csharp
// Uniform retention: same TTL for all audit types
AuditRetentionPolicy.WithUniformRetention(int retentionDays)

// Error priority: keep errors longer than successful executions
AuditRetentionPolicy.WithErrorPriority(int successRetentionDays, int errorRetentionDays)
```

**Examples:**

**Basic Setup (Uniform Retention):**
```csharp
var policy = AuditRetentionPolicy.WithUniformRetention(30);

builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString);

// AddAuditCleanup extends IServiceCollection (EverTask.Storage.EfCore package),
// NOT the EverTask builder: call it as a separate statement. It is the only
// entry-point that applies the policy.
builder.Services.AddAuditCleanup(policy, cleanupIntervalHours: 24);
```

**Advanced Setup (Keep Errors Longer):**
```csharp
var policy = AuditRetentionPolicy.WithErrorPriority(
    successRetentionDays: 7,
    errorRetentionDays: 90);

builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString);

builder.Services.AddAuditCleanup(policy, cleanupIntervalHours: 24);
```

**Custom Policy:**
```csharp
var policy = new AuditRetentionPolicy
{
    StatusAuditRetentionDays = 14,             // Status changes retained for 14 days
    RunsAuditRetentionDays = 7,                // Execution history retained for 7 days
    ErrorAuditRetentionDays = 90,              // Errors retained for 90 days
    ExecutionLogRetentionDays = 30,            // Captured execution logs trimmed after 30 days
    MaxExecutionLogsPerTask = 1000,            // Keep at most the latest 1000 logs per task
    DeleteCompletedTasksAfterRetention = true  // Purge completed task rows once aged out (see below)
};

builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString);

builder.Services.AddAuditCleanup(policy, cleanupIntervalHours: 12);
```

**Retention Policy Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `StatusAuditRetentionDays` | `int?` | `null` | Days to retain status audit records (Queued → InProgress → Completed/Failed) |
| `RunsAuditRetentionDays` | `int?` | `null` | Days to retain execution audit records (recurring task runs) |
| `ErrorAuditRetentionDays` | `int?` | `null` | Days to retain error audit records (overrides above for failures) |
| `ExecutionLogRetentionDays` | `int?` | `null` | Days to retain captured execution logs (`TaskExecutionLog`), trimmed independently of the parent task (anchored on `TimestampUtc`) |
| `MaxExecutionLogsPerTask` | `int?` | `null` | Per-task, cross-run cap: keep at most the latest N execution logs per task and delete the oldest beyond N |
| `DeleteCompletedTasksAfterRetention` | `bool` | `false` | Hard-delete a completed non-recurring task once it is older than the longest retention window **and** has no audit rows |

> `DeleteCompletedTasksWithAudits` is the deprecated alias of `DeleteCompletedTasksAfterRetention`; it still works but forwards to the new property.
>
> **When a completed task is deleted:** when it is older than the longest of `StatusAuditRetentionDays`/`RunsAuditRetentionDays`/`ErrorAuditRetentionDays` (measured from `LastExecutionUtc`, falling back to `CreatedAtUtc`) and has no remaining StatusAudit/RunsAudit rows. If no retention window is configured, no completed tasks are deleted (a non-positive window counts as disabled). **When a log-retention window or cap (`ExecutionLogRetentionDays` / `MaxExecutionLogsPerTask`) is active, a task that still has surviving logs is preserved**, so its logs are never cascade-deleted before their own window expires; once those logs age out the task is purged. With no log retention configured, deleting the task cascades to everything it owns, captured execution logs included.
>
> **Execution-log retention.** `ExecutionLogRetentionDays` and `MaxExecutionLogsPerTask` trim `TaskExecutionLog` rows on their own, without deleting the task, so a long-running service (recurring tasks especially) never accumulates logs without bound. Both default to `null` (unlimited), so enabling persistent logging never starts deleting logs on its own. They are separate from `PersistentLoggerOptions.MaxLogsPerTask`, which caps a single execution's logs at capture time; these two trim logs across all past runs. When both are set, a log is deleted if it breaks either rule. Both are enforced by `AddAuditCleanup()`.

**Cleanup Service Registration:**

The `AddAuditCleanup()` method (an `IServiceCollection` extension from the `EverTask.Storage.EfCore` package) registers a hosted service that periodically deletes old audit records:

```csharp
builder.Services.AddAuditCleanup(
    retentionPolicy,              // The retention policy to apply (required)
    cleanupIntervalHours: 24);    // Cleanup frequency (default: 24 hours)
```

**Important Notes:**

1. **Single Entry-Point**: `AddAuditCleanup(policy, ...)` is the only place that applies the policy. (The former `SetAuditRetentionPolicy()` builder method, which never applied it, has been removed.)
2. **Cleanup Service Required**: Retention is enforced by `AddAuditCleanup()` - without it, policy has no effect
3. **Recurring Tasks**: Never auto-deleted, even with `DeleteCompletedTasksAfterRetention = true` (they need to reschedule)
4. **Failed/Cancelled Tasks**: Preserved for visibility, even with `DeleteCompletedTasksAfterRetention = true`
5. **Database Impact**: Cleanup runs in background, deletes only tasks past the retention cutoff (no immediate hard-delete)

**Monitoring Cleanup:**

Check cleanup service logs:
```
[02:00:15 INF] AuditCleanupHostedService: Starting audit cleanup cycle
[02:00:16 INF] Deleted 1,543 status audit records older than 30 days
[02:00:16 INF] Deleted 8,921 runs audit records older than 30 days
[02:00:16 INF] Deleted 234 completed tasks with no remaining audits
[02:00:16 INF] AuditCleanupHostedService: Cleanup cycle completed in 1.2s
```

**Recommended Settings by Workload:**

| Workload Type | Success Retention | Error Retention | Cleanup Interval |
|---------------|------------------|-----------------|------------------|
| **Development** | 7 days | 30 days | 24 hours |
| **Production (Low Volume)** | 30 days | 90 days | 24 hours |
| **Production (High Volume)** | 7 days | 90 days | 12 hours |
| **Compliance/Audit** | 365 days | 365 days | 24 hours |

### SetThrowIfUnableToPersist

Controls what happens when a task can't be saved to storage.

**Signature:**
```csharp
SetThrowIfUnableToPersist(bool value)
```

**Parameters:**
- `value` (bool): Whether to throw on persistence failure

**Default:** `true`

**Examples:**
```csharp
// Throw on persistence failure (recommended)
opt.SetThrowIfUnableToPersist(true)

// Don't throw (tasks may be lost)
opt.SetThrowIfUnableToPersist(false)
```

**Notes:**
- When `true`, the dispatch fails immediately if the task can't be saved
- When `false`, the task might run but won't be saved (risky!)
- Keep this `true` unless you have a good reason not to

### UseShardedScheduler

Enables a sharded scheduler that can handle extremely high loads by distributing work across multiple internal schedulers.

**Signature:**
```csharp
UseShardedScheduler(int shardCount = 0)
```

**Parameters:**
- `shardCount` (int): Number of shards; `0` (default) auto-scales to `Math.Max(4, ProcessorCount)`

**Default:** Not enabled (uses `PeriodicTimerScheduler`)

**Examples:**
```csharp
// Auto-scale based on CPUs
opt.UseShardedScheduler()

// Fixed shard count
opt.UseShardedScheduler(8)

// Scale with CPUs
opt.UseShardedScheduler(Environment.ProcessorCount)
```

**When to Use:**
You probably need this if you're seeing:
- Sustained load above 10,000 `Schedule()` calls/second
- Burst spikes above 20,000 `Schedule()` calls/second
- More than 100,000 tasks scheduled at once
- High lock contention showing up in your profiler

### RegisterTasksFromAssembly

Scans an assembly and registers all task handlers it finds.

**Signature:**
```csharp
RegisterTasksFromAssembly(Assembly assembly)
```

**Parameters:**
- `assembly` (Assembly): Assembly containing task handlers

**Examples:**
```csharp
// Current assembly
opt.RegisterTasksFromAssembly(typeof(Program).Assembly)

// Specific assembly
opt.RegisterTasksFromAssembly(typeof(MyTask).Assembly)

// Assembly by name
opt.RegisterTasksFromAssembly(Assembly.Load("MyTasksAssembly"))
```

### RegisterTasksFromAssemblies

Scans multiple assemblies and registers all task handlers from them.

**Signature:**
```csharp
RegisterTasksFromAssemblies(params Assembly[] assemblies)
```

**Parameters:**
- `assemblies` (Assembly[]): Assemblies containing task handlers

**Examples:**
```csharp
opt.RegisterTasksFromAssemblies(
    typeof(CoreTask).Assembly,
    typeof(ApiTask).Assembly,
    typeof(BackgroundTask).Assembly)
```

### SetUseLazyHandlerResolution

Controls whether EverTask uses lazy handler resolution for scheduled and recurring tasks. When enabled (default), handlers are disposed after dispatch and recreated at execution time based on task scheduling characteristics.

**Signature:**
```csharp
SetUseLazyHandlerResolution(bool enabled)
DisableLazyHandlerResolution()  // Convenience method for disabling
```

**Parameters:**
- `enabled` (bool): True to enable lazy resolution (default), false to disable

**Default:** `true` (enabled with adaptive algorithm)

**Examples:**
```csharp
// Keep default (recommended - adaptive lazy resolution)
opt.RegisterTasksFromAssembly(typeof(Program).Assembly)

// Explicitly enable (same as default)
opt.SetUseLazyHandlerResolution(true)

// Disable lazy resolution (handlers kept in memory)
opt.SetUseLazyHandlerResolution(false)

// Convenience method for disabling
opt.DisableLazyHandlerResolution()
```

**Adaptive Algorithm:**

When enabled, EverTask automatically chooses the best resolution strategy:

- **Immediate tasks**: Lazy mode (v3.7+: the worker resolves a fresh handler in its per-task scope; an eager instance resolved at dispatch would stay pinned in the root container until shutdown)
- **Recurring tasks with intervals ≥ 5 minutes**: Lazy mode (memory efficient)
- **Recurring tasks with intervals < 5 minutes**: Eager mode (performance efficient)
- **Delayed tasks with delay ≥ 30 minutes**: Lazy mode
- **Delayed tasks with delay < 30 minutes**: Eager mode

**Benefits:**
- **Memory Optimization**: Handlers are disposed after dispatch, reducing memory footprint for long-running scheduled tasks
- **Fresh Dependencies**: Handlers get fresh scoped services at execution time (important for DbContext, etc.)
- **Automatic Tuning**: Adaptive algorithm balances memory and performance

**When to Disable:**

Only disable lazy resolution if:
- You have handlers with expensive initialization that should be cached
- Your environment has issues with lazy resolution (rare)
- You're debugging handler lifecycle issues

**Performance Impact:**

- **Memory**: Up to 43,000 fewer handler allocations per day for high-frequency recurring tasks
- **CPU**: Negligible overhead (handler instantiation is fast with DI)

**Notes:**
- Handler dependencies are resolved at execution time, ensuring fresh scoped services
- At dispatch time, a short-lived metadata instance is resolved (and disposed with its scope) to extract handler options

### SetRateLimiterOptions

Configures the global infrastructure knobs of the keyed rate limiter (v3.7+). See the dedicated [Rate Limiting Configuration](#rate-limiting-configuration) section below for the full reference (global knobs, per-handler `RateLimitPolicy`, key source).

## Queue Configuration

You can set up multiple queues to isolate different types of work and give them different priorities or resource allocations.

### ConfigureDefaultQueue

Customizes the default queue (used when you don't specify a queue name for a task).

**Signature:**
```csharp
ConfigureDefaultQueue(Action<QueueConfiguration> configure)
```

**Example:**
```csharp
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(1000)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(5))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))))
```

### AddQueue

Creates a new named queue with its own configuration.

**Signature:**
```csharp
AddQueue(string name, Action<QueueConfiguration>? configure = null)
```

**Parameters:**
- `name` (string): Queue name (throws `ArgumentException` when null/whitespace)
- `configure` (Action, optional): Queue configuration

**Defaults for new queues** (different from the auto-created `default` queue, which inherits the global settings with `QueueFullBehavior.Wait`):
- `MaxDegreeOfParallelism` = **1** (sequential: set it explicitly for parallel consumption)
- Channel capacity = **500** (`FullMode.Wait`)
- `QueueFullBehavior` = **`FallbackToDefault`** (a full queue spills to the default queue)
- Retry policy / timeout = unset (fall back to the global defaults)

Calling `AddQueue` again with the same name **replaces** the previous configuration (no error is raised).

**Example:**
```csharp
.AddQueue("high-priority", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait))

.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(100)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))
```

### ConfigureRecurringQueue

Customizes the recurring queue (EverTask automatically creates this queue for recurring tasks).

**Signature:**
```csharp
ConfigureRecurringQueue(Action<QueueConfiguration> configure)
```

**Example:**
```csharp
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(200)
    .SetDefaultTimeout(TimeSpan.FromMinutes(10)))
```

### QueueConfiguration Methods

Each queue supports these configuration methods:

```csharp
// Parallelism
SetMaxDegreeOfParallelism(int parallelism)

// Capacity
SetChannelCapacity(int capacity)

// Full channel options replacement (FullMode, SingleReader/Writer, ...)
SetChannelOptions(BoundedChannelOptions options)

// Full behavior
SetFullBehavior(QueueFullBehavior behavior)

// Timeout (null reverts to the global default)
SetDefaultTimeout(TimeSpan? timeout)

// Retry policy (null reverts to the global default)
SetDefaultRetryPolicy(IRetryPolicy? policy)
```

Per-queue retry/timeout resolution chain (v3.7+): **handler override → queue default → global default**. The queue is the task's *declared* queue: a task rerouted by `FallbackToDefault` keeps its declared queue's retry/timeout.

## Rate Limiting Configuration

Keyed rate limiting (v3.7+) constrains how often tasks of a type execute **per key** (tenant, account, external resource). Behavior, semantics, and edge cases are documented in [Keyed Rate Limiting](rate-limiting.md); this section covers the configuration surface.

Configuration lives in three places:

1. **Per-handler policy**: the `RateLimitPolicy` property on the handler declares the limit.
2. **Key source**: the task implements `IRateLimitedTask`, or the handler overrides `GetRateLimitKey`.
3. **Global knobs**: `SetRateLimiterOptions` bounds the limiter infrastructure.

### RateLimitPolicy (per handler)

Declares the per-key execution budget for a task type.

**Declaration:**
```csharp
public class SyncTenantHandler : EverTaskHandler<SyncTenantTask>
{
    // 15 executions per minute PER KEY
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(permits: 15, period: TimeSpan.FromMinutes(1))
        {
            Burst                 = 15,
            ThrottleRetries       = true,
            StartEmpty            = false,
            MaxReservationHorizon = TimeSpan.FromHours(1),
            MaxInSlotWait         = TimeSpan.FromSeconds(1),
            OverflowBehavior      = RateLimitOverflowBehavior.WaitForCapacity
        };

    public override async Task Handle(SyncTenantTask task, CancellationToken ct) { ... }
}
```

**Constructor:**
- `permits` (int): executions allowed per `period`. Must be > 0.
- `period` (TimeSpan): the budget window. Must be > 0.

**Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Burst` | `int` | `Permits` | Burst tolerance (≥ 1). `1` = strict even spacing (`Period / Permits` between executions); `Permits` = the full budget can front-load |
| `ThrottleRetries` | `bool` | `true` | Retry attempts re-acquire the key's budget (the wait happens between attempts, before the per-attempt timeout starts) |
| `StartEmpty` | `bool` | `false` | Fresh buckets admit at the steady rate instead of starting with full burst, capping the post-restart burst |
| `MaxReservationHorizon` | `TimeSpan` | `1 hour` | Slots farther than this are never parked: terminal rejection (one-shot → `Failed` + `OnError`; recurring → occurrence skipped) |
| `MaxInSlotWait` | `TimeSpan` | `1 second` | Slots nearer than this are awaited inline by the worker instead of re-parking |
| `OverflowBehavior` | `RateLimitOverflowBehavior` | `WaitForCapacity` | `WaitForCapacity` defers over-budget tasks to their reserved slot; `Discard` terminally rejects them (`Failed` + `OnError`) |

**Notes:**
- The policy is read **once per handler type** (first-wins cache); changing it requires a restart.
- A policy without a key (see below) logs a warning once per task type and executes **ungated** (fail-safe).

### Rate-Limit Key Source

The key is derived **per dispatch** from the task:

```csharp
// Option A: the task declares its key
public record SyncTenantTask(Guid TenantId) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId.ToString();
}

// Option B: the handler derives the key (overrides IRateLimitedTask if both present)
public class SyncTenantHandler : EverTaskHandler<SyncTenantTask>
{
    public override string? GetRateLimitKey(SyncTenantTask task) => task.TenantId.ToString();
}
```

Keep keys low-cardinality and stable (tenant ids, account ids); see [Best Practices](rate-limiting.md#best-practices).

### SetRateLimiterOptions (global knobs)

Bounds the limiter infrastructure process-wide. These are safety valves, not per-task limits.

**Signature:**
```csharp
SetRateLimiterOptions(Action<RateLimiterOptions> configure)
```

**Example:**
```csharp
opt.SetRateLimiterOptions(o =>
{
    o.MaxParkedTasks     = 5000;
    o.MaxTrackedKeys     = 100_000;
    o.MaxKeyLength       = 256;
    o.EmitDeferralEvents = true;
});
```

**Options:**

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `MaxParkedTasks` | `int` | `min(5000, 2 × default-queue channel capacity)` | Cap of DISTINCT rate-limited tasks parked waiting for budget; at the cap, consumers pause (bounded) before dequeued rate-limited tasks of the affected queues (unthrottled traffic keeps flowing), so backpressure reaches producers |
| `MaxTrackedKeys` | `int` | `100,000` | Maximum (task type, key) buckets tracked in memory; new keys beyond the cap fail OPEN (execute unthrottled) with a warning and a monitoring event |
| `MaxKeyLength` | `int` | `256` | Keys longer than this are hashed (SHA-256) before use |
| `EmitDeferralEvents` | `bool` | `true` | Publish deferral monitoring events (aggregated at the source: first deferral per key per window plus periodic summaries) |

**Notes:**
- The `MaxParkedTasks` default is computed at first resolution, after builder methods like `ConfigureDefaultQueue(q => q.SetChannelCapacity(...))` have run.
- The rate limiter is per-instance (in-memory): N app instances each enforce the limit independently; see [Multi-Instance](rate-limiting.md#multi-instance).

## Storage Configuration

Choose where EverTask saves task data.

### AddMemoryStorage

Uses in-memory storage (fine for development/testing, but tasks won't survive a restart).

**Signature:**
```csharp
AddMemoryStorage()
```

**Example:**
```csharp
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage();
```

**Characteristics:**
- No external dependencies
- Fast performance
- Tasks lost on restart

### AddSqlServerStorage

Uses SQL Server for persistent storage.

**Signature:**
```csharp
AddSqlServerStorage(string connectionString, Action<SqlServerTaskStoreOptions>? configure = null)
```

**Parameters:**
- `connectionString` (string): SQL Server connection string
- `configure` (Action, optional): Storage configuration options

**Examples:**
```csharp
// Basic
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True;")

// With options
.AddSqlServerStorage(
    connectionString,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = true;
    })
```

**SqlServerTaskStoreOptions Properties:**
- `SchemaName` (string?): Database schema name (default: "EverTask", null = main schema)
- `AutoApplyMigrations` (bool): Auto-apply EF Core migrations (default: true)

### AddSqliteStorage

Uses SQLite for persistent storage.

**Signature:**
```csharp
AddSqliteStorage(string connectionString = "Data Source=EverTask.db",
                 Action<SqliteTaskStoreOptions>? configure = null)
```

**Parameters:**
- `connectionString` (string, optional): SQLite connection string; defaults to `"Data Source=EverTask.db"`, so `.AddSqliteStorage()` with no arguments is valid
- `configure` (Action, optional): Storage configuration options

**Examples:**
```csharp
// Zero-config (Data Source=EverTask.db)
.AddSqliteStorage()

// Basic
.AddSqliteStorage("Data Source=evertask.db")

// With options
.AddSqliteStorage(
    "Data Source=evertask.db;Cache=Shared;",
    opt =>
    {
        opt.AutoApplyMigrations = true;
    })
```

**Notes:**
- `SchemaName` must remain an empty string (`""`): SQLite has no schema concept, do not change it

## Logging Configuration

### AddSerilog

Integrates Serilog for structured logging throughout EverTask.

**Package:** `EverTask.Logging.Serilog`

**Signature:**
```csharp
AddSerilog(Action<LoggerConfiguration>? configure = null)
```

**Parameters:**
- `configure` (Action, optional): Serilog logger configuration; calling `.AddSerilog()` with no arguments configures a Console-only sink

**Example:**
```csharp
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
```

**appsettings.json Example:**
```json
{
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/evertask-.txt",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 10
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
    "Properties": {
      "Application": "MyApp"
    }
  }
}
```

### WithPersistentLogger

**Available since:** v3.0

Configures persistent handler logging options. When enabled, logs written via `Logger` property in handlers are stored in the database for audit trails.

**Important:** Logs are ALWAYS forwarded to ILogger infrastructure (console, file, Serilog, etc.) regardless of this setting. This option only controls database persistence.

**Signature:**
```csharp
WithPersistentLogger(Action<PersistentLoggerOptions> configure)
```

**Parameters:**
- `configure` (Action): Configuration action for persistent logger options

**Default:** Disabled

**Example:**
```csharp
.AddEverTask(opt => opt
    .WithPersistentLogger(log => log
        .SetMinimumLevel(LogLevel.Information)
        .SetMaxLogsPerTask(1000)))
```

**Note:** Calling `.WithPersistentLogger()` automatically enables database persistence. You don't need to call `.Enable()`.

**PersistentLoggerOptions Methods:**

#### Disable()
Disables persistent logging to the database (logs still go to ILogger). Use this if you want to temporarily disable persistence.
```csharp
.WithPersistentLogger(log => log.Disable())
```

#### SetMinimumLevel(LogLevel level)
Sets the minimum log level for database persistence. Logs below this level are not stored in the database but are still forwarded to ILogger.

**Parameters:**
- `level` (LogLevel): Minimum level to persist (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`)

**Default:** `LogLevel.Information`

**Example:**
```csharp
.WithPersistentLogger(log => log
    .SetMinimumLevel(LogLevel.Warning)) // Only persist Warning and above
```

**Note:** This only affects database persistence. ILogger receives all log levels regardless of this setting.

#### SetMaxLogsPerTask(int? maxLogs)
Sets the maximum number of logs to persist per task execution. Once this limit is reached, additional logs are not persisted (but still forwarded to ILogger).

**Parameters:**
- `maxLogs` (int?): Maximum logs to persist. `null` = unlimited (not recommended for production)

**Default:** `1000`

**Example:**
```csharp
.WithPersistentLogger(log => log
    .SetMaxLogsPerTask(500)) // Limit to 500 logs
```

**Performance:** ~100 bytes per log in memory during execution. Single bulk INSERT to database after task completion.

**Complete Example:**
```csharp
.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .WithPersistentLogger(log => log
        .SetMinimumLevel(LogLevel.Information)
        .SetMaxLogsPerTask(1000)))
```

## Monitoring Configuration

### AddMonitoringApi

Adds EverTask Monitoring API with optional embedded React dashboard for comprehensive task monitoring, analytics, and management.

**Package:** `EverTask.Monitor.Api`

**Signature:**
```csharp
AddMonitoringApi()
AddMonitoringApi(Action<EverTaskApiOptions> configure)
```

**Parameters:**
- `configure` (Action): Configuration options for the monitoring API

**Examples:**

**Basic Setup (Default Settings):**
```csharp
.AddMonitoringApi()

// Dashboard: http://localhost:5000/evertask-monitoring
// API:       http://localhost:5000/evertask-monitoring/api
// Credentials: admin / admin
```

**Custom Configuration:**
```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI = true;
    options.Username = "monitor_user";
    options.Password = "secure_password_123";
    options.EnableAuthentication = true;
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[] { "https://myapp.com" };
})
```

**API-Only Mode (No Dashboard):**
```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI = false;  // Disable embedded dashboard
    options.EnableAuthentication = false;  // Open API for custom frontend
})
```

**Environment-Specific Configuration:**
```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI = true;

    if (builder.Environment.IsDevelopment())
    {
        // Development: No authentication
        options.EnableAuthentication = false;
    }
    else
    {
        // Production: Secure credentials from environment
        options.EnableAuthentication = true;
        options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME")
            ?? throw new InvalidOperationException("MONITOR_USERNAME not set");
        options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD")
            ?? throw new InvalidOperationException("MONITOR_PASSWORD not set");
        options.EnableCors = true;
        options.CorsAllowedOrigins = new[] { "https://app.example.com" };
    }
})
```

**EverTaskApiOptions Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableUI` | `bool` | `true` | Enable embedded React dashboard |
| `EnableSwagger` | `bool` | `false` | Enable Swagger/OpenAPI documentation |
| `Username` | `string` | `"admin"` | JWT Authentication username |
| `Password` | `string` | `"admin"` | JWT Authentication password (CHANGE IN PRODUCTION!) |
| `EnableAuthentication` | `bool` | `true` | Enable JWT Authentication |
| `JwtSecret` | `string?` | `null` | JWT signing key; when unset, a random 256-bit secret is generated per instance. Set it explicitly (≥ 32 bytes) for multi-instance deployments |
| `JwtIssuer` | `string` | `"EverTask.Monitor.Api"` | JWT issuer claim |
| `JwtAudience` | `string` | `"EverTask.Monitor.Api"` | JWT audience claim |
| `JwtExpirationHours` | `int` | `8` | JWT token TTL in hours |
| `EnableCors` | `bool` | `true` | Enable CORS for API endpoints |
| `CorsAllowedOrigins` | `string[]` | `[]` | CORS allowed origins (empty = allow all) |
| `AllowedIpAddresses` | `string[]` | `[]` | IP address whitelist (empty = allow all IPs). Supports IPv4, IPv6, and CIDR notation |
| `MagicLinkToken` | `string?` | `null` | Static token for magic link authentication. When set, enables instant access via `/api/auth/magic?token=...` |
| `EventDebounceMs` | `int` | `1000` | Debounce time in milliseconds for SignalR event-driven cache invalidation in the dashboard. Higher values reduce API load during task bursts but introduce slight UI update delays. Recommended: 300ms (very responsive), 500ms (balanced), 1000ms (conservative for high-volume) |

#### EnableUI

Controls whether the embedded React dashboard is served.

**Examples:**
```csharp
// Full mode (default): API + Dashboard
options.EnableUI = true;

// API-only mode: REST API without dashboard
options.EnableUI = false;
```

**Use Cases for API-Only Mode:**
- Building custom frontend applications
- Mobile app integration
- Third-party monitoring system integration
- Headless server environments

#### EnableSwagger

Controls whether Swagger/OpenAPI documentation is generated for the monitoring API.

When enabled, EverTask creates a **separate Swagger document** that includes only monitoring endpoints and automatically excludes them from your application's Swagger document.

**Examples:**
```csharp
// Enable Swagger for monitoring API
options.EnableSwagger = true;

// Configure SwaggerUI in your application
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "My Application API", Version = "v1" });
});

app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My Application API");
    c.SwaggerEndpoint("/swagger/evertask-monitoring/swagger.json", "EverTask Monitoring API");
});
```

**How It Works:**
- Swagger document name: `evertask-monitoring`
- Swagger JSON endpoint: `/swagger/evertask-monitoring/swagger.json`
- Includes only EverTask monitoring controllers (`/evertask-monitoring/api/*`)
- Your application's Swagger document automatically excludes EverTask endpoints
- No manual filtering or namespace predicates required

**Use Cases:**
- API documentation and exploration
- Integration with API clients and code generators
- Testing monitoring endpoints with Swagger UI
- API versioning and contract validation

#### Username / Password

JWT Authentication credentials for accessing the monitoring dashboard and API.

**Examples:**
```csharp
// Development (not recommended for production)
options.Username = "admin";
options.Password = "admin";

// Production: Environment variables
options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? "changeme";

// Production: Configuration
options.Username = configuration["Monitoring:Username"];
options.Password = configuration["Monitoring:Password"];
```

**Security Notes:**
- Always change default credentials in production
- Use environment variables or secure configuration systems
- Always use HTTPS when authentication is enabled
- Consider using anonymous read access for internal networks

#### EnableAuthentication

Controls whether JWT Authentication is required for API endpoints and SignalR hub.

**Examples:**
```csharp
// Require authentication (default, recommended for production)
options.EnableAuthentication = true;

// No authentication (development only)
options.EnableAuthentication = false;

// Environment-specific
options.EnableAuthentication = !builder.Environment.IsDevelopment();
```

**Protection Scope:**
- **API endpoints**: All `/api/*` endpoints (except login and config)
- **SignalR hub**: Real-time monitoring hub at `/hub`
- **UI**: Not protected by JWT (only IP whitelist, see `AllowedIpAddresses`)

**Always Accessible (No JWT Required):**
- `/api/config` - Dashboard configuration endpoint
- `/api/auth/login` - Login endpoint for obtaining JWT
- `/api/auth/validate` - Token validation endpoint
- UI static files (HTML, JS, CSS)

**JWT Authentication Flow:**
1. Client authenticates via `/api/auth/login` with username/password
2. Server returns JWT token
3. Client includes token in subsequent requests:
   - **API**: `Authorization: Bearer <token>` header
   - **SignalR**: `accessTokenFactory` option or `?access_token=<token>` query string

**Notes:**
- When disabled, all API and hub endpoints are publicly accessible (only IP whitelist applies)
- UI is always accessible (relies on IP whitelist for protection)
- JWT tokens expire after 8 hours by default (see `JwtExpirationHours`)

#### SignalRHubPath

The SignalR hub path is now fixed to `/evertask-monitoring/hub` and cannot be changed.

**Notes:**
- The hub path is readonly and set to `/evertask-monitoring/hub`
- SignalR monitoring is automatically configured if not already registered
- Dashboard automatically uses this fixed path for real-time updates

#### EnableCors

Enables CORS (Cross-Origin Resource Sharing) for API endpoints.

**Examples:**
```csharp
// Enable CORS (default)
options.EnableCors = true;

// Disable CORS
options.EnableCors = false;
```

**Notes:**
- Required when dashboard/frontend is hosted on different origin
- Required for custom frontend applications
- Not needed when API and frontend are on same origin

#### CorsAllowedOrigins

Specifies allowed origins for CORS requests.

**Examples:**
```csharp
// Allow all origins (default, useful for development)
options.CorsAllowedOrigins = Array.Empty<string>();

// Restrict to specific origins (production)
options.CorsAllowedOrigins = new[]
{
    "https://myapp.com",
    "https://dashboard.myapp.com"
};

// Environment-specific origins
options.CorsAllowedOrigins = builder.Environment.IsDevelopment()
    ? Array.Empty<string>()  // Allow all in development
    : new[] { "https://app.example.com" };  // Restrict in production
```

**Security Notes:**
- Empty array = allow all origins (convenient for development)
- Always restrict origins in production
- Use HTTPS origins in production

#### AllowedIpAddresses

Restricts monitoring access to specific IP addresses or CIDR ranges. Applies to both API endpoints and SignalR hub.

**Examples:**
```csharp
// Allow all IPs (default)
options.AllowedIpAddresses = Array.Empty<string>();

// Restrict to specific IPs (production)
options.AllowedIpAddresses = new[]
{
    "192.168.1.100",        // Specific admin workstation
    "10.0.0.0/8",           // Internal network (CIDR notation)
    "172.16.0.0/12",        // Another internal range
    "::1"                   // IPv6 localhost
};

// Reverse proxy scenario (public IP ranges)
options.AllowedIpAddresses = new[]
{
    "203.0.113.0/24"        // Office public IP range
};
```

**Features:**
- Supports **IPv4** and **IPv6** addresses
- Supports **CIDR notation** (e.g., `192.168.0.0/24`)
- Checks `X-Forwarded-For` header first (reverse proxy support)
- Returns **403 Forbidden** if IP not in whitelist
- IP check runs **before authentication** (more efficient)

**Security Notes:**
- Empty array = **allow all IPs** (default, suitable for internal networks)
- Always configure in production when exposed to internet
- Works with reverse proxies (nginx, IIS, etc.)
- Protects both API and SignalR hub endpoints
- More efficient than firewall rules at application level

**Reverse Proxy Configuration:**
When behind a reverse proxy, ensure `X-Forwarded-For` header is set:
```nginx
# Nginx example
location /evertask-monitoring {
    proxy_pass http://localhost:5000/evertask-monitoring;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

#### MagicLinkToken

Enables instant authentication via a static token URL. Useful for embedding the dashboard in other systems or providing quick access without credential management.

**Examples:**
```csharp
// Enable magic link access
options.MagicLinkToken = "your-very-long-secret-token-here-min-32-chars";

// Combined with IP whitelist for extra security
options.MagicLinkToken = "your-secret-token";
options.AllowedIpAddresses = new[] { "10.0.0.0/8" };
```

**Access URL:**
```
https://your-server/evertask-monitoring/magic?token=your-very-long-secret-token-here-min-32-chars
```

**How it works:**
1. User visits the magic link URL
2. Backend validates the token against `MagicLinkToken`
3. If valid, generates a standard JWT session token
4. User is redirected to the dashboard, fully authenticated

**Security Notes:**
- Use a long, random token (32+ characters recommended)
- Token never expires - change it in configuration to revoke all magic link access
- Combine with `AllowedIpAddresses` for defense in depth
- If `MagicLinkToken` is not set, the endpoint returns 404

**Token Generation:**
```powershell
# PowerShell - generate secure random token
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

### API Endpoints

Once configured, the monitoring API provides REST endpoints for task querying, statistics, and analytics. All endpoints are relative to `{BasePath}/api` (default: `/evertask-monitoring/api`).

**Key Endpoints:**
- `GET /tasks` - Paginated task list with filtering
- `GET /tasks/{id}` - Task details
- `GET /tasks/{id}/status-audit` - Status change history
- `GET /tasks/{id}/runs-audit` - Execution history
- `GET /dashboard/overview` - Dashboard statistics
- `GET /queues` - Queue metrics
- `GET /statistics/success-rate-trend` - Success rate trends

See [Monitoring Dashboard](monitoring-dashboard.md) for complete API documentation.

### Dashboard Features

When `EnableUI` is true, the embedded React dashboard provides:

- **Overview Dashboard**: Total tasks, success rate, active queues, execution times
- **Task List**: Filtering, sorting, pagination, status filters
- **Task Details**: Complete information, execution history, error details
- **Queue Metrics**: Per-queue statistics and health monitoring
- **Analytics**: Success rate trends, task type distribution, execution times
- **Real-Time Updates**: Live task updates via SignalR

### Mapping Endpoints

After configuring the monitoring API, map the endpoints in your application:

```csharp
var app = builder.Build();

// Map EverTask monitoring endpoints (includes SignalR hub automatically)
app.MapEverTaskApi();

app.Run();
```

`MapEverTaskApi()`:
- Maps SignalR monitoring hub (at `/evertask-monitoring/hub`) with automatic JWT authentication
- Maps all API controllers
- Serves embedded dashboard (if `EnableUI` is true)
- Configures authentication middleware
- Applies CORS policy (if enabled)

**Important Notes:**
- The monitoring API handles SignalR setup completely autonomously:
  - `AddMonitoringApi()` automatically registers SignalR monitoring services (if not already registered)
  - `MapEverTaskApi()` automatically maps the SignalR hub endpoint with authentication
  - No additional SignalR configuration is required unless you want to customize hub options
- To customize hub options, pass an `Action<HttpConnectionDispatcherOptions>` to `MapEverTaskApi()`:
  ```csharp
  app.MapEverTaskApi(hubOptions => {
      // Custom SignalR hub configuration
      hubOptions.TransportMaxBufferSize = 1024 * 1024; // 1MB buffer
      hubOptions.ApplicationMaxBufferSize = 1024 * 1024;
  });
  ```

### Integration with SignalR

The monitoring API automatically configures SignalR monitoring if it hasn't been added:

```csharp
// This is sufficient - SignalR is auto-configured
.AddMonitoringApi()

// Manual SignalR configuration (if you need more control)
.AddSignalRMonitoring(opt =>
{
    opt.IncludeExecutionLogs = true;  // Include logs in SignalR events
})
.AddMonitoringApi()
// Note: SignalRHubPath is now fixed to "/evertask-monitoring/hub" and cannot be changed
```

### AddSignalRMonitoring

Enables real-time task monitoring via SignalR.

**Package:** `EverTask.Monitor.AspnetCore.SignalR`

**Signature:**
```csharp
AddSignalRMonitoring()
AddSignalRMonitoring(Action<SignalRMonitoringOptions> configure)
AddSignalRMonitoring(Action<HubOptions> hubOptions)
AddSignalRMonitoring(Action<HubOptions> hubOptions, Action<SignalRMonitoringOptions> configure)
```

**Parameters:**
- `configure` (Action): Monitoring configuration options (`SignalRMonitoringOptions`)
- `hubOptions` (Action): SignalR `HubOptions` customization

**Examples:**
```csharp
// Basic (default configuration)
.AddSignalRMonitoring()

// With execution log streaming enabled
.AddSignalRMonitoring(opt =>
{
    opt.IncludeExecutionLogs = true;  // Stream logs to SignalR clients (increases bandwidth)
})
```

**SignalRMonitoringOptions Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `IncludeExecutionLogs` | `bool` | `false` | Include execution logs in SignalR events (increases message size) |

**Standalone usage (without Monitor.Api):**

When using the SignalR package without `AddMonitoringApi()`/`MapEverTaskApi()`, you MUST map the hub yourself or no events are broadcast:

```csharp
app.MapEverTaskMonitorHub();  // default route /evertask-monitoring/hub; a custom pattern overload exists
```

**Important Notes:**

- **Hub Route**: Fixed at `/evertask-monitoring/hub` (automatically mapped by `MapEverTaskApi()`; standalone via `MapEverTaskMonitorHub()`)
- **Log Streaming**: Execution logs are always available via ILogger and database persistence (if enabled)
- **Performance Impact**: Enabling `IncludeExecutionLogs` significantly increases SignalR message size and network bandwidth
- **Use Case**: Enable only when you need real-time log streaming to monitoring dashboards

**Client-Side Setup:**

```html
<!-- Add SignalR client library -->
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>

<script>
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/evertask-monitoring/hub")  // Fixed hub route
    .withAutomaticReconnect()
    .build();

connection.on("EverTaskEvent", (eventData) => {
    console.log("Task event:", eventData);
    // eventData.TaskId, eventData.Severity, eventData.Message, eventData.Exception, etc.
});

connection.start()
    .then(() => console.log("SignalR connected"))
    .catch(err => console.error("SignalR connection error:", err));
</script>
```

**Event Data Structure:**

```javascript
{
    "TaskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "EventDateUtc": "2024-10-19T16:10:20Z",
    "Severity": "Information",  // "Information" | "Warning" | "Error"
    "TaskType": "MyApp.Tasks.SendEmailTask",
    "TaskHandlerType": "MyApp.Tasks.SendEmailHandler",
    "TaskParameters": "{\"Email\":\"user@example.com\"}",
    "Message": "Task completed successfully",
    "Exception": null  // Stack trace if task failed
}
```

**Severity Levels:**
- `Information`: Task started, completed, or scheduled
- `Warning`: Task cancelled or timed out
- `Error`: Task failed with exception

## Storage Provider Details

### SQL Server Storage Options

**Package:** `EverTask.Storage.SqlServer`

**Advanced Configuration:**

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    // Schema name (default: "EverTask", null = main schema)
    opt.SchemaName = "EverTask";

    // Auto-apply migrations (default: true)
    opt.AutoApplyMigrations = true;

    // Connection pooling (enabled by default in v2.0+)
    // Uses DbContextFactory for 30-50% performance improvement

    // Stored procedures (enabled by default in v2.0+)
    // Reduces roundtrips for status updates
})
```

**Manual Migrations:**

For production environments, apply migrations manually:

```bash
# Generate migration script (run from src/Storage/EverTask.Storage.SqlServer/)
dotnet ef migrations script --context SqlServerTaskStoreContext --output migration.sql

# Apply via your deployment pipeline
sqlcmd -S localhost -d EverTaskDb -i migration.sql
```

**Stored Procedures:**

EverTask uses stored procedures for critical operations:

- `[EverTask].[usp_SetTaskStatus]` (v2.0+): Atomic status update + audit insert
- `[EverTask].[usp_UpdateCurrentRun]` (v3.6+): Single-roundtrip recurring-run update
- Performance: 50% fewer roundtrips for status changes

**Connection String Options:**

```csharp
// Basic
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;"

// With pooling (recommended)
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;Min Pool Size=5;Max Pool Size=100;"

// Azure SQL
"Server=tcp:yourserver.database.windows.net,1433;Database=EverTaskDb;User ID=user;Password=pass;Encrypt=True;"
```

**Schema Customization:**

```sql
-- Custom schema
CREATE SCHEMA [CustomSchema]
GO

-- Configure in code
opt.SchemaName = "CustomSchema";
```

### SQLite Storage Options

**Package:** `EverTask.Storage.Sqlite`

**Advanced Configuration:**

```csharp
.AddSqliteStorage(connectionString, opt =>
{
    // Auto-apply migrations (default: true)
    opt.AutoApplyMigrations = true;

    // Note: SchemaName must remain "" (empty string): SQLite has no schema concept
})
```

**Connection String Options:**

```csharp
// Basic
"Data Source=evertask.db"

// In-memory (for testing)
"Data Source=:memory:"

// Shared cache
"Data Source=evertask.db;Cache=Shared;"

// Full options
"Data Source=evertask.db;Mode=ReadWriteCreate;Cache=Shared;Foreign Keys=True;"
```

**Performance Tuning:**

```sql
-- WAL mode for better concurrency
PRAGMA journal_mode=WAL;

-- Optimize for performance
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=10000;
PRAGMA temp_store=MEMORY;
```

**Limitations:**
- No schema support (unlike SQL Server)
- Not recommended for high-concurrency scenarios (>100 tasks/sec)
- Best for: Single-server deployments, development, small workloads

## Handler Configuration

You can configure behavior at the handler level to override global defaults.

### Handler Properties

All handler-level settings are virtual properties to override. They are get-only: assigning them in a constructor does not compile.

```csharp
public class MyHandler : EverTaskHandler<MyTask>
{
    // Timeout
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(10);

    // Retry policy
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));

    // Queue routing
    public override string? QueueName => "high-priority";

    // Per-key rate limiting (v3.7+), see rate-limiting.md
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(15, TimeSpan.FromMinutes(1));

    public override async Task Handle(MyTask task, CancellationToken cancellationToken)
    {
        // Handler logic
    }
}
```

**Available Properties:**
- `Timeout` (TimeSpan?): Handler-specific timeout (falls back to queue, then global default)
- `RetryPolicy` (IRetryPolicy?): Handler-specific retry policy (falls back to queue, then global default)
- `QueueName` (string?): Target queue for this handler
- `RateLimitPolicy` (RateLimitPolicy?): Per-key execution frequency constraint; the key comes from `IRateLimitedTask` on the task or a `GetRateLimitKey` override on the handler (see [Rate Limiting Configuration](#rate-limiting-configuration))

## Complete Examples

### Basic Configuration

The simplest setup for getting started:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();

var app = builder.Build();
app.Run();
```

### Production Configuration

A more robust setup with SQL Server storage, proper retry policies, and logging:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(5000)
       .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
       .SetDefaultTimeout(TimeSpan.FromMinutes(5))
       .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = false; // Manual migrations in production
    })
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        builder.Configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

### Multi-Queue Configuration

Isolating different workloads into separate queues:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(1000))

.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))))

.AddQueue("email", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(10000)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

.AddQueue("reports", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(50)
    .SetDefaultTimeout(TimeSpan.FromMinutes(30)))

.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(200))

.AddSqlServerStorage(connectionString);
```

### High-Performance Configuration

Optimized for handling massive workloads:

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: Environment.ProcessorCount)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(10000)
    .SetDefaultTimeout(TimeSpan.FromMinutes(10))
)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
    opt.AutoApplyMigrations = false;
});
```

### Multi-Assembly Configuration

When your task handlers are spread across multiple assemblies:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssemblies(
        typeof(CoreTasks.MyTask).Assembly,
        typeof(ApiTasks.MyTask).Assembly,
        typeof(BackgroundTasks.MyTask).Assembly)
       .SetMaxDegreeOfParallelism(20);
})
.AddSqlServerStorage(connectionString);
```

### Environment-Specific Configuration

Adjusting configuration based on your environment:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Storage methods extend the EverTask builder returned by AddEverTask,
// NOT IServiceCollection: keep a reference when branching by environment
var everTask = builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);

    if (builder.Environment.IsProduction())
    {
        opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
           .SetChannelOptions(10000)
           .SetDefaultTimeout(TimeSpan.FromMinutes(10));
    }
    else
    {
        opt.SetMaxDegreeOfParallelism(2)
           .SetChannelOptions(100);
    }
});

if (builder.Environment.IsProduction())
{
    everTask.AddSqlServerStorage(
        builder.Configuration.GetConnectionString("EverTaskDb")!,
        opt => opt.AutoApplyMigrations = false);
}
else
{
    everTask.AddMemoryStorage();
}
```

## Configuration Validation

What EverTask actually checks at startup:

**Errors:**
- No assemblies registered for handler scanning: `AddEverTask` throws `ArgumentException`
- Channel capacity < 1: `ArgumentOutOfRangeException` (raised by the BCL `BoundedChannelOptions` constructor)

**Warnings:**
- `MaxDegreeOfParallelism == 1`: a startup warning is logged (single consumer is usually a bad idea in production)

**Behaviors to be aware of (no error raised):**
- Re-adding a queue with an existing name silently **replaces** the previous configuration
- `SetMaxDegreeOfParallelism` performs no validation: `0` fails later at runtime, negative values are treated as unlimited by `ParallelOptions`

## Performance Tuning Guidelines

### CPU-Bound Tasks

If your tasks do heavy computation, match your parallelism to your CPU cores:

```csharp
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount) // Match CPU cores
   .SetChannelOptions(100); // Small queue
```

### I/O-Bound Tasks

If your tasks spend most of their time waiting on I/O (database, APIs, files), you can run many more in parallel:

```csharp
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4) // Higher parallelism
   .SetChannelOptions(5000); // Larger queue
```

### Mixed Workloads

When you have different types of tasks, use separate queues:

```csharp
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 2))

.AddQueue("cpu-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount))

.AddQueue("io-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4))
```

### Extreme High Load

For truly massive workloads, enable the sharded scheduler:

```csharp
opt.UseShardedScheduler(Environment.ProcessorCount)
   .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
   .SetChannelOptions(10000);
```

## Next Steps

- **[Getting Started](getting-started.md)** - Setup guide
- **[Scalability](scalability.md)** - Multi-queue and sharded scheduler
- **[Resilience](resilience.md)** - Retry policies and timeouts
- **[Storage](storage.md)** - Storage options and configuration
