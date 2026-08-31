---
layout: default
title: Scalability
nav_order: 5
has_children: true
---

# Scalability

EverTask scales from modest workloads to high-load ones. This section covers the features that scale background task processing on a single host: queues, parallelism, sharding and the storage knobs. Running the scheduler on several hosts at once is a separate epic (see the single-active-host note in the durable-occurrences guide).

## Overview

When your application's workload grows, these features help:

- **[Multi-Queue Support](multi-queue.md)** - Isolate workloads across multiple independent execution queues
- **[Sharded Scheduler](sharded-scheduler.md)** - Reduce scheduler lock contention under a high rate of `Schedule()` calls (a scheduling-side concern, distinct from task-execution throughput)
- **[Keyed Rate Limiting](rate-limiting.md)** - Throttle task execution per tenant/account/resource against external API limits, without blocking workers or other keys

> **Two axes, often confused.** Task execution throughput (tasks/sec actually run end to end) is bound by
> your storage: each task makes a few synchronous database round-trips, so the database sets the ceiling.
> Scheduling load (`Schedule()` calls per second, and how many timed tasks sit in memory at once) is a
> separate thing, bound by CPU and lock contention. Multi-queue and the sharded scheduler help the
> scheduling and isolation side. Neither makes tasks execute faster; that is the database's job.

## When to Consider Scalability Features

Most applications work well with EverTask's default configuration. Consider these scalability features when you're experiencing:

### Multi-Queue Support
- Need to separate critical operations from background tasks
- Different workload types with varying resource requirements
- Priority-based task execution
- Workload isolation requirements

### Sharded Scheduler
- A high sustained rate of `Schedule()` calls (timed/recurring registrations)
- Lock contention in profiling pointing at the scheduler's priority queue
- A very large number of tasks scheduled concurrently
- Note: this is the scheduling side only. It does not make tasks execute faster; that stays storage-bound
  (see [Performance](#measured-performance-indicative) below)

### Keyed Rate Limiting
- Tasks calling external APIs with per-tenant/per-account rate limits
- A noisy key must never delay other keys (no head-of-line blocking)
- Frequency constraints that are NOT parallelism constraints (workers stay free while a key waits for budget)

## Scaling Strategy

### Vertical Scaling (Single Instance)

Start here for most applications:

1. **Optimize Queue Configuration** - Tune parallelism and capacity
2. **Add Multi-Queue Support** - Separate workloads by characteristics
3. **Enable Sharded Scheduler** - Scale scheduling throughput

### Horizontal Scaling (Multiple Instances)

**EverTask executes on ONE active instance.** There is no cross-process claim on a task, so two live instances
pointed at the same database both recover the same pending row and both run it. Sharing a connection string is
not how you distribute work here.

What a second instance is good for is availability: run it as a **standby** that is deployed and ready but
whose EverTask host is not started, and start it when the active one is gone. Everything is in the database,
so the standby picks up exactly what the active instance left behind.

Two things that ARE safe across instances, because the database enforces them:

- **Materialization of durable occurrences**: the unique index on (schedule, slot) means two instances
  racing the same backlog still produce one row per slot.
- **Idempotent registration by task key**: the unique index on the key means a schedule registered at every
  startup exists once, whichever instance got there first.

Neither of those makes execution distributed. An occurrence materialized once can still be DELIVERED twice if
two instances are live.

> **Planned**: a distributed execution lease (a claim taken on a task before it runs, with fencing and
> expiry) is a separate epic that will apply to every task, not only to recurring ones. Until it exists, size
> a single instance vertically (parallelism, multi-queue, sharded scheduler) and keep the second one on
> standby.

## Best Practices

### Start Simple
Begin with the default configuration and measure performance under realistic load before optimizing.

### Measure Before Optimizing
Use the monitoring dashboard to identify actual bottlenecks rather than optimizing prematurely.

### Scale Gradually
Enable multi-queue support first, then consider sharded scheduler only if needed for extreme loads.

### Monitor Continuously
Track queue depths, processing rates, and scheduler throughput to validate scaling decisions.

## Measured performance (indicative)

> **Smoke-level numbers, one machine.** Measured on a Ryzen 9 7950X, .NET 10 / EF Core 10, databases in
> Docker (WSL2), audit off, small payloads, 16 concurrent producers. They show *order of magnitude*, not a
> guarantee: your throughput depends on hardware, network latency to the database, payload size and audit
> level. **Measure in your own environment.** Methodology and full data: `benchmarks/RESULTS.md`.

### Task execution (storage-bound)

Every task makes a few synchronous database round-trips (persist on dispatch, plus status transitions), so
the database sets the ceiling, not EverTask. Indicative throughput, audit off:

| Storage backend | Indicative task-execution throughput |
|-----------------|--------------------------------------|
| PostgreSQL | ~2,500 tasks/sec |

SQLite is a single writer (a local file), so it runs much lower and gets nothing from parallelism: around
200/sec on this machine. SQL Server is left out on purpose. The only figure we have comes from Docker under
WSL2, where its I/O is heavily penalized and not representative, so we'll publish a number once it's
measured on real hardware. MySQL and MariaDB run the same server-side relational profile as PostgreSQL
(recovery and cleanup run on the server); we haven't benchmarked them separately yet.

Neither multi-queue nor the sharded scheduler raises this number; they handle scheduling and isolation, not
execution. The levers for execution throughput live on the storage side: a faster database, fewer
round-trips per task, lower network latency, and keeping audit (and the persistent proxy logger) off when
you don't need them. Every audit row and every persisted log line is an extra synchronous write, so a higher
audit level lowers task throughput. That's expected, not a bug (see [Audit Configuration](storage/audit-configuration.md#database-impact)).

### Scheduling load (CPU and contention)

A separate axis: how fast the scheduler ingests and manages timed or recurring registrations, and how many
it holds at once. This is independent of storage. The sharded scheduler targets exactly this. It spreads the
scheduler's priority queue across shards to cut lock contention when `Schedule()` is called at a high rate.
It does not speed up task execution.

> We haven't benchmarked the scheduling rate yet. The sharded scheduler is an architectural option for high
> `Schedule()`-call rates and large scheduled-task counts, not a task-execution speedup. See
> [Sharded Scheduler](sharded-scheduler.md).

## Next Steps

- **[Multi-Queue Support](multi-queue.md)** - Isolate workloads with independent queues
- **[Sharded Scheduler](sharded-scheduler.md)** - Scale to extreme scheduling loads
- **[Keyed Rate Limiting](rate-limiting.md)** - Per-key throttling against external API limits
- **[Configuration Reference](configuration-reference.md)** - Performance tuning options
- **[Monitoring](monitoring.md)** - Track performance metrics
- **[Architecture](architecture.md)** - Understand internal performance characteristics
