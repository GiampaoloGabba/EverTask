---
layout: default
title: Sharded Scheduler
parent: Scalability
nav_order: 2
---

# Sharded Scheduler

For workloads that register timed or recurring tasks at a very high rate, EverTask offers a sharded scheduler that splits the scheduler's priority queue across multiple independent shards, reducing lock contention on `Schedule()`.

> The sharded scheduler is a scheduling-side optimization. It raises the rate of `Schedule()` calls you can sustain and the number of tasks you can keep scheduled at once. It does not make tasks execute faster; execution is storage-bound (see [Scalability](scalability.md#measured-performance-indicative)). If your bottleneck is task execution, sharding the scheduler won't help.

## When to Use

Consider the sharded scheduler if you're hitting:

- A high sustained rate of `Schedule()` calls (timed/recurring registrations)
- Bursts of scheduling activity the single priority queue can't absorb
- A very large number of tasks scheduled concurrently
- Profiling that shows significant CPU time in the scheduler's priority-queue lock

> **Note**: The default `PeriodicTimerScheduler` handles most workloads just fine. Reach for the sharded scheduler only when profiling shows the *scheduler* (not the storage) is the bottleneck.

## Configuration

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: 8) // Recommended: 4-16 shards
)
.AddSqlServerStorage(connectionString);
```

### Auto-scaling

Automatically scale based on CPU cores:

```csharp
.UseShardedScheduler() // Uses Environment.ProcessorCount (minimum 4 shards)
```

### Manual Configuration

```csharp
.UseShardedScheduler(shardCount: Environment.ProcessorCount) // Scale with CPUs
```

## How it compares (architectural, not yet benchmarked)

| Metric | Default (PeriodicTimer) | Sharded (N shards) |
|--------|-------------------------|--------------------|
| Priority queues | 1 (single lock) | N (contention divided ~N) |
| Background timers/threads | 1 | N |
| Memory overhead | baseline | ~300 B per shard |
| `Schedule()`-call contention under load | higher | lower |
| Task-execution throughput | baseline | same (storage-bound) |

> Those differences are an architectural property, not a measured multiplier: fewer threads contend on each
> queue, and work spreads across N parallel queues. We haven't benchmarked the scheduling axis yet. Expect
> lower scheduler contention, not a task-execution speedup.

## How It Works

The sharded scheduler uses hash-based distribution:

1. Each task gets assigned to a shard based on its `PersistenceId` hash
2. Tasks distribute uniformly across all shards
3. Each shard runs independently with its own timer and priority queue
4. Shards process tasks in parallel without stepping on each other's toes

```csharp
// Task distribution example
Task A (ID: abc123) → Shard 0
Task B (ID: def456) → Shard 3
Task C (ID: ghi789) → Shard 7
// ... uniform distribution
```

## Trade-offs

**Pros:**
- ✅ Lower lock contention on `Schedule()` (the single priority-queue lock is split across shards)
- ✅ Better burst handling (independent shard processing)
- ✅ Complete failure isolation (issues in one shard don't affect others)
- ✅ Higher sustainable `Schedule()`-call rate and scheduled-task count (scheduling axis only)

**Cons:**
- ❌ Additional memory (~300 bytes per shard - negligible)
- ❌ Additional background threads (1 per shard)
- ❌ Slightly more complex debugging (multiple timers)

## High-Load Example

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: Environment.ProcessorCount)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(10000)
)
.AddSqlServerStorage(connectionString);
```

## Migration

Switching between default and sharded schedulers is painless:

- Both implement the same `IScheduler` interface
- Task execution behavior stays the same
- Storage format is compatible
- No breaking changes in handlers

> **Tip**: Start with the default scheduler and only switch to sharded if you're actually hitting performance bottlenecks. The default scheduler handles most workloads well.

## Next Steps

- [Multi-Queue Support](multi-queue.md) - Isolate workloads with multiple queues
- [Task Orchestration](task-orchestration.md) - Chain and coordinate tasks
- [Monitoring](monitoring.md) - Track scheduler performance
