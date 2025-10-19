---
layout: default
title: Resilience
nav_order: 9
---

# Resilience & Error Handling

EverTask helps you build fault-tolerant background tasks that can recover from transient failures automatically.

## Table of Contents

- [Retry Policies](#retry-policies)
- [Timeout Management](#timeout-management)
- [CancellationToken Usage](#cancellationtoken-usage)
- [Handling WorkerService Stops](#handling-workerservice-stops)
- [Error Observation](#error-observation)
- [Best Practices](#best-practices)

## Retry Policies

Retry policies let you automatically retry failed tasks without writing custom error-handling code. When a task fails, the retry policy kicks in and tries again based on the rules you've defined.

### Default Linear Retry Policy

By default, tasks use `LinearRetryPolicy`, which you can configure globally:

```csharp
builder.Services.AddEverTask(opt =>
{
    // Default: 3 attempts with 500ms delay between retries
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500)));
});
```

### LinearRetryPolicy Options

#### Fixed Retry Count and Delay

```csharp
// 5 attempts with 1 second between retries
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1)));
});
```

#### Custom Delay Array

```csharp
// Custom delays for each retry
var delays = new TimeSpan[]
{
    TimeSpan.FromMilliseconds(100),  // First retry after 100ms
    TimeSpan.FromMilliseconds(500),  // Second retry after 500ms
    TimeSpan.FromSeconds(2),         // Third retry after 2s
    TimeSpan.FromSeconds(5)          // Fourth retry after 5s
};

builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(delays));
});
```

### Per-Handler Retry Policy

You can override the global policy on a per-handler basis when you need different retry behavior for specific task types:

```csharp
public class CriticalTaskHandler : EverTaskHandler<CriticalTask>
{
    public CriticalTaskHandler()
    {
        // More aggressive retries for critical tasks
        RetryPolicy = new LinearRetryPolicy(10, TimeSpan.FromSeconds(1));
    }

    public override async Task Handle(CriticalTask task, CancellationToken cancellationToken)
    {
        // Task logic - will retry up to 10 times if it fails
    }
}
```

### Per-Handler Custom Delays

```csharp
public class CustomRetryHandler : EverTaskHandler<CustomRetryTask>
{
    public CustomRetryHandler()
    {
        // Exponential backoff-like delays
        RetryPolicy = new LinearRetryPolicy(new TimeSpan[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(16)
        });
    }

    public override async Task Handle(CustomRetryTask task, CancellationToken cancellationToken)
    {
        // Task logic with custom retry pattern
    }
}
```

### Custom Retry Policies

Need full control over retry behavior? Implement `IRetryPolicy` yourself:

```csharp
public class ExponentialBackoffPolicy : IRetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;

    public ExponentialBackoffPolicy(int maxAttempts = 5, TimeSpan? baseDelay = null)
    {
        _maxAttempts = maxAttempts;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        for (int attempt = 0; attempt < _maxAttempts; attempt++)
        {
            try
            {
                await action(token);
                return; // Success
            }
            catch (Exception) when (attempt < _maxAttempts - 1)
            {
                // Calculate exponential delay: base * 2^attempt
                var delay = TimeSpan.FromMilliseconds(
                    _baseDelay.TotalMilliseconds * Math.Pow(2, attempt));

                await Task.Delay(delay, token);
                // Loop continues to retry
            }
        }
    }
}

// Use in handler
public class MyHandler : EverTaskHandler<MyTask>
{
    public MyHandler()
    {
        RetryPolicy = new ExponentialBackoffPolicy(maxAttempts: 5);
    }
}
```

### Polly Integration

If you're already using [Polly](https://github.com/App-vNext/Polly) in your project, you can wrap it in an `IRetryPolicy` implementation:

```csharp
using Polly;

public class PollyRetryPolicy : IRetryPolicy
{
    private readonly AsyncRetryPolicy _pollyPolicy;

    public PollyRetryPolicy()
    {
        _pollyPolicy = Policy
            .Handle<HttpRequestException>() // Only retry on HTTP errors
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    // Log retry attempt
                    Console.WriteLine($"Retry {retryCount} after {timeSpan.TotalSeconds}s due to {exception.GetType().Name}");
                });
    }

    public async Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        await _pollyPolicy.ExecuteAsync(async (ct) =>
        {
            await action(ct);
        }, token);
    }
}

// Use in handler
public class ApiCallHandler : EverTaskHandler<ApiCallTask>
{
    public ApiCallHandler()
    {
        RetryPolicy = new PollyRetryPolicy();
    }
}

// Or set globally
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new PollyRetryPolicy());
});
```

### Circuit Breaker with Polly

```csharp
public class CircuitBreakerRetryPolicy : IRetryPolicy
{
    private readonly AsyncPolicy _policy;

    public CircuitBreakerRetryPolicy()
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>()
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        var circuitBreakerPolicy = Policy
            .Handle<HttpRequestException>()
            .CircuitBreakerAsync(
                exceptionsAllowedBeforeBreaking: 5,
                durationOfBreak: TimeSpan.FromMinutes(1));

        // Combine retry + circuit breaker
        _policy = Policy.WrapAsync(retryPolicy, circuitBreakerPolicy);
    }

    public async Task Execute(Func<CancellationToken, Task> action, CancellationToken token = default)
    {
        await _policy.ExecuteAsync(async (ct) => await action(ct), token);
    }
}
```

## Timeout Management

Timeouts prevent runaway tasks from consuming resources indefinitely. You can set them globally or per-handler.

### Global Default Timeout

The simplest approach is setting a default timeout that applies to all tasks:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultTimeout(TimeSpan.FromMinutes(5)); // All tasks timeout after 5 minutes
});
```

### Per-Handler Timeout

Different tasks have different performance characteristics, so you'll often want to override the global timeout:

```csharp
public class QuickTaskHandler : EverTaskHandler<QuickTask>
{
    public QuickTaskHandler()
    {
        Timeout = TimeSpan.FromSeconds(30); // This handler times out after 30 seconds
    }

    public override async Task Handle(QuickTask task, CancellationToken cancellationToken)
    {
        // Must complete within 30 seconds
    }
}

public class LongRunningTaskHandler : EverTaskHandler<LongRunningTask>
{
    public LongRunningTaskHandler()
    {
        Timeout = TimeSpan.FromHours(2); // This handler gets 2 hours
    }

    public override async Task Handle(LongRunningTask task, CancellationToken cancellationToken)
    {
        // Long-running processing
    }
}
```

### Per-Queue Timeout

You can also set default timeouts at the queue level, which is useful when you organize tasks by execution characteristics:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddQueue("quick", q => q
    .SetDefaultTimeout(TimeSpan.FromSeconds(30)))
.AddQueue("long-running", q => q
    .SetDefaultTimeout(TimeSpan.FromHours(1)));
```

### No Timeout

Sometimes you need a task to run to completion no matter how long it takes:

```csharp
public class NoTimeoutHandler : EverTaskHandler<NoTimeoutTask>
{
    public NoTimeoutHandler()
    {
        Timeout = null; // No timeout - runs until complete
    }
}
```

### Handling Timeout

When a task times out, EverTask cancels its `CancellationToken`. Your task code should check this token regularly to respond to cancellation:

```csharp
public class TimeoutAwareHandler : EverTaskHandler<TimeoutAwareTask>
{
    public TimeoutAwareHandler()
    {
        Timeout = TimeSpan.FromMinutes(5);
    }

    public override async Task Handle(TimeoutAwareTask task, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 1000; i++)
            {
                // Check cancellation regularly
                cancellationToken.ThrowIfCancellationRequested();

                await ProcessItemAsync(i, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task timed out after {Timeout}", Timeout);
            throw; // Re-throw to mark as timed out
        }
    }
}
```

## CancellationToken Usage

The `CancellationToken` parameter in your handler is how you implement cooperative cancellation. It gets signaled in three situations:

1. The task's timeout is reached
2. Someone explicitly cancels the task via `dispatcher.Cancel(taskId)`
3. The WorkerService is shutting down

### Basic Usage

```csharp
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    // Pass token to async operations
    var data = await _repository.GetDataAsync(cancellationToken);

    // Check before expensive operations
    cancellationToken.ThrowIfCancellationRequested();

    await ProcessDataAsync(data, cancellationToken);
}
```

### Loop Processing

```csharp
public override async Task Handle(BatchTask task, CancellationToken cancellationToken)
{
    foreach (var item in task.Items)
    {
        // Check at the start of each iteration
        cancellationToken.ThrowIfCancellationRequested();

        await ProcessItemAsync(item, cancellationToken);
    }
}
```

### Parallel Processing

```csharp
public override async Task Handle(ParallelTask task, CancellationToken cancellationToken)
{
    await Parallel.ForEachAsync(
        task.Items,
        new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken // Pass to parallel operations
        },
        async (item, ct) =>
        {
            await ProcessItemAsync(item, ct);
        });
}
```

### HTTP Requests

```csharp
public override async Task Handle(ApiCallTask task, CancellationToken cancellationToken)
{
    using var httpClient = new HttpClient();

    // Pass token to HTTP calls
    var response = await httpClient.GetAsync(task.Url, cancellationToken);

    var content = await response.Content.ReadAsStringAsync(cancellationToken);
}
```

### Database Operations

```csharp
public override async Task Handle(DataTask task, CancellationToken cancellationToken)
{
    // Pass token to EF Core operations
    var users = await _dbContext.Users
        .Where(u => u.IsActive)
        .ToListAsync(cancellationToken);

    foreach (var user in users)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessUserAsync(user, cancellationToken);
    }
}
```

## Handling WorkerService Stops

When your application shuts down and the EverTask WorkerService stops, all running tasks get their `CancellationToken` cancelled.

### Graceful Shutdown

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

### Partial Execution Tracking

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

### Idempotent Operations

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

## Error Observation

Beyond just retrying, you can observe and react to errors using lifecycle hooks in your handlers:

### OnError Hook

```csharp
public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    _logger.LogError(exception, "Task {TaskId} failed: {Message}", taskId, message);

    // Send alerts
    if (exception is CriticalException)
    {
        _alertService.SendCriticalAlert(taskId, exception);
    }

    // Log to external monitoring
    _telemetry.TrackException(exception);

    // Dispatch compensation task
    if (_shouldRollback)
    {
        _dispatcher.Dispatch(new RollbackTask(taskId));
    }

    return ValueTask.CompletedTask;
}
```

### Error Categorization

```csharp
public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    switch (exception)
    {
        case OperationCanceledException:
            _logger.LogWarning("Task {TaskId} was cancelled", taskId);
            break;

        case TimeoutException:
            _logger.LogWarning("Task {TaskId} timed out", taskId);
            // Maybe retry with longer timeout
            break;

        case HttpRequestException httpEx:
            _logger.LogError(httpEx, "API call failed for task {TaskId}", taskId);
            // Retry policy already handled this, just log
            break;

        case DbUpdateException dbEx:
            _logger.LogError(dbEx, "Database error for task {TaskId}", taskId);
            // Might need manual intervention
            _alertService.SendDatabaseAlert(taskId, dbEx);
            break;

        default:
            _logger.LogError(exception, "Unexpected error for task {TaskId}", taskId);
            break;
    }

    return ValueTask.CompletedTask;
}
```

## Best Practices

### Retry Policies

1. **Use retries for transient failures** - Things like network errors, timeouts, or temporary service unavailability are good candidates for retry logic
2. **Don't retry permanent failures** - Validation errors, 404s, or authentication failures won't fix themselves on retry
3. **Implement exponential backoff** - Give failing services breathing room instead of hammering them with requests
4. **Set reasonable retry limits** - Usually 3-5 attempts is enough; more than that and you're probably dealing with a non-transient issue
5. **Log retry attempts** - Patterns in retry behavior can reveal systemic problems worth investigating

```csharp
// ✅ Good: Retry transient HTTP errors
public class ApiTaskHandler : EverTaskHandler<ApiTask>
{
    public ApiTaskHandler()
    {
        RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(2));
    }

    public override async Task Handle(ApiTask task, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(task.Url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

// ❌ Bad: Retrying validation errors
public class ValidationTaskHandler : EverTaskHandler<ValidationTask>
{
    public override async Task Handle(ValidationTask task, CancellationToken cancellationToken)
    {
        if (!task.IsValid)
        {
            throw new ValidationException(); // Will retry unnecessarily
        }
    }
}
```

### Timeouts

1. **Set appropriate timeouts** - Base them on expected execution time plus a reasonable buffer
2. **Monitor timeout rates** - If tasks are timing out frequently, you've got a performance problem to investigate
3. **Handle timeouts gracefully** - Clean up resources and save state when possible
4. **Different timeouts for different task types** - A quick API call shouldn't have the same timeout as a report generation job

```csharp
// ✅ Good: Appropriate timeout for task type
public class QuickApiCallHandler : EverTaskHandler<QuickApiCallTask>
{
    public QuickApiCallHandler()
    {
        Timeout = TimeSpan.FromSeconds(30); // Quick API call
    }
}

public class ReportGenerationHandler : EverTaskHandler<ReportGenerationTask>
{
    public ReportGenerationHandler()
    {
        Timeout = TimeSpan.FromMinutes(30); // Long-running report
    }
}
```

### CancellationToken

1. **Always check the token** - Put checks in loops and before expensive operations
2. **Pass to all async operations** - Let the framework propagate cancellation through your call stack
3. **Don't catch OperationCanceledException** - Unless you need specific cleanup, let it bubble up
4. **Test cancellation** - Actually verify your handlers respond correctly to cancellation signals

```csharp
// ✅ Good: Proper cancellation handling
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    for (int i = 0; i < 1000; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessAsync(i, cancellationToken);
    }
}

// ❌ Bad: Ignoring cancellation token
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    for (int i = 0; i < 1000; i++)
    {
        await ProcessAsync(i); // Not passing token!
    }
}
```

### Error Handling

1. **Log errors appropriately** - Match log levels to severity
2. **Don't swallow exceptions** - Your retry policy can't work if you catch everything
3. **Use OnError for side effects** - Things like alerting, telemetry, or triggering compensation tasks
4. **Design for idempotency** - Make sure tasks can be safely retried without causing duplicate side effects

```csharp
// ✅ Good: Let exceptions bubble for retry
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    await ProcessAsync(task, cancellationToken); // Exceptions propagate to retry policy
}

// ❌ Bad: Swallowing exceptions
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    try
    {
        await ProcessAsync(task, cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error occurred");
        // Exception swallowed - retry policy won't trigger!
    }
}
```

## Next Steps

- **[Monitoring](monitoring.md)** - Track task failures and retries
- **[Advanced Features](advanced-features.md)** - Continuations and error compensation
- **[Configuration Reference](configuration-reference.md)** - All timeout and retry options
- **[Architecture](architecture.md)** - How retry and timeout mechanisms work internally
