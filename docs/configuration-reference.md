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
- [Dispatch Parameters](#dispatch-parameters)
- [Recurring Task Builder](#recurring-task-builder)
- [Runtime Schedule Management](#runtime-schedule-management)
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

// Custom configuration (keep FullMode = Wait; see warning below)
opt.SetChannelOptions(new BoundedChannelOptions(5000)
{
    FullMode = BoundedChannelFullMode.Wait
})
```

**FullMode Options:**
- `Wait`: Block until space is available (default). **The only mode EverTask's queue-full handling supports**
- `DropWrite` / `DropOldest` / `DropNewest`: ⚠ **Not recommended.** EverTask's queue-full detection and the scheduler's backoff/`QueueFullBehavior` rely on `TryWrite` rejecting when the channel is full. With `Drop*` modes `TryWrite` **never rejects**, so a write is treated as a successful enqueue even when the channel silently drops the item: the `QueueFull` signal (and the scheduler backoff that depends on it) never fires. A dropped task is **not silently lost**, though: the channel's `itemDropped` callback releases the delivery registration and reverts the victim's storage row to `WaitingQueue`, so startup recovery re-queues it later, but it will **not** run in the current process and there is no immediate backpressure. Use `Wait` (and tune capacity / `MaxDegreeOfParallelism`) instead of a `Drop*` mode.

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

// Exponential backoff: 500ms, 1s, 2s, 4s, 8s
opt.SetDefaultRetryPolicy(new ExponentialRetryPolicy(5, TimeSpan.FromMilliseconds(500)))

// Exponential backoff with cap and jitter: 1s, 3s, 9s, 10s, 10s (±20% jitter, still capped)
opt.SetDefaultRetryPolicy(new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1),
    backoffFactor: 3.0, maxDelay: TimeSpan.FromSeconds(10), useJitter: true))

// Custom retry policy (your own IRetryPolicy implementation; see
// resilience/retry-policies.md)
opt.SetDefaultRetryPolicy(new MyCustomRetryPolicy())
```

**Notes:**
- Built-in policies: `LinearRetryPolicy` (fixed delay or per-attempt delay array) and `ExponentialRetryPolicy` (growing delay: `initialDelay × backoffFactor^(n-1)`, optional `maxDelay` cap, optional ±20% per-attempt jitter). `retryCount` is the number of retries AFTER the initial attempt (e.g. `LinearRetryPolicy(3, ...)` = up to 4 executions); `retryCount` and delays must be greater than zero, `backoffFactor` must be >= 1.0 (1.0 behaves like linear), and `maxDelay` (when set) must be >= `initialDelay`. Without a `maxDelay`, exponential growth is clamped at the longest wait `Task.Delay` accepts (about 49.7 days); jitter respects the same ceiling. `LinearRetryPolicy` rejects explicit delays above that ceiling at construction, timeouts and audit cleanup intervals are clamped to it, and the bundled analyzer reports **ET0009** on constant over-limit values.
- Retries cannot be disabled via the built-in policies: to disable them, implement a trivial `IRetryPolicy` that invokes the action once; see [Custom Retry Policies](resilience/retry-policies.md#custom-retry-policies).

**Exception filtering** (fluent, shared by `LinearRetryPolicy` and `ExponentialRetryPolicy` via `RetryPolicyBase<TPolicy>`): by default every exception is retried **except** `OperationCanceledException` and `TimeoutException`, which are always fail-fast (hardcoded, cannot be overridden by a filter). Configure which exceptions retry with one of these modes (whitelist and blacklist **cannot** be mixed: doing so throws `InvalidOperationException`):

```csharp
.Handle<DbException>().Handle<HttpRequestException>()        // whitelist: retry ONLY these (+ derived)
.DoNotHandle<ArgumentException>()                            // blacklist: retry all EXCEPT these
.HandleWhen(ex => ex is HttpRequestException h && (int?)h.StatusCode >= 500)  // predicate (highest priority)
.HandleTransientDatabaseErrors()                             // preset: DbException (+ TimeoutException, but it's blocked by the fail-fast guard → effectively DbException only)
.HandleTransientNetworkErrors()                              // preset: HttpRequestException, SocketException, WebException, TaskCanceledException (⚠ TaskCanceledException : OperationCanceledException → blocked by the fail-fast guard, never actually retried)
.HandleAllTransientErrors()                                  // both presets combined
```

Resolution priority: OCE/TimeoutException fail-fast → `HandleWhen` → whitelist → blacklist → retry-all. Because the OCE/TimeoutException guard runs **first**, any preset entry that is (or derives from) those types (`TaskCanceledException`, `TimeoutException`) is never retried even though it appears in the preset. See [Resilience › Exception Filtering](resilience/exception-filtering.md).

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
  - `Full` (default): Complete audit trail: `StatusAudit` for all status transitions and `RunsAudit` for every run
  - `Minimal`: `StatusAudit` only on real errors; `RunsAudit` is **still written for every recurring run** (so run-frequency history is preserved) and `QueuedTask.LastExecutionUtc` is updated
  - `ErrorsOnly`: a `StatusAudit`/`RunsAudit` row is written only for a run that **records a non-empty exception string or ends in status `Failed`** (successful runs write neither; `QueuedTask` status is still updated to `Completed`)
  - `None`: no `StatusAudit`/`RunsAudit` rows at all

  > `Minimal` and `ErrorsOnly` differ **only** in `RunsAudit`: `Minimal` records every recurring run, `ErrorsOnly` only the runs with a non-empty exception string or status `Failed`. The authoritative rules are in `AuditPolicy.ShouldCreateStatusAudit` / `ShouldCreateRunsAudit`.

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
- For a recurring task running every 5 minutes: `Full` ≈ 1,152 audit records/day (StatusAudit + RunsAudit); `Minimal` ≈ 288 RunsAudit/day (one per successful run, no StatusAudit); `ErrorsOnly`/`None` ≈ 0 when executions succeed
- You can override this per task when dispatching
- Use lower levels (Minimal/ErrorsOnly/None) for high-frequency recurring tasks
- See [Audit Configuration](storage/audit-configuration.md) for detailed usage guide

### SetMisfireThreshold

Sets how late a delivery may start before its execution context reports it as a misfire.

**Signature:**
```csharp
SetMisfireThreshold(TimeSpan threshold)
```

**Parameters:**
- `threshold` (TimeSpan): tolerance between the nominal slot (`ITaskExecutionContext.ScheduledAtUtc`) and the actual start (`StartedAtUtc`). Must not be negative; `TimeSpan.Zero` reports every delivery that starts after its slot.

**Default:** 5 seconds

**Examples:**
```csharp
// A handler that compensates for lateness wants to know early
opt.SetMisfireThreshold(TimeSpan.FromMilliseconds(500))

// A nightly report does not care about a minute of scheduler drift
opt.SetMisfireThreshold(TimeSpan.FromMinutes(5))
```

**Notes:**
- This is a **classification** threshold. It decides what gets *reported* about a delivery or persisted as durable-occurrence misfire metadata, never whether anything runs: a late task runs exactly as it did before, and no status, retry or execution decision depends on it.
- Below the threshold `Misfire` is `null`, so a handler that does not care never has to inspect a kind. Above it, `Misfire.Kind` is `Late` and `Misfire.Lateness` is the real gap.
- It is the same threshold a **durable schedule** applies one step earlier, when it materializes an occurrence: a slot that came due longer ago than this produces an occurrence stamped with the backlog it stands for (`Misfire.Kind` `CatchUp` or `FireOnce`, plus the missed range and count), while a slot inside it produces an ordinary occurrence. See [Durable Occurrences](recurring-tasks/durable-occurrences.md).
- A run of **more than one** missed slot is reported whatever the threshold says. `FireOnce` is about to collapse those slots and `CatchUp` to replay them, and neither may happen unreported just because the grid ticks faster than the tolerance.
- A task dispatched to run immediately has no slot, so it can never be late.
- The one-second tolerance the recurring skip-forward path uses to avoid treating a just-scheduled occurrence as past is a **separate rule**, unchanged by this setting.
- See [Task Creation › Execution Context](task-creation.md#execution-context).

### SetDefaultScheduleTimeZone

Sets the time zone every calendar-anchored schedule is read on when it does not name one itself.

**Signature:**
```csharp
SetDefaultScheduleTimeZone(TimeZoneInfo timeZone)
```

**Parameters:**
- `timeZone` (TimeZoneInfo): a system time zone. Its IANA id is what gets persisted with each schedule, so the row resolves the same way on any host. A zone built with `TimeZoneInfo.CreateCustomTimeZone` has no such id and throws `ArgumentException`.

**Default:** `null`. Schedules with no zone of their own are computed in UTC, as they always were.

**Examples:**
```csharp
// One application, one zone
opt.SetDefaultScheduleTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome"))

// A single schedule can still opt out
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Asia/Tokyo")
```

**Notes:**
- It applies at dispatch, to schedules built through `Dispatch(task, r => ...)` that are **calendar-anchored** (days, weeks, months, `AtTime`/`AtTimes`, weekday and month selectors, cron) and did not call `InTimeZone`. An explicit `InTimeZone` always wins.
- A plain cadence (`Every(n).Seconds/Minutes/Hours`) is never touched: it is a constant step in elapsed time and produces identical instants in every zone, and `InTimeZone` on one throws.
- The zone is written **into the definition** when the schedule is built. Rows already stored keep whatever they were dispatched with, so changing this default later does not silently move existing schedules by an hour; re-register them under the same `taskKey` to move them.
- See [Recurring Tasks › Time Zones](recurring-tasks/time-zones.md) for daylight-saving behaviour and the id rules.

### SetMaterializationConcurrency

Sets how many durable schedules may be turning due slots into occurrence rows at the same time.

**Signature:**
```csharp
SetMaterializationConcurrency(int concurrency)
```

**Parameters:**
- `concurrency` (int): maximum concurrent materializations. Must be at least 1.

**Default:** the value of `SetMaxDegreeOfParallelism`, which is also what bounds startup recovery. It is
resolved lazily, so setting the parallelism after this call still takes effect.

**Examples:**
```csharp
// A restart with many durable schedules puts more pressure on the database than the work itself
opt.SetMaterializationConcurrency(4)
```

**Notes:**
- Materialization is a short burst of storage writes, so this bounds how much of that the store sees at once.
- It is **not** how many occurrences execute concurrently — that is the queue's parallelism — and not how many
  may be alive per schedule, which is `CatchUpOptions.MaxPendingOccurrences`.
- See [Recurring Tasks › Durable Occurrences](recurring-tasks/durable-occurrences.md).

### SetBacklogRetryInterval

Sets how long a durable schedule waits before trying again when it could not make progress.

**Signature:**
```csharp
SetBacklogRetryInterval(TimeSpan interval)
```

**Parameters:**
- `interval` (TimeSpan): the retry interval. Must be at least one second and at most one day; anything outside
  that range throws `ArgumentOutOfRangeException`.

**Default:** 1 minute

**Examples:**
```csharp
// A schedule with a large backlog and a wide MaxPendingOccurrences drains faster on a shorter retry
opt.SetBacklogRetryInterval(TimeSpan.FromSeconds(15))
```

**Notes:**
- A schedule cannot make progress when its concurrency budget is full, or when one of its compare-and-swapped
  writes lost a race and the run has to look at the row again. The ordinary way it resumes is the kick each
  occurrence gives when it ends, which is immediate; this interval is the guarantee behind that kick.
- Startup recovery runs once, so without a retry a schedule whose kick was lost would wait for the next
  restart.
- A **halted** catch-up is the one thing this interval does not cover. A halt never releases itself, not by
  ageing and not by restarting, so retrying it would put the schedule row back through the worker queue every
  interval for ever: a status transition and an audit row each time, to produce nothing. Only
  an explicit schedule change releases one — `ResumeSchedule`, `Reschedule`, or dispatching the series again
  after a cancel.
- Below the scheduler's own one-second tick it buys nothing, which is the lower bound.
- The upper bound is one day. This interval is the last thing between a blocked schedule and the next restart,
  so a value measured in weeks guarantees nothing. It is also added to a UTC instant at every re-park,
  including on the failure path, which repeats the same addition: an interval of centuries overflows both, and
  the schedule then sits parked nowhere at all.
- See [Recurring Tasks › Durable Occurrences](recurring-tasks/durable-occurrences.md).

### SetOccurrenceProviderRetry

Sets how long a schedule waits before asking an `INextOccurrenceProvider` that could not answer again.

**Signature:**
```csharp
SetOccurrenceProviderRetry(Action<OccurrenceProviderRetryOptions> configure)
```

**Options:**
- `InitialBackoff` (TimeSpan): the wait after the first failure. Default: 1 minute.
- `MaxBackoff` (TimeSpan): the longest wait the doubling reaches. Default: 15 minutes.

Both must be positive and at most one day; anything else throws `ArgumentOutOfRangeException`. A `MaxBackoff`
below `InitialBackoff` simply makes every wait that long.

**Examples:**
```csharp
// A calendar read from a local table recovers in seconds, so waiting a minute is waiting for nothing
opt.SetOccurrenceProviderRetry(retry =>
{
    retry.InitialBackoff = TimeSpan.FromSeconds(30);
    retry.MaxBackoff     = TimeSpan.FromMinutes(5);
})
```

**Notes:**
- A provider that throws is treated as TRANSIENT: the database a calendar is read from being briefly down
  must not end a series. Nothing is written — the row keeps its cursor and stays recoverable — and the
  schedule is parked to ask again after this wait.
- The wait doubles at each consecutive failure of the SAME schedule, up to `MaxBackoff`, and one answer
  resets it. The counter is in memory and per host: a restart starts over at `InitialBackoff`, which costs
  nothing, because the row was never written.
- The startup-recovery poison counter is not touched by a provider failure. A calendar down across five
  restarts would otherwise mark the series `Failed` for ever.
- An unregistered provider key is NOT covered by this: it is a configuration error, refused at dispatch with
  `ArgumentException` and poisoned at recovery like a corrupt cron expression.
- See [Recurring Tasks › Occurrence Providers](recurring-tasks/occurrence-providers.md).

### AddOccurrenceProvider&lt;T&gt;

Registers an occurrence provider under the key schedules name it by. It is a method on the
`EverTaskServiceBuilder` (what `AddEverTask` returns), not on the configuration object.

**Signature:**
```csharp
AddOccurrenceProvider<TProvider>(string key) where TProvider : class, INextOccurrenceProvider
```

**Examples:**
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
        .AddSqlServerStorage(connectionString)
        .AddOccurrenceProvider<BusinessDaysProvider>("business-days");
```

**Notes:**
- `TProvider` is registered as **scoped** with `TryAdd`, so an application that wants another lifetime (a
  singleton holding a cached calendar) registers it itself and keeps that registration. It is resolved in a
  fresh scope for every question.
- The KEY is what every schedule using the provider persists — never a type name, which a rename would
  orphan. Treat it as part of the durable contract; it is matched ordinally.
- Registering the same type under the same key twice is a no-op, so a registration that runs at every startup
  is idempotent. A DIFFERENT type under a key already taken throws `ArgumentException`.
- See [Recurring Tasks › Occurrence Providers](recurring-tasks/occurrence-providers.md).

### Audit & Execution-Log Retention (`AddAuditCleanup`)

Configure automatic retention to prevent unbounded growth of the audit and execution-log tables. Retention is enforced by the optional `AuditCleanupHostedService`, registered with **`AddAuditCleanup(policy, cleanupIntervalHours)`**, the single entry-point that actually applies the policy.

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
    OccurrenceRetentionDays = 60,              // Finished occurrences of durable schedules pruned after 60 days
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
| `OccurrenceRetentionDays` | `int?` | `null` | Days to retain the finished occurrences of a durable recurring schedule (the child rows it materializes, one per slot) |
| `DeleteCompletedTasksAfterRetention` | `bool` | `false` | Hard-delete a completed non-recurring task once it is older than the longest retention window **and** has no audit rows |

> `DeleteCompletedTasksWithAudits` is **`[Obsolete]`**: a legacy alias that forwards to `DeleteCompletedTasksAfterRetention`. Don't use it in new code; it remains only for source compatibility with pre-rename configs.
>
> **When a completed task is deleted:** when it is older than the longest of `StatusAuditRetentionDays`/`RunsAuditRetentionDays`/`ErrorAuditRetentionDays` (measured from `LastExecutionUtc`, falling back to `CreatedAtUtc`) and has no remaining StatusAudit/RunsAudit rows. If no retention window is configured, no completed tasks are deleted (a non-positive window counts as disabled). **When a log-retention window or cap (`ExecutionLogRetentionDays` / `MaxExecutionLogsPerTask`) is active, a task that still has surviving logs is preserved**, so its logs are never cascade-deleted before their own window expires; once those logs age out the task is purged. With no log retention configured, deleting the task cascades to everything it owns, captured execution logs included.
>
> **Occurrence retention.** `OccurrenceRetentionDays` prunes the finished occurrences of a durable recurring schedule, whatever terminal state they ended in: Completed, Failed and Cancelled alike. That is where it differs from `DeleteCompletedTasksAfterRetention`, which only removes completed rows with no audit trail left; without a window of its own, a busy schedule's failed and cancelled occurrences would pile up forever. Pruning them loses nothing, because a durable schedule is driven by its cursor and not by its past occurrence rows, so a pruned slot is never materialized again. The schedule row is recurring, and this pass never deletes it. **When a log-retention window or cap is active, an occurrence that still owns execution logs is preserved**, exactly as for completed tasks: the occurrence window is usually much shorter than the log one, and deleting the row would cascade to logs the log retention chose to keep. **Each audit trail has a guard of its own**: with `StatusAuditRetentionDays` set, an occurrence whose status rows are still inside that window is preserved, and `RunsAuditRetentionDays` guards the runs trail the same way. Deleting the row cascades both. An occurrence window of 7 days against an error window of 90 would otherwise erase a failure on day eight, and the cleanup line would report only an occurrence count. There is one guard per trail rather than one for both, because each audit pass runs only when its own knob is set: a window you never configured prunes nothing, so it has nothing to hold back. Default `null` (unlimited); enforced by `AddAuditCleanup(policy, …)`.
>
> **Execution-log retention.** `ExecutionLogRetentionDays` and `MaxExecutionLogsPerTask` trim `TaskExecutionLog` rows on their own, without deleting the task, so a long-running service (recurring tasks especially) never accumulates logs without bound. Both default to `null` (unlimited), so enabling persistent logging never starts deleting logs on its own. They are separate from `PersistentLoggerOptions.MaxLogsPerTask`, which caps a single execution's logs at capture time; these two trim logs across all past runs. When both are set, a log is deleted if it breaks either rule. Both are enforced by `AddAuditCleanup(policy, …)`.

**Cleanup Service Registration:**

The `AddAuditCleanup(policy, …)` method (an `IServiceCollection` extension from the `EverTask.Storage.EfCore` package) registers a hosted service that periodically deletes old audit records:

```csharp
builder.Services.AddAuditCleanup(
    retentionPolicy,              // The retention policy to apply (required)
    cleanupIntervalHours: 24);    // Cleanup frequency (default: 24 hours)
```

The service waits `AuditCleanupOptions.InitialDelay` (default **1 minute**) after startup before its first sweep. `InitialDelay` is not a parameter of `AddAuditCleanup`; override it via `services.Configure<AuditCleanupOptions>(o => o.InitialDelay = ...)` if needed.

**Important Notes:**

1. **Single Entry-Point**: `AddAuditCleanup(policy, ...)` is the only place that applies the policy. (The former `SetAuditRetentionPolicy()` builder method, which never applied it, has been removed.)
2. **Cleanup Service Required**: Retention is enforced by `AddAuditCleanup(policy, …)` - without it, policy has no effect
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

### The Scheduling Clock (`TimeProvider`)

Not a builder method, but a DI registration. `AddEverTask` registers `TimeProvider.System` with `TryAddSingleton`, and that single instance is what answers "what time is it?" for dispatch delays, the occurrence grid of a recurring schedule, both schedulers, startup recovery, and the rate limiter with its gate and parking lot.

**Signature:**
```csharp
services.AddSingleton<TimeProvider>(myProvider);   // before AddEverTask, or on .Services afterwards
```

**Default:** `TimeProvider.System`

**Examples:**
```csharp
// Production: nothing to do. AddEverTask registers the system clock.
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly));

// Tests: register a controllable clock and the whole pipeline follows it.
var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
services.AddSingleton<TimeProvider>(clock);
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
        .AddMemoryStorage();

// ...dispatch a schedule, then move time forward instead of waiting for it:
clock.Advance(TimeSpan.FromHours(2));
```

**Notes:**
- `TryAddSingleton` is what makes this a seam: register your own provider first and `AddEverTask` leaves it alone. Registering it afterwards works too, as long as it is a plain `AddSingleton` that replaces the entry.
- The schedulers wait on `Task.Delay(delay, timeProvider)`, not on a wall-clock timeout, so a test clock that stands still keeps an occurrence pending no matter how much real time passes. A fake provider has to drive its timers as well as `GetUtcNow()` for that to hold.
- Storage never resolves the clock on its own: the core passes the instant into `RetrievePending` and `TrySetQueuedIfRecoverable`, so the recovery filter judges a row against the same "now" the rest of the pipeline sees. A custom store that only implements the older signatures keeps working and reads the real clock (see [Custom Storage](storage/custom-storage.md)).
- Retry delays, audit timestamps and log timestamps stay on the real clock deliberately. `IRetryPolicy` is a public interface that owns its own waits, and an audit row records when something really happened. Do not expect a fake clock to complete a retry delay.

## Queue Configuration

You can set up multiple queues to isolate different types of work and give them different priorities or resource allocations.

> The `EverTaskServiceBuilder` returned by `AddEverTask(...)` also exposes a public `.Services` property (the underlying `IServiceCollection`), so you can register your own services mid-chain without breaking the fluent flow, e.g. `builder.Services.AddSingleton<IKeyedRateLimiter, MyRedisLimiter>()` or a custom `IGuidGenerator`. There is also `EnsureRecurringQueue()` to create the recurring queue with defaults without a configure action (normally unnecessary: both the `default` and `recurring` queues are auto-created during service registration, in `RegisterQueueManager`, if not configured). The well-known queue names are the public constants `QueueNames.Default` (`"default"`) and `QueueNames.Recurring` (`"recurring"`).

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

> **Raw object defaults.** The values above are what `AddQueue` applies. A bare `new QueueConfiguration()` (if you construct one directly rather than via `AddQueue`) defaults to: `Name = "default"`, `MaxDegreeOfParallelism = 1`, `ChannelOptions = new BoundedChannelOptions(2000) { FullMode = Wait, SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false }`, `QueueFullBehavior = FallbackToDefault`, and null retry policy / timeout. Note the raw channel capacity is **2000**, whereas `AddQueue` sets **500**.

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

### EnsureRecurringQueue

Creates the recurring queue with default settings **only if it doesn't already exist**. Normally unnecessary: both the `default` and `recurring` queues are auto-created during service registration (`RegisterQueueManager`). Use it only if you want to guarantee the recurring queue exists without supplying a configure action (it is idempotent: a no-op when the queue is already present).

**Signature:**
```csharp
EnsureRecurringQueue()   // returns EverTaskServiceBuilder for chaining
```

**Behavior:** when the `recurring` queue is absent, it **clones** the existing `default` queue configuration (or, if even that is absent, a fresh `QueueConfiguration` seeded from the global `MaxDegreeOfParallelism` / `ChannelOptions` / retry / timeout) and registers it under `QueueNames.Recurring`. When the queue already exists it does nothing.

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

**`QueueFullBehavior` values** (applies to **immediate** dispatches only; scheduler-triggered dispatches use a non-blocking write + backoff):

| Value | Behavior |
|-------|----------|
| `Wait` | Block until space frees (cancellable via the dispatch `CancellationToken`). Default of the auto-created `default` queue. |
| `FallbackToDefault` | First tries the **target** queue with a non-blocking write; if it's full, logs a warning and re-routes the task to the `default` queue with **blocking `Wait` backpressure** (it does not throw unless the default queue itself is unavailable). Two consequences: (1) once on the default queue the task runs there, so it does **not** honor the target queue's `MaxDegreeOfParallelism`/isolation; (2) if the target queue **is** the default queue, this degenerates to plain `Wait` (self-reference). Default for `AddQueue`-created queues. |
| `ThrowException` | Throw `QueueFullException`; the task stays persisted as `WaitingQueue` and is re-enqueued by startup recovery. |

## Rate Limiting Configuration

Keyed rate limiting (v3.7+) constrains how often tasks of a type execute **per key** (tenant, account, external resource). Behavior, semantics, and edge cases are documented in [Keyed Rate Limiting](rate-limiting.md); this section covers the configuration surface.

Configuration lives in three places:

1. **Per-handler policy**: the `RateLimitPolicy` property on the handler declares the limit.
2. **Key source**: the task implements `IRateLimitedTask`, or the handler overrides `GetRateLimitKey`. If the key is null/empty (or the key selector **throws**), the exception is caught, a warning is logged, and the task runs **ungated** (fail-open), never failing the task over a key-resolution error.
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
| `Permits` | `int` | n/a (constructor, required) | Public get-only property set from the constructor: executions allowed per `Period`. Must be > 0 |
| `Period` | `TimeSpan` | n/a (constructor, required) | Public get-only property set from the constructor: the rolling window for `Permits`. Must be > 0 |
| `Burst` | `int` | `Permits` | Burst tolerance (≥ 1). `1` = strict even spacing (`Period / Permits` between executions); `Permits` = the full budget can front-load |
| `ThrottleRetries` | `bool` | `true` | Retry attempts re-acquire the key's budget through the gate (the re-acquire happens before the per-attempt timeout starts, so a budget wait never erodes it). This is **not** an inline wait between attempts: if the next free slot is far, the retry path stops the in-process retry loop and **re-parks the task to the scheduler**; it fires again via redelivery, and the **retry attempt numbering restarts** from the redelivered execution. `false` lets retries run without re-acquiring budget. |
| `StartEmpty` | `bool` | `false` | When `false` (default), a fresh bucket starts **full**: the entire burst is available immediately (a restart can front-load up to `Burst` executions). Set to `true` to start at the steady rate from the first execution, capping the post-restart burst |
| `MaxReservationHorizon` | `TimeSpan` | `1 hour` | Slots farther than this are never parked: terminal rejection (one-shot → `Failed` + `OnError`; recurring → occurrence skipped) |
| `MaxInSlotWait` | `TimeSpan` | `1 second` | **No-op, retained for binary compatibility only.** The gate no longer waits inline on the consumer: every over-budget task (near or far slot) is re-parked to the scheduler and fires at its reserved slot via redelivery. An inline wait would head-of-line-block the single consumer (including unthrottled tasks behind it). |
| `OverflowBehavior` | `RateLimitOverflowBehavior` | `WaitForCapacity` | `WaitForCapacity` defers over-budget tasks to their reserved slot; `Discard` terminally rejects them (one-shot → `Failed` + `OnError`; recurring → occurrence skipped) |

**Notes:**
- The policy is read **once per handler type** (first-wins cache); changing it requires a restart.
- A policy without a key (see below) logs a warning once per task type and executes **ungated** (fail-safe).
- **Limiter outage fails open.** If the `IKeyedRateLimiter` itself **throws** while acquiring budget (most relevant for a custom/distributed implementation, e.g. Redis unreachable), the gate logs a warning and lets the task execute **unthrottled** rather than failing it (the never-lose-a-task contract). The same fail-open applies when `MaxTrackedKeys` overflows. Only `OperationCanceledException` (service shutdown) propagates, leaving the task in a recoverable status for next-startup recovery.

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
- `SchemaName` (string?): Database schema name (default: "EverTask"); `null` or empty falls back to `dbo` (used for stored-procedure execution)
- `AutoApplyMigrations` (bool): Auto-apply EF Core migrations (default: true)

### AddPostgresStorage

Uses PostgreSQL (via Npgsql) for persistent storage.

**Signature:**
```csharp
AddPostgresStorage(string connectionString, Action<PostgresTaskStoreOptions>? configure = null)
```

**Parameters:**
- `connectionString` (string): Npgsql connection string (e.g. `Host=localhost;Database=evertask;Username=...;Password=...`)
- `configure` (Action, optional): Storage configuration options

**Examples:**
```csharp
// Basic
.AddPostgresStorage("Host=localhost;Database=evertask;Username=evertask;Password=***")

// With options
.AddPostgresStorage(
    connectionString,
    opt =>
    {
        opt.SchemaName = "evertask";
        opt.AutoApplyMigrations = true;
    })
```

**PostgresTaskStoreOptions Properties:**
- `SchemaName` (string?): Database schema name (default: "evertask", **must be lowercase**; null = `public` schema)
- `AutoApplyMigrations` (bool): Auto-apply EF Core migrations (default: true)

### AddMySqlStorage

Uses MySQL or MariaDB (via Microting.EntityFrameworkCore.MySql) for persistent storage. **Targets net9.0/net10.0 only.**

**Signature:**
```csharp
AddMySqlStorage(string connectionString, Action<MySqlTaskStoreOptions>? configure = null)
```

**Parameters:**
- `connectionString` (string): MySQL/MariaDB connection string (e.g. `Server=localhost;Database=evertask;User=...;Password=...`)
- `configure` (Action, optional): Storage configuration options

**Examples:**
```csharp
// Basic
.AddMySqlStorage("Server=localhost;Database=evertask;User=evertask;Password=***")

// With options
.AddMySqlStorage(
    connectionString,
    opt =>
    {
        opt.AutoApplyMigrations = true;
        opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11)); // optional, skips auto-detect
    })
```

**MySqlTaskStoreOptions Properties:**
- `AutoApplyMigrations` (bool): Auto-apply EF Core migrations (default: true)
- `ServerVersion` (ServerVersion?): Explicit server version (default: null = `ServerVersion.AutoDetect`)
- `SchemaName` (string?): Defaults to `""` and must stay empty — MySQL/MariaDB have no sub-database schema (a "schema" is a database).

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

#### Enable() / Disable()
`Enable()` turns on database persistence; `Disable()` turns it off (logs still flow to ILogger in both cases). `WithPersistentLogger(...)` already calls `Enable()` for you, so `Enable()` is rarely needed explicitly. There is also a settable `Enabled` (bool) property backing both.
```csharp
.WithPersistentLogger(log => log.Disable())   // configured but persistence off
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
Sets the maximum number of logs to persist per task execution. Once this limit is reached, additional logs are not persisted (but still forwarded to ILogger), except for a single appended **truncation marker** record noting that logs were dropped.

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

Adds the EverTask Monitoring API with an optional embedded React dashboard for monitoring and managing tasks.

**Package:** `EverTask.Monitor.Api`

**Signature:**
```csharp
AddMonitoringApi()                                    // on EverTaskServiceBuilder
AddMonitoringApi(Action<EverTaskApiOptions> configure)
```

For apps that don't use the EverTask builder chain, there is an `IServiceCollection` variant:
`services.AddEverTaskMonitoringApiStandalone(Action<EverTaskApiOptions>? configure = null)`: it does
not auto-register SignalR monitoring and requires you to register `ITaskStorage` yourself.

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
| `EnableOpenApiDocument` | `bool` | `false` | Serve the monitoring OpenAPI document (net9.0+; auto-enabled by the Scalar package) |
| `EnableSwagger` | `bool` | `false` | Obsolete no-op since 4.0.0 (use `EnableOpenApiDocument`) |
| `Username` | `string` | `"admin"` | JWT Authentication username |
| `Password` | `string` | `"admin"` | JWT Authentication password (CHANGE IN PRODUCTION!) |
| `EnableAuthentication` | `bool` | `true` | Enable JWT Authentication |
| `EnableManagementEndpoints` | `bool` | `false` | Expose the management (write) endpoints under `/evertask-monitoring/api/management`. While false, every path under that prefix answers 404 |
| `ManagementUsername` | `string?` | `null` | Username of the second, operate-level credential. Logging in with it returns a token carrying the operate role, which is what the management endpoints require |
| `ManagementPassword` | `string?` | `null` | Password of the operate-level credential (compared in fixed time). Both halves must be set for the credential to exist |
| `ManagementAuthorization` | `Func<HttpContext, Task<bool>>?` | `null` | Host-supplied authorization for the management endpoints. When set it **replaces** the role check; returning false answers 403 |
| `JwtSecret` | `string?` | `null` | JWT signing key; when unset, a random 256-bit secret is generated per instance. Set it explicitly (≥ 32 bytes) for multi-instance deployments |
| `JwtIssuer` | `string` | `"EverTask.Monitor.Api"` | JWT issuer claim |
| `JwtAudience` | `string` | `"EverTask.Monitor.Api"` | JWT audience claim |
| `JwtExpirationHours` | `int` | `8` | JWT token TTL in hours |
| `EnableCors` | `bool` | `true` | **Registers** a named CORS policy (`EverTaskMonitoringApi`); EverTask does NOT apply it: your app must (`app.UseCors(...)`). See note below |
| `CorsAllowedOrigins` | `string[]` | `[]` | Origins for the registered policy (empty = allow-any). Only effective once the policy is actually applied |
| `AllowedIpAddresses` | `string[]` | `[]` | IP address whitelist (empty = allow all IPs). Supports IPv4, IPv6, and CIDR notation |
| `MagicLinkToken` | `string?` | `null` | Static token for magic link authentication. When set, enables instant access via `/evertask-monitoring/magic#token=...` (exchanged with `POST /api/auth/magic`; the `?token=` query form is deprecated since 4.0.0 because it lands in request logs) |
| `EventDebounceMs` | `int` | `1000` | Debounce time in milliseconds for SignalR event-driven cache invalidation in the dashboard. Higher values reduce API load during task bursts but introduce slight UI update delays. Recommended: 300ms (very responsive), 500ms (balanced), 1000ms (conservative for high-volume) |
| `BasePath` | `string` | `/evertask-monitoring` | **Read-only** computed property (fixed; cannot be set) |
| `ApiBasePath` | `string` | `/evertask-monitoring/api` | **Read-only** computed property (`{BasePath}/api`) |
| `UIBasePath` | `string` | `/evertask-monitoring` | **Read-only** computed property (= `BasePath`) |
| `SignalRHubPath` | `string` | `/evertask-monitoring/hub` | **Read-only** computed property (fixed when using `AddMonitoringApi`/`MapEverTaskApi`) |

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

#### EnableOpenApiDocument

Serves an OpenAPI document for the monitoring API, generated with the built-in ASP.NET Core
generator (`Microsoft.AspNetCore.OpenApi`). Requires net9.0 or later: on net8.0 the setting is a
no-op and the bundled analyzer reports `ET0008`.

**Examples:**
```csharp
options.EnableOpenApiDocument = true;
```

**How It Works:**
- Document name: `evertask-monitoring`
- Document endpoint: `/evertask-monitoring/openapi/evertask-monitoring.json`
- Includes only EverTask monitoring controllers (they carry the `evertask-monitoring` ApiExplorer group)
- Fully isolated from the host's OpenAPI/Swagger/Scalar setup, with nothing to configure on the host side
- The `EverTask.Monitor.Api.Scalar` package enables this automatically and adds an interactive
  API reference at `/evertask-monitoring/scalar` (`.AddMonitoringApiScalar()` after `AddMonitoringApi()`)

See [monitoring-dashboard.md](monitoring-dashboard.md#openapi-document-and-scalar-ui) for the
Scalar setup and the optional recipe to surface the document inside the host's own Swagger UI.

#### EnableSwagger (obsolete)

No-op since 4.0.0: the Swashbuckle integration was removed (it broke .NET 10 hosts using the
built-in OpenAPI stack, issue #20). Use `EnableOpenApiDocument` and optionally the
`EverTask.Monitor.Api.Scalar` package instead.

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
- **SignalR hub**: Real-time monitoring hub at `/evertask-monitoring/hub`
- **UI**: Not protected by JWT (only IP whitelist, see `AllowedIpAddresses`)

**Behind a path base.** The checks are enforced inside routing, so they hold when the application runs under
`app.UsePathBase("/tenant")`: what is judged is the path routing resolved, which is the monitoring path
without the base. Before 4.0.0 they were enforced only by a middleware that runs before `UsePathBase`, so on
such a host every layer was skipped at once — the read endpoints answered anonymously, the IP whitelist never
ran and the SignalR handshake was granted (issue #46). Only the monitoring CORS policy still keys off the
pre-`UsePathBase` path, so under a path base you may need your own CORS setup for cross-origin dashboards.

**Always Accessible (No JWT Required):**
- `/api/config` - Dashboard configuration endpoint
- `/api/auth/login` - Login endpoint for obtaining JWT
- `/api/auth/validate` - Token validation endpoint
- `/api/auth/magic` - Magic-link token exchange, `POST` with the token in the body (the `GET ?token=` form is deprecated); returns 404 when `MagicLinkToken` is not configured
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

#### EnableManagementEndpoints, ManagementUsername / ManagementPassword, ManagementAuthorization

The monitoring API is read-only by construction. These four options are what opens the one exception to that
— the management endpoints, which requeue a terminal occurrence, resume a halted catch-up or cancel a
schedule — and they start from the **authorization**, not from the endpoints.

**Why a second credential.** `Username`/`Password` is the dashboard credential: everyone who looks at the
dashboard shares it, and looking is all it is for. A requeue puts a handler with side effects back into
execution, so it does not travel on that credential. `ManagementUsername`/`ManagementPassword` is a separate
account, and logging in with it returns a token carrying the **operate** role; every other login — the
dashboard credential and every magic link, which is a URL and gets forwarded — returns a read-only one.

**Examples:**
```csharp
// Default: no write surface at all. A host that upgrades gains nothing it did not ask for.
options.EnableManagementEndpoints = false;

// Opened, behind a second credential
options.EnableManagementEndpoints = true;
options.ManagementUsername        = "evertask-operator";
options.ManagementPassword        = builder.Configuration["EverTask:OperatePassword"];

// Or decided by the application's own authorization, whatever it is. The hook runs inside routing, after
// the host's UseAuthentication, so context.User is the principal the application authenticated.
options.EnableManagementEndpoints = true;
options.ManagementAuthorization   = context =>
    Task.FromResult(context.User.IsInRole("BackgroundJobsOperator"));
```

**How a request is decided** (`/evertask-monitoring/api/management/*` only):
1. `EnableManagementEndpoints` is false → **404**. The prefix does not exist; an API that never opened a
   write surface does not advertise one.
2. Authentication is enabled and no valid token is presented → **401**, as everywhere else.
3. `ManagementAuthorization` is set → the host decides. It **replaces** the role check, so holding the
   operate credential does not bypass it. Returning false → **403**.
4. Otherwise the session must carry the operate role → **403** without it.

With `EnableAuthentication = false` there is no session and therefore no role: the management endpoints are
refused (403) unless `ManagementAuthorization` says otherwise. Opening the read API must not silently mean
"anyone may cancel a schedule".

**Where the decision runs.** Inside routing, as an MVC authorization filter on the management routes — not in
the monitoring middleware. Two things follow, and both matter:

- It sees the request as routing does, so a host that calls `app.UsePathBase("/tenant")` is covered.
  `UsePathBase` moves the prefix out of `Request.Path` *after* the monitoring middleware has run, so a check
  living only there would miss the very request that routing then resolves to the action.
- It runs after the host's `UseAuthentication`, so `context.User` inside `ManagementAuthorization` is the
  principal your application authenticated. `context.User.IsInRole(...)`, a claims check or anything else you
  already use answers exactly what it answers in your own controllers.

**The management credential must really be a second one.** Registration throws `InvalidOperationException`
when `ManagementPassword` equals `Password` or `MagicLinkToken`, and when only one half of
`ManagementUsername` / `ManagementPassword` is set. A username is not a secret: an operate password the host
already hands out for reading is not a second credential, it is the shared one with a different name on it.

**CSRF.** These endpoints need no anti-forgery token: the API authenticates a session with a Bearer token in
the `Authorization` header, never with a cookie (the `?access_token=` fallback exists on the SignalR hub path
alone). A browser attaches neither to a cross-site request, so a page the operator did not open cannot make
one of these calls in their name — which also means the dashboard's token must stay out of cookies.

The endpoints themselves are documented in
[Monitoring API Reference](monitoring-api-reference.md#management-endpoints). The application-side road is
unchanged and still the right one for anything programmatic: `ITaskScheduleManager`, called behind the
application's own authorization (see
[Managing schedules at runtime](recurring-tasks/managing-tasks.md)).

#### SignalRHubPath

The SignalR hub path is now fixed to `/evertask-monitoring/hub` and cannot be changed.

**Notes:**
- The hub path is readonly and set to `/evertask-monitoring/hub`
- SignalR monitoring is automatically configured if not already registered
- Dashboard automatically uses this fixed path for real-time updates

#### EnableCors

When `true`, registers the `EverTaskMonitoringApi` CORS policy and applies it to requests under
`/evertask-monitoring` (since 4.0.0). The host pipeline is untouched: no global `UseCors` and
nothing to wire manually. With `CorsAllowedOrigins` empty the policy allows any origin; with
origins set it restricts to them and adds `AllowCredentials`.

**Examples:**
```csharp
// Apply the monitoring CORS policy (default)
options.EnableCors = true;

// No CORS handling on the monitoring endpoints
options.EnableCors = false;
```

#### Login rate limiting

`POST /evertask-monitoring/api/auth/login` carries the `evertask-monitoring-login` rate-limit
policy (`EverTaskApiOptions.LoginRateLimitPolicyName`): 5 attempts per 15 minutes per client IP,
429 on rejection. The package registers the policy; ASP.NET Core only enforces rate limiting when
the host pipeline runs the middleware:

```csharp
app.UseRateLimiter(); // after UseRouting()
```

The name is namespaced so it cannot merge with a `login` policy the host may define.

> ⚠ **Important:** EverTask only *registers* this policy; it does **not** apply it (`MapEverTaskApi`/the startup filter never call `UseCors` or `RequireCors`). For cross-origin requests to actually be permitted, your application must apply the policy itself, e.g. `app.UseCors("EverTaskMonitoringApi")` in the pipeline. With API and dashboard on the same origin (the default embedded-UI setup) no CORS is needed.

**Notes:**
- Relevant when the dashboard/frontend is hosted on a different origin from the API
- Not needed when API and frontend are on the same origin (default embedded UI)

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
- The client address is `Connection.RemoteIpAddress` — **no header is trusted** (see below)
- Returns **403 Forbidden** if IP not in whitelist
- IP check runs **before authentication** (more efficient)

**Security Notes:**
- Empty array = **allow all IPs** (default, suitable for internal networks)
- Always configure in production when exposed to internet
- Protects the API, the SignalR hub and the dashboard files (which no JWT covers)
- More efficient than firewall rules at application level

**Behind a reverse proxy** (changed in 4.0.0 — see below)

The whitelist compares the address of the connection EverTask actually sees. Behind a proxy that address is
the proxy's, so the host must let ASP.NET Core replace it first, with the standard
[forwarded headers middleware](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer):

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    // Only these peers may be believed. Without them nothing is forwarded.
    options.KnownProxies.Add(IPAddress.Parse("10.0.0.7"));
    // options.KnownNetworks.Add(new IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
});

var app = builder.Build();
app.UseForwardedHeaders();   // before UseRouting
```

and the proxy must send the header:

```nginx
# Nginx example
location /evertask-monitoring {
    proxy_pass http://localhost:5000/evertask-monitoring;
    proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
}
```

> **BREAKING (4.0.0), security.** Before 4.0.0 EverTask read `X-Forwarded-For` itself and believed it
> unconditionally, so **any** direct caller could bypass the whitelist by sending a whitelisted address in
> that header (issue #47). It no longer reads the header at all. If you are behind a proxy and relied on the
> old behavior, configure `UseForwardedHeaders` as above — with `KnownProxies` or `KnownNetworks` set, which
> is what decides whether the header may be believed. Hosts not behind a proxy need no change.

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

**Access URL** (since 4.0.0, token in the URL fragment):
```
https://your-server/evertask-monitoring/magic#token=your-very-long-secret-token-here-min-32-chars
```

**How it works:**
1. User visits the magic link URL; the fragment stays in the browser and is never sent to the server
2. The dashboard reads the token from the fragment and exchanges it with `POST /api/auth/magic` (token in the request body)
3. Backend validates the token against `MagicLinkToken` (fixed-time comparison)
4. If valid, generates a standard JWT session token
5. User is redirected to the dashboard, fully authenticated

**Security Notes:**
- Use a long, random token (32+ characters recommended)
- Token never expires - change it in configuration to revoke all magic link access
- Use the `#token=` fragment form. The older `?token=` query form still works, but every component that logs request URLs (Serilog `UseSerilogRequestLogging()` through `RawTarget`, reverse proxies, Azure App Service HTTP logs, browser history) stores the token verbatim, and it does not expire. See [Magic Link Access](monitoring-dashboard.md#magic-link-access) for host-side mitigations
- `GET /api/auth/magic?token=` is deprecated and kept only for existing integrations; the dashboard uses the POST form
- Both exchange endpoints share the `evertask-monitoring-login` rate-limit policy (5 attempts per 15 minutes per client IP, enforced when the host runs `UseRateLimiter()`)
- Combine with `AllowedIpAddresses` for defense in depth
- If `MagicLinkToken` is not set, the endpoint returns 404

**Token Generation:**
```powershell
# PowerShell - generate secure random token
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
```

### API Endpoints

Once configured, the monitoring API exposes REST endpoints for querying tasks and reading statistics. All endpoints are relative to `{BasePath}/api` (default: `/evertask-monitoring/api`).

**Main endpoints:**
- `GET /tasks` - Paginated task list with filtering, including the `parentTaskId`, `onlyOccurrences` and `onlyCatchUp` filters for [durable occurrences](recurring-tasks/durable-occurrences.md)
- `GET /tasks/{id}` - Task details
- `GET /tasks/counts` - Task counts by category (all, standard, recurring, failed, occurrences)
- `GET /tasks/{id}/status-audit` - Status change history
- `GET /tasks/{id}/runs-audit` - Execution history
- `GET /tasks/{id}/execution-logs` - Persisted handler logs (when persistent logging is enabled)
- `GET /tasks/{id}/occurrences` - The occurrences a durable schedule has materialized, newest slot first, paged by the storage itself
- `GET /dashboard/overview` - Dashboard statistics, including the catch-up backlog of every durable schedule by state
- `GET /queues` - Queue metrics
- `GET /statistics/success-rate-trend` - Success rate trends
- `GET /rate-limits` - Keyed rate-limit state (per-key parked count, next slot, tracked keys, fail-open count; in-memory, single-node)

Every endpoint is read-only: nothing here changes a task or a schedule. Changing a schedule while the application runs is [`ITaskScheduleManager`](recurring-tasks/managing-tasks.md), called from your own code behind your own authorization — the dashboard credentials are one read credential shared by everyone who looks at it.

See [Monitoring Dashboard](monitoring-dashboard.md) for complete API documentation.

### Dashboard Features

When `EnableUI` is true, the embedded React dashboard provides:

- **Overview Dashboard**: Total tasks, success rate, active queues, execution times
- **Catch-up Backlog**: The occurrences of every durable schedule by state (pending, active, failed, skipped, completed), how far behind the oldest pending slot is, and how many schedules stopped themselves over their catch-up cap. Shown only when a durable schedule exists
- **Task List**: Filtering, sorting, pagination, status filters, plus a catch-up badge and a lateness badge on the rows that stand for missed work
- **Task Details**: Complete information, execution history, error details
- **Occurrences**: A tab on a durable schedule — the occurrences it materialized, newest slot first, with page controls
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

`MapEverTaskApi()` maps endpoints only:
- Maps SignalR monitoring hub (at `/evertask-monitoring/hub`) with automatic JWT authentication
- Maps all API controllers
- Serves embedded dashboard (if `EnableUI` is true)

> `MapEverTaskApi()` does **not** wire JWT authentication middleware or apply a CORS policy. The JWT middleware is wired automatically by `AddMonitoringApi()` (via an `IStartupFilter`); the CORS policy is only **registered** by `AddMonitoringApi()` (`AddCors`) and is **not applied**: if you need it enforced, call `app.UseCors("EverTaskMonitoringApi")` yourself. No manual `UseEverTaskApiMiddleware()` call is needed (that method is obsolete).

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
AddSignalRMonitoring(Action<SignalRMonitoringOptions> monitoringConfiguration)
AddSignalRMonitoring(Action<HubOptions> hubConfiguration)
AddSignalRMonitoring(Action<HubOptions> hubConfiguration, Action<SignalRMonitoringOptions> monitoringConfiguration)
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
app.MapEverTaskMonitorHub();                       // default route /evertask-monitoring/hub
app.MapEverTaskMonitorHub("/custom/hub");          // custom route
app.MapEverTaskMonitorHub("/custom/hub", hub =>     // custom route + SignalR hub dispatcher options
{
    hub.TransportMaxBufferSize = 1024 * 1024;
});
```

`MapEverTaskMonitorHub` maps the hub **and** subscribes the monitor, so it is required in standalone mode. Overloads: `()` (default pattern), `(string pattern)`, and `(string pattern, Action<HttpConnectionDispatcherOptions>)`.

**Important Notes:**

- **Hub Route**: When mapped by `MapEverTaskApi()` the route is **fixed** at `EverTaskApiOptions.SignalRHubPath` (`/evertask-monitoring/hub`, read-only). In **standalone** mode the route is **configurable**: `MapEverTaskMonitorHub(pattern)` accepts any pattern (defaulting to `/evertask-monitoring/hub`); if you choose a custom pattern, point your client at the same path.
- **Log Streaming**: Execution logs are always available via ILogger and database persistence (if enabled)
- **Performance Impact**: Enabling `IncludeExecutionLogs` significantly increases SignalR message size and network bandwidth
- **Use Case**: Enable only when you need real-time log streaming to monitoring dashboards

**Client-Side Setup:**

```html
<!-- Add SignalR client library -->
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>

<script>
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/evertask-monitoring/hub")  // must match the mapped hub route (fixed under MapEverTaskApi; configurable under standalone MapEverTaskMonitorHub)
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
    // Schema name (default: "EverTask"); null/empty falls back to dbo
    opt.SchemaName = "EverTask";

    // Auto-apply migrations (default: true)
    opt.AutoApplyMigrations = true;

})

