---
layout: default
title: Task Creation
parent: Getting Started
nav_order: 1
---

# Task Creation

This guide covers everything you need to know about creating and configuring tasks and handlers in EverTask.

## Table of Contents

- [Creating Task Requests](#creating-task-requests)
- [Creating Task Handlers](#creating-task-handlers)
- [Lifecycle Hooks](#lifecycle-hooks)
- [Handler Configuration](#handler-configuration)
- [Best Practices](#best-practices)

## Creating Task Requests

Task requests are straightforward data objects that implement `IEverTask`. Think of them as the instructions for what work needs to be done, bundled with all the necessary parameters.

### Basic Request

```csharp
public record ProcessOrderTask(int OrderId, string CustomerEmail) : IEverTask;
```

### Complex Request with Multiple Parameters

```csharp
public record GenerateReportTask(
    Guid ReportId,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    string Format,
    List<string> Recipients) : IEverTask;
```

### Request Design Guidelines

**DO:**
- ✅ Use `record` types for immutability
- ✅ Use primitive types whenever possible (int, string, DateTime, etc.)
- ✅ Keep data structures simple and flat
- ✅ Use `List<T>` or arrays for collections
- ✅ Include all necessary context in the request

**DON'T:**
- ❌ Include services, DbContexts, or other dependencies
- ❌ Use complex object graphs with circular references
- ❌ Include non-serializable types
- ❌ Store sensitive data in plain text (consider encryption for sensitive fields)

> **Why these guidelines?** Since EverTask serializes tasks to JSON for persistence, simple and flat structures will serialize reliably and deserialize correctly even after application restarts.

## Creating Task Handlers

Handlers define the logic for executing tasks. They inherit from `EverTaskHandler<TTask>` and implement the `Handle` method.

### Basic Handler

```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    private readonly IOrderService _orderService;
    private readonly ILogger<ProcessOrderHandler> _logger;

    public ProcessOrderHandler(
        IOrderService orderService,
        ILogger<ProcessOrderHandler> logger)
    {
        _orderService = orderService;
        _logger = logger;
    }

    public override async Task Handle(
        ProcessOrderTask task,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Processing order {OrderId}", task.OrderId);

        await _orderService.ProcessAsync(task.OrderId, cancellationToken);

        _logger.LogInformation("Order {OrderId} processed successfully", task.OrderId);
    }
}
```

### Dependency Injection

Handlers support dependency injection out of the box - just inject whatever services you need through the constructor:

```csharp
public class SendNotificationHandler : EverTaskHandler<SendNotificationTask>
{
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly IDbContext _dbContext;
    private readonly ILogger<SendNotificationHandler> _logger;

    public SendNotificationHandler(
        IEmailService emailService,
        ISmsService smsService,
        IDbContext dbContext,
        ILogger<SendNotificationHandler> logger)
    {
        _emailService = emailService;
        _smsService = smsService;
        _dbContext = dbContext;
        _logger = logger;
    }

    public override async Task Handle(
        SendNotificationTask task,
        CancellationToken cancellationToken)
    {
        // Handler implementation
    }
}
```

> **Note:** Each handler execution gets its own service scope, so scoped services (like DbContext) are properly isolated per task.

## Lifecycle Hooks

EverTask gives you optional hooks to observe and react to task events throughout their lifecycle.

### OnStarted

Called right when a task begins execution:

```csharp
public class MyTaskHandler : EverTaskHandler<MyTask>
{
    private readonly ILogger<MyTaskHandler> _logger;

    public MyTaskHandler(ILogger<MyTaskHandler> logger)
    {
        _logger = logger;
    }

    public override ValueTask OnStarted(Guid taskId)
    {
        _logger.LogInformation("Task {TaskId} started at {Time}", taskId, DateTime.UtcNow);
        return ValueTask.CompletedTask;
    }

    public override async Task Handle(MyTask task, CancellationToken cancellationToken)
    {
        // Task execution logic
    }
}
```

### OnCompleted

Called when a task completes successfully:

```csharp
public override ValueTask OnCompleted(Guid taskId)
{
    _logger.LogInformation("Task {TaskId} completed successfully", taskId);

    // Could dispatch follow-up tasks here
    // await _dispatcher.Dispatch(new FollowUpTask(taskId));

    return ValueTask.CompletedTask;
}
```

### OnError

Called when a task ultimately fails after exhausting all retry attempts:

```csharp
public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    _logger.LogError(
        exception,
        "Task {TaskId} failed: {Message}",
        taskId,
        message);

    // Could send alerts, update status, dispatch compensation tasks, etc.

    return ValueTask.CompletedTask;
}
```

### DisposeAsyncCore

Called during handler disposal for any cleanup you need:

```csharp
protected override ValueTask DisposeAsyncCore()
{
    _logger.LogInformation("Handler resources being cleaned up");

    // Perform any custom cleanup

    return base.DisposeAsyncCore();
}
```

### Complete Lifecycle Example

```csharp
public class CompleteLifecycleHandler : EverTaskHandler<CompleteLifecycleTask>
{
    private readonly ILogger<CompleteLifecycleHandler> _logger;
    private readonly ITaskDispatcher _dispatcher;

    public CompleteLifecycleHandler(
        ILogger<CompleteLifecycleHandler> logger,
        ITaskDispatcher dispatcher)
    {
        _logger = logger;
        _dispatcher = dispatcher;
    }

    public override ValueTask OnStarted(Guid taskId)
    {
        _logger.LogInformation("Task {TaskId} started", taskId);
        return ValueTask.CompletedTask;
    }

    public override async Task Handle(
        CompleteLifecycleTask task,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Executing task logic for {Data}", task.Data);

        // Simulate work
        await Task.Delay(1000, cancellationToken);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        _logger.LogInformation("Task {TaskId} completed, dispatching follow-up", taskId);

        // Chain to next task
        await _dispatcher.Dispatch(new FollowUpTask());
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Task {TaskId} failed: {Message}", taskId, message);

        // Could dispatch error handling task, send alerts, etc.

        return ValueTask.CompletedTask;
    }

    protected override ValueTask DisposeAsyncCore()
    {
        _logger.LogInformation("Handler disposed");
        return base.DisposeAsyncCore();
    }
}
```

## Handler Configuration

You can customize individual handlers by overriding their virtual properties.

### Custom Timeout

Need more (or less) time for a particular handler? Override the global timeout:

```csharp
public class LongRunningTaskHandler : EverTaskHandler<LongRunningTask>
{
    // This handler gets 10 minutes instead of the global default
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(10);

    public override async Task Handle(LongRunningTask task, CancellationToken cancellationToken)
    {
        // Long-running work here
        // CancellationToken will be cancelled after 10 minutes
    }
}
```

### Custom Retry Policy

Some tasks need more aggressive retries than others. You can override the global policy per handler:

```csharp
public class CriticalTaskHandler : EverTaskHandler<CriticalTask>
{
    // Retry 5 times with 1 second between attempts
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(1));

    public override async Task Handle(CriticalTask task, CancellationToken cancellationToken)
    {
        // Critical work with more aggressive retries
    }
}
```

See [Resilience & Error Handling](resilience.md) for more details on retry policies.

### Queue Routing

Want to isolate certain workloads? Route tasks to specific queues:

```csharp
public class HighPriorityHandler : EverTaskHandler<HighPriorityTask>
{
    public override string? QueueName => "high-priority";

    public override async Task Handle(HighPriorityTask task, CancellationToken cancellationToken)
    {
        // This task runs in the "high-priority" queue
    }
}
```

See [Multi-Queue Support](multi-queue.md) for more details.

### Combined Configuration

```csharp
public class CustomizedHandler : EverTaskHandler<CustomizedTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(5);
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(2));
    public override string? QueueName => "background";

    public override async Task Handle(CustomizedTask task, CancellationToken cancellationToken)
    {
        // Custom timeout, retry policy, and queue
    }
}
```

## Best Practices

### Task Design

1. **Keep tasks focused** - Each task should do one thing well. Breaking work into smaller tasks makes them easier to test and debug.
2. **Make tasks idempotent** - Design them so they're safe to retry if execution gets interrupted partway through.
3. **Include correlation IDs** - When you have workflows that chain multiple tasks together, correlation IDs make tracing much easier.
4. **Version your tasks** - If your task structure might evolve over time, include a version field. Future you will thank present you.

```csharp
// Good: Focused, idempotent, traceable
public record ProcessPaymentTask(
    Guid PaymentId,
    Guid OrderId,
    Guid CorrelationId,
    int Version = 1) : IEverTask;
```

### Handler Design

1. **Use CancellationToken** - Always check and respect the cancellation token, especially before expensive operations.
2. **Log appropriately** - Use Info for start/complete, Error for actual failures. Don't spam the logs with Debug messages nobody will read.
3. **Handle errors gracefully** - Let retry policies handle transient failures. Don't catch and swallow exceptions that should trigger retries.
4. **Keep handlers stateless** - Each execution should be independent. Don't store state between task executions.

```csharp
public class WellDesignedHandler : EverTaskHandler<WellDesignedTask>
{
    private readonly IService _service;
    private readonly ILogger<WellDesignedHandler> _logger;

    public WellDesignedHandler(IService service, ILogger<WellDesignedHandler> logger)
    {
        _service = service;
        _logger = logger;
    }

    public override async Task Handle(WellDesignedTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting task {TaskId}", task.Id);

        try
        {
            // Check cancellation before expensive operations
            cancellationToken.ThrowIfCancellationRequested();

            var result = await _service.ProcessAsync(task, cancellationToken);

            _logger.LogInformation("Task {TaskId} completed with result {Result}", task.Id, result);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task {TaskId} was cancelled", task.Id);
            throw; // Re-throw to mark as cancelled
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Task {TaskId} failed", task.Id);
            throw; // Re-throw to trigger retry policy
        }
    }
}
```

### Performance Considerations

1. **Async all the way** - Use async/await for all I/O operations. Don't mix sync and async code.
2. **Avoid blocking calls** - Never use `.Wait()` or `.Result`. They'll deadlock in some contexts and hurt scalability in others.
3. **Batch database operations** - When you're processing multiple items, batch your database calls instead of hitting the DB for each item.
4. **Use appropriate queues** - CPU-intensive tasks belong in a queue with low parallelism so they don't starve I/O-bound tasks.

```csharp
// Good: Fully async, batched operations
public override async Task Handle(BatchProcessTask task, CancellationToken cancellationToken)
{
    var items = await _repository.GetBatchAsync(task.BatchId, cancellationToken);

    // Process in batches for better performance
    foreach (var batch in items.Chunk(100))
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _service.ProcessBatchAsync(batch, cancellationToken);
    }
}
```

## Next Steps

- **[Task Dispatching](task-dispatching.md)** - Learn how to dispatch tasks
- **[Recurring Tasks](recurring-tasks.md)** - Schedule recurring tasks
- **[Resilience](resilience.md)** - Configure retry policies and timeouts
- **[Advanced Features](advanced-features.md)** - Multi-queue, continuations, and more
