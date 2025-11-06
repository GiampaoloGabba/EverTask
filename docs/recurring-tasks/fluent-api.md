---
layout: default
title: Fluent Scheduling API
parent: Recurring Tasks
nav_order: 2
---

# Fluent Scheduling API

The fluent API lets you build schedules in a type-safe, readable way.

## Basic Intervals

### Every Minute

```csharp
// Every minute
await dispatcher.Dispatch(
    new MonitorTask(),
    builder => builder.Schedule().EveryMinute());

// Every minute at specific second
await dispatcher.Dispatch(
    new MonitorTask(),
    builder => builder.Schedule().EveryMinute().AtSecond(30));

// Every N minutes
await dispatcher.Dispatch(
    new QuickCheckTask(),
    builder => builder.Schedule().Every(5).Minutes());
```

### Every Hour

```csharp
// Every hour
await dispatcher.Dispatch(
    new HourlyTask(),
    builder => builder.Schedule().EveryHour());

// Every hour at specific minute
await dispatcher.Dispatch(
    new HourlyReportTask(),
    builder => builder.Schedule().EveryHour().AtMinute(45));

// Every N hours at specific minute
await dispatcher.Dispatch(
    new PeriodicTask(),
    builder => builder.Schedule().Every(2).Hours().AtMinute(15));
```

### Every Day

```csharp
// Every day at midnight
await dispatcher.Dispatch(
    new DailyTask(),
    builder => builder.Schedule().EveryDay());

// Every day at specific time
await dispatcher.Dispatch(
    new DailyReportTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(14, 30)));

// Every N days at specific time
await dispatcher.Dispatch(
    new BiDailyTask(),
    builder => builder.Schedule().Every(2).Days().AtTime(new TimeOnly(9, 0)));

// Multiple times per day
var times = new[] { new TimeOnly(9, 0), new TimeOnly(14, 0), new TimeOnly(18, 0) };
await dispatcher.Dispatch(
    new MultipleTimesTask(),
    builder => builder.Schedule().EveryDay().AtTimes(times));
```

## Weekly Schedules

```csharp
// Every Monday
await dispatcher.Dispatch(
    new WeeklyTask(),
    builder => builder.Schedule().EveryWeek().OnDay(DayOfWeek.Monday));

// Multiple days per week
var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
await dispatcher.Dispatch(
    new BusinessDaysTask(),
    builder => builder.Schedule().EveryWeek().OnDays(days).AtTime(new TimeOnly(9, 0)));

// Every N weeks on specific day
await dispatcher.Dispatch(
    new BiWeeklyTask(),
    builder => builder.Schedule().Every(2).Weeks().OnDay(DayOfWeek.Friday));
```

## Monthly Schedules

```csharp
// First day of every month
await dispatcher.Dispatch(
    new MonthlyBillingTask(),
    builder => builder.Schedule().EveryMonth().OnDay(1));

// Last day of every month (day 31 automatically adjusts for shorter months)
await dispatcher.Dispatch(
    new EndOfMonthTask(),
    builder => builder.Schedule().EveryMonth().OnDay(31));

// Specific day of month at specific time
await dispatcher.Dispatch(
    new MidMonthTask(),
    builder => builder.Schedule().EveryMonth().OnDay(15).AtTime(new TimeOnly(12, 0)));

// First Monday of every month
await dispatcher.Dispatch(
    new FirstMondayTask(),
    builder => builder.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday));

// Last Friday of every month
await dispatcher.Dispatch(
    new LastFridayTask(),
    builder => builder.Schedule().EveryMonth().OnLast(DayOfWeek.Friday));

// Every N months
await dispatcher.Dispatch(
    new QuarterlyTask(),
    builder => builder.Schedule().Every(3).Months().OnDay(1));

// Specific months only
int[] quarterlyMonths = { 1, 4, 7, 10 }; // Jan, Apr, Jul, Oct
await dispatcher.Dispatch(
    new QuarterlyReportTask(),
    builder => builder.Schedule().OnMonths(quarterlyMonths).OnDay(1));
```

