---
layout: default
title: Error Observation
parent: Resilience
nav_order: 8
---

# Error Observation

Beyond just retrying, you can observe and react to errors using lifecycle hooks in your handlers.

## OnError Hook

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

## Error Categorization

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

1. **Log errors appropriately** - Match log levels to severity
2. **Don't swallow exceptions** - Your retry policy can't work if you catch everything
3. **Use OnError for side effects** - Things like alerting, telemetry, or triggering compensation tasks
4. **Design for idempotency** - Make sure tasks can be safely retried without causing duplicate side effects

### Good: Let Exceptions Bubble for Retry
```csharp
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    await ProcessAsync(task, cancellationToken); // Exceptions propagate to retry policy
}
```

### Bad: Swallowing Exceptions
```csharp
// ❌ Bad: Exception swallowed
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

- **[Retry Callbacks](retry-callbacks.md)** - Track retry attempts
- **[Best Practices](best-practices.md)** - Build robust error handling strategies
- **[Monitoring](../monitoring.md)** - Track task failures and retries
