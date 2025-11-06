---
layout: default
title: Sharded Scheduler
parent: Scalability
nav_order: 2
---

# Sharded Scheduler

For extreme high-load scenarios, EverTask offers a sharded scheduler that splits the workload across multiple independent shards. This reduces lock contention and boosts throughput when you're dealing with massive scheduling loads.

## When to Use

Consider the sharded scheduler if you're hitting:

- Sustained load above 10,000 `Schedule()` calls/second
- Burst spikes exceeding 20,000 `Schedule()` calls/second
- 100,000+ tasks scheduled at once
- High lock contention in profiling (over 5% CPU time spent in scheduler operations)

> **Note**: The default `PeriodicTimerScheduler` (v2.0+) handles most workloads just fine. Only reach for the sharded scheduler when you've measured actual performance problems.

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

## Performance Comparison

| Metric | Default Scheduler | Sharded Scheduler (8 shards) |
|--------|------------------|----------------------------|
| `Schedule()` throughput | ~5-10k/sec | ~15-30k/sec |
| Lock contention | Moderate | Low (8x reduction) |
| Scheduled tasks capacity | ~50-100k | ~200k+ |
| Memory overhead | Baseline | +2-3KB (negligible) |
| Background threads | 1 | N (shard count) |

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
- ✅ 2-4x throughput improvement for high-load scenarios
- ✅ Better spike handling (independent shard processing)
- ✅ Complete failure isolation (issues in one shard don't affect others)
- ✅ Reduced lock contention (divided by shard count)

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
