---
layout: default
title: Cancellation Tokens
parent: Resilience
nav_order: 6
---

# CancellationToken Usage

The `CancellationToken` parameter in your handler is how you implement cooperative cancellation. It gets signaled in three situations:

1. The task's timeout is reached
2. Someone explicitly cancels the task via `dispatcher.Cancel(taskId)`
3. The WorkerService is shutting down

## Basic Usage

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

## Loop Processing

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

## Parallel Processing

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

## HTTP Requests

```csharp
public override async Task Handle(ApiCallTask task, CancellationToken cancellationToken)
{
    using var httpClient = new HttpClient();

    // Pass token to HTTP calls
    var response = await httpClient.GetAsync(task.Url, cancellationToken);

    var content = await response.Content.ReadAsStringAsync(cancellationToken);
}
```

## Database Operations

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

## Best Practices

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

## Next Steps

- **[Timeout Management](timeout-management.md)** - Configure task timeouts
- **[Graceful Shutdown](graceful-shutdown.md)** - Handle application restarts
- **[Best Practices](best-practices.md)** - Build robust cancellation strategies