// Note: there are only two configurable options (SchemaName, AutoApplyMigrations).
// DbContext pooling (via AddPooledDbContextFactory) and the status-update stored
// procedures are always on: baked into the provider/migrations, not user-toggleable.
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

- `[EverTask].[usp_SetTaskStatus]` (v2.0+): status update + audit insert in one round-trip and one transaction
- `[EverTask].[usp_UpdateCurrentRun]` (v3.6+): single-round-trip recurring-run update
- The procs do the audit insert and status update in one round-trip instead of two statements, kept atomic. That saves a round-trip on the status-change path; it is not a task-throughput multiplier

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
- Single writer: tops out around a couple hundred tasks/sec on this hardware, and parallelism does not help
- Best for: Single-server deployments, development, small workloads

### PostgreSQL Storage Options

**Package:** `EverTask.Storage.Postgres`

**Advanced Configuration:**

```csharp
.AddPostgresStorage(connectionString, opt =>
{
    // Schema name (default: "evertask"). MUST be lowercase (matches ^[a-z_][a-z0-9_]*$):
    // Npgsql always double-quotes generated identifiers, so a mixed-case schema becomes
    // permanently case-sensitive. null = the "public" schema.
    opt.SchemaName = "evertask";

    // Auto-apply migrations (default: true). Disable for DBA-controlled / staged deploys.
    opt.AutoApplyMigrations = true;
})

// Note: there are only two configurable options (SchemaName, AutoApplyMigrations).
// DbContext pooling is always on. Status/run updates use single-statement data-modifying
// CTEs (the Postgres analog of SQL Server's stored procedures): versioned in C#, no DB objects.
```

