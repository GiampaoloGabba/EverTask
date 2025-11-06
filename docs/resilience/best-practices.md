---
layout: default
title: Best Practices
parent: Resilience
nav_order: 9
---

# Best Practices

Follow these best practices to build robust, resilient background task systems with EverTask.

## Retry Policies

1. **Use retries for transient failures** - Things like network errors, database timeouts, or temporary service unavailability are good candidates for retry logic
2. **Don't retry permanent failures** - Validation errors, 404s, or authentication failures won't fix themselves on retry
3. **Use exception filtering to fail-fast** - Configure `Handle<T>()` to only retry transient errors, saving resources and improving error visibility
4. **Implement exponential backoff** - Give failing services breathing room instead of hammering them with requests
5. **Set reasonable retry limits** - Usually 3-5 attempts is enough; more than that and you're probably dealing with a non-transient issue
6. **Log retry attempts** - Use `OnRetry` callback to track patterns in retry behavior that reveal systemic problems

### Good: Exception Filtering with Transient Errors Only
```csharp
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
```

### Bad: No Exception Filtering
```csharp
// ❌ Bad: Retries validation errors
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
```

### Better: Fail-Fast on Validation Errors
```csharp
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

## Exception Filtering

1. **Whitelist approach for specific integrations** - Use `Handle<T>()` when you know exactly which errors are transient (database, HTTP API)
2. **Blacklist approach for general handlers** - Use `DoNotHandle<T>()` when most errors are retriable except specific permanent ones
3. **Use predefined sets** - `HandleTransientDatabaseErrors()` and `HandleTransientNetworkErrors()` cover common scenarios
4. **Consider derived types** - Exception filtering matches derived types automatically (e.g., `Handle<IOException>()` catches `FileNotFoundException`)
5. **Don't over-filter** - Only filter when you're confident an error is truly permanent vs. transient

### Good: Whitelist for Database Operations
```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors(); // Retry DB timeouts, deadlocks, etc.
}
```

### Good: Predicate for HTTP Status Codes
```csharp
public class HttpApiHandler : EverTaskHandler<HttpApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);
    // Only retry 5xx server errors, fail fast on 4xx client errors
}
```

### Bad: Mixing Whitelist and Blacklist
```csharp
// ❌ Bad: Cannot mix approaches
public class BadHandler : EverTaskHandler<BadTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .Handle<HttpRequestException>()
        .DoNotHandle<ArgumentException>(); // ERROR: Cannot mix approaches!
}
```

## OnRetry Callback

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

## Timeouts

1. **Set appropriate timeouts** - Base them on expected execution time plus a reasonable buffer
2. **Monitor timeout rates** - If tasks are timing out frequently, you've got a performance problem to investigate
3. **Handle timeouts gracefully** - Clean up resources and save state when possible
4. **Different timeouts for different task types** - A quick API call shouldn't have the same timeout as a report generation job

### Good: Appropriate Timeout for Task Type
```csharp
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

## CancellationToken

1. **Always check the token** - Put checks in loops and before expensive operations
2. **Pass to all async operations** - Let the framework propagate cancellation through your call stack
3. **Don't catch OperationCanceledException** - Unless you need specific cleanup, let it bubble up
4. **Test cancellation** - Actually verify your handlers respond correctly to cancellation signals

### Good: Proper Cancellation Handling
```csharp
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    for (int i = 0; i < 1000; i++)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessAsync(i, cancellationToken);
    }
}
```

### Bad: Ignoring Cancellation Token
```csharp
// ❌ Bad: Not passing token
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    for (int i = 0; i < 1000; i++)
    {
        await ProcessAsync(i); // Not passing token!
    }
}
```

## Error Handling

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

- **[Monitoring](../monitoring.md)** - Track task failures and retries
- **[Task Orchestration](../advanced-features.md)** - Continuations and error compensation
- **[Configuration Reference](../configuration-reference.md)** - All timeout and retry options
- **[Architecture](../architecture.md)** - How retry and timeout mechanisms work internally
