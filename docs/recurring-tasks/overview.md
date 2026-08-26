---
layout: default
title: Overview
parent: Recurring Tasks
nav_order: 1
---

# Recurring Tasks Overview

Schedule recurring tasks with a type-safe fluent API or cron, from an hourly job to a cron schedule.

## Key Features

- **Fluent API**: Type-safe, readable schedule building
- **Cron Support**: Full cron expression support for complex patterns
- **Idempotent Registration**: Prevent duplicate tasks with task keys
- **Flexible Starting Strategies**: Run immediately, delay, or schedule first run
- **Execution Limits**: MaxRuns and RunUntil for time-limited tasks
- **Time Zones**: Read a calendar schedule on a real clock, daylight saving included
- **Durable Occurrences**: One row per occurrence, with misfire policies that replay what a downtime missed
- **Occurrence Providers**: Take the grid from your own calendar when no interval or cron can express it
- **Persistent Schedules**: Recurring tasks survive application restarts

## Quick Examples

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

### [Fluent Scheduling API](fluent-api.md)
Learn how to use the fluent API to build schedules for minute-based, hourly, daily, weekly, and monthly recurring tasks. Covers basic intervals, starting strategies, execution limits, and complex schedules.

### [Cron Expressions](cron-expressions.md)
Use cron expressions for maximum scheduling flexibility. Learn the syntax, common patterns, and how to combine cron with starting strategies and limits.

### [Time Zones](time-zones.md)
Run a calendar schedule on a real clock with `InTimeZone`, set a default zone for the whole application, and see what happens on the two days a year a local hour is skipped or repeated.

### [Durable Occurrences](durable-occurrences.md)
Give every due slot its own persisted row, and choose what a downtime does to the slots it missed: skip them,
collapse them into one run, or replay them under explicit caps.

### [Occurrence Providers](occurrence-providers.md)
Take the occurrence grid from your own calendar — business days, a holiday table, opening hours — when no
interval or cron expression can express it, and keep every other feature working over it.

### [Idempotent Task Registration](idempotent-registration.md)
Prevent duplicate recurring tasks using task keys. Learn about update behavior, startup registration patterns, and dynamic configuration.

### [Managing Recurring Tasks](managing-tasks.md)
Reschedule, re-evaluate, resume and cancel a running schedule with `ITaskScheduleManager`, requeue a failed
occurrence, and monitor schedules through lifecycle hooks and storage queries.

### [Best Practices](best-practices.md)
Follow best practices for task keys, schedule format selection, long-running tasks, time zones, execution limits, and health monitoring.

## Next Steps

Start with the [Fluent Scheduling API](fluent-api.md) to learn the type-safe way to build recurring schedules, or jump to [Cron Expressions](cron-expressions.md) if you need complex scheduling patterns.

---

> **Note**: Recurring tasks persist across application restarts. Your schedules survive even if your app crashes or redeploys!