**Connection String Examples:**

```csharp
// Basic
"Host=localhost;Database=evertask;Username=evertask;Password=***"

// With port + SSL
"Host=db.example.com;Port=5432;Database=evertask;Username=app;Password=***;SSL Mode=Require;Trust Server Certificate=true"
```

**Manual Migrations:** same pattern as SQL Server, using `--context PostgresTaskStoreContext`.

**Notes / limitations:**
- `SchemaName` lowercase-only (see above).
- All `DateTimeOffset` values map to `timestamptz` (UTC).
- High-write-concurrency support on one active EverTask host per store; an inactive standby is fine. See
  [Horizontal Scaling](scalability.md#horizontal-scaling-multiple-instances).

### MySQL / MariaDB Storage Options

**Package:** `EverTask.Storage.MySql` (targets net9.0/net10.0 only)

**Advanced Configuration:**

```csharp
.AddMySqlStorage(connectionString, opt =>
{
    // Auto-apply migrations (default: true). Disable for DBA-controlled / staged deploys.
    opt.AutoApplyMigrations = true;

    // Optional explicit server version. Default null -> ServerVersion.AutoDetect(connectionString)
    // (one short connect at startup). Set to skip the probe.
    opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11));
})

// Note: there is NO SchemaName option. MySQL/MariaDB have no sub-database schema (a "schema" IS a
// database, chosen by the connection string), so the tables live in the connection's database.
// DbContext pooling is always on. The provider inherits the optimized, server-side EF Core base, and the
// hot writes (SetStatus / UpdateCurrentRun / CompleteRecurringRun) use stored procedures: single-statement,
// atomic, one round-trip (the SQL Server analog; MySQL has no writable CTE / UPDATE...RETURNING).
```

**Connection String Examples:**

```csharp
// Basic
"Server=localhost;Database=evertask;User=evertask;Password=***"

// With port + SSL
"Server=db.example.com;Port=3306;Database=evertask;User=app;Password=***;SslMode=Required"
```

**Manual Migrations:** same pattern as SQL Server, using `--context MySqlTaskStoreContext`.

**Notes / limitations:**
- No schema concept (see above).
- Built on Microting.EntityFrameworkCore.MySql (maintained Pomelo fork); MySQL 8.0+ and MariaDB 10.11+.
- All `DateTimeOffset` values map to `datetime(6)` (UTC).
- High-write-concurrency support on one active EverTask host per store; an inactive standby is fine. See
  [Horizontal Scaling](scalability.md#horizontal-scaling-multiple-instances).

## Handler Configuration

You can configure behavior at the handler level to override global defaults.

### Handler Properties

The active handler-level settings (`Timeout`, `RetryPolicy`, `QueueName`, `RateLimitPolicy`) are `virtual` properties you override (expression-bodied / get-only: you don't assign them in a constructor). The obsolete `CpuBoundOperation` is the exception: a plain settable, non-virtual, no-op property (don't use it).

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
- `QueueName` (string?): Target queue for this handler. If the name is **not registered** (typo, or a queue you never added via `AddQueue`), routing logs a warning (`Queue '{name}' not found, falling back to 'default' queue`) and the task runs on the `default` queue; the per-queue retry/timeout resolution falls back to the `default` queue's config the same way, so an unknown name never throws and never silently drops the task.
- `RateLimitPolicy` (RateLimitPolicy?): Per-key execution frequency constraint; the key comes from `IRateLimitedTask` on the task or a `GetRateLimitKey` override on the handler (see [Rate Limiting Configuration](#rate-limiting-configuration))
- `CpuBoundOperation` (bool): **OBSOLETE, no effect.** Deprecated; EverTask's async execution is already non-blocking. For CPU-intensive synchronous work, use `Task.Run` inside `Handle`.

**Overridable methods:**
- `GetRateLimitKey(TTask task)`: derive the rate-limit bucket key from task data (e.g. `task.TenantId.ToString()`) without implementing `IRateLimitedTask`. Default reads `IRateLimitedTask.RateLimitKey`.
- Lifecycle callbacks: `OnStarted(Guid)`, `OnCompleted(Guid)`, `OnError(Guid, Exception?, string?)`, `OnRetry(Guid, int attemptNumber, Exception, TimeSpan delay)`, and `DisposeAsyncCore()`. See [Resilience › Error Observation](resilience/error-observation.md) and [Retry Callbacks](resilience/retry-callbacks.md).

**Injected per delivery (read, don't override):**
- `Logger` (`ITaskLogCapture`): task-scoped logging, persisted when `WithPersistentLogger` is configured.
- `Context` (`ITaskExecutionContext`): the identity of the delivery being executed. `TaskId`, `ScheduleId`, `TaskKey`, `ScheduledAtUtc` (the nominal slot; a rate-limit deferral moves the delivery, not this), `ScheduledAtLocal`, `TimeZoneId`, `StartedAtUtc`, `Attempt`, `RunNumber` (durable across restarts), `ScheduleVersion`, `IsRecurring`, `IsOccurrence` and `Misfire`. Both are injected before `OnStarted`, so they are readable in `Handle` and in every callback, and reading `Context` from a constructor throws `InvalidOperationException`. Services that are not the handler read the same instance through `ITaskExecutionContextAccessor` (singleton; `Current` follows the delivery's asynchronous flow and is null outside one). Full walkthrough: [Task Creation › Execution Context](task-creation.md#execution-context).

## Dispatch Parameters

Every `ITaskDispatcher.Dispatch(...)` overload accepts these optional parameters (see [Task Dispatching](task-dispatching.md) for full behavior):

| Parameter | Type | Default | Behavior |
|-----------|------|---------|----------|
| `auditLevel` | `AuditLevel?` | `null` → the global `SetDefaultAuditLevel` (default `Full`) | Per-dispatch override of the audit level for this task |
| `taskKey` | `string?` | `null` (no deduplication) | Idempotency key (≤ 200 chars, stored-column length-limited). **Non-recurring**: `InProgress` → no-op; an **immediate one-shot whose delivery is already in flight** → no-op (returns existing id, before any status update); `Pending`/`Queued`/`WaitingQueue` → update; terminal (`Completed`/`Failed`/`Cancelled`/`ServiceStopped`) → remove + recreate. **Recurring**: `InProgress` → no-op; every other status incl. `Completed`/`Failed` → **update in place** (a recurring row is never "terminated"/replaced), preserving `NextRunUtc` + `CurrentRunCount` **only when `NextRunUtc.HasValue`**: an exhausted series (no stored next run) is recalculated instead of preserved; a re-dispatch with no recurring config (recurring to one-shot) is **discarded** to avoid destroying the schedule. Essential for idempotent recurring registration across restarts |
| `cancellationToken` | `CancellationToken` | `default` | Cancels the **dispatch operation** (e.g. a blocking enqueue on a full `Wait` queue), not the task's execution |

The scheduling discriminator (`TimeSpan` delay, `DateTimeOffset` time, or `Action<IRecurringTaskBuilder>`) is a positional argument that selects the overload.

## Recurring Task Builder

The `Action<IRecurringTaskBuilder>` overload of `Dispatch` configures a recurring schedule via a fluent builder (`src/EverTask.Abstractions/Recurring/IRecurringTaskBuilder.cs`). All times are **UTC** unless the schedule names a zone. Full feature docs: [Recurring Tasks](recurring-tasks.md).

**Entry / first run:**
- `Schedule()`: pure recurring, no initial one-off run.
- `RunNow()` / `RunDelayed(TimeSpan)` / `RunAt(DateTimeOffset)` → `.Then()`: run once first (now / after a delay / at a time), then follow the recurring schedule.

**Interval:**
- `Every(int n)` followed by `.Seconds()` / `.Minutes()` / `.Hours()` / `.Days()` / `.Weeks()` / `.Months()`.
- `EverySecond()` / `EveryMinute()` / `EveryHour()` / `EveryDay()` / `EveryWeek()` / `EveryMonth()`.
- `OnDays(params DayOfWeek[])`: specific weekdays; `OnMonths(params int[])`: specific months.
- There is no hourly counterpart of those two. `OnHours()` is on the concrete `IntervalSchedulerBuilder` but not on `IIntervalSchedulerBuilder`, so `Schedule().OnHours()` does not compile, and it selects no hours in any case: it builds `EveryHour()`'s plain cadence, which a time zone does not govern. For specific hours of the day use `EveryDay().AtTimes(...)`.

**Refinement:**
- Hour → `.AtMinute(0–59)`; minute → `.AtSecond(0–59)`.
- Day → `.AtTime(TimeOnly)` or `.AtTimes(params TimeOnly[])`.
- Week → `.OnDay(DayOfWeek)` / `.OnDays(params DayOfWeek[])` → then `.AtTime(...)`.
- Month → `.OnDay(1–31)` / `.OnDays(params int[])` / `.OnFirst(DayOfWeek)` → then `.AtTime(...)`.

**Cron:** `UseCron("expr")`: 5-field (`min hour dom month dow`) or 6-field (with seconds), via Cronos. **Overrides** every other interval call; invalid expressions throw `ArgumentException` on the first schedule calculation.

**Time zone:** `.InTimeZone(TimeZoneInfo)` / `.InTimeZone(string)`, accepted before the interval (on `Schedule()`), on the interval builder itself (`EveryDay().InTimeZone(z).AtTime(...)`) and after the final refinement: every position but between `Every(n)` and its unit. The id may be IANA or Windows; the IANA form is what gets persisted, inside the schedule definition, with no new column. It governs **calendar-anchored** schedules only: days, weeks and months (cadences included: `Every(3).Days()` lands on local midnight), `AtTime`/`AtTimes`, weekday and month selectors, cron. On a plain cadence (`Every(n).Seconds/Minutes/Hours`, with `AtSecond`/`AtMinute`) it throws `InvalidOperationException` when the schedule is built: an elapsed step is the same set of instants in every zone. An unresolvable id, or a zone with no IANA id, throws `ArgumentException` at build; an id that stops resolving later is poisoned at recovery like a corrupt cron. Across daylight saving, a skipped local time fires at the gap's exit (several slots inside one gap produce one occurrence) and a repeated one fires on its first pass. Global default: [`SetDefaultScheduleTimeZone`](#setdefaultscheduletimezone). Full rules: [Time Zones](recurring-tasks/time-zones.md).

**Occurrence provider:** `.UseOccurrenceProvider(string key, string? config = null)` on `Schedule()`, for a calendar no interval and no cron can express. The grid then comes from the `INextOccurrenceProvider` registered as [`AddOccurrenceProvider<T>(key)`](#addoccurrenceprovidert), which answers "which occurrence comes strictly after this instant" in UTC; `null` ends the series. **Exclusive** with every interval and with cron — a provider replaces the grid instead of refining it, and naming both throws `InvalidOperationException` at build. Only the key and the opaque `config` string are persisted (never a type name), and the schedule's `InTimeZone` id travels to the provider, which is what reads the calendar on it. Everything else applies unchanged: misfire policies, durable occurrences, `MaxRuns`/`RunUntil`, the skip-forward after a downtime, and `ReevaluateSchedule` as the way to say the calendar changed. Two exceptions: `CatchUpOverflowPolicy.SkipOldest` needs `IsDeterministic => true` on the provider (refused at dispatch otherwise) and `RescheduleMode.RebaseFromCursor` is refused, because a provider exposes no nominal period. An unknown key is a configuration error (`ArgumentException` at dispatch, terminal poison at recovery); a provider that throws is transient — nothing is written, the schedule is re-parked after [`SetOccurrenceProviderRetry`](#setoccurrenceproviderretry)'s backoff, and it surfaces as `OccurrenceProviderException` only where a caller is holding the call: a dispatch, and the `ITaskScheduleManager` methods that decide a new cursor (`Reschedule`, `ReevaluateSchedule`). Full rules: [Occurrence Providers](recurring-tasks/occurrence-providers.md).

**Limits:** `.RunUntil(DateTimeOffset)` (must be future) and `.MaxRuns(int)` (counts real executions only; occurrences skipped to realign after downtime do not consume the budget). Stops at whichever is reached first. On a **durable** schedule `MaxRuns` counts materializations instead — an occurrence that later fails or is cancelled still spent a run, because the schedule did produce it.

**Durable occurrences:** `.WithDurableOccurrences()`, `.OnMisfire(Action<IMisfirePolicyBuilder>)` and `.BackfillFrom(DateTimeOffset)`, accepted in the same positions as `InTimeZone`. They turn every due slot into its own one-shot row — its own status, retries, audit trail and rate-limit budget — and the schedule row stops running the handler.

- `.OnMisfire(m => m.Skip())` is the default written out: missed slots are dropped, at most the one still current runs, and no occurrence rows are created.
- `.OnMisfire(m => m.FireOnce(options))` collapses a whole run of missed slots into ONE occurrence at the most recent of them, with the range it covers in `ITaskExecutionContext.Misfire`. `FireOnceOptions.MaxAge` (default `null`) drops the run entirely when even its newest slot is older than the window.
- `.OnMisfire(m => m.CatchUp(options))` replays every missed slot, oldest first. `CatchUpOptions(maxAge, maxOccurrences)` requires both caps; `MaxPendingOccurrences` (default `1`) is how many occurrences may be alive at once; `OverflowPolicy` is `Halt` (default — nothing is materialized, a durable marker is written, the schedule stops being parked so it costs no further deliveries or writes, and neither time nor a restart releases it: only an explicit schedule change does — [`ResumeSchedule` or `Reschedule`](#runtime-schedule-management), or dispatching the series again after a cancel) or `SkipOldest`, which keeps the most recent `MaxOccurrences`.
- Both replaying policies imply durable occurrences; `.WithDurableOccurrences()` gives the rows without the replay.
- `.BackfillFrom(startUtc)` starts the cursor at the first occurrence on or after `startUtc` instead of after the dispatch. New registrations only, and still bounded by the caps above.
- Requires a storage that implements the atomic occurrence operations. Every built-in provider does; a custom one that does not is refused at dispatch with `NotSupportedException`.
- Contracts: at-least-once (write idempotent handlers) and **one active host**. Full rules: [Durable Occurrences](recurring-tasks/durable-occurrences.md).

> `OnLast(DayOfWeek)` is **not** implemented (only `OnFirst`). For idempotent registration across restarts, pass a stable `taskKey` (see [Dispatch Parameters](#dispatch-parameters)).

## Runtime Schedule Management

`ITaskScheduleManager` changes a schedule that is already registered. `AddEverTask` registers it next to `ITaskDispatcher`, which is untouched: a dispatch registers a schedule, this manages the one already registered. Schedules are addressed by the `taskKey` they were dispatched with; occurrences by their own id.

```csharp
public class ScheduleAdmin(ITaskScheduleManager schedules)
{
    public Task<ScheduleUpdateResult> MoveDailyReport(TimeOnly at) =>
        schedules.Reschedule(
            "daily-report",
            r => r.Schedule().EveryDay().AtTime(at).InTimeZone("Europe/Rome"),
            RescheduleMode.RebaseFromCursor);
}
```

| Method | Returns | Purpose |
|--------|---------|---------|
| `Reschedule(taskKey, configure, mode, ct)` | `ScheduleUpdateResult` | Replace the definition and choose a new cursor |
| `ReevaluateSchedule(taskKey, ct)` | `ScheduleUpdateResult` | Keep the definition, recompute the cursor from now — a durable backlog is discarded |
| `ResumeSchedule(taskKey, ct)` | `ScheduleUpdateResult` | Release a durable catch-up halt, keeping the cursor |
| `RequeueFailedOccurrence(occurrenceId, ct)` | `bool` | Put one terminal occurrence back in the queue |
| `CancelSchedule(taskKey, ct)` | `Task` | Cancel the schedule and every pending occurrence of it |

**Requirements.** Every method needs a registered storage, and beyond that each one asks for what it actually uses. `Reschedule`, `ReevaluateSchedule` and `ResumeSchedule` rewrite a schedule row and need `SupportsScheduleVersioning`; `RequeueFailedOccurrence` addresses a child row and needs `SupportsDurableOccurrences`; `CancelSchedule` needs neither, because writing a cancellation is something every storage has always done. What a call is asked to WRITE counts too: a `Reschedule` whose new definition turns the schedule durable — `WithDurableOccurrences()`, or an `OnMisfire` policy of `FireOnce`/`CatchUp` — needs `SupportsDurableOccurrences` on top of the versioning, and is refused before anything is written, exactly as a dispatch of the same definition would be. Both capabilities are true for all built-in providers. A storage without the one a call needs throws `NotSupportedException` rather than degrading: without a real compare-and-swap a reschedule could report success while a run finishing at the same moment silently overwrote it, and there is no half-atomic emulation of the occurrence operations to fall back on.

**Storage is the source of truth.** Each schedule row carries a `ScheduleVersion`. A reschedule writes the definition, the cursor, the bounds and the version in one conditional update, guarded by the version it read; every advance of a managed schedule carries the version its run belonged to, so a completion that lands after a reschedule loses the guard, records its run against the row's own cursor and lets the new definition stand.

### RescheduleMode

`RecalculateFromNow` (the default) points the cursor at the new definition's first occurrence after now. On a durable schedule, whatever the old definition still owed is dropped and reported — `DiscardedBacklog`, `DiscardedBacklogIsExact` and a monitoring event naming the count.

`RebaseFromCursor` keeps the schedule inside the calendar period it was already in. The day, week or month the old cursor fell in is read on the old definition's clock, and the new cursor is the new definition's occurrence at the same POSITION inside that period, read on the new one. That is what preserves the logical date when the time of day or the zone changes. Position matters as soon as a period holds more than one slot: with `OnDays(Monday, Wednesday).AtTimes(09:00, 15:00)`, a cursor standing on the afternoon run rebases onto the new afternoon time and never back onto the morning one that has already run — which would replay it and spend one more of `MaxRuns`, where `RecalculateFromNow` on the same definition answers the later slot. It is refused, with `InvalidOperationException` and no write, when:

- the two definitions have different shapes (a different cadence, different weekday or month selectors, a different period kind — only the time of day, the zone, `RunUntil`/`MaxRuns` and the misfire settings may move);
- either side is a cron schedule or a schedule driven by an occurrence provider, neither of which states a nominal period;
- the period holds no slot of the new definition, or fewer slots than the cursor had already passed, so there is no position to land on. A rebase never crosses into the next period: doing so would skip a period of work or replay one;
- the new definition's bounds are already past. `RunUntil` and `MaxRuns` are what an operator changes to wind a series down, and the period arithmetic applies neither: a plain cadence keeps its cursor verbatim and the day-carrying cadences place their slot by hand, so neither ever asks the grid, which is the only thing that applies `RunUntil`. Both bounds are checked here instead. A definition one mode would refuse is refused by the other too, rather than running one occurrence past the end just set.

A plain cadence has no calendar structure to preserve, so its cursor is carried over unchanged — which is how a halted catch-up keeps its backlog while its caps are widened. A week or month cadence that names no day inside its period — `EveryWeek()` and `EveryMonth()` without `OnDay`/`OnDays`/`OnFirst` — carries that day on the cursor rather than in the definition, so the day it was already on is what the rebase keeps, and only the time of day and the zone move.

### Linearization

A reschedule takes effect immediately for occurrences that have not fired: the scheduler's registration is replaced in place, with no window in which the schedule is parked nowhere. A delivery already handed to a worker queue is past the scheduler's reach and is considered fired; inside the process that rescheduled it, EverTask drops that delivery rather than running the definition just replaced. After a restart nothing has been published, so a delivery recovered from storage always runs and its advance is what applies the new definition.

If the re-park itself fails, nothing is published and the update still stands: the previous occurrence runs once more, and its advance loses the compare-and-swap, applies the new definition and parks the schedule from the row it has just read — so a re-park that threw costs one extra run of the old definition, never a series that stops.

### ScheduleUpdateResult

| Property | Meaning |
|----------|---------|
| `TaskId` | The schedule row that was updated |
| `ScheduleVersion` / `PreviousScheduleVersion` | The version now on the row, and the one it replaced |
| `NextRunUtc` / `PreviousNextRunUtc` | Where the schedule now stands, and where it stood |
| `Mode` | The mode the cursor was decided with |
| `DiscardedBacklog` / `DiscardedBacklogIsExact` | Due slots this call dropped, and whether that number is a total or a lower bound |
| `ReleasedHalt` | Whether this call cleared a durable catch-up halt |

### What it refuses

`InvalidOperationException`, always before anything is written:

- no task carries that key, or the task it names is a one-shot;
- the schedule was cancelled — a cancellation is terminal, dispatch it again;
- the new definition has no occurrence left to run (its bounds are already past). Use `CancelSchedule` to end a series on purpose, instead of leaving a row nothing can finish;
- the row's payload or definition cannot be rebuilt by this build;
- a rebase that cannot map the cursor (see above);
- the row changed under the call and the conditional update lost. Read it again and retry.

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

A fuller setup with SQL Server storage, retry policies, and logging:

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

This setup isolates different workloads into separate queues:

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

Tuned for very large workloads:

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

Here the configuration changes based on your environment:

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
- **Global** `MaxDegreeOfParallelism == 1`: a startup warning is logged (a single consumer is usually a bad idea in production); the value is honored as-is
- **Per-queue** `MaxDegreeOfParallelism < 1`: clamped to **1 consumer** at startup with a warning (prevents a zero-consumer deadlock), never treated as "unlimited". (The per-queue path clamps `< 1`; the global-level warning fires specifically at `== 1`.)

**Behaviors to be aware of (no error raised):**
- Re-adding a queue with an existing name silently **replaces** the previous configuration
- `SetMaxDegreeOfParallelism` performs no validation at configuration time; the worker clamps any value `< 1` to 1 at startup (see above)

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

For very large workloads, enable the sharded scheduler:

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
