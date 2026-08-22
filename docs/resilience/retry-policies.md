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

## ExponentialRetryPolicy

`ExponentialRetryPolicy` is the built-in exponential backoff policy: the delay grows after every attempt (`initialDelay × backoffFactor^(n-1)`), so a struggling downstream service gets a longer break each time instead of the same hammering at fixed intervals. [Exception filtering](exception-filtering.md) and [retry callbacks](retry-callbacks.md) work the same way as on `LinearRetryPolicy`.

```csharp
// 500ms, 1s, 2s, 4s, 8s (default backoffFactor: 2.0)
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultRetryPolicy(new ExponentialRetryPolicy(5, TimeSpan.FromMilliseconds(500)));
});
```

### Backoff Factor, Max Delay and Jitter

```csharp
// 1s, 3s, 9s, 10s, 10s: growth capped at maxDelay
var policy = new ExponentialRetryPolicy(
    retryCount: 5,
    initialDelay: TimeSpan.FromSeconds(1),
    backoffFactor: 3.0,
    maxDelay: TimeSpan.FromSeconds(10));

// Add ±20% jitter so many tasks failing together don't retry in lockstep.
// Jitter is computed per attempt and never exceeds maxDelay.
var jittered = new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1),
    maxDelay: TimeSpan.FromSeconds(30), useJitter: true);
```

Validation rules: `retryCount` and `initialDelay` must be greater than zero, `backoffFactor` must be >= 1.0 (a factor of 1.0 behaves like a linear policy), and `maxDelay`, when provided, must be >= `initialDelay`.

Without a `maxDelay` the growth is still bounded: a single delay never exceeds the longest wait `Task.Delay` accepts (about 49.7 days), so a high retry count with a large factor clamps at that ceiling instead of failing at execution time. Jitter respects the same ceiling. In practice you will want a much lower `maxDelay` anyway.

The same ceiling applies everywhere EverTask arms a timer: `LinearRetryPolicy` rejects explicit delays above it at construction, and task timeouts and audit cleanup intervals are clamped to it. The bundled analyzer reports **ET0009** when a constant value above the ceiling appears at one of these call sites, so the mismatch shows up at compile time rather than as a surprise at runtime.

### With Exception Filtering

Filtering works exactly like on `LinearRetryPolicy` (whitelist, blacklist, predicate, presets):

```csharp
public class ApiCallHandler : EverTaskHandler<ApiCallTask>
{
    public override IRetryPolicy? RetryPolicy =>
        new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30))
            .HandleTransientNetworkErrors();
}
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
    // Arbitrary per-attempt delays (for a standard doubling pattern,
    // prefer ExponentialRetryPolicy above)
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(new TimeSpan[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromMinutes(1)
    });

    public override async Task Handle(CustomRetryTask task, CancellationToken cancellationToken)
    {
        // Task logic with custom retry pattern
    }
}
```

## Custom Retry Policies

If neither built-in policy fits, implement `IRetryPolicy` yourself. One case where you need that: the built-in policies cannot disable retries, but a single-attempt policy can:

```csharp
using Microsoft.Extensions.Logging;

public class NoRetryPolicy : IRetryPolicy
{
    public Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null)
    {
        // Single attempt: any failure propagates immediately, no retries
        return action(token);
    }
}

// Use in handler
public class MyHandler : EverTaskHandler<MyTask>
{
    public override IRetryPolicy? RetryPolicy => new NoRetryPolicy();
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
