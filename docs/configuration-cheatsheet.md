---
layout: default
title: Configuration Cheatsheet
parent: Configuration
nav_order: 2
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

**Key Points:**
- Logs are ALWAYS forwarded to ILogger (console, file, Serilog, etc.) regardless of persistence settings
- **Full structured logging support**: `Logger.LogInformation("User {UserId} logged in", userId)`
- **Exception overloads**: `Logger.LogError(exception, "Failed {TaskId}", taskId)`
- Access via `Logger` property in handlers (no need to inject `ILogger<T>`)

## Monitoring Configuration

### Monitoring API & Dashboard

```csharp
.AddMonitoringApi(opt => {
    // Note: BasePath and SignalRHubPath are now fixed and cannot be changed
    // BasePath: "/evertask-monitoring" (readonly)
    // SignalRHubPath: "/evertask-monitoring/hub" (readonly)
    opt.EnableUI = true;                       // Default: true
    opt.EnableSwagger = false;                 // Default: false
    opt.Username = "admin";                    // Default: "admin"
    opt.Password = "admin";                    // Default: "admin" (CHANGE IN PRODUCTION!)
    opt.EnableAuthentication = true;           // Default: true
    opt.EventDebounceMs = 1000;                // Default: 1000ms (dashboard auto-refresh debounce)
    opt.EnableCors = true;                     // Default: true
    opt.CorsAllowedOrigins = new[] {           // Default: empty (allow all)
        "https://myapp.com"
    };
    opt.AllowedIpAddresses = new[] {           // Default: empty (allow all IPs)
        "192.168.1.100",                       // Specific IP
        "10.0.0.0/8",                          // CIDR notation (entire network)
        "::1"                                  // IPv6 localhost
    };
    opt.MagicLinkToken = "your-secret-token"; // Default: null (disabled)
})
```

- **Package:** `EverTask.Monitor.Api`
- **Features:** REST API + embedded React dashboard
- **Dashboard URL:** `/evertask-monitoring` (fixed)
- **API URL:** `/evertask-monitoring/api` (fixed)
- **SignalR Hub:** `/evertask-monitoring/hub` (fixed, automatically mapped)
- **Auto-configures SignalR:** Automatically adds SignalR monitoring if not already registered

**Common Patterns:**

```csharp
// Development: No authentication
.AddMonitoringApi(opt => {
    opt.EnableAuthentication = false;
})

// Production: Environment variables
.AddMonitoringApi(opt => {
    opt.Username = Environment.GetEnvironmentVariable("MONITOR_USER") ?? "admin";
    opt.Password = Environment.GetEnvironmentVariable("MONITOR_PASS") ?? throw new Exception();
})

// API-only mode (no UI)
.AddMonitoringApi(opt => {
    opt.EnableUI = false;
})

// Custom frontend integration
.AddMonitoringApi(opt => {
    opt.EnableUI = false;
    opt.EnableCors = true;
    opt.CorsAllowedOrigins = new[] { "https://dashboard.myapp.com" };
})

// IP Whitelist (production security)
.AddMonitoringApi(opt => {
    opt.AllowedIpAddresses = new[] {
        "10.0.0.0/8",              // Internal network only
        "203.0.113.100"            // Specific admin IP
    };
})

// Magic link (external integration)
.AddMonitoringApi(opt => {
    opt.MagicLinkToken = "your-32-char-secret-token-here";
    // Access: /evertask-monitoring/magic?token=your-32-char-secret-token-here
})

// Swagger/OpenAPI documentation
.AddMonitoringApi(opt => {
    opt.EnableSwagger = true;      // Creates separate Swagger document
})

// In SwaggerUI configuration:
app.UseSwaggerUI(c => {
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My Application API");
    c.SwaggerEndpoint("/swagger/evertask-monitoring/swagger.json", "EverTask Monitoring API");
});
```

### SignalR
```csharp
.AddSignalRMonitoring(opt => {
    opt.IncludeExecutionLogs = false;     // Default: false (increases bandwidth if enabled)
})
```
- **Package:** `EverTask.Monitor.AspnetCore.SignalR`
- **Hub Route:** `/evertask-monitoring/hub` (automatically mapped by `MapEverTaskApi()`)
- **Events:** TaskStarted, TaskCompleted, TaskFailed, TaskCancelled, TaskTimeout
- **Event Method:** `EverTaskEvent` (receives `EverTaskEventData`)

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
- [Scalability](scalability.md) - Multi-queue and sharded scheduler
