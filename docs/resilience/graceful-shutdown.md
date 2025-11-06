---
layout: default
title: Graceful Shutdown
parent: Resilience
nav_order: 7
---

# Handling WorkerService Stops

When your application shuts down and the EverTask WorkerService stops, all running tasks get their `CancellationToken` cancelled.

## Graceful Shutdown

If a task gets interrupted during shutdown, EverTask marks it as `ServiceStopped` and automatically re-queues it when your application restarts:

```csharp
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    try
    {
        await LongRunningOperationAsync(cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        _logger.LogWarning("Task interrupted by service shutdown");

        // Clean up any partial work
        await CleanupAsync();

        // Re-throw so task is marked as ServiceStopped
        throw;
    }
}
```

## Partial Execution Tracking

For long-running tasks, you might want to track progress so they can resume where they left off after a restart:

```csharp
public record ProgressTrackingTask(Guid BatchId, int LastProcessedIndex) : IEverTask;

public class ProgressTrackingHandler : EverTaskHandler<ProgressTrackingTask>
{
    public override async Task Handle(ProgressTrackingTask task, CancellationToken cancellationToken)
    {
        var items = await GetBatchItemsAsync(task.BatchId);

        for (int i = task.LastProcessedIndex; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessItemAsync(items[i], cancellationToken);

            // Save progress periodically
            if (i % 10 == 0)
            {
                await SaveProgressAsync(task.BatchId, i);
            }
        }
    }
}
```

## Idempotent Operations

The best practice is designing tasks that can be safely retried multiple times without causing problems:

```csharp
public override async Task Handle(IdempotentTask task, CancellationToken cancellationToken)
{
    // Check if already processed
    if (await IsAlreadyProcessedAsync(task.OperationId))
    {
        _logger.LogInformation("Task {OperationId} already completed, skipping", task.OperationId);
        return;
    }

    // Process
    await ProcessAsync(task, cancellationToken);

    // Mark as completed
    await MarkAsProcessedAsync(task.OperationId);
}
```

## Next Steps

- **[Cancellation Tokens](cancellation-tokens.md)** - Implement cooperative cancellation
- **[Error Observation](error-observation.md)** - Track and respond to errors
- **[Best Practices](best-practices.md)** - Build robust shutdown strategies
