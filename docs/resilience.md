---
layout: default
title: Resilience
nav_order: 4
has_children: true
---

# Resilience & Error Handling

EverTask helps you build fault-tolerant background tasks that can recover from transient failures automatically.

## Overview

Building resilient background task systems requires handling failures gracefully, retrying transient errors, managing timeouts, and ensuring graceful shutdown. EverTask provides comprehensive resilience features to make this easy.

**Key Features:**
- **Retry Policies**: Automatically retry failed tasks with configurable policies
- **Exception Filtering**: Fail-fast on permanent errors, retry transient failures
- **OnRetry Callbacks**: Track and debug retry attempts
- **Timeout Management**: Prevent runaway tasks with flexible timeouts
- **Cancellation Support**: Implement cooperative cancellation with CancellationTokens
- **Graceful Shutdown**: Handle application restarts with automatic task recovery
- **Error Observation**: React to errors with lifecycle hooks

## Topics

### [Overview](resilience/overview.md)
Introduction to resilience features with quick examples and feature overview.

### [Retry Policies](resilience/retry-policies.md)
Configure automatic retry behavior for failed tasks. Learn about LinearRetryPolicy, custom policies, and Polly integration.

### [Exception Filtering](resilience/exception-filtering.md)
Control which exceptions trigger retries and which fail immediately. Use whitelist, blacklist, or predicate-based filtering to save resources and improve error visibility.

### [Retry Callbacks](resilience/retry-callbacks.md)
Track retry attempts with OnRetry callbacks. Implement metrics tracking, circuit breaker patterns, and debugging for intermittent failures.

### [Timeout Management](resilience/timeout-management.md)
Prevent runaway tasks with global, per-handler, and per-queue timeout configuration.

### [Cancellation Tokens](resilience/cancellation-tokens.md)
Implement cooperative cancellation for graceful shutdown and resource cleanup using CancellationTokens.

### [Graceful Shutdown](resilience/graceful-shutdown.md)
Handle application restarts gracefully with automatic task recovery and progress tracking.

### [Error Observation](resilience/error-observation.md)
React to errors using OnError lifecycle hooks for alerting, telemetry, and compensation workflows.

### [Best Practices](resilience/best-practices.md)
Follow best practices for retry policies, exception filtering, timeouts, cancellation, and error handling.

## Quick Start

### Basic Retry Policy
```csharp
builder.Services.AddEverTask(opt =>
{
    // 3 attempts with 500ms delay between retries
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500)));
});
```

### Exception Filtering
```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors(); // Only retry database-related errors
}
```

### Timeout Configuration
```csharp
public class QuickTaskHandler : EverTaskHandler<QuickTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(30);
}
```

### Cancellation Token Usage
```csharp
public override async Task Handle(MyTask task, CancellationToken cancellationToken)
{
    foreach (var item in task.Items)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await ProcessItemAsync(item, cancellationToken);
    }
}
```

## Common Patterns

### Robust Database Handler
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
            "Database task {TaskId} retry {Attempt} after {DelayMs}ms",
            taskId, attemptNumber, delay.TotalMilliseconds);

        _metrics.IncrementCounter("db_task_retries");
        return ValueTask.CompletedTask;
    }

    public override async Task Handle(DatabaseTask task, CancellationToken ct)
    {
        await _dbContext.ProcessAsync(task.Data, ct);
    }
}
```

### HTTP API with Circuit Breaker
```csharp
public class HttpApiHandler : EverTaskHandler<HttpApiTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
        .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);

    public override async Task Handle(HttpApiTask task, CancellationToken ct)
    {
        var response = await _httpClient.GetAsync(task.Url, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

## Next Steps

Start with the [Overview](resilience/overview.md) to learn about resilience features, or jump directly to:
- **[Retry Policies](resilience/retry-policies.md)** - Configure automatic retry behavior
- **[Exception Filtering](resilience/exception-filtering.md)** - Fail-fast on permanent errors
- **[Best Practices](resilience/best-practices.md)** - Build robust, resilient systems

---

> **Note**: Proper resilience configuration is critical for production systems. Start with sensible defaults and tune based on your specific workload characteristics!
