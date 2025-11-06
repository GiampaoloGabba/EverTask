---
layout: default
title: Overview
parent: Resilience
nav_order: 1
---

# Resilience & Error Handling Overview

EverTask provides comprehensive resilience features to help you build fault-tolerant background tasks that can recover from transient failures automatically.

## Key Features

### Retry Policies
Automatically retry failed tasks with configurable policies. Choose from built-in linear retry, implement custom exponential backoff, or integrate with Polly for advanced resilience patterns.

**Learn more**: [Retry Policies](retry-policies.md)

### Exception Filtering
Configure which exceptions should trigger retries and which should fail immediately. Prevent wasting retry attempts on permanent errors while handling transient failures gracefully.

**Learn more**: [Exception Filtering](exception-filtering.md)

### OnRetry Callbacks
Get visibility into retry attempts with lifecycle callbacks. Track metrics, implement circuit breaker patterns, and debug intermittent failures.

**Learn more**: [Retry Callbacks](retry-callbacks.md)

### Timeout Management
Prevent runaway tasks with flexible timeout configuration at global, per-handler, and per-queue levels.

**Learn more**: [Timeout Management](timeout-management.md)

### Cancellation Support
Implement cooperative cancellation for graceful shutdown and resource cleanup using CancellationTokens.

**Learn more**: [Cancellation Tokens](cancellation-tokens.md)

### Graceful Shutdown
Handle application restarts gracefully with automatic task recovery and progress tracking.

**Learn more**: [Graceful Shutdown](graceful-shutdown.md)

### Error Observation
React to errors using lifecycle hooks for alerting, telemetry, and compensation workflows.

**Learn more**: [Error Observation](error-observation.md)

## Quick Examples

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

## Next Steps

Start with [Retry Policies](retry-policies.md) to learn the basics, then explore:
- **[Exception Filtering](exception-filtering.md)** - Fail-fast on permanent errors
- **[Retry Callbacks](retry-callbacks.md)** - Track and debug retry attempts
- **[Timeout Management](timeout-management.md)** - Prevent runaway tasks
- **[Best Practices](best-practices.md)** - Build robust, resilient systems
