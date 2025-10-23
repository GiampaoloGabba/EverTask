---
layout: default
title: Advanced Features
nav_order: 11
---

# Advanced Features

This guide covers advanced EverTask features for complex scenarios, high-load systems, and sophisticated workflows.

## Table of Contents

- [Multi-Queue Support](#multi-queue-support)
- [Sharded Scheduler](#sharded-scheduler)
- [Task Continuations](#task-continuations)
- [Task Cancellation](#task-cancellation)
- [Custom Workflows](#custom-workflows)
- [Task Execution Log Capture](#task-execution-log-capture)

## Multi-Queue Support

EverTask supports multiple execution queues so you can isolate workloads, manage priorities, and control resources independently. You might want to keep payment processing separate from bulk email jobs, or run reports on their own queue with limited parallelism to avoid hogging CPU.

### Why Use Multiple Queues?

- **Workload Isolation**: Keep background tasks from blocking critical operations
- **Resource Prioritization**: Give more workers to high-priority queues
- **Capacity Management**: Size different queues based on workload type
- **Performance Optimization**: Tune parallelism to match task characteristics (I/O-bound vs CPU-bound)

### Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
// Configure the default queue
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(100)
    .SetFullBehavior(QueueFullBehavior.Wait))

// Add a high-priority queue for critical tasks
.AddQueue("high-priority", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(200)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(5)))

// Add a background queue for CPU-intensive tasks
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2)  // Limit CPU usage
    .SetChannelCapacity(50)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// Configure the recurring queue (created automatically)
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(3)
    .SetChannelCapacity(75))

.AddSqlServerStorage(connectionString);
```

### Queue Configuration Options

#### MaxDegreeOfParallelism

Controls how many tasks run concurrently in this queue:

```csharp
.AddQueue("api-calls", q => q
    .SetMaxDegreeOfParallelism(20)) // High for I/O-bound tasks

.AddQueue("cpu-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount)) // Limited for CPU-bound
```

#### ChannelCapacity

Sets the maximum number of tasks that can wait in the queue:

```csharp
.AddQueue("email", q => q
    .SetChannelCapacity(5000)) // Large capacity for bulk operations

.AddQueue("critical", q => q
    .SetChannelCapacity(100)) // Smaller capacity for critical path
```

#### QueueFullBehavior

Determines what happens when the queue is full:

```csharp
// Wait: Block until space is available (default)
.AddQueue("important", q => q
    .SetFullBehavior(QueueFullBehavior.Wait))

// FallbackToDefault: Try the default queue if this queue is full
.AddQueue("optional", q => q
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// ThrowException: Throw immediately if full
.AddQueue("strict", q => q
    .SetFullBehavior(QueueFullBehavior.ThrowException))
```

#### DefaultRetryPolicy

Sets the default retry policy for all tasks in this queue:

```csharp
.AddQueue("resilient", q => q
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))))
```

#### DefaultTimeout

Sets the default execution timeout for tasks in this queue:

```csharp
.AddQueue("quick", q => q
    .SetDefaultTimeout(TimeSpan.FromSeconds(30)))
```

### Queue Routing

To send tasks to specific queues, override `QueueName` in your handler:

```csharp
// High-priority queue for critical operations
public class PaymentProcessingHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override string? QueueName => "high-priority";

    public override async Task Handle(ProcessPaymentTask task, CancellationToken cancellationToken)
    {
        // Critical payment processing logic
    }
}

// Background queue for CPU-intensive work
public class ReportGenerationHandler : EverTaskHandler<GenerateReportTask>
{
    public override string? QueueName => "background";

    public override async Task Handle(GenerateReportTask task, CancellationToken cancellationToken)
    {
        // CPU-intensive report generation
    }
}

// Default queue (no QueueName override)
public class EmailHandler : EverTaskHandler<SendEmailTask>
{
    public override async Task Handle(SendEmailTask task, CancellationToken cancellationToken)
    {
        // Regular email sending
    }
}
```

### Automatic Queue Routing

- **Default Queue**: Tasks without a specified `QueueName` go to the "default" queue
- **Recurring Queue**: Recurring tasks go to the "recurring" queue unless explicitly overridden
- **Fallback Behavior**: When a queue is full with `FallbackToDefault`, tasks fall back to the "default" queue

### Real-World Example

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
// Default queue: General background tasks
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 2)
    .SetChannelCapacity(1000))

// Critical queue: Payments, orders, user operations
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))))

// Email queue: Bulk email sending
.AddQueue("email", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(10000)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

// Reports queue: Heavy processing
.AddQueue("reports", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(50)
    .SetDefaultTimeout(TimeSpan.FromMinutes(30)))

// Recurring queue: Scheduled maintenance
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(4)
    .SetChannelCapacity(100))

.AddSqlServerStorage(connectionString);
```

### Best Practices

