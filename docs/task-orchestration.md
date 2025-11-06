---
layout: default
title: Task Orchestration
parent: Advanced Features
nav_order: 3
---

# Task Orchestration

This guide covers techniques for coordinating and managing task execution workflows, including continuations, cancellation, and rescheduling patterns.

## Task Continuations

You can chain tasks together for sequential execution using the lifecycle methods in task handlers.

### Basic Continuation

```csharp
public class FirstTaskHandler : EverTaskHandler<FirstTask>
{
    private readonly ITaskDispatcher _dispatcher;

    public FirstTaskHandler(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override async Task Handle(FirstTask task, CancellationToken cancellationToken)
    {
        // First task logic
        await ProcessDataAsync(task.Data, cancellationToken);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        // Dispatch next task in the chain
        await _dispatcher.Dispatch(new SecondTask());
    }
}
```

### Passing Context

To pass data between tasks, use task parameters:

```csharp
public class DataProcessingHandler : EverTaskHandler<DataProcessingTask>
{
    private readonly ITaskDispatcher _dispatcher;

    public DataProcessingHandler(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public override async Task Handle(DataProcessingTask task, CancellationToken cancellationToken)
    {
        var result = await ProcessAsync(task.InputData, cancellationToken);

        // Store result ID for next task
        _continuationData = new { ResultId = result.Id, CorrelationId = task.CorrelationId };
    }

    private object? _continuationData;

    public override async ValueTask OnCompleted(Guid taskId)
    {
        if (_continuationData != null)
        {
            await _dispatcher.Dispatch(new NotificationTask(
                ((dynamic)_continuationData).ResultId,
                ((dynamic)_continuationData).CorrelationId));
        }
    }
}
```

### Conditional Continuations

Branch your workflow based on task results:

```csharp
public class PaymentProcessingHandler : EverTaskHandler<ProcessPaymentTask>
{
    private readonly ITaskDispatcher _dispatcher;
    private bool _paymentSuccessful;

    public override async Task Handle(ProcessPaymentTask task, CancellationToken cancellationToken)
    {
        _paymentSuccessful = await ProcessPaymentAsync(task, cancellationToken);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        if (_paymentSuccessful)
        {
            await _dispatcher.Dispatch(new SendReceiptTask(taskId));
            await _dispatcher.Dispatch(new FulfillOrderTask(taskId));
        }
        else
        {
            await _dispatcher.Dispatch(new SendPaymentFailedEmailTask(taskId));
        }
    }
}
```

### Error Handling with Continuations

When things go wrong, dispatch compensation tasks:

```csharp
public class OrderProcessingHandler : EverTaskHandler<ProcessOrderTask>
{
    private readonly ITaskDispatcher _dispatcher;

    public override async Task Handle(ProcessOrderTask task, CancellationToken cancellationToken)
    {
        await ProcessOrderAsync(task.OrderId, cancellationToken);
    }

    public override async ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Order processing failed, initiating rollback");

        // Dispatch compensation task
        await _dispatcher.Dispatch(new RollbackOrderTask(taskId));

        // Notify administrators
        await _dispatcher.Dispatch(new SendAlertTask(taskId, message));
    }
}
```

### Complex Workflows

Build multi-stage workflows by chaining continuations:

```csharp
// Stage 1: Validate order
public class ValidateOrderHandler : EverTaskHandler<ValidateOrderTask>
{
    public override async ValueTask OnCompleted(Guid taskId)
    {
        await _dispatcher.Dispatch(new ProcessPaymentTask(_task.OrderId));
    }
}

// Stage 2: Process payment
public class ProcessPaymentHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override async ValueTask OnCompleted(Guid taskId)
    {
        await _dispatcher.Dispatch(new ReserveInventoryTask(_task.OrderId));
    }
}

// Stage 3: Reserve inventory
public class ReserveInventoryHandler : EverTaskHandler<ReserveInventoryTask>
{
    public override async ValueTask OnCompleted(Guid taskId)
    {
        await _dispatcher.Dispatch(new ShipOrderTask(_task.OrderId));
        await _dispatcher.Dispatch(new SendConfirmationEmailTask(_task.OrderId));
    }
}
```

## Task Cancellation

You can cancel tasks before they start, or signal running tasks to stop gracefully.

### Cancelling Pending Tasks

```csharp
// Dispatch a delayed task
Guid taskId = await _dispatcher.Dispatch(
    new ProcessDataTask(data),
    TimeSpan.FromMinutes(10));

// Store the ID
await _database.SaveTaskIdAsync(operationId, taskId);

// User cancels the operation
await _dispatcher.Cancel(taskId);
```

### Cancelling Running Tasks

When you call `Cancel` on a running task, it signals the handler's `CancellationToken`. Your handler needs to check this token regularly for cooperative cancellation to work:

```csharp
public class LongRunningTaskHandler : EverTaskHandler<LongRunningTask>
{
    public override async Task Handle(LongRunningTask task, CancellationToken cancellationToken)
    {
        for (int i = 0; i < 1000; i++)
        {
            // Check cancellation regularly
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessItemAsync(i, cancellationToken);
        }
    }
}
```

### Bulk Cancellation

To cancel multiple related tasks, loop through their IDs:

```csharp
// Dispatch batch of tasks
var taskIds = new List<Guid>();
foreach (var item in batch)
{
    var taskId = await _dispatcher.Dispatch(new ProcessItemTask(item));
    taskIds.Add(taskId);
}

// Cancel all if needed
foreach (var taskId in taskIds)
{
    await _dispatcher.Cancel(taskId);
}
```

### Cancellation in Lifecycle Hooks

Handle cancellation gracefully in your lifecycle hooks:

```csharp
public class CancellableTaskHandler : EverTaskHandler<CancellableTask>
{
    public override async Task Handle(CancellableTask task, CancellationToken cancellationToken)
    {
        try
        {
            await LongRunningOperationAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task was cancelled");
            throw; // Re-throw to mark as cancelled
        }
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        if (exception is OperationCanceledException)
        {
            _logger.LogInformation("Task {TaskId} was cancelled by user", taskId);
            // Could dispatch cleanup tasks here
        }

        return ValueTask.CompletedTask;
    }
}
```

## Task Rescheduling

Sometimes you need to reschedule tasks dynamically based on runtime conditions:

```csharp
public class RetryableTaskHandler : EverTaskHandler<RetryableTask>
{
    private readonly ITaskDispatcher _dispatcher;

    public override async Task Handle(RetryableTask task, CancellationToken cancellationToken)
    {
        var result = await TryProcessAsync(task, cancellationToken);

        if (result.ShouldRetry && task.RetryCount < 5)
        {
            // Reschedule with exponential backoff
            var delay = TimeSpan.FromSeconds(Math.Pow(2, task.RetryCount));

            await _dispatcher.Dispatch(
                new RetryableTask(task.Data, task.RetryCount + 1),
                delay);
        }
    }
}
```

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

- [Custom Workflows](custom-workflows.md) - Build sophisticated workflows
- [Resilience](resilience.md) - Retry policies and error handling
- [Monitoring](monitoring.md) - Track task execution
