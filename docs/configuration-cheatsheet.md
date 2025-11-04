---
layout: default
title: Configuration Cheatsheet
nav_order: 8
---

# Configuration Cheatsheet

Quick reference for all EverTask configuration options.

## Service Configuration

| Method | Parameters | Default | Notes |
|--------|-----------|---------|-------|
| `SetChannelOptions` | `int capacity` | `ProcessorCount * 200` (min 1000) | Max queued tasks |
| `SetMaxDegreeOfParallelism` | `int parallelism` | `ProcessorCount * 2` (min 4) | Concurrent workers |
| `SetDefaultRetryPolicy` | `IRetryPolicy` | `LinearRetryPolicy(3, 500ms)` | Global retry policy |
| `SetDefaultTimeout` | `TimeSpan?` | `null` | Global timeout |
| `SetDefaultAuditLevel` | `AuditLevel` | `Full` | Audit trail verbosity |
| `SetThrowIfUnableToPersist` | `bool` | `true` | Throw on save failure |
| `UseShardedScheduler` | `int shardCount` | Auto (min 4) | For >10k/sec loads |
| `RegisterTasksFromAssembly` | `Assembly` | - | Scan for handlers |

## Audit Configuration

### Audit Levels
| Level | StatusAudit | RunsAudit | Records/Day* | Use Case |
|-------|-------------|-----------|--------------|----------|
| `Full` (default) | All transitions | All executions | ~2,304 | Critical tasks |
| `Minimal` | Errors only | All executions | ~576 | High-frequency recurring |
| `ErrorsOnly` | Errors only | Errors only | ~903** | Fire-and-forget |
| `None` | Never | Never | 0 | Extremely high-frequency |

*For 100 tasks every 5 minutes. **Assuming typical failure rates.

### Configuration Examples
```csharp
// Global default
.SetDefaultAuditLevel(AuditLevel.Minimal)

// Per-task override
await dispatcher.Dispatch(task, auditLevel: AuditLevel.ErrorsOnly);
await dispatcher.Dispatch(task, recurring => ..., auditLevel: AuditLevel.Minimal);
```

## Queue Configuration

| Method | Parameters | Default | Notes |
|--------|-----------|---------|-------|
| `SetMaxDegreeOfParallelism` | `int` | Inherits global | Queue-specific |
| `SetChannelCapacity` | `int` | Inherits global | Queue-specific |
| `SetFullBehavior` | `QueueFullBehavior` | `Wait` | `Wait`/`FallbackToDefault`/`ThrowException` |
| `SetDefaultTimeout` | `TimeSpan?` | Inherits global | Queue-specific |
| `SetDefaultRetryPolicy` | `IRetryPolicy` | Inherits global | Queue-specific |

**Queue Methods:**
- `ConfigureDefaultQueue(Action<QueueConfiguration>)` - Configure default queue
- `AddQueue(string name, Action<QueueConfiguration>)` - Add named queue
- `ConfigureRecurringQueue(Action<QueueConfiguration>)` - Configure recurring queue

## Storage Configuration

### Memory Storage
```csharp
.AddMemoryStorage()
```
- **Use for:** Development, testing
- **Persistence:** None (tasks lost on restart)

### SQL Server Storage
```csharp
.AddSqlServerStorage(connectionString, opt => {
    opt.SchemaName = "EverTask";         // Default: "EverTask"
    opt.AutoApplyMigrations = true;       // Default: true
})
```
- **Package:** `EverTask.Storage.SqlServer`
- **Features:** DbContext pooling, stored procedures (v2.0+)

### SQLite Storage
```csharp
.AddSqliteStorage(connectionString, opt => {
    opt.AutoApplyMigrations = true;       // Default: true
})
```
- **Package:** `EverTask.Storage.Sqlite`
- **Note:** No schema support

## Logging Configuration

### Serilog
```csharp
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(config,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))
```
- **Package:** `EverTask.Logging.Serilog`

### Task Execution Log Capture (v3.0+)
```csharp
.WithPersistentLogger(log => log       // Auto-enables persistence
    .SetMinimumLevel(LogLevel.Information) // Min level to persist
    .SetMaxLogsPerTask(1000))              // Max logs per task
```

| Method | Parameters | Default | Notes |
|--------|-----------|---------|-------|
| `WithPersistentLogger` | `Action<PersistentLoggerOptions>` | Disabled | Auto-enables persistence. **Logs always go to ILogger!** |
| `Disable()` | - | - | Disable DB persistence (logs still to ILogger) |
| `SetMinimumLevel` | `LogLevel` | `Information` | Min level to persist (not ILogger) |
| `SetMaxLogsPerTask` | `int?` | `1000` | Max logs per task. `null` = unlimited |

