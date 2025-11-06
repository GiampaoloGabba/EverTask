---
layout: default
title: Overview
parent: Recurring Tasks
nav_order: 1
---

# Recurring Tasks Overview

EverTask provides a powerful fluent API for scheduling recurring tasks, from simple hourly jobs to complex cron-based schedules.

## Key Features

- **Fluent API**: Type-safe, readable schedule building
- **Cron Support**: Full cron expression support for complex patterns
- **Idempotent Registration**: Prevent duplicate tasks with task keys
- **Flexible Starting Strategies**: Run immediately, delay, or schedule first run
- **Execution Limits**: MaxRuns and RunUntil for time-limited tasks
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

### [Idempotent Task Registration](idempotent-registration.md)
Prevent duplicate recurring tasks using task keys. Learn about update behavior, startup registration patterns, and dynamic configuration.

### [Managing Recurring Tasks](managing-tasks.md)
Cancel, retrieve information about, and monitor recurring tasks using lifecycle hooks and storage queries.

### [Best Practices](best-practices.md)
Follow best practices for task keys, schedule format selection, long-running tasks, time zones, execution limits, and health monitoring.

## Next Steps

Start with the [Fluent Scheduling API](fluent-api.md) to learn the type-safe way to build recurring schedules, or jump to [Cron Expressions](cron-expressions.md) if you need complex scheduling patterns.

---

> **Note**: Recurring tasks persist across application restarts. Your schedules survive even if your app crashes or redeploys!
