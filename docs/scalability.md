---
layout: default
title: Scalability
nav_order: 5
has_children: true
---

# Scalability

EverTask is designed to scale from modest workloads to extreme high-load scenarios. This section covers features that help you achieve horizontal and vertical scalability for your background task processing.

## Overview

When your application needs to handle increasing workload volumes, EverTask provides specialized features to maintain performance and reliability:

- **[Multi-Queue Support](multi-queue.md)** - Isolate workloads and optimize resource allocation with multiple independent execution queues
- **[Sharded Scheduler](sharded-scheduler.md)** - Scale scheduling infrastructure to handle extreme loads (>10,000 tasks/second)

## When to Consider Scalability Features

Most applications work well with EverTask's default configuration. Consider these scalability features when you're experiencing:

### Multi-Queue Support
- Need to separate critical operations from background tasks
- Different workload types with varying resource requirements
- Priority-based task execution
- Workload isolation requirements

### Sharded Scheduler
- Sustained scheduling load above 10,000 Schedule() calls per second
- Lock contention in profiling showing scheduler bottlenecks
- 100,000+ tasks scheduled concurrently
- Need for extreme throughput capacity

## Scaling Strategy

### Vertical Scaling (Single Instance)

Start here for most applications:

1. **Optimize Queue Configuration** - Tune parallelism and capacity
2. **Add Multi-Queue Support** - Separate workloads by characteristics
3. **Enable Sharded Scheduler** - Scale scheduling throughput

### Horizontal Scaling (Multiple Instances)

For distributed deployments:

1. **Shared Storage** - Use SQL Server or SQLite with shared database
2. **Queue-Based Distribution** - Multiple instances process from shared queues
3. **Load Balancing** - Natural distribution through persistent storage

> **Note**: Full distributed clustering with leader election and automatic failover is planned for future releases.

## Best Practices

### Start Simple
Begin with the default configuration and measure performance under realistic load before optimizing.

### Measure Before Optimizing
Use the monitoring dashboard to identify actual bottlenecks rather than optimizing prematurely.

### Scale Gradually
Enable multi-queue support first, then consider sharded scheduler only if needed for extreme loads.

### Monitor Continuously
Track queue depths, processing rates, and scheduler throughput to validate scaling decisions.

## Performance Targets

| Scenario | Default Config | With Multi-Queue | With Sharded Scheduler |
|----------|---------------|------------------|----------------------|
| Task execution | ~1k-5k tasks/sec | ~5k-15k tasks/sec | ~5k-15k tasks/sec |
| Scheduling calls | ~5k-10k/sec | ~5k-10k/sec | ~20k-40k/sec |
| Queue isolation | Single queue | Multiple queues | Multiple queues |
| Lock contention | Low | Low | Very Low |

## Next Steps

- **[Multi-Queue Support](multi-queue.md)** - Isolate workloads with independent queues
- **[Sharded Scheduler](sharded-scheduler.md)** - Scale to extreme scheduling loads
- **[Configuration Reference](configuration-reference.md)** - Performance tuning options
- **[Monitoring](monitoring.md)** - Track performance metrics
- **[Architecture](architecture.md)** - Understand internal performance characteristics