**Key Point:** Logs are ALWAYS forwarded to ILogger (console, file, Serilog, etc.) regardless of persistence settings.

## Monitoring Configuration

### SignalR
```csharp
.AddSignalRMonitoring(opt => {
    opt.HubRoute = "/evertask-hub";       // Default: "/evertask-hub"
    opt.EnableDetailedErrors = false;     // Default: false
})
```
- **Package:** `EverTask.Monitor.AspnetCore.SignalR`
- **Events:** TaskDispatched, TaskStarted, TaskCompleted, TaskFailed, TaskCancelled

## Handler Configuration

### Properties
```csharp
public class MyHandler : EverTaskHandler<MyTask>
{
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(10);
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));
    public override string? QueueName => "high-priority";
}
```

| Property | Type | Default | Notes |
|----------|------|---------|-------|
| `Timeout` | `TimeSpan?` | Inherits global/queue | Handler timeout |
| `RetryPolicy` | `IRetryPolicy` | Inherits global/queue | Handler retry |
| `QueueName` | `string?` | `"default"` | Target queue |

## Quick Examples

### Minimal Setup
```csharp
builder.Services.AddEverTask(opt =>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
.AddMemoryStorage();
```

### Production Setup
```csharp
builder.Services.AddEverTask(opt => opt
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(5000)
    .SetDefaultTimeout(TimeSpan.FromMinutes(5))
    .SetDefaultRetryPolicy(new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)))
    .RegisterTasksFromAssembly(typeof(Program).Assembly))
.AddSqlServerStorage(connectionString, opt => {
    opt.SchemaName = "EverTask";
    opt.AutoApplyMigrations = false;
})
.AddSerilog(opt => opt.ReadFrom.Configuration(config));
```

### Multi-Queue Setup
```csharp
builder.Services.AddEverTask(opt =>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
.ConfigureDefaultQueue(q => q
    .SetMaxDegreeOfParallelism(10))
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2)))
.AddQueue("background", q => q
    .SetMaxDegreeOfParallelism(2))
.AddSqlServerStorage(connectionString);
```

### High-Performance Setup
```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .UseShardedScheduler(Environment.ProcessorCount)
    .SetMaxDegreeOfParallelism(Environment.ProcessorCount * 4)
    .SetChannelOptions(10000))
.AddSqlServerStorage(connectionString);
```

## Performance Tuning

| Workload Type | Max Parallelism | Channel Capacity | Notes |
|---------------|----------------|------------------|-------|
| **CPU-Bound** | `ProcessorCount` | Small (100-500) | Heavy computation |
| **I/O-Bound** | `ProcessorCount * 4` | Large (5000+) | API/DB/File operations |
| **Mixed** | Use separate queues | Varies | Different configs per queue |
| **Extreme Load** | `ProcessorCount * 4+` | 10000+ | Enable sharded scheduler |

## Connection Strings

### SQL Server
```csharp
// Basic
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;"

// With pooling
"Server=localhost;Database=EverTaskDb;Trusted_Connection=True;Min Pool Size=5;Max Pool Size=100;"

// Azure SQL
"Server=tcp:server.database.windows.net,1433;Database=EverTaskDb;User ID=user;Password=pass;Encrypt=True;"
```

### SQLite
```csharp
// Basic
"Data Source=evertask.db"

// Shared cache
"Data Source=evertask.db;Cache=Shared;"

// Full options
"Data Source=evertask.db;Mode=ReadWriteCreate;Cache=Shared;Foreign Keys=True;"
```

## Common Patterns

### Environment-Based Config
```csharp
if (env.IsProduction())
{
    opt.SetMaxDegreeOfParallelism(ProcessorCount * 4)
       .SetChannelOptions(10000);
}
else
{
    opt.SetMaxDegreeOfParallelism(2)
       .SetChannelOptions(100);
}
```

### Conditional Storage
```csharp
if (env.IsProduction())
    services.AddSqlServerStorage(connectionString);
else
    services.AddMemoryStorage();
```

## See Also

- [Full Configuration Reference](configuration-reference.md) - Detailed documentation
- [Getting Started](getting-started.md) - Setup guide
- [Advanced Features](advanced-features.md) - Multi-queue and sharded scheduler
