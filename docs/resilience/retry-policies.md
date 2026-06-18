---
layout: default
title: Retry Policies
parent: Resilience
nav_order: 2
---

# Retry Policies

Retry policies let you automatically retry failed tasks without writing custom error-handling code. When a task fails, the retry policy kicks in and tries again based on the rules you've defined.

EverTask supports both simple retry configurations and advanced exception filtering to fail-fast on permanent errors while retrying transient failures.

## Default Linear Retry Policy

By default, tasks use `LinearRetryPolicy`, which you can configure globally:

```csharp
builder.Services.AddEverTask(opt =>
{
    // Default: 3 retries (up to 4 executions) with 500ms delay between attempts
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500)));
});
```

## LinearRetryPolicy Options

### Fixed Retry Count and Delay

```csharp
// 5 retries (up to 6 executions) with 1 second between attempts
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1)));
});
```

### Custom Delay Array

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

## Per-Handler Retry Policy

A handler's retry policy is resolved through a chain: the handler override takes precedence, then the declared queue's default, then the global default (v3.7+). Override the policy on a handler when you need different retry behavior for specific task types:

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

## Custom Retry Policies

Need full control over retry behavior? Implement `IRetryPolicy` yourself:

```csharp
using Microsoft.Extensions.Logging;

public class ExponentialBackoffPolicy : IRetryPolicy
{
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    public ExponentialBackoffPolicy(int maxRetries = 5, TimeSpan? baseDelay = null)
    {
        _maxRetries = maxRetries;
        _baseDelay = baseDelay ?? TimeSpan.FromSeconds(1);
    }

    public async Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null)
    {
        // maxRetries retries means up to maxRetries + 1 total executions
        for (int attempt = 0; attempt <= _maxRetries; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await action(token);
                return; // Success
            }
            catch (Exception ex) when (attempt < _maxRetries)
            {
                // Calculate exponential delay: base * 2^attempt
                var delay = TimeSpan.FromMilliseconds(
                    _baseDelay.TotalMilliseconds * Math.Pow(2, attempt));

                await Task.Delay(delay, token);

                // Notify the worker so OnRetry callbacks fire (attempt is 1-based)
                if (onRetryCallback != null)
                    await onRetryCallback(attempt + 1, ex, delay);
                // Loop continues to retry
            }
        }
    }
}

// Use in handler
public class MyHandler : EverTaskHandler<MyTask>
{
    public override IRetryPolicy? RetryPolicy => new ExponentialBackoffPolicy(maxRetries: 5);
}
```

## Polly Integration

If you're already using [Polly](https://github.com/App-vNext/Polly) in your project, you can wrap it in an `IRetryPolicy` implementation:

```csharp
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

public class PollyRetryPolicy : IRetryPolicy
{
    private readonly AsyncRetryPolicy _pollyPolicy;
    private Func<int, Exception, TimeSpan, ValueTask>? _onRetryCallback;

    public PollyRetryPolicy()
    {
        _pollyPolicy = Policy
            .Handle<HttpRequestException>() // Only retry on HTTP errors
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: retryAttempt =>
                    TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    // Surface the retry to EverTask's OnRetry callback (attempt is 1-based)
                    _onRetryCallback?.Invoke(retryCount, exception, timeSpan);
                });
    }

    public async Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null)
    {
        _onRetryCallback = onRetryCallback;
        await _pollyPolicy.ExecuteAsync(async (ct) => await action(ct), token);
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

## Circuit Breaker with Polly

```csharp
using Microsoft.Extensions.Logging;
using Polly;

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

    public async Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null)
    {
        await _policy.ExecuteAsync(async (ct) => await action(ct), token);
    }
}
```

## Rate-Limited Retries

When a handler also declares a [RateLimitPolicy](../rate-limiting.md), retry attempts re-acquire the key's budget by default (`ThrottleRetries = true`). The budget wait happens between attempts, before the per-attempt timeout starts. A retry whose slot is too far away re-parks the task at its reserved slot instead of consuming the retry budget; the attempt count restarts on redelivery. See [Keyed Rate Limiting → Retries](../rate-limiting.md#retries).

## Next Steps

- **[Exception Filtering](exception-filtering.md)** - Configure which exceptions trigger retries
- **[Retry Callbacks](retry-callbacks.md)** - Track retry attempts and implement circuit breakers
- **[Best Practices](best-practices.md)** - Patterns and pitfalls