## Starting Strategies

### Run Immediately, Then Recur

```csharp
// Run now, then every hour
await dispatcher.Dispatch(
    new CacheRefreshTask(),
    builder => builder.RunNow().Then().EveryHour());

// Run now, then every day at 2 AM
await dispatcher.Dispatch(
    new DataSyncTask(),
    builder => builder.RunNow().Then().EveryDay().AtTime(new TimeOnly(2, 0)));
```

### Delay First Run

```csharp
// Wait 5 minutes, then run every hour
await dispatcher.Dispatch(
    new DelayedTask(),
    builder => builder.RunDelayed(TimeSpan.FromMinutes(5)).Then().EveryHour());

// Wait 10 seconds, then run daily
await dispatcher.Dispatch(
    new WarmupTask(),
    builder => builder.RunDelayed(TimeSpan.FromSeconds(10)).Then().EveryDay());
```

### Schedule First Run

```csharp
// Start at specific time, then recur
var startTime = new DateTimeOffset(2024, 12, 1, 9, 0, 0, TimeSpan.Zero);
await dispatcher.Dispatch(
    new ScheduledRecurringTask(),
    builder => builder.RunAt(startTime).Then().EveryDay());
```

## Limiting Executions

### Maximum Run Count

```csharp
// Run 10 times, then stop
await dispatcher.Dispatch(
    new LimitedTask(),
    builder => builder.Schedule().EveryHour().MaxRuns(10));

// Run now, then 5 more times
await dispatcher.Dispatch(
    new OnboardingTask(),
    builder => builder.RunNow().Then().EveryDay().MaxRuns(5));
```

### Run Until Date

```csharp
// Run until end of year
await dispatcher.Dispatch(
    new PromotionalTask(),
    builder => builder.Schedule()
        .EveryDay()
        .RunUntil(new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.Zero)));

// Run for next 7 days
await dispatcher.Dispatch(
    new TrialTask(),
    builder => builder.Schedule()
        .EveryDay()
        .RunUntil(DateTimeOffset.UtcNow.AddDays(7)));
```

### Combining Limits

```csharp
// Run daily, max 30 times OR until end date (whichever comes first)
await dispatcher.Dispatch(
    new CampaignTask(),
    builder => builder.Schedule()
        .EveryDay()
        .MaxRuns(30)
        .RunUntil(DateTimeOffset.UtcNow.AddMonths(1)));
```

## Complex Schedules

```csharp
// Business hours monitoring: Every 15 minutes, Mon-Fri, 9 AM - 5 PM
// (Requires multiple task registrations or custom cron)
var businessDays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                           DayOfWeek.Thursday, DayOfWeek.Friday };

for (int hour = 9; hour < 17; hour++)
{
    for (int minute = 0; minute < 60; minute += 15)
    {
        await dispatcher.Dispatch(
            new BusinessHoursMonitorTask(),
            builder => builder.Schedule()
                .EveryWeek()
                .OnDays(businessDays)
                .AtTime(new TimeOnly(hour, minute)),
            taskKey: $"monitor-{hour:D2}-{minute:D2}"); // Unique key per schedule
    }
}

// OR use cron for more complex patterns
await dispatcher.Dispatch(
    new BusinessHoursMonitorTask(),
    builder => builder.Schedule().UseCron("*/15 9-16 * * 1-5")); // Every 15 min, 9-5, Mon-Fri
```

## Next Steps

- **[Cron Expressions](cron-expressions.md)** - Maximum flexibility with cron syntax
- **[Idempotent Task Registration](idempotent-registration.md)** - Prevent duplicate tasks with task keys
- **[Managing Recurring Tasks](managing-tasks.md)** - Cancel and monitor recurring tasks
- **[Best Practices](best-practices.md)** - Follow recurring task best practices
