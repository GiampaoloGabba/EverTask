---
layout: default
title: Advanced Features
nav_order: 11
has_children: true
---

# Advanced Features

This guide covers advanced EverTask features for complex scenarios, high-load systems, and sophisticated workflows.

## Overview

EverTask provides powerful advanced capabilities for building resilient, scalable background task systems:

- **[Multi-Queue Support](multi-queue.md)**: Isolate workloads and manage priorities with multiple execution queues
- **[Sharded Scheduler](sharded-scheduler.md)**: Scale to extreme loads with parallel scheduling infrastructure
- **[Task Orchestration](task-orchestration.md)**: Chain tasks, handle cancellation, and reschedule dynamically
- **[Custom Workflows](custom-workflows.md)**: Build sophisticated business processes with state machines and sagas

## When to Use Advanced Features

Most applications do well with EverTask's default configuration. Consider these advanced features when you have specific needs:

### Multi-Queue Support
Use when you need to:
- Separate critical operations from background tasks
- Control resource allocation per workload type
- Optimize parallelism for different task characteristics (I/O vs CPU-bound)

### Sharded Scheduler
Use when you're experiencing:
- 10,000+ schedule calls per second
- Lock contention in profiling
- 100,000+ concurrently scheduled tasks

### Task Orchestration
Use when you need to:
- Chain multiple tasks in sequence
- Cancel long-running operations
- Implement complex multi-step workflows

### Custom Workflows
Use when building:
- Multi-stage business processes
- Saga patterns with compensation
- State machine-based workflows

## Best Practices

### Multi-Queue

1. **Profile Before Optimizing**: Stick with the default queue unless you have real performance needs
2. **Separate by Characteristics**: Group tasks by I/O vs CPU, priority, or how critical they are
3. **Monitor Queue Depths**: Watch how full your queues get and adjust capacities accordingly
4. **Test Fallback Behavior**: Make sure queues degrade gracefully when full

### Sharded Scheduler

1. **Measure First**: Don't use this unless you've measured actual performance problems
2. **Start Conservative**: Begin with 4-8 shards and increase only if needed
3. **Monitor Metrics**: Keep an eye on scheduler throughput and lock contention
4. **Consider CPU Count**: Shard count usually makes sense when aligned with CPU cores

### Continuations

1. **Keep Chains Short**: Long chains are debugging nightmares
2. **Store Correlation IDs**: Use GUIDs to trace through multiple tasks
3. **Handle Failures Gracefully**: Always implement `OnError` to clean up after failures
4. **Consider Idempotency**: Tasks might get retried or run multiple times

### Cancellation

1. **Check CancellationToken**: Respect the cancellation token in your handlers
2. **Clean Up Resources**: Dispose resources properly when cancelled
3. **Log Cancellations**: Track when and why tasks get cancelled
4. **Test Cancellation**: Make sure your handlers actually handle cancellation correctly

## Next Steps

- **[Resilience](resilience.md)** - Retry policies and error handling
- **[Monitoring](monitoring.md)** - Track task execution
- **[Configuration Reference](configuration-reference.md)** - All configuration options
- **[Architecture](architecture.md)** - How EverTask works internally
