---
layout: default
title: Recurring Tasks
nav_order: 8
---

# Recurring Tasks

EverTask provides a fluent API for scheduling recurring tasks, from simple hourly jobs to complex cron-based schedules.

## Table of Contents

- [Quick Examples](#quick-examples)
- [Fluent Scheduling API](#fluent-scheduling-api)
- [Cron Expressions](#cron-expressions)
- [Idempotent Task Registration](#idempotent-task-registration)
- [Managing Recurring Tasks](#managing-recurring-tasks)
- [Best Practices](#best-practices)

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

## Fluent Scheduling API

The fluent API lets you build schedules in a type-safe, readable way.

### Basic Intervals

#### Every Minute

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

#### Every Hour

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

#### Every Day

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

### Weekly Schedules

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

### Monthly Schedules

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

### Starting Strategies

#### Run Immediately, Then Recur

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

#### Delay First Run

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

#### Schedule First Run

```csharp
// Start at specific time, then recur
var startTime = new DateTimeOffset(2024, 12, 1, 9, 0, 0, TimeSpan.Zero);
await dispatcher.Dispatch(
    new ScheduledRecurringTask(),
    builder => builder.RunAt(startTime).Then().EveryDay());
```

### Limiting Executions

#### Maximum Run Count

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

#### Run Until Date

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

#### Combining Limits

```csharp
// Run daily, max 30 times OR until end date (whichever comes first)
await dispatcher.Dispatch(
    new CampaignTask(),
    builder => builder.Schedule()
        .EveryDay()
        .MaxRuns(30)
        .RunUntil(DateTimeOffset.UtcNow.AddMonths(1)));
```

### Complex Schedules

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

## Cron Expressions

For complex scheduling patterns, cron expressions give you maximum flexibility:

### Cron Syntax

```
* * * * *
│ │ │ │ │
│ │ │ │ └─── Day of week (0-6, Sunday = 0)
│ │ │ └───── Month (1-12)
│ │ └─────── Day of month (1-31)
│ └───────── Hour (0-23)
└─────────── Minute (0-59)
```

### Common Cron Patterns

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

### Combining Cron with Starting Strategies

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

### Cron with Limits

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

## Idempotent Task Registration

Task keys prevent duplicate recurring tasks from being created. When you register a task with the same key twice, EverTask handles it intelligently instead of blindly creating a duplicate:

### Basic Usage

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

### Update Behavior

What happens when you dispatch with an existing key depends on the current task status:

| Existing Task Status | Behavior |
|---------------------|----------|
| **InProgress** | Returns existing task ID without making changes |
| **Pending/Queued/WaitingQueue** | Updates the task configuration |
| **Completed/Failed/Cancelled** | Removes the old task and creates a new one |

### Updating Schedules

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

### Startup Task Registration

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

### Dynamic Configuration

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

### Task Key Guidelines

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

## Managing Recurring Tasks

### Cancelling Recurring Tasks

```csharp
// Store task ID when registering
Guid taskId = await dispatcher.Dispatch(
    new RecurringTask(),
    builder => builder.Schedule().EveryHour(),
    taskKey: "my-recurring-task");

// Later, cancel it
await dispatcher.Cancel(taskId);
```

### Retrieving Task Information

```csharp
// Get task by key
var task = await _taskStorage.GetByTaskKey("daily-report");

if (task != null)
{
    Console.WriteLine($"Task ID: {task.PersistenceId}");
    Console.WriteLine($"Status: {task.Status}");
    Console.WriteLine($"Current Run Count: {task.CurrentRunCount}");
    Console.WriteLine($"Next Run: {task.ExecutionTime}");
}
```

### Monitoring Recurring Tasks

Lifecycle hooks let you track execution patterns and catch issues:

```csharp
public class MonitoredRecurringHandler : EverTaskHandler<MonitoredRecurringTask>
{
    private readonly ILogger<MonitoredRecurringHandler> _logger;

    public MonitoredRecurringHandler(ILogger<MonitoredRecurringHandler> logger)
    {
        _logger = logger;
    }

    public override async Task Handle(MonitoredRecurringTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Recurring task execution #{Count}", task.CurrentExecutionCount);

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

## Best Practices

### 1. Always Use Task Keys for Recurring Tasks

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

### 2. Use Meaningful Task Keys

```csharp
// ✅ Good: Descriptive and unique
taskKey: "cleanup-expired-sessions"
taskKey: "user-{userId}:daily-summary"
taskKey: "tenant-{tenantId}:billing:monthly"

// ❌ Bad: Generic or unclear
taskKey: "task1"
taskKey: "job"
```

### 3. Choose the Right Schedule Format

Use the fluent API for readability when the pattern is straightforward. Save cron for complex schedules that would be awkward to express with the fluent API.

```csharp
// ✅ Good: Fluent API is clear and readable for simple schedules
builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(14, 0))

// ✅ Good: Cron shines for complex patterns
builder => builder.Schedule().UseCron("*/15 9-17 * * 1-5")

// ❌ Avoid: Cron makes simple patterns harder to understand
builder => builder.Schedule().UseCron("0 14 * * *")
```

### 4. Handle Long-Running Recurring Tasks

If your task takes a while to complete, set a timeout that gives it room to breathe. Also respect cancellation tokens so tasks can be stopped gracefully.

```csharp
public class LongRunningRecurringHandler : EverTaskHandler<LongRunningRecurringTask>
{
    public LongRunningRecurringHandler()
    {
        Timeout = TimeSpan.FromMinutes(30); // Generous timeout for long operations
    }

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

### 5. Consider Time Zones

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

### 6. Limit Recurring Tasks Appropriately

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

### 7. Monitor Recurring Task Health

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

- **[Advanced Features](advanced-features.md)** - Multi-queue, continuations, sharded scheduler
- **[Resilience](resilience.md)** - Retry policies and error handling
- **[Monitoring](monitoring.md)** - Track recurring task executions
- **[Configuration Reference](configuration-reference.md)** - All configuration options

---

> **Note**: Recurring tasks persist across application restarts. Your schedules survive even if your app crashes or redeploys!
