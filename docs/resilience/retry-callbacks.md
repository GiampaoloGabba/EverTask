---
layout: default
title: Retry Callbacks
parent: Resilience
nav_order: 4
---

# OnRetry Lifecycle Callback

The `OnRetry` callback gives you visibility into individual retry attempts, enabling logging, metrics, alerting, and debugging of intermittent failures.

## Basic Usage

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

## Tracking Metrics

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

## Circuit Breaker Pattern

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

## Debugging Intermittent Failures

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

## Error Handling in OnRetry

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

## Async Operations in OnRetry

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

## Combining Exception Filtering and OnRetry

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

## Best Practices

1. **Keep callbacks fast** - `OnRetry` is invoked synchronously during retry flow; avoid expensive operations
2. **Use for observability** - Log retry attempts, track metrics, send alerts on excessive retries
3. **Don't throw exceptions** - Exceptions in `OnRetry` are logged but don't prevent retry (by design)
4. **Use structured logging** - Include task ID, attempt number, and exception type for easy filtering
5. **Track retry metrics** - Monitor retry rates to detect systemic issues early

### Good: Fast, Informative OnRetry Callback
```csharp
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
```

### Bad: Expensive Operations in OnRetry
```csharp
// ❌ Bad: Expensive operations block retry
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

## Next Steps

- **[Exception Filtering](exception-filtering.md)** - Configure which exceptions trigger retries
- **[Retry Policies](retry-policies.md)** - Core retry policy concepts
- **[Best Practices](best-practices.md)** - Build robust retry strategies
