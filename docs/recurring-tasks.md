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
- **Time Zones**: Read a calendar schedule on a real clock, daylight saving included
- **Durable Occurrences**: One row per occurrence, with misfire policies that replay what a downtime missed
- **Runtime Management**: Reschedule, re-evaluate, resume and cancel a schedule while the app runs
- **Occurrence Providers**: Take the grid from your own calendar when no interval or cron can express it
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

## Excluding moments

Subtract fixed weekdays, dates or absolute windows from any built-in interval or cron grid:

```csharp
.Schedule().Every(1).Days().AtTime(new TimeOnly(8, 0))
    .Except(e => e
        .OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday)
        .OnDates(new DateOnly(2026, 12, 25))
        .Between(maintenanceStart, maintenanceEnd))

.Schedule().Every(4).Hours().ExceptWeekends()
```

Calls to `Except` are additive and repeatable. `OnDays` and `OnDates` use the schedule's persisted time zone,
or UTC when none is named; `Between(from, to)` compares absolute instants in the half-open range `[from, to)`.
`ExceptWeekends()` is sugar for excluding Saturday and Sunday.

An excluded grid slot does not exist: it consumes no `MaxRuns` budget, misfire or skipped count, durable row,
audit entry, log or monitoring event. Explicit first-run overrides (`RunNow`, `RunDelayed`, `RunAt`) are not
grid slots and are not filtered.

Exclusions compose with cron and every built-in interval. They automatically apply to `RunUntil`, misfire
`Skip`/`FireOnce`/`CatchUp`, `SkipOldest`, backfill and durable occurrences because all of those operate on the
filtered grid. They cannot be combined with `INextOccurrenceProvider`, whose implementation owns its own
calendar. `RescheduleMode.RebaseFromCursor` is also refused when either definition has exclusions; use
`RecalculateFromNow`. A search that cannot find a usable slot within the fixed evaluation budget fails
explicitly—it is never treated as the end of the series.

## Topics

### [Overview](recurring-tasks/overview.md)
Introduction to recurring tasks with quick examples and feature overview.

### [Fluent Scheduling API](recurring-tasks/fluent-api.md)
Learn how to use the fluent API to build schedules for minute-based, hourly, daily, weekly, and monthly recurring tasks. Covers basic intervals, starting strategies, execution limits, and complex schedules.

### [Cron Expressions](recurring-tasks/cron-expressions.md)
Use cron expressions for maximum scheduling flexibility. Learn the syntax, common patterns, and how to combine cron with starting strategies and limits.

### [Time Zones](recurring-tasks/time-zones.md)
Run a calendar schedule on a real clock with `InTimeZone`, set a default zone for the whole application, and see what happens on the two days a year a local hour is skipped or repeated.

### [Durable Occurrences](recurring-tasks/durable-occurrences.md)
Give every due slot its own persisted row, and choose what a downtime does to the slots it missed: skip them, collapse them into one run, or replay them under explicit caps.

### [Occurrence Providers](recurring-tasks/occurrence-providers.md)
Take the occurrence grid from your own calendar — business days, a holiday table, opening hours — when no interval or cron expression can express it.

### [Idempotent Task Registration](recurring-tasks/idempotent-registration.md)
Prevent duplicate recurring tasks using task keys. Learn about update behavior, startup registration patterns, and dynamic configuration.

### [Managing Recurring Tasks](recurring-tasks/managing-tasks.md)
Reschedule, re-evaluate, resume and cancel a running schedule with `ITaskScheduleManager`, requeue a failed occurrence, and monitor schedules through lifecycle hooks and storage queries.

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

Re-dispatching under the same key is the registration-time way to change a schedule. To change one **while it
is running** — and to decide what happens to the occurrences it had already planned — use
[`ITaskScheduleManager`](recurring-tasks/managing-tasks.md):

```csharp
public async Task MoveUserReport(string userId, TimeOnly newTime)
{
    await _scheduleManager.Reschedule(
        $"user-report-{userId}",
        r => r.Schedule().EveryDay().AtTime(newTime),
        RescheduleMode.RecalculateFromNow);
}
```

## Next Steps

Start with the [Overview](recurring-tasks/overview.md) to learn about recurring task features, or jump directly to:
- **[Fluent Scheduling API](recurring-tasks/fluent-api.md)** - Type-safe schedule building
- **[Cron Expressions](recurring-tasks/cron-expressions.md)** - Complex scheduling patterns
- **[Time Zones](recurring-tasks/time-zones.md)** - Local hours that survive daylight saving
- **[Durable Occurrences](recurring-tasks/durable-occurrences.md)** - One row per occurrence, and what a downtime does to the ones it missed
- **[Managing Tasks](recurring-tasks/managing-tasks.md)** - Change a schedule while the app runs
- **[Best Practices](recurring-tasks/best-practices.md)** - Patterns and pitfalls

---

> **Note**: Recurring schedules are persisted, so they survive restarts and redeploys.
