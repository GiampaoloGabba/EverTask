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
    // Default: 3 attempts with 500ms delay between retries
    opt.SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500)));
});
```

## LinearRetryPolicy Options

### Fixed Retry Count and Delay

```csharp
// 5 attempts with 1 second between retries
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

## Custom Retry Policies

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

## Polly Integration

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

## Circuit Breaker with Polly

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

## Next Steps

- **[Exception Filtering](exception-filtering.md)** - Configure which exceptions trigger retries
- **[Retry Callbacks](retry-callbacks.md)** - Track retry attempts and implement circuit breakers
- **[Best Practices](best-practices.md)** - Build robust retry strategies
