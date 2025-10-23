![EverTask Logo](https://raw.githubusercontent.com/GiampaoloGabba/EverTask/master/assets/logo-main.png)

[![Build](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml/badge.svg)](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.svg?label=Evertask)](https://www.nuget.org/packages/evertask)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.abstractions.svg?label=Evertask.Abstractions)](https://www.nuget.org/packages/evertask.abstractions)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.sqlserver.svg?label=Evertask.SqlServer)](https://www.nuget.org/packages/evertask.sqlserver)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.serilog.svg?label=Evertask.Serilog)](https://www.nuget.org/packages/evertask.serilog)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR)

## Overview

**EverTask** is a high-performance .NET library for background task execution. It handles everything from simple fire-and-forget operations to complex recurring schedules, with persistence that survives application restarts.

If you've used MediatR, you'll feel right at home with the request/handler pattern. But unlike traditional in-memory task queues, EverTask persists tasks to storage, supports multi-queue workload isolation, and scales to extreme loads (>10k tasks/sec) when needed.

Works great with ASP.NET Core, Windows Services, or any .NET project that needs reliable background processing.

## Key Features

- 🚀 **Background Execution** - Fire-and-forget, scheduled, and recurring tasks with elegant API
- 🎯 **Multi-Queue Support** (v1.6+) - Isolate workloads by priority, resource type, or business domain
- 🔑 **Idempotent Task Registration** (v1.6+) - Prevent duplicate recurring tasks with unique keys
- ⚡ **High-Performance Scheduler** (v2.0+) - PeriodicTimerScheduler with 90% less lock contention and zero CPU when idle
- 🔥 **Extreme Load Support** (v2.0+) - Optional sharded scheduler for >10k tasks/sec scenarios
- 💾 **Smart Persistence** - Tasks resume after application restarts (SQL Server, SQLite, In-Memory)
- 🔄 **Powerful Retry Policies** - Built-in linear retry, custom policies, Polly integration
- ⏱️ **Timeout Management** - Global and per-task timeout configuration
- 📊 **Real-Time Monitoring** - Local events + SignalR remote monitoring
- 🎨 **Fluent Scheduling API** - Intuitive recurring task configuration (every minute, hour, day, week, month, cron)
- 🔧 **Extensible Architecture** - Custom storage, retry policies, and schedulers
- 🏎️ **Optimized Performance** (v2.0+) - Reflection caching, lazy serialization, DbContext pooling
- 📈 **Auto-Scaling Defaults** (v2.0+) - Configuration that scales with your CPU cores
- 🔌 **Serilog Integration** - Detailed structured logging
- ✨ **Async All The Way** - Fully asynchronous for maximum scalability

## Quick Start

### Installation

```bash
dotnet add package EverTask
dotnet add package EverTask.SqlServer  # Or EverTask.Sqlite
```

### Configuration

```csharp
using EverTask;

var builder = WebApplication.CreateBuilder(args);

// Register EverTask with SQL Server storage
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = true;
    });

var app = builder.Build();
app.Run();
```

### Create Your First Task

Define a task request:

```csharp
public record SendWelcomeEmailTask(string UserEmail, string UserName) : IEverTask;
```

Create a handler:

```csharp
public class SendWelcomeEmailHandler : EverTaskHandler<SendWelcomeEmailTask>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendWelcomeEmailHandler> _logger;

    public SendWelcomeEmailHandler(IEmailService emailService, ILogger<SendWelcomeEmailHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public override async Task Handle(SendWelcomeEmailTask task, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending welcome email to {Email}", task.UserEmail);

        await _emailService.SendWelcomeEmailAsync(
            task.UserEmail,
            task.UserName,
            cancellationToken);
    }
}
```

Dispatch the task:

```csharp
public class UserController : ControllerBase
{
    private readonly ITaskDispatcher _dispatcher;

    public UserController(ITaskDispatcher dispatcher) => _dispatcher = dispatcher;

    [HttpPost("register")]
    public async Task<IActionResult> RegisterUser(UserRegistrationDto dto)
    {
        // Create user...

        // Send welcome email in background
        await _dispatcher.Dispatch(new SendWelcomeEmailTask(dto.Email, dto.Name));

        return Ok();
    }
}
```

## Documentation

📚 **[Full Documentation](https://GiampaoloGabba.github.io/EverTask)** - Complete guides, tutorials, and API reference

### Quick Links

- **[Getting Started](https://GiampaoloGabba.github.io/EverTask/getting-started.html)** - Installation, configuration, and your first task
- **[Task Creation](https://GiampaoloGabba.github.io/EverTask/task-creation.html)** - Requests, handlers, lifecycle hooks, and best practices
- **[Task Dispatching](https://GiampaoloGabba.github.io/EverTask/task-dispatching.html)** - Fire-and-forget, delayed, and scheduled tasks
- **[Recurring Tasks](https://GiampaoloGabba.github.io/EverTask/recurring-tasks.html)** - Fluent scheduling API, cron expressions, idempotent registration
- **[Advanced Features](https://GiampaoloGabba.github.io/EverTask/advanced-features.html)** - Multi-queue, sharded scheduler, continuations, cancellation
- **[Resilience & Error Handling](https://GiampaoloGabba.github.io/EverTask/resilience.html)** - Retry policies, timeouts, CancellationToken usage
- **[Monitoring](https://GiampaoloGabba.github.io/EverTask/monitoring.html)** - Events, SignalR integration, custom monitoring
- **[Storage Configuration](https://GiampaoloGabba.github.io/EverTask/storage.html)** - SQL Server, SQLite, In-Memory, custom implementations
- **[Configuration Reference](https://GiampaoloGabba.github.io/EverTask/configuration-reference.html)** - Complete configuration documentation
- **[Configuration Cheatsheet](https://GiampaoloGabba.github.io/EverTask/configuration-cheatsheet.html)** - Quick reference for all config options
- **[Architecture & Internals](https://GiampaoloGabba.github.io/EverTask/architecture.html)** - How EverTask works under the hood

## Showcase: Powerful Features

### Fluent Recurring Scheduler

Schedule recurring tasks with an intuitive, type-safe API:

```csharp
// Run every day at 3 AM
await dispatcher.Dispatch(
    new DailyCleanupTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)));

// Run every Monday, Wednesday, Friday at 9 AM
var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
await dispatcher.Dispatch(
    new BackupTask(),
    builder => builder.Schedule().EveryWeek().OnDays(days).AtTime(new TimeOnly(9, 0)));

// Run on the first day of every month
await dispatcher.Dispatch(
    new MonthlyBillingTask(),
    builder => builder.Schedule().EveryMonth().OnDay(1));

// Complex: Every 15 minutes during business hours, weekdays only
await dispatcher.Dispatch(
    new HealthCheckTask(),
    builder => builder.Schedule().UseCron("*/15 9-17 * * 1-5"));

// Limit executions: Run daily for 30 days, then stop
await dispatcher.Dispatch(
    new TrialFeatureTask(userId),
    builder => builder.Schedule()
        .EveryDay()
        .MaxRuns(30)
        .RunUntil(DateTimeOffset.UtcNow.AddDays(30)));
```

### Multi-Queue Workload Isolation

Keep critical tasks separate from heavy background work:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
// High-priority queue for critical operations
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2)))

// Background queue for CPU-intensive work
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(100))

// Email queue for bulk operations
.AddQueue("email", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(10000)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

.AddSqlServerStorage(connectionString);
```

Route tasks to queues:

```csharp
public class PaymentProcessingHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override string? QueueName => "critical"; // High-priority queue

    public override async Task Handle(ProcessPaymentTask task, CancellationToken cancellationToken)
    {
        // Critical payment processing
    }
}
```

### Smart Retry Policies with Exception Filtering

Control which exceptions trigger retries to fail-fast on permanent errors:

```csharp
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    private readonly ILogger<DatabaseTaskHandler> _logger;

    // Only retry transient database errors
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors();

    // Track retry attempts for monitoring
    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _logger.LogWarning(exception,
            "Database task {TaskId} retry {Attempt} after {DelayMs}ms",
            taskId, attemptNumber, delay.TotalMilliseconds);

        return ValueTask.CompletedTask;
    }

    public override async Task Handle(DatabaseTask task, CancellationToken ct)
    {
        await _dbContext.ProcessAsync(task.Data, ct);
    }
}
```

**Exception Filtering Options**:

```csharp
// Whitelist: Only retry specific exceptions
RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .Handle<DbException>()
    .Handle<HttpRequestException>();

// Blacklist: Retry all except permanent errors
RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .DoNotHandle<ArgumentException>()
    .DoNotHandle<ValidationException>();

// Predicate: Custom logic (e.g., HTTP 5xx only)
RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);

// Predefined sets for common scenarios
RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .HandleAllTransientErrors(); // Database + Network errors
```

### Idempotent Task Registration

Use unique keys to safely register recurring tasks at startup without creating duplicates:

```csharp
// At application startup
public class RecurringTasksRegistrar : IHostedService
{
    private readonly ITaskDispatcher _dispatcher;

    public async Task StartAsync(CancellationToken ct)
    {
        // Register recurring tasks - safe to call on every startup
        await _dispatcher.Dispatch(
            new DailyCleanupTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)),
            taskKey: "daily-cleanup"); // Won't create duplicates

        await _dispatcher.Dispatch(
            new HealthCheckTask(),
            r => r.Schedule().Every(5).Minutes(),
            taskKey: "health-check");

        await _dispatcher.Dispatch(
            new WeeklySummaryTask(),
            r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(8, 0)),
            taskKey: "weekly-summary");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}

builder.Services.AddHostedService<RecurringTasksRegistrar>();
```

### Task Execution Log Capture

Capture all logs written during task execution and persist them to the database for debugging and auditing:

```csharp
// Enable log capture in configuration
services.AddEverTask(cfg =>
{
    cfg.RegisterTasksFromAssembly(typeof(Program).Assembly);
    cfg.EnableLogCapture = true;                      // Opt-in feature
    cfg.MinimumLogLevel = LogLevel.Information;       // Filter log level
    cfg.MaxLogsPerTask = 1000;                        // Prevent unbounded growth
})
.AddSqlServerStorage(connectionString);

// Use the built-in Logger property in handlers
public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        Logger.LogInformation($"Sending email to {task.Recipient}");

        try
        {
            await _emailService.SendAsync(task.Recipient, task.Subject, task.Body);
            Logger.LogInformation("Email sent successfully");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to send email: {ex.Message}", ex);
            throw;
        }
    }
}

// Retrieve logs via storage
var logs = await storage.GetExecutionLogsAsync(taskId);
foreach (var log in logs)
{
    Console.WriteLine($"[{log.Level}] {log.Message}");
    if (log.ExceptionDetails != null)
        Console.WriteLine($"Exception: {log.ExceptionDetails}");
}
```

**Performance Notes:**
- **Zero overhead when disabled** - JIT optimizations eliminate all log capture code paths
- **Minimal impact when enabled** - ~5-10ms overhead for typical tasks
- **Logs persist even on failure** - Captured in the finally block for debugging failed tasks

## What's New in v2.0

Version 2.0 is all about performance. We've optimized every hot path and made the defaults much smarter.

### Scheduler Improvements
- **PeriodicTimerScheduler** is now the default, cutting lock contention by 90% and using zero CPU when idle
- **ShardedScheduler** available for extreme loads—delivers 2-4x throughput when you're scheduling >10k tasks/sec

### Storage Optimizations
- DbContext pooling makes storage operations 30-50% faster
- SQL Server now uses stored procedures, cutting status update roundtrips in half

### Dispatcher Performance
- Reflection caching speeds up repeated task dispatching by 93% (~150μs → ~10μs)
- Lazy serialization eliminates unnecessary JSON conversion entirely

### Worker Executor Enhancements
- Event data caching slashes monitoring serializations by 99% (60k-80k → ~10-20 per 10k tasks)
- Handler options caching eliminates 99% of runtime casts
- Parallel pending task processing makes startup 80% faster with 1000+ queued tasks

### Auto-Scaling Configuration
No more manual tuning—defaults now scale with your CPU cores:
- `MaxDegreeOfParallelism`: `Environment.ProcessorCount * 2` (previously hardcoded to 1)
- `ChannelCapacity`: `Environment.ProcessorCount * 200` (previously hardcoded to 500)

### Better Developer Experience
- Configuration validation catches problems early with helpful warnings
- Zero-allocation patterns on .NET 7+
- Thread safety improvements and race condition fixes throughout

[View Complete Changelog](CHANGELOG.md)

## Quick Links

- 📦 **NuGet Packages**
  - [EverTask](https://www.nuget.org/packages/evertask) - Core library
  - [EverTask.SqlServer](https://www.nuget.org/packages/evertask.sqlserver) - SQL Server storage
  - [EverTask.Sqlite](https://www.nuget.org/packages/evertask.sqlite) - SQLite storage
  - [EverTask.Serilog](https://www.nuget.org/packages/evertask.serilog) - Serilog integration
  - [EverTask.Monitor.AspnetCore.SignalR](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR) - Real-time monitoring

- 📝 **Resources**
  - [Changelog](CHANGELOG.md) - Version history and release notes
  - [Attribution](ATTRIBUTION.md) - Acknowledgements and license information
  - [GitHub Repository](https://github.com/GiampaoloGabba/EverTask) - Source code and issues
  - [Examples](samples/) - Sample applications (ASP.NET Core, Console)

## Roadmap

We have some exciting features in the pipeline:

- **Web Dashboard**: A simple web UI for monitoring and managing tasks
- **WebAPI Endpoints**: RESTful API for remote task management
- **Additional Monitoring**: Sentry Crons, Application Insights, OpenTelemetry support
- **More Storage Options**: PostgreSQL, MySQL, Redis, Cosmos DB
- **Clustering**: Multi-server task distribution with load balancing and failover

## Contributing

Contributions are welcome! Bug reports, feature requests, and pull requests all help make EverTask better.

- Report issues: https://github.com/GiampaoloGabba/EverTask/issues
- Contribute code: https://github.com/GiampaoloGabba/EverTask/pulls

## License

EverTask is licensed under the [Apache License 2.0](LICENSE).

See [ATTRIBUTION.md](ATTRIBUTION.md) for acknowledgements and attributions.

---

**Developed with ❤️ by [Giampaolo Gabba](https://github.com/GiampaoloGabba)**
