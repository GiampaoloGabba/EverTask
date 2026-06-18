---
layout: default
title: Recurring Tasks
nav_order: 3
has_children: true
---

# Recurring Tasks

Schedule work that runs on a repeating cadence, from an hourly job to a cron schedule.

## Overview

Recurring tasks run on a schedule you define with either a type-safe fluent API or a cron expression. The schedule is persisted, so it survives restarts.

**Key Features:**
- **Fluent API**: Type-safe, readable schedule building
- **Cron Support**: Full cron expression support for complex patterns
- **Idempotent Registration**: Prevent duplicate tasks with task keys
- **Flexible Starting Strategies**: Run immediately, delay, or schedule first run
- **Execution Limits**: MaxRuns and RunUntil for time-limited tasks
- **Persistent Schedules**: Recurring tasks survive application restarts

## Quick Start

```csharp
// Run every minute at the 30th second
await dispatcher.Dispatch(
    new HealthCheckTask(),
    builder => builder.Schedule().EveryMinute().AtSecond(30));

// Run daily at 3 AM
await dispatcher.Dispatch(
    new DailyCleanupTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)));

// Run every Monday at 9 AM
await dispatcher.Dispatch(
    new WeeklyReportTask(),
    builder => builder.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(9, 0)));

// Run on the first day of every month
await dispatcher.Dispatch(
    new MonthlyBillingTask(),
    builder => builder.Schedule().EveryMonth().OnDay(1));

// Run immediately, then every hour
await dispatcher.Dispatch(
    new RefreshCacheTask(),
    builder => builder.RunNow().Then().EveryHour());
```

## Topics

### [Overview](recurring-tasks/overview.md)
Introduction to recurring tasks with quick examples and feature overview.

### [Fluent Scheduling API](recurring-tasks/fluent-api.md)
Learn how to use the fluent API to build schedules for minute-based, hourly, daily, weekly, and monthly recurring tasks. Covers basic intervals, starting strategies, execution limits, and complex schedules.

### [Cron Expressions](recurring-tasks/cron-expressions.md)
Use cron expressions for maximum scheduling flexibility. Learn the syntax, common patterns, and how to combine cron with starting strategies and limits.

### [Idempotent Task Registration](recurring-tasks/idempotent-registration.md)
Prevent duplicate recurring tasks using task keys. Learn about update behavior, startup registration patterns, and dynamic configuration.

### [Managing Recurring Tasks](recurring-tasks/managing-tasks.md)
Cancel, retrieve information about, and monitor recurring tasks using lifecycle hooks and storage queries.

### [Best Practices](recurring-tasks/best-practices.md)
Follow best practices for task keys, schedule format selection, long-running tasks, time zones, execution limits, and health monitoring.

## Common Patterns

### Startup Registration

Register all recurring tasks at application startup using a hosted service:

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
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

// Register in Program.cs
builder.Services.AddHostedService<RecurringTasksRegistrar>();
```

### Dynamic Scheduling

Update task schedules based on user preferences or configuration changes:

```csharp
public async Task UpdateUserReportSchedule(string userId, TimeOnly newTime)
{
    await _dispatcher.Dispatch(
        new UserReportTask(userId),
        r => r.Schedule().EveryDay().AtTime(newTime),
        taskKey: $"user-report-{userId}");
}
```

## Next Steps

Start with the [Overview](recurring-tasks/overview.md) to learn about recurring task features, or jump directly to:
- **[Fluent Scheduling API](recurring-tasks/fluent-api.md)** - Type-safe schedule building
- **[Cron Expressions](recurring-tasks/cron-expressions.md)** - Complex scheduling patterns
- **[Best Practices](recurring-tasks/best-practices.md)** - Patterns and pitfalls

---

> **Note**: Recurring schedules are persisted, so they survive restarts and redeploys.
