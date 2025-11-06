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
- **Handles extreme loads** (>10k tasks/sec without breaking a sweat)
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
         ├──> Create service scope
         ├──> Resolve handler
         ├──> Apply retry policy
         ├──> Apply timeout
         ├──> Execute handler.Handle()
         ├──> Update storage status
         └──> Publish monitoring events
```

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

**Performance Comparison:**

| Scheduler | CPU Usage (Idle) | Lock Contention | Throughput |
|-----------|------------------|-----------------|------------|
| TimerScheduler (v1.x) | ~0.5-1% | Moderate | ~5-10k/sec |
| PeriodicTimerScheduler (v2.0+) | ~0% | Low | ~10-15k/sec |
| ShardedScheduler (v2.0+) | ~0% | Very Low | ~20-40k/sec |

### ShardedScheduler (v2.0+, Opt-in)

For extreme high-load scenarios (>10k Schedule() calls/sec):

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

Sharding distributes the work across multiple independent schedulers, each operating in parallel. Lock contention gets divided by the shard count, and issues in one shard won't affect others. You'll see 2-4x throughput improvement for workloads exceeding 10k Schedule() calls per second.

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

We compile the reflection once and cache it. Repeated dispatches of the same task type went from 150μs to 10μs - a 93% improvement.

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
    JsonConvert.SerializeObject(task), // Every time!
    message,
    exception);

// v2.0: Cache serialized data
private static readonly ConditionalWeakTable<IEverTask, string> _taskJsonCache = new();
private static readonly ConcurrentDictionary<Type, string> _typeNameCache = new();

var taskJson = _taskJsonCache.GetValue(task, t => JsonConvert.SerializeObject(t));
var typeName = _typeNameCache.GetOrAdd(task.GetType(), t => t.AssemblyQualifiedName);
```

Monitoring events were triggering 60k-80k JSON serializations per 10k tasks. Now it's down to around 10-20 - a 99% reduction.

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

We were doing runtime casts on every execution. Now we cache handler options per type, cutting casts from 10k to around 100 per 10k executions.

### DbContext Pooling (Storage, v2.0+)

```csharp
// v1.x: DbContext per scope
builder.Services.AddDbContext<TaskStoreDbContext>();

// v2.0: DbContext factory with pooling
builder.Services.AddDbContextFactory<TaskStoreDbContext>(options =>
    options.UseSqlServer(connectionString));
```

DbContext pooling alone gives us 30-50% faster storage operations and reduces memory allocations significantly.

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

### Throughput

| Scenario | Throughput | Notes |
|----------|-----------|-------|
| In-memory, fire-and-forget | ~50k-100k tasks/sec | Limited by CPU |
| SQL Server persistence | ~5k-10k tasks/sec | Limited by database |
| Scheduled tasks (default) | ~5k-10k Schedule()/sec | PeriodicTimerScheduler |
| Scheduled tasks (sharded) | ~20k-40k Schedule()/sec | ShardedScheduler |

### Latency

| Operation | Latency | Notes |
|-----------|---------|-------|
| Dispatch (in-memory) | ~10-50μs | Reflection cached |
| Dispatch (SQL Server) | ~1-5ms | Database write |
| Schedule | ~10-50μs | Priority queue insert |
| Task execution start | <10ms | From dispatch to handler start |

### Memory

| Component | Memory Usage | Notes |
|-----------|--------------|-------|
| Base overhead | ~1-2 MB | Core services |
| Per task (queued) | ~500 bytes | TaskHandlerExecutor |
| Per task (scheduled) | ~600 bytes | QueuedTask in priority queue |
| Per shard | ~300 bytes | ShardedScheduler |
| Caches (total) | ~5-10 KB | Expression, type, JSON caches |

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - Tune performance settings
- **[Scalability](scalability.md)** - Multi-queue and sharded scheduler
- **[Getting Started](getting-started.md)** - Setup guide
- **[Resilience](resilience.md)** - Error handling and retries
