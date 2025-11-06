---
layout: default
title: Task Dispatching
parent: Task Operations
nav_order: 2
---

# Task Dispatching

EverTask provides several ways to dispatch tasks for immediate, delayed, or scheduled execution. This guide covers all dispatching patterns.

## Table of Contents

- [Getting the Dispatcher](#getting-the-dispatcher)
- [Fire-and-Forget Tasks](#fire-and-forget-tasks)
- [Delayed Tasks](#delayed-tasks)
- [Scheduled Tasks](#scheduled-tasks)
- [Capturing Task IDs](#capturing-task-ids)
- [Dispatch Patterns](#dispatch-patterns)

## Getting the Dispatcher

The dispatcher is available through dependency injection via the `ITaskDispatcher` interface:

```csharp
public class MyController : ControllerBase
{
    private readonly ITaskDispatcher _dispatcher;

    public MyController(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ... use _dispatcher
}
```

Or via service locator pattern (not recommended):

```csharp
var dispatcher = serviceProvider.GetRequiredService<ITaskDispatcher>();
```

## Fire-and-Forget Tasks

The simplest form of task dispatching queues a task for immediate background execution:

```csharp
// Basic dispatch
await _dispatcher.Dispatch(new SendEmailTask(email, subject, body));
```

When you dispatch a task, it gets persisted to storage (if configured), added to the worker queue, and executed by the next available worker.

### When to Use

Use fire-and-forget tasks for processing that doesn't need to block the HTTP response - things like sending emails, updating caches, or generating thumbnails. They're perfect for background operations that should start immediately but don't need to complete before you return a response.

### Example: User Registration

```csharp
[HttpPost("register")]
public async Task<IActionResult> RegisterUser(UserRegistrationDto dto)
{
    // Synchronous work (must complete before response)
    var user = await _userService.CreateUserAsync(dto);

    // Fire-and-forget tasks (run in background)
    await _dispatcher.Dispatch(new SendWelcomeEmailTask(user.Email, user.Name));
    await _dispatcher.Dispatch(new CreateUserProfileTask(user.Id));
    await _dispatcher.Dispatch(new NotifyAdminsTask(user.Id));

    return Ok(new { userId = user.Id });
}
```

## Delayed Tasks

Delayed tasks execute after a specified time period using `TimeSpan`:

```csharp
// Execute after 30 minutes
var delay = TimeSpan.FromMinutes(30);
await _dispatcher.Dispatch(
    new SendReminderTask(userId),
    delay);
```

### When to Use

Delayed tasks work great for reminders, follow-ups, retry mechanisms with backoff, or any time-based workflow where you want something to happen after a specific amount of time passes.

### Example: Order Processing Workflow

```csharp
// Send order confirmation immediately
await _dispatcher.Dispatch(new SendOrderConfirmationTask(orderId));

// Check payment status after 5 minutes
await _dispatcher.Dispatch(
    new CheckPaymentStatusTask(orderId),
    TimeSpan.FromMinutes(5));

// Send reminder after 1 hour if not processed
await _dispatcher.Dispatch(
    new SendPaymentReminderTask(orderId),
    TimeSpan.FromHours(1));

// Cancel order after 24 hours if still pending
await _dispatcher.Dispatch(
    new CancelPendingOrderTask(orderId),
    TimeSpan.FromHours(24));
```

### Delay Precision

The delay scheduler is pretty precise - tasks typically execute within milliseconds of the scheduled time under normal load. Starting in v2.0, we use `PeriodicTimerScheduler` for high-precision timing. And like everything else in EverTask, delayed tasks persist across application restarts, so you don't lose them if your app goes down.

## Scheduled Tasks

Scheduled tasks execute at a specific date and time using `DateTimeOffset`:

```csharp
// Execute at a specific time
var scheduledTime = new DateTimeOffset(2024, 12, 25, 10, 0, 0, TimeSpan.Zero);
await _dispatcher.Dispatch(
    new SendChristmasGreetingTask(),
    scheduledTime);
```

### When to Use

Scheduled tasks are ideal when you need something to happen at a specific date and time - maintenance windows, scheduled reports, marketing campaign launches, or any time-zone specific operations.

### Example: Campaign Management

```csharp
// Schedule marketing campaign for specific time
var campaignLaunchTime = new DateTimeOffset(2024, 12, 1, 9, 0, 0, TimeSpan.FromHours(-5)); // 9 AM EST
await _dispatcher.Dispatch(
    new LaunchMarketingCampaignTask(campaignId),
    campaignLaunchTime);

// Schedule report generation for end of month
var endOfMonth = new DateTimeOffset(2024, 12, 31, 23, 59, 0, TimeSpan.Zero);
await _dispatcher.Dispatch(
    new GenerateMonthlyReportTask(userId),
    endOfMonth);
```

### Time Zone Considerations

```csharp
// Schedule in user's local time zone
var userTimeZone = TimeZoneInfo.FindSystemTimeZoneById(user.TimeZoneId);
var localTime = new DateTimeOffset(2024, 12, 25, 10, 0, 0, userTimeZone.BaseUtcOffset);

await _dispatcher.Dispatch(
    new SendBirthdayGreetingTask(user.Id),
    localTime);
```

### Scheduled Task Behavior

A few things to keep in mind: if the scheduled time is already in the past when you dispatch, the task will execute immediately. Scheduled tasks persist across application restarts, so they'll still run even if your app goes down. For time zones, use UTC when you want absolute time regardless of location, or use specific offsets when you need local time behavior.

## Capturing Task IDs

Every task you dispatch gets a unique `Guid` that you can capture and use later:

```csharp
Guid taskId = await _dispatcher.Dispatch(new MyTask());

// Store the ID for later reference
await _database.SaveTaskIdAsync(orderId, taskId);
```

### Using Task IDs

#### Cancellation

You can cancel a task before it starts executing:

```csharp
// Dispatch task
Guid taskId = await _dispatcher.Dispatch(
    new ProcessPaymentTask(paymentId),
    TimeSpan.FromMinutes(10));

// User cancelled - stop the task
await _dispatcher.Cancel(taskId);
```

> **Note:** Cancellation only works for tasks that haven't started executing yet. For running tasks, the `CancellationToken` in the handler will be triggered.

#### Tracking

Task IDs are also useful for tracking operation status. For example, if you dispatch multiple related tasks, you can store their IDs to check on them later:

```csharp
// Dispatch multiple related tasks
var emailTaskId = await _dispatcher.Dispatch(new SendEmailTask(...));
var smsTaskId = await _dispatcher.Dispatch(new SendSmsTask(...));

// Store for tracking
var notification = new Notification
{
    Id = notificationId,
    EmailTaskId = emailTaskId,
    SmsTaskId = smsTaskId
};
await _database.SaveAsync(notification);
```

#### Querying Task Status

```csharp
// Later, check task status from storage
var task = await _taskStorage.GetAsync(taskId);

switch (task.Status)
{
    case TaskStatus.Pending:
        // Not started yet
        break;
    case TaskStatus.InProgress:
        // Currently executing
        break;
    case TaskStatus.Completed:
        // Finished successfully
        break;
    case TaskStatus.Failed:
        // Failed after all retries
        break;
    case TaskStatus.Cancelled:
        // Was cancelled
        break;
}
```

## Dispatch Patterns

### Batch Dispatching

When you need to dispatch multiple tasks, you can loop through them and collect the task IDs:

```csharp
var taskIds = new List<Guid>();

foreach (var user in users)
{
    var taskId = await _dispatcher.Dispatch(new SendNewsletterTask(user.Id));
    taskIds.Add(taskId);
}

// Store all task IDs
await _database.SaveBatchTaskIdsAsync(batchId, taskIds);
```

### Conditional Dispatching

Sometimes you want different dispatch strategies based on your business logic:

```csharp
if (order.Total > 1000)
{
    // High-value orders get immediate processing
    await _dispatcher.Dispatch(new ProcessHighValueOrderTask(order.Id));
}
else
{
    // Regular orders can be delayed
    await _dispatcher.Dispatch(
        new ProcessRegularOrderTask(order.Id),
        TimeSpan.FromMinutes(5));
}
```

### Task Chains

You can build sequential workflows by dispatching the next task when the previous one completes:

```csharp
// In a handler's OnCompleted method
public override async ValueTask OnCompleted(Guid taskId)
{
    // First task completed, dispatch next step
    await _dispatcher.Dispatch(new SecondStepTask(_task.CorrelationId));
}
```

See [Task Continuations](task-orchestration.md) for more details.

### Error Recovery

When things go wrong, you can dispatch compensating tasks to roll back or clean up:

```csharp
// In a handler's OnError method
public override async ValueTask OnError(Guid taskId, Exception? exception, string? message)
{
    _logger.LogError(exception, "Task {TaskId} failed, dispatching rollback", taskId);

    // Dispatch compensating task
    await _dispatcher.Dispatch(new RollbackOperationTask(_task.OperationId));
}
```

## Best Practices

### 1. Always Await Dispatch

Don't fire-and-forget your dispatch calls - always await them to ensure the task gets persisted:

```csharp
// ✅ Good: Await to ensure persistence
await _dispatcher.Dispatch(new MyTask());

// ❌ Bad: Fire-and-forget without await (might not persist)
_ = _dispatcher.Dispatch(new MyTask()); // DON'T DO THIS
```

### 2. Handle Dispatch Failures

Wrap dispatch calls in try-catch blocks so you can handle failures gracefully:

```csharp
try
{
    await _dispatcher.Dispatch(new MyTask());
}
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to dispatch task");
    // Handle error (retry, alert, fallback, etc.)
}
```

### 3. Use Appropriate Timing

Choose the right timing method for your use case - `TimeSpan` for relative delays, `DateTimeOffset` for specific times:

```csharp
// ✅ Good: Relative delay for "X time from now"
await _dispatcher.Dispatch(task, TimeSpan.FromHours(1));

// ✅ Good: Absolute schedule for specific time
await _dispatcher.Dispatch(task, new DateTimeOffset(2024, 12, 25, 10, 0, 0, TimeSpan.Zero));

// ❌ Bad: Absolute time for relative delay (harder to understand)
await _dispatcher.Dispatch(task, DateTimeOffset.UtcNow.AddHours(1));
```

### 4. Store Important Task IDs

Capture task IDs when you need to track or cancel tasks, but don't bother if it's truly fire-and-forget:

```csharp
// ✅ Good: Store task ID when you need to track or cancel
var taskId = await _dispatcher.Dispatch(new CriticalTask(...));
await _database.SaveTaskIdAsync(referenceId, taskId);

// ✅ Good: Ignore task ID for fire-and-forget
await _dispatcher.Dispatch(new LoggingTask(...));
```

### 5. Consider Idempotency

For recurring tasks, use task keys to prevent accidentally registering the same task multiple times:

```csharp
// For critical recurring dispatches, use task keys to prevent duplicates
await _dispatcher.Dispatch(
    new DailyReportTask(),
    recurring => recurring.Schedule().EveryDay(),
    taskKey: "daily-report"); // Prevents duplicate registration
```

See [Idempotent Task Registration](recurring-tasks/idempotent-registration.md) for more details.

## Performance Considerations

### Queue Capacity

If you're dispatching a lot of tasks quickly, you might need to bump up the queue capacity:

```csharp
// Configure sufficient capacity in startup
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(5000); // Increase if dispatching in bulk
});
```

### Batching Database Operations

When you're dispatching thousands of tasks, think about batching instead of individual dispatches:

```csharp
// Less efficient: Many small dispatches
foreach (var user in users) // 10,000 users
{
    await _dispatcher.Dispatch(new SendEmailTask(user.Id));
}

// More efficient: Batch dispatch
await _dispatcher.Dispatch(new SendBulkEmailTask(users.Select(u => u.Id).ToList()));
```

### High-Load Scenarios

If you're pushing extreme dispatch rates (think 10,000+ tasks per second), enable the sharded scheduler to auto-scale with your CPU cores:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly)
       .UseShardedScheduler(); // Auto-scale with CPU cores
});
```

Check out [Sharded Scheduler](sharded-scheduler.md) for the full details.

## Next Steps

- **[Recurring Tasks](recurring-tasks.md)** - Schedule tasks to run repeatedly
- **[Task Orchestration](advanced-features.md)** - Continuations and workflow patterns
- **[Scalability](scalability.md)** - Multi-queue and sharded scheduler
- **[Resilience](resilience.md)** - Configure retry policies and error handling
- **[Monitoring](monitoring.md)** - Track task execution with events
