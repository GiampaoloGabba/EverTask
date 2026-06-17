---
layout: default
title: Architecture
nav_order: 10
---

# Architecture & Internals

This document explains how EverTask works internally, its architecture, performance characteristics, and design decisions.

## Table of Contents

- [Overview](#overview)
- [Task Execution Flow](#task-execution-flow)
- [Efficient Task Processing](#efficient-task-processing)
- [Scheduler Architecture](#scheduler-architecture)
- [Performance Optimizations](#performance-optimizations)
- [Threading Model](#threading-model)
- [Design Principles](#design-principles)

## Overview

EverTask is a high-performance background task execution library built for persistence and reliability. The architecture is built around several key principles:

- **Event-driven scheduling** instead of polling
- **Aggressive caching** to minimize allocations
- **Fully asynchronous** execution from top to bottom
- **Storage-bound task execution**: throughput is set by your database (a few synchronous round-trips per task), not by the engine (see [Scalability](scalability.md#measured-performance-indicative))
- **Built-in resilience** with retry policies, timeouts, and graceful degradation

### Component Overview

```
┌─────────────┐
│ Application │
└──────┬──────┘
       │ Dispatch
       ▼
┌─────────────┐     ┌──────────────┐
│  Dispatcher │────>│ TaskStorage  │
└──────┬──────┘     └──────────────┘
       │
       ▼
┌─────────────┐     ┌──────────────┐
│   Scheduler │────>│ PriorityQueue│
└──────┬──────┘     └──────────────┘
       │
       ▼
┌─────────────┐     ┌──────────────┐
│ WorkerQueue │────>│ BoundedQueue │
└──────┬──────┘     └──────────────┘
       │
       ▼
┌─────────────┐     ┌──────────────┐
│RateLimitGate│────>│ KeyedLimiter │  (only for handlers declaring a RateLimitPolicy;
└──────┬──────┘     └──────────────┘   deferred tasks re-park into the Scheduler)
       │
       ▼
┌─────────────┐     ┌──────────────┐
│   Workers   │────>│   Handlers   │
└─────────────┘     └──────────────┘
```

## Task Execution Flow

### 1. Task Dispatch

When you call `dispatcher.Dispatch()`:

```
Application Code
    │
    ▼
ITaskDispatcher.Dispatch(task)
    │
    ├──> Serialize task (if storage configured)
    ├──> Persist to storage
    ├──> Determine target queue
    │
    ├──> Immediate execution?
    │    ├──> Yes: Enqueue to WorkerQueue
    │    └──> No: Schedule with Scheduler
    │
    └──> Return task ID
```

### 2. Immediate Execution Path

For immediate (fire-and-forget) tasks:

```
Dispatcher
    │
    ▼
WorkerQueueManager.TryEnqueue()
    │
    ├──> Lookup target queue
    ├──> Check queue capacity
    │
    └──> Write to BoundedQueue (Channel)
         │
         ▼
    WorkerService receives task
         │
         ▼
    WorkerExecutor.Execute()
         │
         ├──> Blacklist check (cancelled tasks discarded)
         ├──> Rate-limit gate (only when the handler declares a RateLimitPolicy):
         │    no budget for the task's key → reserve the next slot and re-park into
         │    the in-memory scheduler (storage untouched, status stays Queued); the
         │    worker is immediately free for other tasks
         ├──> Create service scope
         ├──> Resolve handler
         ├──> Apply retry policy
         ├──> Apply timeout
         ├──> Execute handler.Handle()
         ├──> Update storage status
         └──> Publish monitoring events
```

See [Keyed Rate Limiting](rate-limiting.md) for the full gate semantics (reservations, retries, recurring, bounds).

### 3. Delayed/Scheduled Execution Path

For delayed or scheduled tasks:

```
Dispatcher
    │
    ▼
Scheduler.Schedule(task, executionTime)
    │
    ├──> Calculate delay
    ├──> Add to PriorityQueue (ordered by execution time)
    │
    └──> Wake up scheduler timer
         │
         ▼
    Scheduler background thread
         │
         ├──> Calculate next wake time
         ├──> Sleep until next task
         │
         └──> On wake: Dequeue ready tasks
              │
              └──> Dispatch to WorkerQueue
```

### 4. Recurring Task Cycle

Recurring tasks follow a continuous cycle:

```
Initial Dispatch
    │
    ▼
Scheduler schedules first execution
    │
    ▼
Task executes in WorkerExecutor
    │
    ▼
Handler.Handle() completes
    │
    ▼
WorkerExecutor checks if recurring
    │
    ├──> Calculate next execution time
    ├──> Increment run count
    ├──> Check MaxRuns/RunUntil
    │
    └──> Re-schedule with Scheduler
         │
         └──> (Cycle continues)
```

## Efficient Task Processing

EverTask avoids polling entirely, using an event-driven approach for maximum efficiency.

### BoundedQueue Architecture

We use `System.Threading.Channels.BoundedChannel<T>` for the worker queues:

```csharp
// Conceptual implementation
var channel = Channel.CreateBounded<TaskHandlerExecutor>(new BoundedChannelOptions(capacity)
{
    FullMode = BoundedChannelFullMode.Wait // Block when full
});

// Writer (Dispatcher)
await channel.Writer.WriteAsync(taskExecutor);

// Reader (WorkerService)
await foreach (var task in channel.Reader.ReadAllAsync(cancellationToken))
{
    _ = ExecuteTaskAsync(task); // Fire-and-forget execution
}
```

Why channels? They give us zero polling overhead through OS-level signaling, built-in backpressure that blocks when full, and lock-free operation for better concurrency under load.

### ConcurrentPriorityQueue

For scheduled tasks, we use a custom priority queue:

```csharp
// Tasks ordered by execution time
public class ConcurrentPriorityQueue<T>
{
    private readonly SortedSet<(DateTimeOffset ExecutionTime, T Item)> _queue;
    private readonly SemaphoreSlim _semaphore;

    public void Enqueue(T item, DateTimeOffset executionTime)
    {
        lock (_lock)
        {
            _queue.Add((executionTime, item));
        }
        _semaphore.Release(); // Wake up scheduler
    }

    public bool TryPeek(out T item, out DateTimeOffset executionTime)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                var first = _queue.First();
                item = first.Item;
                executionTime = first.ExecutionTime;
                return true;
            }
        }
        item = default;
        executionTime = default;
        return false;
    }
}
```

The priority queue gives us O(log n) insert and remove operations, and we always know exactly when the next task needs to run. Locks are fine here since the scheduler isn't on the hot path.

## Scheduler Architecture

### PeriodicTimerScheduler (v2.0+, Default)

The default high-performance scheduler:

```csharp
public class PeriodicTimerScheduler : IScheduler
{
    private readonly ConcurrentPriorityQueue<QueuedTask> _queue;
    private readonly SemaphoreSlim _wakeSignal;
    private readonly CancellationTokenSource _cts;

    private async Task RunAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            // Calculate delay to next task
            var delay = CalculateNextDelay();

            if (delay == Timeout.InfiniteTimeSpan)
            {
                // No tasks - wait for signal
                await _wakeSignal.WaitAsync(_cts.Token);
            }
            else
            {
                // Wait until next task OR new task scheduled
                await _wakeSignal.WaitAsync(delay, _cts.Token);
            }

            // Dequeue and dispatch ready tasks
            await DequeueReadyTasksAsync();
        }
    }

    public void Schedule(QueuedTask task, DateTimeOffset executionTime)
    {
        _queue.Enqueue(task, executionTime);
        _wakeSignal.Release(); // Wake up immediately
    }
}
```

The scheduler uses dynamic delays, sleeping only until the next task is due. When idle, it uses zero CPU. New tasks signal immediately for wake-up, resulting in minimal lock contention compared to the old timer-based approach.

**Scheduler comparison** (qualitative; we haven't benchmarked the `Schedule()`-rate axis yet):

| Scheduler | CPU Usage (Idle) | `Schedule()` lock contention |
|-----------|------------------|------------------------------|
| TimerScheduler (v1.x) | ~0.5-1% | Moderate |
| PeriodicTimerScheduler | ~0% | Low (single priority queue) |
| ShardedScheduler | ~0% | Very Low (N parallel queues) |

> These rows describe the **scheduling** side (how `Schedule()` calls contend on the priority-queue lock),
> not task-execution throughput, which is storage-bound and the same for all three.

### ShardedScheduler (Opt-in)

For a high rate of `Schedule()` calls (the scheduling axis, not a task-execution speedup):

```csharp
public class ShardedScheduler : IScheduler
{
    private readonly PeriodicTimerScheduler[] _shards;
    private readonly int _shardCount;

    public ShardedScheduler(int shardCount, ...)
    {
        _shardCount = shardCount;
        _shards = new PeriodicTimerScheduler[shardCount];

        for (int i = 0; i < shardCount; i++)
        {
            _shards[i] = new PeriodicTimerScheduler(...);
        }
    }

    public void Schedule(QueuedTask task, DateTimeOffset executionTime)
    {
        // Hash-based distribution
        var shard = GetShard(task.PersistenceId);
        _shards[shard].Schedule(task, executionTime);
    }

    private int GetShard(Guid taskId)
    {
        return Math.Abs(taskId.GetHashCode()) % _shardCount;
    }
}
```

Sharding distributes the work across multiple independent schedulers, each operating in parallel. Lock contention on the priority queue gets divided across the shards, and issues in one shard won't affect others. This raises the rate of `Schedule()` calls the scheduler can sustain; it does **not** change task-execution throughput (storage-bound).

The trade-off? You'll use additional memory (around 300 bytes per shard) and additional threads (one per shard), plus debugging becomes slightly more complex.

## Performance Optimizations

EverTask v2.0 includes several major performance improvements:

### Reflection Caching (Dispatcher)

```csharp
// v1.x: Reflection on every dispatch
var wrapperType = typeof(TaskHandlerWrapper<>).MakeGenericType(task.GetType());
var wrapper = (TaskHandlerWrapper)Activator.CreateInstance(wrapperType, task);

// v2.0: Compiled expression cache
private static readonly ConcurrentDictionary<Type, Func<IEverTask, TaskHandlerWrapper>> _wrapperCache = new();

var factory = _wrapperCache.GetOrAdd(task.GetType(), type =>
{
    var wrapperType = typeof(TaskHandlerWrapper<>).MakeGenericType(type);
    var ctor = wrapperType.GetConstructor(new[] { type });
    var param = Expression.Parameter(typeof(IEverTask));
    var newExpr = Expression.New(ctor, Expression.Convert(param, type));
    return Expression.Lambda<Func<IEverTask, TaskHandlerWrapper>>(newExpr, param).Compile();
});

var wrapper = factory(task);
```

We compile the reflection once into an expression factory and cache it per task type, so repeated dispatches of the same type skip the `MakeGenericType` + `Activator.CreateInstance` cost on the hot path.

### Lazy Serialization (Dispatcher)

```csharp
// v1.x: Always serialize
var queuedTask = executor.ToQueuedTask();
if (storage != null)
{
    await storage.AddAsync(queuedTask);
}

// v2.0: Serialize only when needed
QueuedTask? queuedTask = null;

if (storage != null)
{
    queuedTask = executor.ToQueuedTask(); // Only when storage configured
    await storage.AddAsync(queuedTask);
}
```

If you're running in-memory only, why serialize? Now we skip it entirely when storage isn't configured.

### Event Data Caching (Worker Executor)

```csharp
// v1.x: Serialize on every event
var eventData = new EverTaskEventData(
    taskId,
    DateTimeOffset.UtcNow,
    severity,
    task.GetType().AssemblyQualifiedName,
    handler.GetType().AssemblyQualifiedName,
    EverTaskJson.Serialize(task), // Every time!
    message,
    exception);

// v2.0: Cache serialized data
private static readonly ConditionalWeakTable<IEverTask, string> _taskJsonCache = new();
private static readonly ConcurrentDictionary<Type, string> _typeNameCache = new();

var taskJson = _taskJsonCache.GetValue(task, t => EverTaskJson.Serialize(t));
var typeName = _typeNameCache.GetOrAdd(task.GetType(), t => t.AssemblyQualifiedName);
```

Monitoring previously re-serialized the task JSON on every event. Now it serializes once per task (cached in a weak table, collected with the task) and reuses it across that task's events, so a burst of events no longer re-serializes the same payload.

### Handler Options Caching (Worker Executor)

```csharp
// v1.x: Cast and read on every execution
var timeout = (handler as EverTaskHandler<T>)?.Timeout;
var retryPolicy = (handler as EverTaskHandler<T>)?.RetryPolicy;

// v2.0: Cache per handler type
private static readonly ConcurrentDictionary<Type, HandlerOptionsCache> _optionsCache = new();

var options = _optionsCache.GetOrAdd(handler.GetType(), _ => new HandlerOptionsCache
{
    Timeout = (handler as dynamic)?.Timeout,
    RetryPolicy = (handler as dynamic)?.RetryPolicy
});
```

We were reading the timeout/retry options via a runtime cast on every execution. Now they're resolved once per handler type and cached, so each execution reads them from the cache instead of re-casting.

### DbContext Pooling (Storage, v2.0+)

```csharp
// v1.x: DbContext per scope
builder.Services.AddDbContext<TaskStoreDbContext>();

// DbContext factory (NOTE: AddDbContextFactory is NOT pooled)
builder.Services.AddDbContextFactory<TaskStoreDbContext>(options =>
    options.UseSqlServer(connectionString));

// Pooled factory (what actually pools): contexts are reset and reused
builder.Services.AddPooledDbContextFactory<TaskStoreDbContext>(options =>
    options.UseSqlServer(connectionString));
```

The EverTask providers use `AddPooledDbContextFactory`, which leases a reset, reused context per operation.
That cuts per-operation allocation (~-88% per write, ~-71% per task end-to-end) and GC pressure. The win is
on allocations, not raw throughput: on a real database the round-trip dominates wall-clock. Plain
`AddDbContextFactory` does not pool.

### SQL Server Stored Procedures (v2.0+)

```sql
-- Atomic status update + audit insert
CREATE PROCEDURE [EverTask].[SetTaskStatus]
    @TaskId UNIQUEIDENTIFIER,
    @Status INT,
    @AuditMessage NVARCHAR(MAX)
AS
BEGIN
    BEGIN TRANSACTION

    UPDATE [EverTask].[QueuedTasks]
    SET [Status] = @Status
    WHERE [PersistenceId] = @TaskId

    INSERT INTO [EverTask].[TaskAudit] (...)
    VALUES (...)

    COMMIT TRANSACTION
END
```

Status updates used to require multiple roundtrips. The stored procedure cuts that in half.

## Threading Model

### Worker Service Thread

Each queue gets a single background thread that consumes from the `BoundedQueue`:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    await foreach (var queue in _queueManager.GetAllQueues())
    {
        _ = ProcessQueueAsync(queue, stoppingToken); // Fire-and-forget per queue
    }
}

private async Task ProcessQueueAsync(WorkerQueue queue, CancellationToken ct)
{
    await foreach (var task in queue.ReadAllAsync(ct))
    {
        // Execute with configured parallelism
        await _semaphore.WaitAsync(ct);

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteTaskAsync(task, ct);
            }
            finally
            {
                _semaphore.Release();
            }
        }, ct);
    }
}
```

### Scheduler Threads

- **PeriodicTimerScheduler**: 1 background thread
- **ShardedScheduler**: N background threads (1 per shard)

### Task Execution Threads

Tasks execute on the thread pool via `Task.Run`:

```csharp
_ = Task.Run(async () =>
{
    using var scope = _serviceProvider.CreateScope();
    var handler = scope.ServiceProvider.GetRequiredService<IEverTaskHandler<T>>();

    await retryPolicy.Execute(async ct =>
    {
        await handler.Handle(task, ct);
    }, timeoutCts.Token);
}, cancellationToken);
```

We let the .NET thread pool handle load balancing and graceful degradation under pressure. It's efficient and scales well naturally.

## Design Principles

### 1. Async All The Way

```csharp
// All APIs are async
Task<Guid> Dispatch(IEverTask task);
Task Handle(TTask task, CancellationToken cancellationToken);
ValueTask OnCompleted(Guid taskId);
```

Asynchronous APIs from top to bottom maximize scalability for I/O-bound workloads.

### 2. Zero Polling

```csharp
// No polling loops like this:
while (true)
{
    var tasks = await storage.GetPendingTasksAsync();
    if (tasks.Any())
    {
        // Process
    }
    await Task.Delay(1000); // Wasteful!
}

// Instead: Event-driven
await foreach (var task in channel.Reader.ReadAllAsync())
{
    // Process immediately when available
}
```

Event-driven design reduces CPU usage dramatically and improves responsiveness.

### 3. Minimal Allocations

```csharp
// Caching strategies:
- Compiled expression cache for reflection
- ConditionalWeakTable for task JSON
- ConcurrentDictionary for type metadata
- Object pooling for frequently created types
```

Less allocation means less GC pressure and better throughput overall.

### 4. Fail-Safe Defaults

```csharp
// Auto-scaling defaults
MaxDegreeOfParallelism = Environment.ProcessorCount * 2 (min 4)
ChannelCapacity = Environment.ProcessorCount * 200 (min 1000)
RetryPolicy = LinearRetryPolicy(3, 500ms)
```

The defaults are tuned to work well out-of-the-box for most scenarios. You can tweak them, but you probably won't need to.

### 5. Extensibility

```csharp
// Implement your own:
- ITaskStorage (custom persistence)
- IRetryPolicy (custom retry logic)
- IScheduler (custom scheduling)
- ITaskStoreDbContextFactory (custom EF Core integration)
```

Extension points let you adapt EverTask to your specific requirements without forking the library.

## Performance Characteristics

> Indicative, smoke-level numbers from one machine (Ryzen 9 7950X, .NET 10 / EF Core 10, databases in
> Docker/WSL2, audit off, small payloads, 16 concurrent producers). Order of magnitude, not a guarantee:
> measure in your own environment. Full methodology and data: `benchmarks/RESULTS.md`. See also
> [Scalability](scalability.md#measured-performance-indicative).

### Throughput (task execution, storage-bound)

| Backend | Indicative throughput | Notes |
|---------|-----------------------|-------|
| PostgreSQL | ~2,500 tasks/sec | scales with parallelism + connection pool |
| SQLite | ~200 tasks/sec | single writer; parallelism does not help |
| Engine only (no persistence) | hundreds of k/sec | diagnostic ceiling; durability off, not a real-app number |

The durable rows are what real apps see. SQL Server is omitted: our only figure is from Docker under WSL2
(I/O-penalized, not representative), pending a measurement on real hardware. Scheduling rate (`Schedule()`
calls/sec) is a separate axis we haven't benchmarked yet (see [Sharded Scheduler](sharded-scheduler.md)).

### Latency

| Path | Measured | Notes |
|------|----------|-------|
| Dispatch → handler start, PostgreSQL | p50 ~2.3 ms | under concurrent load; includes the SetInProgress write |
| `await Dispatch()` call, in-memory | microseconds | enqueue to the channel, no database |
| `await Dispatch()` call, durable | single-digit ms | a database write on the calling thread |

### Memory (GC allocation per task, not steady-state RAM)

| Path | Allocation/task | Notes |
|------|-----------------|-------|
| Engine only (no persistence) | ~3.2 KB | after the System.Text.Json switch |
| PostgreSQL (durable, tiny payload) | ~73 KB | EF command pipeline + `SqlParameter[]` arrays dominate |
| Per shard (ShardedScheduler) | ~300 bytes | fixed overhead per shard |

Larger payloads add serialization allocation on top (see `benchmarks/RESULTS.md` P-G/P-H). Reducing the
durable per-task footprint is tracked in the storage optimization issues.

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - Tune performance settings
- **[Scalability](scalability.md)** - Multi-queue and sharded scheduler
- **[Getting Started](getting-started.md)** - Setup guide
- **[Resilience](resilience.md)** - Error handling and retries
