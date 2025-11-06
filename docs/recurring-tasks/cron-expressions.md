---
layout: default
title: Cron Expressions
parent: Recurring Tasks
nav_order: 3
---

# Cron Expressions

For complex scheduling patterns, cron expressions give you maximum flexibility.

## Cron Syntax

```
* * * * *
│ │ │ │ │
│ │ │ │ └─── Day of week (0-6, Sunday = 0)
│ │ │ └───── Month (1-12)
│ │ └─────── Day of month (1-31)
│ └───────── Hour (0-23)
└─────────── Minute (0-59)
```

## Common Cron Patterns

```csharp
// Every 30 minutes
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("*/30 * * * *"));

// Every day at noon
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("0 12 * * *"));

// Every Monday at 8 AM
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("0 8 * * 1"));

// Every 15 minutes during business hours (9 AM - 5 PM), weekdays only
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("*/15 9-17 * * 1-5"));

// First day of every month at midnight
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("0 0 1 * *"));

// Every quarter (Jan, Apr, Jul, Oct) on the 1st at noon
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("0 12 1 1,4,7,10 *"));
```

## Combining Cron with Starting Strategies

```csharp
// Run immediately, then follow cron schedule
await dispatcher.Dispatch(
    new Task(),
    builder => builder.RunNow().Then().UseCron("*/30 * * * *"));

// Delay start, then follow cron schedule
await dispatcher.Dispatch(
    new Task(),
    builder => builder.RunDelayed(TimeSpan.FromMinutes(5)).Then().UseCron("0 */2 * * *"));

// Schedule start, then follow cron schedule
var startTime = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);
await dispatcher.Dispatch(
    new Task(),
    builder => builder.RunAt(startTime).Then().UseCron("0 2 * * *"));
```

## Cron with Limits

```csharp
// Cron schedule with max runs
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule().UseCron("0 9 * * *").MaxRuns(10));

// Cron schedule with end date
await dispatcher.Dispatch(
    new Task(),
    builder => builder.Schedule()
        .UseCron("*/30 * * * *")
        .RunUntil(DateTimeOffset.UtcNow.AddDays(7)));
```

## Next Steps

- **[Fluent Scheduling API](fluent-api.md)** - Type-safe schedule building for simple patterns
- **[Idempotent Task Registration](idempotent-registration.md)** - Prevent duplicate tasks
- **[Managing Recurring Tasks](managing-tasks.md)** - Cancel and monitor tasks
- **[Best Practices](best-practices.md)** - Choose the right schedule format
