---
layout: default
title: Configuration Reference
nav_order: 7
---

# Configuration Reference

This is a complete reference for all EverTask configuration options.

## Table of Contents

- [Service Configuration](#service-configuration)
- [Queue Configuration](#queue-configuration)
- [Storage Configuration](#storage-configuration)
- [Logging Configuration](#logging-configuration)
- [Monitoring Configuration](#monitoring-configuration)
- [Storage Provider Details](#storage-provider-details)
- [Handler Configuration](#handler-configuration)
- [Complete Examples](#complete-examples)

## Service Configuration

Use the fluent API in `AddEverTask()` to configure EverTask's core behavior.

### SetChannelOptions

Controls how many tasks can be queued and what happens when the queue fills up.

**Signatures:**
```csharp
SetChannelOptions(int capacity)
SetChannelOptions(Action<BoundedChannelOptions> configure)
```

**Parameters:**
- `capacity` (int): Maximum number of tasks that can be queued
- `configure` (Action): Custom configuration for `BoundedChannelOptions`

**Default:** `Environment.ProcessorCount * 200` (minimum 1000)

**Examples:**
```csharp
// Simple capacity
opt.SetChannelOptions(5000)

// Custom configuration
opt.SetChannelOptions(options =>
{
    options.Capacity = 5000;
    options.FullMode = BoundedChannelFullMode.Wait; // or DropWrite, DropOldest
})
```

**FullMode Options:**
- `Wait`: Block until space is available (default)
- `DropWrite`: Drop the new item if full
- `DropOldest`: Drop the oldest item and add the new one

### SetMaxDegreeOfParallelism

Controls how many tasks can run at the same time.

**Signature:**
```csharp
SetMaxDegreeOfParallelism(int maxDegreeOfParallelism)
```

**Parameters:**
- `maxDegreeOfParallelism` (int): Number of concurrent workers

**Default:** `Environment.ProcessorCount * 2` (minimum 4)

**Examples:**
```csharp
// Fixed parallelism
opt.SetMaxDegreeOfParallelism(16)

// Scale with CPUs
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
```

**Notes:**
- Use higher values for I/O-bound tasks like API calls or database operations
- Use lower values for CPU-intensive tasks
- Setting to 1 will log a warning since it's generally a bad idea in production

### SetDefaultRetryPolicy

Sets how tasks should retry when they fail (applies to all tasks unless overridden).

**Signature:**
```csharp
SetDefaultRetryPolicy(IRetryPolicy retryPolicy)
```

**Parameters:**
- `retryPolicy` (IRetryPolicy): Retry policy implementation

**Default:** `LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500))`

**Examples:**
```csharp
// Linear retry with fixed delay
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1)))

// Linear retry with custom delays
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(new[]
{
    TimeSpan.FromMilliseconds(100),
    TimeSpan.FromMilliseconds(500),
    TimeSpan.FromSeconds(2)
}))

// Custom retry policy
opt.SetDefaultRetryPolicy(new ExponentialBackoffPolicy())

// No retries
opt.SetDefaultRetryPolicy(new LinearRetryPolicy(1, TimeSpan.Zero))
```

### SetDefaultTimeout

Sets a maximum execution time for tasks (applies globally unless overridden).

**Signature:**
```csharp
SetDefaultTimeout(TimeSpan? timeout)
```

**Parameters:**
- `timeout` (TimeSpan?): Maximum execution time, or `null` for no timeout

**Default:** `null` (no timeout)

**Examples:**
```csharp
// 5 minute timeout
opt.SetDefaultTimeout(TimeSpan.FromMinutes(5))

// 30 second timeout
opt.SetDefaultTimeout(TimeSpan.FromSeconds(30))

// No timeout (explicit)
opt.SetDefaultTimeout(null)
```

**Notes:**
- When the timeout is reached, the `CancellationToken` gets cancelled
- Your handler needs to check the token for this to work (cooperative cancellation)
- You can override this per handler or per queue

### SetThrowIfUnableToPersist

Controls what happens when a task can't be saved to storage.

**Signature:**
```csharp
SetThrowIfUnableToPersist(bool throwIfUnableToPersist)
```

**Parameters:**
- `throwIfUnableToPersist` (bool): Whether to throw on persistence failure

**Default:** `true`

**Examples:**
```csharp
// Throw on persistence failure (recommended)
opt.SetThrowIfUnableToPersist(true)

// Don't throw (tasks may be lost)
opt.SetThrowIfUnableToPersist(false)
```

**Notes:**
- When `true`, the dispatch fails immediately if the task can't be saved
- When `false`, the task might run but won't be saved (risky!)
- Keep this `true` unless you have a good reason not to

### UseShardedScheduler

Enables a sharded scheduler that can handle extremely high loads by distributing work across multiple internal schedulers.

**Signature:**
```csharp
UseShardedScheduler()
UseShardedScheduler(int shardCount)
```

**Parameters:**
- `shardCount` (int): Number of shards (default: auto-scale based on CPU count, minimum 4)

**Default:** Not enabled (uses `PeriodicTimerScheduler`)

**Examples:**
```csharp
// Auto-scale based on CPUs
opt.UseShardedScheduler()

// Fixed shard count
opt.UseShardedScheduler(8)

// Scale with CPUs
opt.UseShardedScheduler(Environment.ProcessorCount)
```

**When to Use:**
You probably need this if you're seeing:
- Sustained load above 10,000 `Schedule()` calls/second
- Burst spikes above 20,000 `Schedule()` calls/second
- More than 100,000 tasks scheduled at once
- High lock contention showing up in your profiler

### RegisterTasksFromAssembly

Scans an assembly and registers all task handlers it finds.

**Signature:**
```csharp
RegisterTasksFromAssembly(Assembly assembly)
```

**Parameters:**
- `assembly` (Assembly): Assembly containing task handlers

**Examples:**
```csharp
// Current assembly
opt.RegisterTasksFromAssembly(typeof(Program).Assembly)

// Specific assembly
opt.RegisterTasksFromAssembly(typeof(MyTask).Assembly)

// Assembly by name
opt.RegisterTasksFromAssembly(Assembly.Load("MyTasksAssembly"))
```

### RegisterTasksFromAssemblies

Scans multiple assemblies and registers all task handlers from them.

**Signature:**
```csharp
RegisterTasksFromAssemblies(params Assembly[] assemblies)
```

**Parameters:**
- `assemblies` (Assembly[]): Assemblies containing task handlers

**Examples:**
```csharp
opt.RegisterTasksFromAssemblies(
    typeof(CoreTask).Assembly,
    typeof(ApiTask).Assembly,
    typeof(BackgroundTask).Assembly)
```

## Queue Configuration

You can set up multiple queues to isolate different types of work and give them different priorities or resource allocations.

### ConfigureDefaultQueue

Customizes the default queue (used when you don't specify a queue name for a task).

**Signature:**
```csharp
ConfigureDefaultQueue(Action<QueueConfiguration> configure)
```

**Example:**
```csharp
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(1000)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(5))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))))
```

### AddQueue

Creates a new named queue with its own configuration.

**Signature:**
```csharp
AddQueue(string queueName, Action<QueueConfiguration> configure)
```

**Parameters:**
- `queueName` (string): Unique queue name
- `configure` (Action): Queue configuration

**Example:**
```csharp
.AddQueue("high-priority", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait))

.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(100)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))
```

### ConfigureRecurringQueue

Customizes the recurring queue (EverTask automatically creates this queue for recurring tasks).

**Signature:**
```csharp
ConfigureRecurringQueue(Action<QueueConfiguration> configure)
```

**Example:**
```csharp
.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(200)
    .SetDefaultTimeout(TimeSpan.FromMinutes(10)))
```

### QueueConfiguration Methods

Each queue supports these configuration methods:

```csharp
// Parallelism
SetMaxDegreeOfParallelism(int maxDegreeOfParallelism)

// Capacity
SetChannelCapacity(int capacity)

// Full behavior
SetFullBehavior(QueueFullBehavior behavior)

// Timeout
SetDefaultTimeout(TimeSpan? timeout)

// Retry policy
SetDefaultRetryPolicy(IRetryPolicy retryPolicy)
```

## Storage Configuration

Choose where EverTask saves task data.

### AddMemoryStorage

Uses in-memory storage (fine for development/testing, but tasks won't survive a restart).

**Signature:**
```csharp
AddMemoryStorage()
```

**Example:**
```csharp
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage();
```

**Characteristics:**
- No external dependencies
- Fast performance
- Tasks lost on restart

### AddSqlServerStorage

Uses SQL Server for persistent storage.

**Signature:**
```csharp
AddSqlServerStorage(string connectionString)
AddSqlServerStorage(string connectionString, Action<StorageOptions> configure)
```

**Parameters:**
- `connectionString` (string): SQL Server connection string
- `configure` (Action): Storage configuration options

**Examples:**
```csharp
// Basic
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True;")

// With options
.AddSqlServerStorage(
    connectionString,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = true;
    })
```

**StorageOptions Properties:**
- `SchemaName` (string?): Database schema name (default: "EverTask", null = main schema)
- `AutoApplyMigrations` (bool): Auto-apply EF Core migrations (default: true)

### AddSqliteStorage

Uses SQLite for persistent storage.

**Signature:**
```csharp
AddSqliteStorage(string connectionString)
AddSqliteStorage(string connectionString, Action<StorageOptions> configure)
```

**Parameters:**
- `connectionString` (string): SQLite connection string
- `configure` (Action): Storage configuration options

**Examples:**
```csharp
// Basic
.AddSqliteStorage("Data Source=evertask.db")

// With options
.AddSqliteStorage(
    "Data Source=evertask.db;Cache=Shared;",
    opt =>
    {
        opt.AutoApplyMigrations = true;
    })
```

**Notes:**
- `SchemaName` is not supported in SQLite (always null)

## Logging Configuration

### AddSerilog

Integrates Serilog for structured logging throughout EverTask.

**Package:** `EverTask.Logging.Serilog`

**Signature:**
```csharp
AddSerilog(Action<LoggerConfiguration> configure)
```

**Parameters:**
- `configure` (Action): Serilog logger configuration

**Example:**
```csharp
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
```

**appsettings.json Example:**
```json
{
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/evertask-.txt",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 10
        }
      }
    ],
    "Enrich": ["FromLogContext", "WithMachineName", "WithThreadId"],
    "Properties": {
      "Application": "MyApp"
    }
  }
}
```

### WithPersistentLogger

**Available since:** v3.0

Configures persistent handler logging options. When enabled, logs written via `Logger` property in handlers are stored in the database for audit trails.

**Important:** Logs are ALWAYS forwarded to ILogger infrastructure (console, file, Serilog, etc.) regardless of this setting. This option only controls database persistence.

**Signature:**
```csharp
WithPersistentLogger(Action<PersistentLoggerOptions> configure)
```

**Parameters:**
- `configure` (Action): Configuration action for persistent logger options

**Default:** Disabled

**Example:**
```csharp
.AddEverTask(opt => opt
    .WithPersistentLogger(log => log
        .SetMinimumLevel(LogLevel.Information)
        .SetMaxLogsPerTask(1000)))
```

**Note:** Calling `.WithPersistentLogger()` automatically enables database persistence. You don't need to call `.Enable()`.

**PersistentLoggerOptions Methods:**

#### Disable()
Disables persistent logging to the database (logs still go to ILogger). Use this if you want to temporarily disable persistence.
```csharp
.WithPersistentLogger(log => log.Disable())
```

#### SetMinimumLevel(LogLevel level)
Sets the minimum log level for database persistence. Logs below this level are not stored in the database but are still forwarded to ILogger.

**Parameters:**
- `level` (LogLevel): Minimum level to persist (`Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`)

**Default:** `LogLevel.Information`

**Example:**
```csharp
.WithPersistentLogger(log => log
    .SetMinimumLevel(LogLevel.Warning)) // Only persist Warning and above
```

**Note:** This only affects database persistence. ILogger receives all log levels regardless of this setting.

#### SetMaxLogsPerTask(int? maxLogs)
Sets the maximum number of logs to persist per task execution. Once this limit is reached, additional logs are not persisted (but still forwarded to ILogger).

**Parameters:**
- `maxLogs` (int?): Maximum logs to persist. `null` = unlimited (not recommended for production)

**Default:** `1000`

**Example:**
```csharp
.WithPersistentLogger(log => log
    .SetMaxLogsPerTask(500)) // Limit to 500 logs
```

**Performance:** ~100 bytes per log in memory during execution. Single bulk INSERT to database after task completion.

**Complete Example:**
```csharp
.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .WithPersistentLogger(log => log
        .SetMinimumLevel(LogLevel.Information)
        .SetMaxLogsPerTask(1000)))
```

## Monitoring Configuration

### AddSignalRMonitoring

Enables real-time task monitoring via SignalR.

**Package:** `EverTask.Monitor.AspnetCore.SignalR`

**Signature:**
```csharp
AddSignalRMonitoring()
AddSignalRMonitoring(Action<SignalRMonitorOptions> configure)
```

**Parameters:**
- `configure` (Action): Monitoring configuration options

**Examples:**
```csharp
// Basic
.AddSignalRMonitoring()

// With options
.AddSignalRMonitoring(opt =>
{
    opt.HubRoute = "/evertask-hub";
    opt.EnableDetailedErrors = true;
})
```

**SignalRMonitorOptions Properties:**
- `HubRoute` (string): SignalR hub endpoint (default: "/evertask-hub")
- `EnableDetailedErrors` (bool): Include detailed error messages (default: false)

**Client-Side Setup:**

```html
<!-- Add SignalR client library -->
<script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>

<script>
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/evertask-hub")
    .build();

connection.on("TaskEvent", (event) => {
    console.log("Task event:", event);
    // event.TaskId, event.EventType, event.Timestamp, etc.
});

connection.start().catch(err => console.error(err));
</script>
```

**Event Types:**
- `TaskDispatched`: Task was dispatched
- `TaskStarted`: Task execution started
- `TaskCompleted`: Task completed successfully
- `TaskFailed`: Task failed after all retries
- `TaskCancelled`: Task was cancelled

## Storage Provider Details

### SQL Server Storage Options

**Package:** `EverTask.Storage.SqlServer`

**Advanced Configuration:**

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    // Schema name (default: "EverTask", null = main schema)
    opt.SchemaName = "EverTask";

    // Auto-apply migrations (default: true)
    opt.AutoApplyMigrations = true;

    // Connection pooling (enabled by default in v2.0+)
    // Uses DbContextFactory for 30-50% performance improvement

    // Stored procedures (enabled by default in v2.0+)
    // Reduces roundtrips for status updates
})
```

**Manual Migrations:**

For production environments, apply migrations manually:

```bash
# Generate migration script
dotnet ef migrations script --context TaskStoreDbContext --output migration.sql

# Apply via your deployment pipeline
sqlcmd -S localhost -d EverTaskDb -i migration.sql
```

**Stored Procedures:**

EverTask v2.0+ uses stored procedures for critical operations:

- `[EverTask].[SetTaskStatus]`: Atomic status update + audit insert
- Performance: 50% fewer roundtrips for status changes

**Connection String Options:**

```csharp
// Basic
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;"

// With pooling (recommended)
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;Min Pool Size=5;Max Pool Size=100;"

// Azure SQL
"Server=tcp:yourserver.database.windows.net,1433;Database=EverTaskDb;User ID=user;Password=pass;Encrypt=True;"
```

**Schema Customization:**

```sql
-- Custom schema
CREATE SCHEMA [CustomSchema]
GO

-- Configure in code
opt.SchemaName = "CustomSchema";
```

### SQLite Storage Options

**Package:** `EverTask.Storage.Sqlite`

**Advanced Configuration:**

```csharp
.AddSqliteStorage(connectionString, opt =>
{
    // Auto-apply migrations (default: true)
    opt.AutoApplyMigrations = true;

    // Note: SchemaName is not supported in SQLite (always null)
})
```

**Connection String Options:**

```csharp
// Basic
"Data Source=evertask.db"

// In-memory (for testing)
"Data Source=:memory:"

// Shared cache
"Data Source=evertask.db;Cache=Shared;"

// Full options
"Data Source=evertask.db;Mode=ReadWriteCreate;Cache=Shared;Foreign Keys=True;"
```

**Performance Tuning:**

```sql
-- WAL mode for better concurrency
PRAGMA journal_mode=WAL;

-- Optimize for performance
PRAGMA synchronous=NORMAL;
PRAGMA cache_size=10000;
PRAGMA temp_store=MEMORY;
```

**Limitations:**
- No schema support (unlike SQL Server)
- Not recommended for high-concurrency scenarios (>100 tasks/sec)
- Best for: Single-server deployments, development, small workloads

## Handler Configuration

You can configure behavior at the handler level to override global defaults.

### Handler Properties

Set these in your handler's constructor:

```csharp
public class MyHandler : EverTaskHandler<MyTask>
{
    public MyHandler()
    {
        // Timeout
        Timeout = TimeSpan.FromMinutes(10);

        // Retry policy
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));
    }

    // Queue routing
    public override string? QueueName => "high-priority";

    public override async Task Handle(MyTask task, CancellationToken cancellationToken)
    {
        // Handler logic
    }
}
```

**Available Properties:**
- `Timeout` (TimeSpan?): Handler-specific timeout
- `RetryPolicy` (IRetryPolicy): Handler-specific retry policy
- `QueueName` (string?): Target queue for this handler

## Complete Examples

### Basic Configuration

The simplest setup for getting started:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();

var app = builder.Build();
app.Run();
```

### Production Configuration

A more robust setup with SQL Server storage, proper retry policies, and logging:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(5000)
       .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
       .SetDefaultTimeout(TimeSpan.FromMinutes(5))
       .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";
        opt.AutoApplyMigrations = false; // Manual migrations in production
    })
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        builder.Configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

### Multi-Queue Configuration

Isolating different workloads into separate queues:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(1000))

.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetFullBehavior(QueueFullBehavior.Wait)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(5, TimeSpan.FromSeconds(1))))

.AddQueue("email", q => q
    .SetMaxDegreeOfParallelism(10)
    .SetChannelCapacity(10000)
    .SetFullBehavior(QueueFullBehavior.FallbackToDefault))

.AddQueue("reports", q => q
    .SetMaxDegreeOfParallelism(2)
    .SetChannelCapacity(50)
    .SetDefaultTimeout(TimeSpan.FromMinutes(30)))

.ConfigureRecurringQueue(q => q
    .SetMaxDegreeOfParallelism(5)
    .SetChannelCapacity(200))

.AddSqlServerStorage(connectionString);
```

### High-Performance Configuration

Optimized for handling massive workloads:

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(shardCount: Environment.ProcessorCount)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(10000)
    .SetDefaultTimeout(TimeSpan.FromMinutes(10))
)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
    opt.AutoApplyMigrations = false;
});
```

### Multi-Assembly Configuration

When your task handlers are spread across multiple assemblies:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssemblies(
        typeof(CoreTasks.MyTask).Assembly,
        typeof(ApiTasks.MyTask).Assembly,
        typeof(BackgroundTasks.MyTask).Assembly)
       .SetMaxDegreeOfParallelism(20);
})
.AddSqlServerStorage(connectionString);
```

### Environment-Specific Configuration

Adjusting configuration based on your environment:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);

    if (builder.Environment.IsProduction())
    {
        opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
           .SetChannelOptions(10000)
           .SetDefaultTimeout(TimeSpan.FromMinutes(10));
    }
    else
    {
        opt.SetMaxDegreeOfParallelism(2)
           .SetChannelOptions(100);
    }
});

if (builder.Environment.IsProduction())
{
    builder.Services.AddSqlServerStorage(
        builder.Configuration.GetConnectionString("EverTaskDb")!,
        opt => opt.AutoApplyMigrations = false);
}
else
{
    builder.Services.AddMemoryStorage();
}
```

