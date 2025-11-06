---
layout: default
title: Task Orchestration
nav_order: 8
has_children: true
---

# Task Orchestration

This guide covers techniques for coordinating and managing complex task execution workflows. Learn how to chain tasks, build multi-step processes, and orchestrate sophisticated business workflows.

## Overview

EverTask provides powerful capabilities for building complex, multi-step workflows:

- **[Task Orchestration](task-orchestration.md)**: Chain tasks, handle cancellation, and reschedule dynamically
- **[Custom Workflows](custom-workflows.md)**: Build sophisticated business processes with state machines and sagas

## When to Use Task Orchestration

Most applications start with simple, independent tasks. Consider orchestration when you need to:

### Task Chaining
- Execute multiple tasks in sequence
- Pass data between related tasks
- Branch workflows based on task results
- Implement conditional task execution

### Complex Workflows
- Multi-stage business processes (order processing, payment flows)
- Saga patterns with compensation and rollback
- State machine-based workflows
- Parallel task execution with coordination

## Best Practices

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

- **[Task Orchestration](task-orchestration.md)** - Continuations, cancellation, and rescheduling
- **[Custom Workflows](custom-workflows.md)** - Build sophisticated workflows
- **[Resilience](resilience.md)** - Retry policies and error handling
- **[Scalability](scalability.md)** - Multi-queue support and sharded scheduler
- **[Monitoring](monitoring.md)** - Track task execution
- **[Configuration Reference](configuration-reference.md)** - All configuration options