1. **Keep Queue Count Reasonable**: Most applications do fine with 3-5 queues. More than that and you're probably over-engineering.
2. **Configure Based on Workload**:
   - I/O operations (API calls, DB queries, file I/O) benefit from higher parallelism
   - CPU-intensive tasks (image processing, calculations) should have lower parallelism
3. **Use Fallback Wisely**: `FallbackToDefault` gives you graceful degradation for non-critical queues
4. **Monitor Queue Metrics**: Track queue depths and processing rates to tune your configuration
5. **Name Queues Clearly**: Use descriptive names like "payments", "email", "reports" instead of generic ones like "queue1", "queue2"

## Sharded Scheduler

For extreme high-load scenarios, EverTask offers a sharded scheduler that splits the workload across multiple independent shards. This reduces lock contention and boosts throughput when you're dealing with massive scheduling loads.

### When to Use

Consider the sharded scheduler if you're hitting:

- Sustained load above 10,000 `Schedule()` calls/second
- Burst spikes exceeding 20,000 `Schedule()` calls/second
- 100,000+ tasks scheduled at once
- High lock contention in profiling (over 5% CPU time spent in scheduler operations)

> **Note**: The default `PeriodicTimerScheduler` (v2.0+) handles most workloads just fine. Only reach for the sharded scheduler when you've measured actual performance problems.

### Configuration

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: 8) // Recommended: 4-16 shards
)
.AddSqlServerStorage(connectionString);
```

#### Auto-scaling

Automatically scale based on CPU cores:

```csharp
.UseShardedScheduler() // Uses Environment.ProcessorCount (minimum 4 shards)
```

#### Manual Configuration

```csharp
.UseShardedScheduler(shardCount: Environment.ProcessorCount) // Scale with CPUs
```

### Performance Comparison

| Metric | Default Scheduler | Sharded Scheduler (8 shards) |
|--------|------------------|----------------------------|
| `Schedule()` throughput | ~5-10k/sec | ~15-30k/sec |
| Lock contention | Moderate | Low (8x reduction) |
| Scheduled tasks capacity | ~50-100k | ~200k+ |
| Memory overhead | Baseline | +2-3KB (negligible) |
| Background threads | 1 | N (shard count) |

### How It Works

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

### Trade-offs

**Pros:**
- ✅ 2-4x throughput improvement for high-load scenarios
- ✅ Better spike handling (independent shard processing)
- ✅ Complete failure isolation (issues in one shard don't affect others)
- ✅ Reduced lock contention (divided by shard count)

**Cons:**
- ❌ Additional memory (~300 bytes per shard - negligible)
- ❌ Additional background threads (1 per shard)
- ❌ Slightly more complex debugging (multiple timers)

### High-Load Example

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: Environment.ProcessorCount)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(10000)
)
.AddSqlServerStorage(connectionString);
```

### Migration

Switching between default and sharded schedulers is painless:

- Both implement the same `IScheduler` interface
- Task execution behavior stays the same
- Storage format is compatible
- No breaking changes in handlers

> **Tip**: Start with the default scheduler and only switch to sharded if you're actually hitting performance bottlenecks. The default scheduler handles most workloads well.

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

## Custom Workflows

Combine continuations, rescheduling, and conditional logic to build sophisticated workflows:

```csharp
public class WorkflowOrchestrator : EverTaskHandler<WorkflowTask>
{
    private readonly ITaskDispatcher _dispatcher;

    public override async Task Handle(WorkflowTask task, CancellationToken cancellationToken)
    {
        // Execute current stage
        await ExecuteStageAsync(task.Stage, cancellationToken);
    }

    public override async ValueTask OnCompleted(Guid taskId)
    {
        switch (_task.Stage)
        {
            case WorkflowStage.Validation:
                // Move to payment stage
                await _dispatcher.Dispatch(new WorkflowTask(
                    _task.WorkflowId,
                    WorkflowStage.Payment));
                break;

            case WorkflowStage.Payment:
                // Wait 1 hour before fulfillment
                await _dispatcher.Dispatch(
                    new WorkflowTask(_task.WorkflowId, WorkflowStage.Fulfillment),
                    TimeSpan.FromHours(1));
                break;

            case WorkflowStage.Fulfillment:
                // Final stage - send confirmation
                await _dispatcher.Dispatch(new SendConfirmationTask(_task.WorkflowId));
                break;
        }
    }

    public override async ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        // Rollback workflow on any stage failure
        await _dispatcher.Dispatch(new RollbackWorkflowTask(_task.WorkflowId, _task.Stage));
    }
}
```

## Task Execution Log Capture

**Available since:** v3.0

EverTask provides a built-in log capture system that records logs written during task execution. The logger acts as a **proxy** that ALWAYS forwards logs to the standard ILogger infrastructure (console, file, Serilog, Application Insights, etc.) and **optionally** persists them to the database for audit trails.

