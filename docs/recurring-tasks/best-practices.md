---
layout: default
title: Best Practices
parent: Recurring Tasks
nav_order: 6
---

# Recurring Tasks Best Practices

Follow these guidelines to build robust, maintainable recurring task systems.

## 1. Always Use Task Keys for Recurring Tasks

```csharp
// ✅ Good: Prevents duplicates on restart
await dispatcher.Dispatch(
    new DailyTask(),
    r => r.Schedule().EveryDay(),
    taskKey: "daily-task");

// ❌ Bad: Creates duplicate every restart
await dispatcher.Dispatch(
    new DailyTask(),
    r => r.Schedule().EveryDay());
```

## 2. Use Meaningful Task Keys

```csharp
// ✅ Good: Descriptive and unique
taskKey: "cleanup-expired-sessions"
taskKey: "user-{userId}:daily-summary"
taskKey: "tenant-{tenantId}:billing:monthly"

// ❌ Bad: Generic or unclear
taskKey: "task1"
taskKey: "job"
```

## 3. Choose the Right Schedule Format

Use the fluent API for readability when the pattern is straightforward. Save cron for complex schedules that would be awkward to express with the fluent API.

```csharp
// ✅ Good: Fluent API is clear and readable for simple schedules
builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(14, 0))

// ✅ Good: Cron shines for complex patterns
builder => builder.Schedule().UseCron("*/15 9-17 * * 1-5")

// ❌ Avoid: Cron makes simple patterns harder to understand
builder => builder.Schedule().UseCron("0 14 * * *")
```

## 4. Handle Long-Running Recurring Tasks

If your task takes a while to complete, set a timeout that gives it room to breathe. Also respect cancellation tokens so tasks can be stopped gracefully.

```csharp
public class LongRunningRecurringHandler : EverTaskHandler<LongRunningRecurringTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(30); // Generous timeout for long operations

    public override async Task Handle(LongRunningRecurringTask task, CancellationToken cancellationToken)
    {
        // Check cancellation periodically so the task can be stopped cleanly
        for (int i = 0; i < 100; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await ProcessBatchAsync(i, cancellationToken);
        }
    }
}
```

## 5. Consider Time Zones

EverTask schedules run in UTC, so be explicit about time zones to avoid surprises:

```csharp
// ✅ Good: Clear about UTC
await dispatcher.Dispatch(
    new GlobalTask(),
    r => r.Schedule().EveryDay().AtTime(new TimeOnly(0, 0)), // Midnight UTC
    taskKey: "global-midnight-task");

// ✅ Good: Convert user's local time to UTC
var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId);
var localTime = TimeZoneInfo.ConvertTimeToUtc(
    DateTime.Today.AddHours(9), // 9 AM in user's local time
    userTimeZone);

await dispatcher.Dispatch(
    new UserTask(user.Id),
    r => r.Schedule().EveryDay().AtTime(TimeOnly.FromDateTime(localTime)),
    taskKey: $"user-{user.Id}-daily-task");
```

## 6. Limit Recurring Tasks Appropriately

Use `MaxRuns()` and `RunUntil()` for tasks that shouldn't run forever:

```csharp
// ✅ Good: Time-limited trial features
await dispatcher.Dispatch(
    new TrialFeatureTask(userId),
    r => r.Schedule().EveryDay().RunUntil(trialEndDate),
    taskKey: $"trial-{userId}");

// ✅ Good: Fixed-duration campaigns
await dispatcher.Dispatch(
    new PromotionalEmailTask(),
    r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Friday).MaxRuns(4),
    taskKey: "monthly-promo");
```

## 7. Monitor Recurring Task Health

It's worth having a watchdog task that checks if your other recurring tasks are behaving:

```csharp
await dispatcher.Dispatch(
    new MonitorRecurringTasksTask(),
    r => r.Schedule().EveryHour(),
    taskKey: "monitor-recurring-tasks");

public class MonitorRecurringTasksHandler : EverTaskHandler<MonitorRecurringTasksTask>
{
    private readonly ITaskStorage _storage;
    private readonly IAlertService _alertService;

    public override async Task Handle(MonitorRecurringTasksTask task, CancellationToken cancellationToken)
    {
        var recurringTasks = await _storage.GetAllRecurringTasksAsync(cancellationToken);

        foreach (var t in recurringTasks)
        {
            if (t.Status == TaskStatus.Failed && t.CurrentRunCount > 0)
            {
                await _alertService.SendAlertAsync(
                    $"Recurring task '{t.TaskKey}' is failing",
                    cancellationToken);
            }
        }
    }
}
```

## Next Steps

- **[Overview](overview.md)** - Introduction to recurring tasks
- **[Fluent Scheduling API](fluent-api.md)** - Build type-safe schedules
- **[Managing Recurring Tasks](managing-tasks.md)** - Cancel and monitor tasks
- **[Task Orchestration](../advanced-features.md)** - Continuations and workflow patterns
- **[Scalability](../scalability.md)** - Multi-queue and sharded scheduler
- **[Resilience](../resilience.md)** - Retry policies and error handling
