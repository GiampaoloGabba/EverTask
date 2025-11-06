---
layout: default
title: Timeout Management
parent: Resilience
nav_order: 5
---

# Timeout Management

Timeouts prevent runaway tasks from consuming resources indefinitely. You can set them globally or per-handler.

## Global Default Timeout

The simplest approach is setting a default timeout that applies to all tasks:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetDefaultTimeout(TimeSpan.FromMinutes(5)); // All tasks timeout after 5 minutes
});
```

## Per-Handler Timeout

Different tasks have different performance characteristics, so you'll often want to override the global timeout:

```csharp
public class QuickTaskHandler : EverTaskHandler<QuickTask>
{
    // This handler times out after 30 seconds
    public override TimeSpan? Timeout => TimeSpan.FromSeconds(30);

    public override async Task Handle(QuickTask task, CancellationToken cancellationToken)
    {
        // Must complete within 30 seconds
    }
}

public class LongRunningTaskHandler : EverTaskHandler<LongRunningTask>
{
    // This handler gets 2 hours
    public override TimeSpan? Timeout => TimeSpan.FromHours(2);

    public override async Task Handle(LongRunningTask task, CancellationToken cancellationToken)
    {
        // Long-running processing
    }
}
```

## Per-Queue Timeout

You can also set default timeouts at the queue level, which is useful when you organize tasks by execution characteristics:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddQueue("quick", q => q
    .SetDefaultTimeout(TimeSpan.FromSeconds(30)))
.AddQueue("long-running", q => q
    .SetDefaultTimeout(TimeSpan.FromHours(1)));
```

## No Timeout

Sometimes you need a task to run to completion no matter how long it takes:

```csharp
public class NoTimeoutHandler : EverTaskHandler<NoTimeoutTask>
{
    // No timeout - runs until complete
    public override TimeSpan? Timeout => null;
}
```

## Handling Timeout

When a task times out, EverTask cancels its `CancellationToken`. Your task code should check this token regularly to respond to cancellation:

```csharp
public class TimeoutAwareHandler : EverTaskHandler<TimeoutAwareTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(5);

    public override async Task Handle(TimeoutAwareTask task, CancellationToken cancellationToken)
    {
        try
        {
            for (int i = 0; i < 1000; i++)
            {
                // Check cancellation regularly
                cancellationToken.ThrowIfCancellationRequested();

                await ProcessItemAsync(i, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Task timed out after {Timeout}", Timeout);
            throw; // Re-throw to mark as timed out
        }
    }
}
```

## Best Practices

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

## Next Steps

- **[Cancellation Tokens](cancellation-tokens.md)** - Implement cooperative cancellation
- **[Graceful Shutdown](graceful-shutdown.md)** - Handle application restarts
- **[Best Practices](best-practices.md)** - Build robust timeout strategies