### Why Use Log Capture?

- **Debugging**: Review exactly what happened during task execution, including retry attempts
- **Audit Trails**: Keep a permanent record of task execution logs in the database
- **Compliance**: Meet regulatory requirements for task execution logging
- **Root Cause Analysis**: Investigate failures with full execution context

### Basic Usage

Access the logger via the `Logger` property in your task handler:

```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    public override async Task Handle(ProcessOrderTask task, CancellationToken ct)
    {
        Logger.LogInformation("Processing order {OrderId}", task.OrderId);

        // Your business logic here
        await ProcessOrder(task.OrderId);

        Logger.LogInformation("Order {OrderId} processed successfully", task.OrderId);
    }
}
```

**Key Point**: Logs are ALWAYS written to ILogger (console, file, etc.) regardless of persistence settings. This ensures you never lose visibility into task execution.

### Configuration

#### Enable Database Persistence (Optional)

```csharp
services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .EnablePersistentHandlerLogging(true)           // Enable database persistence
    .SetMinimumPersistentLogLevel(LogLevel.Information)  // Only persist Information+
    .SetMaxPersistedLogsPerTask(1000))               // Limit logs per task
    .AddSqlServerStorage(connectionString);
```

#### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `EnablePersistentHandlerLogging` | `false` | Whether to persist logs to database. **Logs always go to ILogger regardless!** |
| `MinimumPersistentLogLevel` | `Information` | Minimum log level to persist. Only affects database, not ILogger. |
| `MaxPersistedLogsPerTask` | `1000` | Maximum logs to persist per task execution. `null` = unlimited. |

### How It Works

The log capture system uses a **proxy pattern**:

```
Handler.Logger.LogInformation("msg")
         ↓
   TaskLogCapture (proxy)
    ↙          ↘
ILogger        Database
(always)     (optional)
```

1. **Always Log to ILogger**: Every log call forwards to `ILogger<THandler>` for standard logging infrastructure
2. **Conditional Persistence**: If `EnablePersistentHandlerLogging = true`, logs are also stored in database
3. **Filtered Persistence**: `MinimumPersistentLogLevel` filters only database persistence, not ILogger

### Retrieving Persisted Logs

```csharp
// Get all logs for a task
var logs = await storage.GetExecutionLogsAsync(taskId);

foreach (var log in logs)
{
    Console.WriteLine($"[{log.Level}] {log.TimestampUtc}: {log.Message}");
    if (log.ExceptionDetails != null)
        Console.WriteLine($"Exception: {log.ExceptionDetails}");
}

// Get paginated logs
var page = await storage.GetExecutionLogsAsync(taskId, skip: 0, take: 50);
```

### Retry Attempt Tracking

Logs accumulate across ALL retry attempts:

```csharp
public class RetryTaskHandler : EverTaskHandler<RetryTask>
{
    public override async Task Handle(RetryTask task, CancellationToken ct)
    {
        Logger.LogInformation("Attempt started");

        // If this fails and retries, each attempt logs "Attempt started"
        // Database will contain: ["Attempt started", "Attempt started", "Attempt started", ...]
    }
}
```

This is **intentional** - it provides complete visibility into all execution attempts.

### Performance Considerations

- **When Disabled**: Single `if` check per log call (negligible overhead)
- **When Enabled**: ~100 bytes per log in memory, single bulk INSERT after task completion
- **ILogger Always Invoked**: Standard Microsoft.Extensions.Logging overhead applies

### Best Practices

1. **Use Standard Log Levels**: `LogInformation` for normal flow, `LogWarning` for issues, `LogError` for failures
2. **Include Context**: Log task parameters and key decision points
3. **Set Reasonable Limits**: Default 1000 logs per task prevents unbounded growth
4. **Use for Debugging**: Don't rely on persisted logs for real-time monitoring (use ILogger infrastructure)
5. **Clean Up Old Logs**: Implement retention policies to prevent database bloat

### Example: Audit Trail

```csharp
public class PaymentProcessorHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override async Task Handle(ProcessPaymentTask task, CancellationToken ct)
    {
        Logger.LogInformation("Payment processing started for amount {Amount}", task.Amount);

        // Audit critical steps
        Logger.LogInformation("Validating payment method");
        await ValidatePaymentMethod(task.PaymentMethodId);

        Logger.LogInformation("Charging payment gateway");
        var result = await ChargePaymentGateway(task);

        if (result.IsSuccess)
        {
            Logger.LogInformation("Payment succeeded with transaction ID {TransactionId}", result.TransactionId);
        }
        else
        {
            Logger.LogError("Payment failed: {ErrorMessage}", result.ErrorMessage);
            throw new PaymentException(result.ErrorMessage);
        }
    }
}
```

With `EnablePersistentHandlerLogging = true`, all these logs are stored in the database and queryable by `taskId`.

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