## Configuration Validation

EverTask checks your configuration at startup and will complain if something looks off:

**Warnings:**
- `MaxDegreeOfParallelism = 1`: Usually a bad idea in production - consider scaling with CPU count
- Large `ChannelCapacity` with low `MaxDegreeOfParallelism`: You might end up with a huge backlog

**Errors:**
- `MaxDegreeOfParallelism < 1`: Must be at least 1
- `ChannelCapacity < 1`: Must be at least 1
- Duplicate queue names: Each queue needs a unique name
- No handlers registered: You need to register at least one assembly

## Performance Tuning Guidelines

### CPU-Bound Tasks

If your tasks do heavy computation, match your parallelism to your CPU cores:

```csharp
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount) // Match CPU cores
   .SetChannelOptions(100); // Small queue
```

### I/O-Bound Tasks

If your tasks spend most of their time waiting on I/O (database, APIs, files), you can run many more in parallel:

```csharp
opt.SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4) // Higher parallelism
   .SetChannelOptions(5000); // Larger queue
```

### Mixed Workloads

When you have different types of tasks, use separate queues:

```csharp
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 2))

.AddQueue("cpu-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount))

.AddQueue("io-intensive", q => q
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4))
```

### Extreme High Load

For truly massive workloads, enable the sharded scheduler:

```csharp
opt.UseShardedScheduler(Environment.ProcessorCount)
   .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
   .SetChannelOptions(10000);
```

## Next Steps

- **[Getting Started](getting-started.md)** - Setup guide
- **[Advanced Features](advanced-features.md)** - Multi-queue and sharded scheduler
- **[Resilience](resilience.md)** - Retry policies and timeouts
- **[Storage](storage.md)** - Storage options and configuration
