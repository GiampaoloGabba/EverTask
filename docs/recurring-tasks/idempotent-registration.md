---
layout: default
title: Idempotent Task Registration
parent: Recurring Tasks
nav_order: 4
---

# Idempotent Task Registration

Task keys prevent duplicate recurring tasks from being created. When you register a task with the same key twice, EverTask handles it intelligently instead of blindly creating a duplicate.

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

What happens when you dispatch with an existing key depends on the current task status:

| Existing Task Status | Behavior |
|---------------------|----------|
| **InProgress** | Returns existing task ID without making changes |
| **Pending/Queued/WaitingQueue** | Updates the task configuration |
| **Completed/Failed/Cancelled** | Removes the old task and creates a new one |

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
