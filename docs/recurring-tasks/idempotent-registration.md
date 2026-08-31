---
layout: default
title: Idempotent Task Registration
parent: Recurring Tasks
nav_order: 7
---

# Idempotent Task Registration

Task keys prevent duplicate recurring tasks from being created. When you register a task with the same key twice, EverTask goes back to the existing task instead of blindly creating a duplicate.

## Basic Usage

```csharp
// First registration
await dispatcher.Dispatch(
    new DailyReportTask(),
    recurring => recurring.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
    taskKey: "daily-report");

// Same code runs again on restart - EverTask reuses the existing task
await dispatcher.Dispatch(
    new DailyReportTask(),
    recurring => recurring.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
    taskKey: "daily-report"); // Returns the same task ID
```

## Update Behavior

What happens when you dispatch with an existing key depends on whether the task is recurring and on its current status.

For a **recurring** task, the existing row is reused so its schedule and run history survive a redispatch:

| Existing Task Status | Behavior |
|---------------------|----------|
| **InProgress** | Returns existing task ID without making changes |
| **Any other status** (Pending/Queued/WaitingQueue/Completed/Failed/Cancelled) | Updates the task in place, preserving the stored `NextRunUtc` and `CurrentRunCount` |

A recurring task is never removed and recreated by a redispatch, and a recurring row cannot be converted to a one-shot through its task key.

For a **one-shot** task:

| Existing Task Status | Behavior |
|---------------------|----------|
| **InProgress** | Returns existing task ID without making changes |
| **Pending/Queued/WaitingQueue** | Updates the task configuration |
| **Completed/Failed/Cancelled/ServiceStopped** | Removes the old task and creates a new one |

## Updating Schedules

```csharp
// Initial registration
await dispatcher.Dispatch(
    new ReportTask(format: "PDF"),
    recurring => recurring.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
    taskKey: "daily-report");

// Later, change schedule and parameters
await dispatcher.Dispatch(
    new ReportTask(format: "Excel"), // Different parameter
    recurring => recurring.Schedule().Every(2).Days().AtTime(new TimeOnly(10, 0)), // Different schedule
    taskKey: "daily-report"); // Same key updates the existing task
```

## Startup Task Registration

A common pattern is to register all your recurring tasks in a hosted service at application startup:

```csharp
public class RecurringTasksRegistrar : IHostedService
{
    private readonly ITaskDispatcher _dispatcher;

    public RecurringTasksRegistrar(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        // Cleanup tasks
        await _dispatcher.Dispatch(
            new CleanupOldDataTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)),
            taskKey: "cleanup-old-data");

        // Health checks
        await _dispatcher.Dispatch(
            new HealthCheckTask(),
            r => r.Schedule().Every(5).Minutes(),
            taskKey: "health-check");

        // Daily reports
        await _dispatcher.Dispatch(
            new GenerateReportsTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(6, 0)),
            taskKey: "daily-reports");

        // Weekly summaries
        await _dispatcher.Dispatch(
            new WeeklySummaryTask(),
            r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(8, 0)),
            taskKey: "weekly-summary");

        // Monthly billing
        await _dispatcher.Dispatch(
            new MonthlyBillingTask(),
            r => r.Schedule().EveryMonth().OnDay(1).AtTime(new TimeOnly(0, 0)),
            taskKey: "monthly-billing");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

// Register in Program.cs
builder.Services.AddHostedService<RecurringTasksRegistrar>();
```

## Dynamic Configuration

You can update task schedules on the fly based on user preferences or configuration changes:

```csharp
public class TaskScheduleService
{
    private readonly ITaskDispatcher _dispatcher;

    public TaskScheduleService(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task UpdateReportSchedule(string userId, TimeOnly newTime)
    {
        await _dispatcher.Dispatch(
            new UserReportTask(userId),
            r => r.Schedule().EveryDay().AtTime(newTime),
            taskKey: $"user-report-{userId}");
    }

    public async Task UpdateNotificationFrequency(string userId, int intervalMinutes)
    {
        await _dispatcher.Dispatch(
            new UserNotificationTask(userId),
            r => r.Schedule().Every(intervalMinutes).Minutes(),
            taskKey: $"user-notifications-{userId}");
    }
}
```

### Re-dispatching versus rescheduling

Re-dispatching under the same key updates the row in place and is the right tool at startup, where the code
that registers a schedule is also the code that owns its definition. When something else changes the schedule
of a series that is already running — an admin screen, a tenant setting, a support action — reach for
[`ITaskScheduleManager`](managing-tasks.md#changing-a-schedule-while-it-runs) instead:

- it refuses a key that names no schedule, rather than creating one;
- it bumps the schedule version, so a run finishing at the same moment recomputes against the new definition
  instead of writing the next run it had already worked out;
- it can keep the schedule inside the calendar period it was already in (`RescheduleMode.RebaseFromCursor`),
  which a re-dispatch cannot;
- it tells you what it did: the new cursor, the new version, and any backlog it dropped.

Both go through the same per-key critical section, so a startup registration and a runtime reschedule of the
same key never interleave.

**One limit worth knowing.** A reschedule is immediate for occurrences that have not fired yet: the parked
registration is replaced. An occurrence already handed to a worker queue is considered fired, and within the
process that rescheduled it EverTask drops that delivery instead of running the definition you just replaced.
Across a restart there is nothing to drop against — a fresh process publishes no version — so a delivery
recovered from storage always runs, and its advance is what applies the new definition.

## Task Key Guidelines

Keep these rules in mind when choosing task keys:

- **Max length**: 200 characters
- **Case sensitive**: "task-1" and "TASK-1" are different keys
- **Uniqueness**: Each key must be unique across all tasks
- **Null/empty**: If not provided, tasks are always created (no deduplication)
- **Format**: Use kebab-case or namespaced formats for clarity

```csharp
// Good key formats
taskKey: "daily-cleanup"
taskKey: "reports:daily:sales"
taskKey: "user-notifications-{userId}"
taskKey: "tenant-{tenantId}:billing"

// Avoid
taskKey: "" // Empty = no deduplication
taskKey: "a" // Too generic
taskKey: new string('x', 250) // Too long (max 200)
```

## Next Steps

- **[Fluent Scheduling API](fluent-api.md)** - Build recurring schedules
- **[Managing Recurring Tasks](managing-tasks.md)** - Cancel and monitor tasks
- **[Best Practices](best-practices.md)** - Always use task keys for recurring tasks
