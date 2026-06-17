---
layout: default
title: Exception Filtering
parent: Resilience
nav_order: 3
---

# Exception Filtering

Exception filtering allows you to configure retry policies to only retry transient errors while failing fast on permanent errors. This prevents wasting retry attempts and resources on failures that won't fix themselves.

## Why Exception Filtering?

Without filtering, all exceptions trigger retries:

```csharp
public override async Task Handle(MyTask task, CancellationToken ct)
{
    // Bug: task.Data is null
    var length = task.Data.Length; // NullReferenceException
}
```

With the default retry policy (3 retries, so up to 4 executions):
- Attempt 1: `NullReferenceException` → Retry (wasteful)
- Attempt 2: `NullReferenceException` → Retry (wasteful)
- Attempt 3: `NullReferenceException` → Retry (wasteful)
- Attempt 4: `NullReferenceException` → Final failure

**Total wasted time**: 4 executions × retry delay

With exception filtering, you can fail immediately on permanent errors like `NullReferenceException`, `ArgumentException`, or validation failures.

## Whitelist Approach (Handle)

Use `Handle<TException>()` to specify which exceptions should be retried. All other exceptions fail immediately.

```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .Handle<DbException>()
        .Handle<SqlException>();

    public override async Task Handle(DatabaseTask task, CancellationToken ct)
    {
        await _dbContext.SaveChangesAsync(ct);
    }
}
```

**Result**: Only database-related exceptions trigger retries. Application logic errors (ArgumentException, NullReferenceException) fail immediately. Note that `TimeoutException` is never retried even when whitelisted: the fail-fast guard blocks it before the whitelist is consulted.

## Blacklist Approach (DoNotHandle)

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

**Result**: Permanent errors (argument validation, logic errors) fail immediately. Network errors and other transient issues trigger retries. `TimeoutException` and `OperationCanceledException` are still fail-fast regardless of the blacklist.

## Multiple Exception Types (Params Overload)

For whitelisting or blacklisting many exception types, use the `params Type[]` overload:

```csharp
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .Handle(
        typeof(DbException),
        typeof(SqlException),
        typeof(HttpRequestException),
        typeof(IOException),
        typeof(SocketException)
    );
```

This is more concise than chaining multiple `Handle<T>()` calls.

## Predicate-Based Filtering (HandleWhen)

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

            // Retry network errors (TimeoutException is fail-fast and ignored here)
            return ex is SocketException;
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

## Predefined Exception Sets

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
- `TimeoutException` (never retried — see note below)

`HandleTransientNetworkErrors()`:
- `HttpRequestException`
- `SocketException`
- `WebException`
- `TaskCanceledException` (never retried — see note below)

`HandleAllTransientErrors()`:
- All of the above combined

> **Note**: These presets include `TimeoutException` and `TaskCanceledException` in their whitelists, but the fail-fast guard blocks both before the whitelist is consulted. `TimeoutException` never retries, and `TaskCanceledException` derives from `OperationCanceledException`, so it never retries either. The presets retry only the remaining types.

You can combine predefined sets with custom exceptions:

```csharp
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .HandleAllTransientErrors()
    .Handle<MyCustomTransientException>();
```

## Exception Filter Priority

When multiple filtering strategies are configured, they're evaluated in this order:

1. **Fail-fast guard**: `OperationCanceledException` and `TimeoutException` never retry. This guard runs first and cannot be overridden by any filter below, so a whitelist, blacklist, or predicate that names these types has no effect on them.
2. **Predicate** (`HandleWhen`): Takes precedence over the whitelist and blacklist
3. **Whitelist** (`Handle<T>`): Only retry whitelisted exceptions
4. **Blacklist** (`DoNotHandle<T>`): Retry all except blacklisted exceptions
5. **Default**: Retry every remaining exception

**Important**: You cannot mix `Handle<T>()` and `DoNotHandle<T>()` - choose either whitelist or blacklist approach, not both.

```csharp
// Invalid - throws InvalidOperationException
var policy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .Handle<DbException>()
    .DoNotHandle<ArgumentException>(); // ERROR: Cannot mix approaches
```

## Derived Exception Types

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

## Best Practices

1. **Whitelist approach for specific integrations** - Use `Handle<T>()` when you know exactly which errors are transient (database, HTTP API)
2. **Blacklist approach for general handlers** - Use `DoNotHandle<T>()` when most errors are retriable except specific permanent ones
3. **Use predefined sets** - `HandleTransientDatabaseErrors()` and `HandleTransientNetworkErrors()` cover common scenarios
4. **Consider derived types** - Exception filtering matches derived types automatically
5. **Don't over-filter** - Only filter when you're confident an error is truly permanent vs. transient

## Examples

### Good: Whitelist for Database Operations
```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors(); // Retry transient DB errors (deadlocks, etc.); TimeoutException stays fail-fast
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

## Next Steps

- **[Retry Callbacks](retry-callbacks.md)** - Track and monitor retry attempts
- **[Retry Policies](retry-policies.md)** - Core retry policy concepts
- **[Best Practices](best-practices.md)** - Build robust retry strategies
