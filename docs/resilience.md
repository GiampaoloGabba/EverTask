---
layout: default
title: Resilience
nav_order: 9
---

# Resilience & Error Handling

EverTask helps you build fault-tolerant background tasks that can recover from transient failures automatically.

## Table of Contents

- [Retry Policies](#retry-policies)
  - [Default Linear Retry Policy](#default-linear-retry-policy)
  - [LinearRetryPolicy Options](#linearretrypolicy-options)
  - [Per-Handler Retry Policy](#per-handler-retry-policy)
  - [Custom Retry Policies](#custom-retry-policies)
  - [Polly Integration](#polly-integration)
  - [Exception Filtering](#exception-filtering)
    - [Whitelist Approach (Handle)](#whitelist-approach-handle)
    - [Blacklist Approach (DoNotHandle)](#blacklist-approach-donothandle)
    - [Predicate-Based Filtering (HandleWhen)](#predicate-based-filtering-handlewhen)
    - [Predefined Exception Sets](#predefined-exception-sets)
    - [Exception Filter Priority](#exception-filter-priority)
  - [OnRetry Lifecycle Callback](#onretry-lifecycle-callback)
    - [Basic Usage](#basic-usage)
    - [Tracking Metrics](#tracking-metrics)
    - [Circuit Breaker Pattern](#circuit-breaker-pattern)
    - [Debugging Intermittent Failures](#debugging-intermittent-failures)
  - [Combining Exception Filtering and OnRetry](#combining-exception-filtering-and-onretry)
- [Timeout Management](#timeout-management)
- [CancellationToken Usage](#cancellationtoken-usage)
- [Handling WorkerService Stops](#handling-workerservice-stops)
- [Error Observation](#error-observation)
- [Best Practices](#best-practices)

## Retry Policies

Retry policies let you automatically retry failed tasks without writing custom error-handling code. When a task fails, the retry policy kicks in and tries again based on the rules you've defined.

EverTask supports both simple retry configurations and advanced exception filtering to fail-fast on permanent errors while retrying transient failures.

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
    // More aggressive retries for critical tasks
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(10, TimeSpan.FromSeconds(1));

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
    // Exponential backoff-like delays
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(new TimeSpan[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16)
    });

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
    public override IRetryPolicy? RetryPolicy => new ExponentialBackoffPolicy(maxAttempts: 5);
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
    public override IRetryPolicy? RetryPolicy => new PollyRetryPolicy();
}

// Or set globally
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new PollyRetryPolicy());
});
```

### Exception Filtering

Exception filtering allows you to configure retry policies to only retry transient errors while failing fast on permanent errors. This prevents wasting retry attempts and resources on failures that won't fix themselves.

#### Why Exception Filtering?

Without filtering, all exceptions trigger retries:

```csharp
public override async Task Handle(MyTask task, CancellationToken ct)
{
    // Bug: task.Data is null
    var length = task.Data.Length; // NullReferenceException
}
```

With default retry policy:
- Attempt 1: `NullReferenceException` → Retry (wasteful)
- Attempt 2: `NullReferenceException` → Retry (wasteful)
- Attempt 3: `NullReferenceException` → Retry (wasteful)
- Attempt 4: `NullReferenceException` → Retry (wasteful)
- Attempt 5: `NullReferenceException` → Final failure

**Total wasted time**: 5 attempts × retry delay

With exception filtering, you can fail immediately on permanent errors like `NullReferenceException`, `ArgumentException`, or validation failures.

#### Whitelist Approach (Handle)

Use `Handle<TException>()` to specify which exceptions should be retried. All other exceptions fail immediately.

```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .Handle<DbException>()
        .Handle<SqlException>()
        .Handle<TimeoutException>();

    public override async Task Handle(DatabaseTask task, CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
```

**Result**: Only database-related exceptions trigger retries. Application logic errors (ArgumentException, NullReferenceException) fail immediately.

#### Blacklist Approach (DoNotHandle)

Use `DoNotHandle<TException>()` to specify which exceptions should NOT be retried. All other exceptions trigger retries.

```csharp
public class ApiTaskHandler : EverTaskHandler<ApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))
        .DoNotHandle<ArgumentException>()
        .DoNotHandle<ArgumentNullException>()
        .DoNotHandle<InvalidOperationException>();

    public override async Task Handle(ApiTask task, CancellationToken ct)
    {
        await _apiClient.CallAsync(task.Endpoint, ct);
    }
}
```

**Result**: Permanent errors (argument validation, logic errors) fail immediately. Network errors, timeouts, and other transient issues trigger retries.

#### Multiple Exception Types (Params Overload)

For whitelisting or blacklisting many exception types, use the `params Type[]` overload:

```csharp
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .Handle(
        typeof(DbException),
        typeof(SqlException),
        typeof(HttpRequestException),
        typeof(IOException),
        typeof(SocketException),
        typeof(TimeoutException)
    );
```

This is more concise than chaining multiple `Handle<T>()` calls.

#### Predicate-Based Filtering (HandleWhen)

For complex scenarios, use `HandleWhen(Func<Exception, bool>)` with custom logic:

```csharp
public class HttpApiHandler : EverTaskHandler<HttpApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .HandleWhen(ex =>
        {
            // Only retry 5xx server errors
            if (ex is HttpRequestException httpEx)
            {
                var statusCode = httpEx.StatusCode;
                return statusCode >= 500 && statusCode < 600;
            }

            // Retry timeout and network errors
            return ex is TimeoutException or SocketException;
        });

    public override async Task Handle(HttpApiTask task, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(task.Url, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

**Use Cases for Predicates**:
- HTTP status code-based retry (5xx yes, 4xx no)
- Exception message pattern matching
- Combining multiple conditions
- Dynamic retry logic based on task context

#### Predefined Exception Sets

EverTask provides extension methods for common transient error patterns:

```csharp
// Database errors only
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .HandleTransientDatabaseErrors();

// Network errors only
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .HandleTransientNetworkErrors();

// All transient errors (database + network)
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .HandleAllTransientErrors();
```

**Included Exception Types**:

`HandleTransientDatabaseErrors()`:
- `DbException`
- `TimeoutException` (database-related)

`HandleTransientNetworkErrors()`:
- `HttpRequestException`
- `SocketException`
- `WebException`
- `TaskCanceledException`

`HandleAllTransientErrors()`:
- All of the above combined

You can combine predefined sets with custom exceptions:

```csharp
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .HandleAllTransientErrors()
    .Handle<MyCustomTransientException>();
```

#### Exception Filter Priority

When multiple filtering strategies are configured, they're evaluated in this order:

1. **Predicate** (`HandleWhen`): Takes precedence over all other filters
2. **Whitelist** (`Handle<T>`): Only retry whitelisted exceptions
3. **Blacklist** (`DoNotHandle<T>`): Retry all except blacklisted exceptions
4. **Default**: Retry all exceptions except `OperationCanceledException` and `TimeoutException`

**Important**: You cannot mix `Handle<T>()` and `DoNotHandle<T>()` - choose either whitelist or blacklist approach, not both.

```csharp
// Invalid - throws InvalidOperationException
var policy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .Handle<DbException>()
    .DoNotHandle<ArgumentException>(); // ERROR: Cannot mix approaches
```

#### Derived Exception Types

Exception filtering supports derived types using `Type.IsAssignableFrom()`:

```csharp
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .Handle<IOException>();
```

**This will also retry**:
- `FileNotFoundException` (derives from `IOException`)
- `DirectoryNotFoundException` (derives from `IOException`)
- `PathTooLongException` (derives from `IOException`)

You don't need to explicitly whitelist every derived type.

### OnRetry Lifecycle Callback

The `OnRetry` callback gives you visibility into individual retry attempts, enabling logging, metrics, alerting, and debugging of intermittent failures.

#### Basic Usage

Override `OnRetry` in your handler to track retry attempts:

```csharp
public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    private readonly ILogger<SendEmailHandler> _logger;

    public SendEmailHandler(ILogger<SendEmailHandler> logger)
    {
        _logger = logger;
    }

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _logger.LogWarning(exception,
            "Email task {TaskId} retry attempt {Attempt} after {DelayMs}ms: {ErrorMessage}",
            taskId, attemptNumber, delay.TotalMilliseconds, exception.Message);

        return ValueTask.CompletedTask;
    }

    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        await _emailService.SendAsync(task.To, task.Subject, task.Body, ct);
    }
}
```

**When `OnRetry` is Called**:

1. `Handle()` executes and throws exception
2. Retry policy determines if exception should be retried (`ShouldRetry()`)
3. If yes: Retry policy waits for delay period
4. **`OnRetry()` called** with attempt details
5. `Handle()` retried

**Important**: `OnRetry` is only called for retry attempts, not the initial execution. If a task succeeds on the first attempt, `OnRetry` is never called.

#### Tracking Metrics

Use `OnRetry` to track retry metrics for monitoring and alerting:

```csharp
public class MetricsTrackingHandler : EverTaskHandler<MyTask>
{
    private readonly IMetrics _metrics;

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _metrics.IncrementCounter("task_retries", new
        {
            handler = GetType().Name,
            attempt = attemptNumber,
            exception_type = exception.GetType().Name
        });

        _metrics.RecordHistogram("retry_delay_ms", delay.TotalMilliseconds);

        return ValueTask.CompletedTask;
    }
}
```

#### Circuit Breaker Pattern

Implement basic circuit breaker logic using `OnRetry`:

```csharp
public class CircuitBreakerHandler : EverTaskHandler<ExternalApiTask>
{
    private static int _consecutiveFailures = 0;
    private static DateTimeOffset? _circuitOpenedAt = null;
    private readonly ILogger<CircuitBreakerHandler> _logger;

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        Interlocked.Increment(ref _consecutiveFailures);

        if (_consecutiveFailures >= 10 && _circuitOpenedAt == null)
        {
            _circuitOpenedAt = DateTimeOffset.UtcNow;
            _logger.LogError(
                "Circuit breaker opened due to {Failures} consecutive failures",
                _consecutiveFailures);

            // Send alerts, disable service, etc.
        }

        return ValueTask.CompletedTask;
    }

    public override async Task Handle(ExternalApiTask task, CancellationToken ct)
    {
        // Check circuit breaker
        if (_circuitOpenedAt.HasValue &&
            DateTimeOffset.UtcNow - _circuitOpenedAt.Value < TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Circuit breaker is open");
        }

        await _apiClient.CallAsync(task.Endpoint, ct);

        // Success - reset circuit breaker
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        _circuitOpenedAt = null;
    }
}
```

**Note**: For production circuit breaker implementations, consider using Polly's circuit breaker policy instead of manual tracking.

#### Debugging Intermittent Failures

Use `OnRetry` to capture diagnostic information for failures that only occur occasionally:

```csharp
public class DiagnosticHandler : EverTaskHandler<DataProcessingTask>
{
    private readonly IDiagnosticService _diagnostics;

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _diagnostics.CaptureSnapshot(new
        {
            TaskId = taskId,
            Attempt = attemptNumber,
            Exception = exception.ToString(),
            StackTrace = exception.StackTrace,
            Timestamp = DateTimeOffset.UtcNow,
            Environment = new
            {
                MachineName = Environment.MachineName,
                ThreadId = Environment.CurrentManagedThreadId,
                WorkingSet = Environment.WorkingSet
            }
        });

        return ValueTask.CompletedTask;
    }
}
```

#### Error Handling in OnRetry

If `OnRetry` throws an exception, it's logged but **does not prevent the retry attempt**. The retry proceeds regardless of callback success or failure:

```csharp
public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
{
    // If this throws, it's logged but retry still happens
    _externalMetricsService.TrackRetry(taskId, attemptNumber);

    return ValueTask.CompletedTask;
}
```

This ensures that monitoring/logging failures don't impact task execution reliability.

#### Async Operations in OnRetry

`OnRetry` returns `ValueTask`, allowing async operations like database logging:

```csharp
public override async ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
{
    // Log retry to database for audit trail
    await _auditDb.LogRetryAttempt(new RetryAuditEntry
    {
        TaskId = taskId,
        AttemptNumber = attemptNumber,
        ExceptionType = exception.GetType().Name,
        ExceptionMessage = exception.Message,
        Delay = delay,
        Timestamp = DateTimeOffset.UtcNow
    });
}
```

### Combining Exception Filtering and OnRetry

You can use both features together for comprehensive retry handling:

```csharp
public class RobustDatabaseHandler : EverTaskHandler<DatabaseTask>
{
    private readonly ILogger<RobustDatabaseHandler> _logger;
    private readonly IMetrics _metrics;

    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(
        new[]
        {
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10)
        })
        .HandleTransientDatabaseErrors();

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _logger.LogWarning(exception,
            "Database task {TaskId} retry {Attempt}/{MaxAttempts} after {DelayMs}ms",
            taskId, attemptNumber, 4, delay.TotalMilliseconds);

        _metrics.IncrementCounter("db_task_retries", new
        {
            attempt = attemptNumber,
            exception = exception.GetType().Name
        });

        return ValueTask.CompletedTask;
    }

    public override async Task Handle(DatabaseTask task, CancellationToken ct)
    {
        await _dbContext.ProcessAsync(task.Data, ct);
    }
}
```

**Result**:
- Only database exceptions trigger retries (fail-fast on logic errors)
- Each retry attempt is logged with context
- Metrics track retry patterns for monitoring
- Exponential backoff gives database time to recover

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
    // This handler times out after 30 seconds
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(30);

    public override async Task Handle(QuickTask task, CancellationToken cancellationToken)
    {
        // Must complete within 30 seconds
    }
}

public class LongRunningTaskHandler : EverTaskHandler<LongRunningTask>
{
    // This handler gets 2 hours
    public override TimeSpan? Timeout => TimeSpan.FromHours(2);

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
    // No timeout - runs until complete
    public override TimeSpan? Timeout => null;
}
```

### Handling Timeout

When a task times out, EverTask cancels its `CancellationToken`. Your task code should check this token regularly to respond to cancellation:

```csharp
public class TimeoutAwareHandler : EverTaskHandler<TimeoutAwareTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(5);

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

1. **Use retries for transient failures** - Things like network errors, database timeouts, or temporary service unavailability are good candidates for retry logic
2. **Don't retry permanent failures** - Validation errors, 404s, or authentication failures won't fix themselves on retry
3. **Use exception filtering to fail-fast** - Configure `Handle<T>()` to only retry transient errors, saving resources and improving error visibility
4. **Implement exponential backoff** - Give failing services breathing room instead of hammering them with requests
5. **Set reasonable retry limits** - Usually 3-5 attempts is enough; more than that and you're probably dealing with a non-transient issue
6. **Log retry attempts** - Use `OnRetry` callback to track patterns in retry behavior that reveal systemic problems

```csharp
// ✅ Good: Exception filtering with transient errors only
public class ApiTaskHandler : EverTaskHandler<ApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(2))
        .HandleTransientNetworkErrors(); // Only retry network issues

    public override async Task Handle(ApiTask task, CancellationToken cancellationToken)
    {
        var response = await _httpClient.GetAsync(task.Url, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

// ❌ Bad: No exception filtering - retries validation errors
public class ValidationTaskHandler : EverTaskHandler<ValidationTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1));

    public override async Task Handle(ValidationTask task, CancellationToken cancellationToken)
    {
        if (!task.IsValid)
        {
            throw new ValidationException(); // Will retry unnecessarily!
        }
    }
}

// ✅ Better: Fail-fast on validation errors
public class BetterValidationHandler : EverTaskHandler<ValidationTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .DoNotHandle<ValidationException>(); // Fail immediately on validation errors

    public override async Task Handle(ValidationTask task, CancellationToken cancellationToken)
    {
        if (!task.IsValid)
        {
            throw new ValidationException(); // No retry, immediate failure
        }
    }
}
```

### Exception Filtering Best Practices

1. **Whitelist approach for specific integrations** - Use `Handle<T>()` when you know exactly which errors are transient (database, HTTP API)
2. **Blacklist approach for general handlers** - Use `DoNotHandle<T>()` when most errors are retriable except specific permanent ones
3. **Use predefined sets** - `HandleTransientDatabaseErrors()` and `HandleTransientNetworkErrors()` cover common scenarios
4. **Consider derived types** - Exception filtering matches derived types automatically (e.g., `Handle<IOException>()` catches `FileNotFoundException`)
5. **Don't over-filter** - Only filter when you're confident an error is truly permanent vs. transient

```csharp
// ✅ Good: Whitelist for database operations
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors(); // Retry DB timeouts, deadlocks, etc.
}

// ✅ Good: Predicate for HTTP status codes
public class HttpApiHandler : EverTaskHandler<HttpApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);
    // Only retry 5xx server errors, fail fast on 4xx client errors
}

// ❌ Bad: Mixing whitelist and blacklist
public class BadHandler : EverTaskHandler<BadTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .Handle<HttpRequestException>()
        .DoNotHandle<ArgumentException>(); // ERROR: Cannot mix approaches!
}
```

### OnRetry Callback Best Practices

1. **Keep callbacks fast** - `OnRetry` is invoked synchronously during retry flow; avoid expensive operations
2. **Use for observability** - Log retry attempts, track metrics, send alerts on excessive retries
3. **Don't throw exceptions** - Exceptions in `OnRetry` are logged but don't prevent retry (by design)
4. **Use structured logging** - Include task ID, attempt number, and exception type for easy filtering
5. **Track retry metrics** - Monitor retry rates to detect systemic issues early

```csharp
// ✅ Good: Fast, informative OnRetry callback
public class EmailHandler : EverTaskHandler<SendEmailTask>
{
    private readonly ILogger<EmailHandler> _logger;
    private readonly IMetrics _metrics;

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _logger.LogWarning(exception,
            "Email task {TaskId} retry {Attempt} after {DelayMs}ms",
            taskId, attemptNumber, delay.TotalMilliseconds);

        _metrics.Increment("email_retries", new { attempt = attemptNumber });

        return ValueTask.CompletedTask;
    }
}

// ❌ Bad: Expensive operations in OnRetry
public class SlowHandler : EverTaskHandler<SlowTask>
{
    public override async ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        // BAD: Expensive HTTP call blocks retry
        await _httpClient.PostAsync("https://metrics-api.com/track", ...);

        // BAD: Database query on hot path
        await _dbContext.RetryLogs.AddAsync(new RetryLog { ... });
        await _dbContext.SaveChangesAsync();
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
    // Quick API call
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(30);
}

public class ReportGenerationHandler : EverTaskHandler<ReportGenerationTask>
{
    // Long-running report
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(30);
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
