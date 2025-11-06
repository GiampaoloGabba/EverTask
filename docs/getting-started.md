---
layout: default
title: Task Operations
nav_order: 2
has_children: true
---

# Task Operations

Let's get EverTask running in your .NET application.

## Installation

EverTask is available as NuGet packages. Install the core package and whichever storage or logging providers you need:

```bash
# Core packages
dotnet add package EverTask
dotnet add package EverTask.Abstractions

# Storage providers
dotnet add package EverTask.SqlServer
dotnet add package EverTask.Sqlite

# Optional packages
dotnet add package EverTask.Serilog
dotnet add package EverTask.Monitor.AspnetCore.SignalR
```

Alternatively, you can install via Package Manager Console:

```powershell
Install-Package EverTask
Install-Package EverTask.SqlServer
```

## Basic Configuration

Here's the simplest setup using in-memory storage:

```csharp
using EverTask;

var builder = WebApplication.CreateBuilder(args);

// Register EverTask with in-memory storage
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();

var app = builder.Build();
app.Run();
```

> **Note:** In-memory storage works great for development and testing, but you'll lose all tasks when the application restarts. For production, use SQL Server or SQLite instead.

## Advanced Configuration

In production, you'll want persistent storage and more control over performance:

```csharp
using EverTask;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(500)                          // Queue capacity
       .SetMaxDegreeOfParallelism(16)                   // Concurrent workers
       .SetDefaultTimeout(TimeSpan.FromMinutes(5))      // Global timeout
       .SetThrowIfUnableToPersist(true)                 // Fail-fast on persistence errors
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName          = "EverTask";           // Database schema
        opt.AutoApplyMigrations = true;                 // Auto-apply EF migrations
    })
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        builder.Configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));

var app = builder.Build();
app.Run();
```

### Configuration Options Overview

- **SetChannelOptions**: Task queue capacity and behavior when full
- **SetMaxDegreeOfParallelism**: Maximum concurrent task executions (defaults to processor count × 2)
- **SetDefaultTimeout**: Global timeout for all tasks (override per handler if needed)
- **SetDefaultRetryPolicy**: How many times to retry failed tasks (defaults to 3 attempts with 500ms delay)
- **SetThrowIfUnableToPersist**: Throw exceptions on persistence failures or fail silently
- **RegisterTasksFromAssembly**: Automatically find and register all task handlers in an assembly

See [Configuration Reference](configuration-reference.md) for the complete list of options.

## Creating Your First Task

### 1. Define a Task Request

Create a record implementing `IEverTask`:

```csharp
public record SendWelcomeEmailTask(string UserEmail, string UserName) : IEverTask;
```

> **Tip:** Stick to primitive types and basic objects in your task requests. Complex types can cause serialization issues when persisting to storage.

### 2. Create a Task Handler

Now implement `EverTaskHandler<T>` to handle the actual work:

```csharp
public class SendWelcomeEmailHandler : EverTaskHandler<SendWelcomeEmailTask>
{
    private readonly IEmailService _emailService;
    private readonly ILogger<SendWelcomeEmailHandler> _logger;

    public SendWelcomeEmailHandler(
        IEmailService emailService,
        ILogger<SendWelcomeEmailHandler> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public override async Task Handle(
        SendWelcomeEmailTask task,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Sending welcome email to {Email} for {Name}",
            task.UserEmail,
            task.UserName);

        await _emailService.SendWelcomeEmailAsync(
            task.UserEmail,
            task.UserName,
            cancellationToken);

        _logger.LogInformation("Welcome email sent successfully");
    }
}
```

### 3. Dispatch the Task

Inject `ITaskDispatcher` and dispatch your task:

```csharp
public class UserController : ControllerBase
{
    private readonly ITaskDispatcher _dispatcher;

    public UserController(ITaskDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    [HttpPost]
    public async Task<IActionResult> RegisterUser(UserRegistrationDto dto)
    {
        // ... user registration logic ...

        // Dispatch background task
        await _dispatcher.Dispatch(new SendWelcomeEmailTask(dto.Email, dto.Name));

        return Ok();
    }
}
```

## Running the Worker

EverTask runs as a hosted service and starts automatically with your application. It processes queued tasks using your configured parallelism settings, monitors scheduled and recurring tasks, and resumes any pending tasks when the application restarts (if you're using persistent storage).

### Monitoring Worker Activity

Check your logs to verify the worker is running:

```
[14:30:15 INF] EverTask Worker Service starting
[14:30:15 INF] Processing pending tasks from storage...
[14:30:15 INF] Found 5 pending tasks to resume
[14:30:15 INF] EverTask Worker Service started successfully
```

## Next Steps

Now that you've got EverTask running, dive into these topics:

- **[Task Creation](task-creation.md)** - Lifecycle hooks, custom timeouts, and retry policies
- **[Task Dispatching](task-dispatching.md)** - Delayed and scheduled execution
- **[Recurring Tasks](recurring-tasks.md)** - Fluent scheduling API with cron support
- **[Task Orchestration](advanced-features.md)** - Continuations and workflow patterns
- **[Scalability](scalability.md)** - Multi-queue and sharded scheduler
- **[Monitoring](monitoring.md)** - Track task execution with events and SignalR integration
- **[Storage Configuration](storage.md)** - SQL Server, SQLite, and custom storage implementations

## Troubleshooting

### Tasks not executing

- Make sure `AddEverTask()` is called during service registration
- Verify your handlers are in the assembly you passed to `RegisterTasksFromAssembly()`
- Check that your application stays running (doesn't just start and immediately stop)
- Look for startup errors in your logs

### Persistence errors

- Double-check your connection string
- Make sure the database exists and migrations have been applied
- Enable `AutoApplyMigrations` or run migrations manually
- Verify your application has permissions to create the schema and tables

### Performance issues

- For I/O-bound tasks (like API calls or file operations), increase `MaxDegreeOfParallelism`
- For CPU-intensive tasks, decrease it to avoid overloading your cores
- If your queue is constantly full, bump up `ChannelCapacity`
- Consider [multi-queue configuration](multi-queue.md) to isolate different workloads
- For very high loads (>10k tasks/sec), enable the [sharded scheduler](sharded-scheduler.md)
