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

// Reader: each of the N consumers awaits the same channel and runs its item inline
await foreach (var task in channel.Reader.ReadAllAsync(cancellationToken))
{
    await ExecuteTaskAsync(task); // one consumer takes the item, runs it, then reads the next
}
```

Why channels? They give us zero polling overhead through OS-level signaling, built-in backpressure that blocks when full, and lock-free operation for better concurrency under load.

### ConcurrentPriorityQueue

Scheduled and recurring tasks wait in a custom `ConcurrentPriorityQueue<TElement, TPriority>` (`src/EverTask/Scheduler/ConcurrentPriorityQueue.cs`). It wraps the BCL `PriorityQueue<TElement, TPriority>` (a binary heap) behind a single `lock`, and keys each task by its execution time, so the head is always the next one due.

Enqueue and dequeue are O(log n). Removing an arbitrary task (a cancellation, say) is O(n): the heap has no indexed delete, so the queue is rebuilt without that entry. That's fine in practice: the scheduler isn't on the task-execution hot path, and the lock is uncontended at the rates we see. Moving to an indexed heap is tracked as a future optimization but hasn't been applied.

## Scheduler Architecture

### PeriodicTimerScheduler (default)

The default scheduler runs a single background loop (`ProcessScheduledTasksAsync` in `src/EverTask/Scheduler/PeriodicTimerScheduler.cs`). It peeks the head of the priority queue, works out the delay until that task is due, and waits on a `SemaphoreSlim` (`_wakeUpSignal`) for exactly that long. With an empty queue it waits with no timeout, so an idle scheduler burns zero CPU. Scheduling a new task calls `Release()` on the signal: the loop wakes immediately and recomputes the delay instead of oversleeping a task that should run sooner.

Despite the name, there's no `System.Threading.PeriodicTimer` involved; the "period" is the dynamic, signal-driven delay just described.

**Scheduler comparison** (qualitative; we haven't benchmarked the `Schedule()`-rate axis yet):

| Scheduler | CPU Usage (Idle) | `Schedule()` lock contention |
|-----------|------------------|------------------------------|
| TimerScheduler (v1.x) | ~0.5-1% | Moderate |
| PeriodicTimerScheduler | ~0% | Low (single priority queue) |
| ShardedScheduler | ~0% | Very Low (N parallel queues) |

> These rows describe the **scheduling** side (how `Schedule()` calls contend on the priority-queue lock),
> not task-execution throughput, which is storage-bound and the same for all three.

### ShardedScheduler (opt-in)

For a high rate of `Schedule()` calls (the scheduling axis, not a task-execution speedup), the sharded scheduler runs N independent shards, each with its own priority queue, wake signal, and loop, effectively N `PeriodicTimerScheduler`s side by side. A task is routed to a shard by its persistence id: `(uint)persistenceId.GetHashCode() % shardCount`, with the unsigned cast there to dodge the `Math.Abs(int.MinValue)` overflow. Lock contention on the queue is split across the shards, and a stall in one shard doesn't touch the others. This raises the `Schedule()` rate the scheduler can sustain; it does **not** change task-execution throughput, which is storage-bound.

The trade-off is a little more memory (around 300 bytes per shard) and one thread per shard, plus a bit more to keep in your head when debugging.

## Performance Optimizations

The hot paths lean on a few caches, and storage leans on pooled database contexts. The theme is the same throughout: do the reflection, serialization, and option-reading once, then reuse it.

### Caches on the dispatch and execution paths

Resolving a handler used to mean `MakeGenericType` + `Activator.CreateInstance` on every dispatch. The dispatcher now caches a compiled factory per task type in `WrapperFactoryCache` (a `ConcurrentDictionary<Type, Func<TaskHandlerWrapper>>`, `src/EverTask/Dispatcher/Dispatcher.cs`), so repeat dispatches of the same type skip the reflection. Serialization is lazy in the same spirit: with no storage configured there's nothing to persist, so the dispatcher never builds the `QueuedTask` or serializes the payload, and in-memory-only setups pay nothing for it.

Two more caches sit on the execution side. Monitoring events carry the task's JSON, so rather than re-serialize it per event the executor serializes once into a `ConditionalWeakTable<IEverTask, string>` (`TaskJsonCache`, collected with the task) and keeps type-name strings in `TypeStringCache`; a burst of events for one task never re-serializes the same payload. And a handler's timeout, retry policy, and lifecycle hooks are read once per handler type into `HandlerOptionsInternalCache` (which also holds the `OnStarted`/`OnCompleted`/`OnError`/`OnRetry` `MethodInfo`s), instead of being reflected on every run.

### Pooled DbContext + provider-specific hot writes

The EF Core providers (SQL Server, PostgreSQL, SQLite) register `AddPooledDbContextFactory`, not a per-operation context, so each write leases a reset, reused `DbContext`. (The in-memory store keeps its state in process and uses no `DbContext`.) That cuts per-operation allocation sharply (~-88% per write, ~-71% per task end to end) and the GC pressure with it. On a real database the round-trip dominates wall-clock, so this is an allocation win, not a throughput one. Plain `AddDbContextFactory` does *not* pool.

The status write itself is the hot one: it runs on every state change and adds an audit row. Each provider keeps it to as few round-trips as possible and commits the row update and the audit row together, but the mechanism differs:

- **SQL Server** calls a schema-aware stored procedure (`usp_SetTaskStatus`) that does the UPDATE and the conditional audit INSERT in one transaction: a single round-trip. (Recurring bookkeeping has its own procedures, `usp_UpdateCurrentRun` and `usp_CompleteRecurringRun`.)
- **PostgreSQL** uses a writable CTE: one statement that UPDATEs the row and INSERTs the audit from its `RETURNING` set, atomic by construction, also a single round-trip.
- **SQLite** (and the base EF Core path) wraps an `ExecuteUpdate` and the audit insert in an explicit transaction, so two round-trips, since SQLite has neither stored procedures nor that CTE form.

## Threading Model

### Queue consumers

Each worker queue is drained by a fixed pool of dedicated, long-lived consumers (`MaxDegreeOfParallelism` of them) started once at boot (`WorkerService.StartConsumers`, `src/EverTask/Worker/WorkerService.cs`). They all `await foreach` over the same channel and compete for items, and the channel hands each item to exactly one consumer. There's no per-item `Task.Run` and no semaphore on the read side: parallelism *is* the number of consumers, and backpressure comes from the bounded channel.

### Scheduler threads

- **PeriodicTimerScheduler**: one background loop.
- **ShardedScheduler**: one per shard.

### Execution

A consumer runs the task inline on its loop (a thread-pool thread), inside a fresh DI scope created per task, so the `DbContext` and the handler are never shared across concurrent tasks. The thread pool handles load balancing and degradation under pressure. `WorkerExecutor` also keeps an in-flight guard, so the same persistence id can't execute twice concurrently.

## Recovery & at-least-once delivery

A persisted task has to run even if the process dies between persistence and execution. Two mechanisms make that safe, and neither was covered above.

**Startup recovery.** When the host starts, the consumers come up *first*, then `ProcessPendingAsync` runs concurrently with them (`RunRecoveryAsync`). It reads back rows in a recoverable status (`WaitingQueue`, `Queued`, `Pending`, `InProgress`, `ServiceStopped`, plus recurring tasks parked between runs) and re-dispatches them. The order matters: starting recovery before the consumers would deadlock when the backlog is larger than the channel capacity. Recovery only touches rows created before a `recoveryCutoff` captured at startup, and the comparison is strict (`CreatedAtUtc <`), so a live dispatch sharing the same wall-clock tick isn't grabbed and re-run as if it were recovery.

**Double-execution defense.** The delivery contract is at-least-once, and the guard against accidental in-process double delivery is the `TaskDeliveryRegistry` (one per host). It registers each persistence id from the moment it's written to a channel until that delivery terminally ends; a second write of the same id is rejected at the boundary (`EnqueueResult.DuplicateInProcess`), which recovery and live dispatch both treat as an idempotent skip. The discipline is exactly one `End` per delivery, in the outer `finally` of `WorkerExecutor.DoWork`. Because it's at-least-once and not exactly-once, handlers with side effects should still be idempotent, and a stable task key is the usual lever.

A row whose type loads but whose payload won't deserialize stays recoverable for a few attempts, then gets poisoned (marked `Failed`) rather than retried forever. The full invariants live in `src/EverTask/CLAUDE.md` and the [Resilience](resilience.md) guide.

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
    var tasks = await database.QueryPendingTasks(); // hypothetical naive poller, not EverTask
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
- **[Serialization](storage/serialization.md)** - The payload contract and the compile-time analyzer
