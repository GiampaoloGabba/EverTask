---
layout: default
title: Managing Recurring Tasks
parent: Recurring Tasks
nav_order: 5
---

# Managing Recurring Tasks

Learn how to cancel, retrieve information about, and monitor your recurring tasks.

## Cancelling Recurring Tasks

```csharp
// Store task ID when registering
Guid taskId = await dispatcher.Dispatch(
    new RecurringTask(),
    builder => builder.Schedule().EveryHour(),
    taskKey: "my-recurring-task");

// Later, cancel it
await dispatcher.Cancel(taskId);
```

## Retrieving Task Information

```csharp
// Get task by key
var task = await _taskStorage.GetByTaskKey("daily-report");

if (task != null)
{
    Console.WriteLine($"Task ID: {task.Id}");
    Console.WriteLine($"Status: {task.Status}");
    Console.WriteLine($"Current Run Count: {task.CurrentRunCount}");
    Console.WriteLine($"Next Run: {task.NextRunUtc}");
}
```

## Monitoring Recurring Tasks

Lifecycle hooks let you track execution patterns and catch issues:

```csharp
public class MonitoredRecurringHandler : EverTaskHandler<MonitoredRecurringTask>
{
    private readonly ILogger<MonitoredRecurringHandler> _logger;
    private readonly ITaskStorage _storage;

    public MonitoredRecurringHandler(ILogger<MonitoredRecurringHandler> logger, ITaskStorage storage)
    {
        _logger = logger;
        _storage = storage;
    }

    public override async Task Handle(MonitoredRecurringTask task, CancellationToken cancellationToken)
    {
        // The run count lives on the persisted row, not on the request payload.
        // Read it from storage by task key when you need it inside the handler.
        var persisted = await _storage.GetByTaskKey("monitored-recurring");
        _logger.LogInformation("Recurring task execution #{Count}", persisted?.CurrentRunCount);

        // Task logic here
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        _logger.LogInformation("Recurring task {TaskId} completed successfully", taskId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Recurring task {TaskId} failed: {Message}", taskId, message);

        // Send alerts, page on-call engineers, etc.

        return ValueTask.CompletedTask;
    }
}
```

## Next Steps

- **[Fluent Scheduling API](fluent-api.md)** - Build recurring schedules
- **[Idempotent Task Registration](idempotent-registration.md)** - Prevent duplicate tasks
- **[Best Practices](best-practices.md)** - Monitor recurring task health
- **[Monitoring](../monitoring.md)** - Advanced monitoring with SignalR integration
