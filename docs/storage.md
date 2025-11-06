---
layout: default
title: Storage
nav_order: 6
has_children: true
---

# Storage Configuration

EverTask supports multiple storage providers for task persistence. This guide covers all available options and how to implement custom storage.

## Table of Contents

- [Storage Overview](#storage-overview)
- [Audit Configuration](#audit-configuration)
- [In-Memory Storage](#in-memory-storage)
- [SQL Server Storage](#sql-server-storage)
- [SQLite Storage](#sqlite-storage)
- [Custom Storage](#custom-storage)
- [Serialization](#serialization)
- [Best Practices](#best-practices)

## Storage Overview

Storage providers persist tasks and their state. With persistent storage, tasks can resume after application restarts, you can track task history, maintain an audit trail of execution, and store scheduled or recurring tasks for future execution.

### Choosing a Storage Provider

| Provider | Use Case | Pros | Cons |
|----------|----------|------|------|
| **In-Memory** | Development, Testing | Fast, no setup | Data lost on restart |
| **SQL Server** | Production, Enterprise | Robust, scalable, stored procedures | Requires SQL Server |
| **SQLite** | Small-scale production, Single-server | Simple, file-based, no server | Limited concurrent writes |

## Audit Configuration

EverTask provides configurable audit trail levels to control database bloat from high-frequency tasks. By default, every task execution creates audit records in `StatusAudit` and `RunsAudit` tables. For tasks running every few minutes, this can generate thousands of records per day.

### Audit Levels

Control audit trail verbosity with the `AuditLevel` enum:

| Level | StatusAudit | RunsAudit | Use Case |
|-------|-------------|-----------|----------|
| **Full** (default) | All status transitions | All executions | Critical tasks requiring complete history |
| **Minimal** | Errors only | All executions | High-frequency recurring tasks (tracks last run + errors) |
| **ErrorsOnly** | Errors only | Errors only | Tasks where only failures matter |
| **None** | Never | Never | Extremely high-frequency tasks, no audit needed |

**Database Impact Example** (100 tasks running every 5 minutes):

| Audit Level | Daily Audit Records | Storage Reduction |
|-------------|---------------------|-------------------|
| Full | ~2,304 records/day | Baseline |
| Minimal | ~576 records/day | 75% reduction |
| ErrorsOnly | ~903 records/day* | 60% reduction |
| None | 0 records/day | 100% reduction |

*Assuming typical failure rates. Only errors generate audit records.

### Global Default Configuration

Set the default audit level for all tasks:

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetDefaultAuditLevel(AuditLevel.Minimal)) // Default: AuditLevel.Full
    .AddSqlServerStorage(connectionString);
```

### Per-Task Override

Override audit level when dispatching individual tasks:

```csharp
// High-frequency health check - minimal audit
await dispatcher.Dispatch(
    new HealthCheckTask(),
    recurring => recurring.Every(5).Minutes(),
    auditLevel: AuditLevel.Minimal);

// Critical payment processing - full audit
await dispatcher.Dispatch(
    new ProcessPaymentTask(orderId),
    auditLevel: AuditLevel.Full);

// Background cleanup - no audit needed
await dispatcher.Dispatch(
    new CleanupTempFilesTask(),
    recurring => recurring.EveryDay().AtTime(new TimeOnly(2, 0)),
    auditLevel: AuditLevel.None);
```

All `Dispatch()` overloads support the optional `auditLevel` parameter:

```csharp
// Immediate execution
Task<Guid> Dispatch(IEverTask task, AuditLevel? auditLevel = null, ...);

// Delayed execution
Task<Guid> Dispatch(IEverTask task, TimeSpan delay, AuditLevel? auditLevel = null, ...);

// Scheduled execution
Task<Guid> Dispatch(IEverTask task, DateTimeOffset scheduleTime, AuditLevel? auditLevel = null, ...);

// Recurring execution
Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring,
                    AuditLevel? auditLevel = null, string? taskKey = null, ...);
```

### Audit Level Behavior

#### Full (Default)

Complete audit trail for debugging and compliance:

- **StatusAudit**: Records all status transitions (Queued → InProgress → Completed/Failed)
- **RunsAudit**: Records every execution with timestamp, duration, and result
- **Use When**: Critical business tasks, compliance requirements, production debugging

```csharp
// Critical payment processing - keep full history
await dispatcher.Dispatch(
    new ProcessPaymentTask(orderId),
    auditLevel: AuditLevel.Full);
```

#### Minimal

Optimized for high-frequency recurring tasks:

- **StatusAudit**: Only errors (failed executions, service stopped)
- **RunsAudit**: All executions (tracks last run timestamp)
- **QueuedTask.LastExecutionUtc**: Updated on every execution
- **Use When**: Recurring health checks, periodic data sync, monitoring tasks

```csharp
// Health check every 5 minutes - track last run, only audit errors
await dispatcher.Dispatch(
    new HealthCheckTask(),
    recurring => recurring.Every(5).Minutes(),
    auditLevel: AuditLevel.Minimal,
    taskKey: "health-check");
```

**Performance Impact**: 75% reduction in audit writes compared to Full.

#### ErrorsOnly

Only track failures:

- **StatusAudit**: Only errors (failed executions, service stopped)
- **RunsAudit**: Only errors (no success records)
- **QueuedTask Status**: Updated to Completed on success (no audit)
- **Use When**: Fire-and-forget tasks, background cleanup, non-critical operations

```csharp
// Cleanup task - only care about failures
await dispatcher.Dispatch(
    new CleanupOldFilesTask(),
    recurring => recurring.EveryDay().AtTime(new TimeOnly(3, 0)),
    auditLevel: AuditLevel.ErrorsOnly,
    taskKey: "cleanup-old-files");
```

**Performance Impact**: 60% reduction in audit writes (assuming typical failure rates).

#### None

No audit trail (use with caution):

- **StatusAudit**: Never created
- **RunsAudit**: Never created
- **QueuedTask**: Only the task status and exception fields updated
- **Use When**: Extremely high-frequency tasks (every few seconds), temporary testing tasks

```csharp
// Cache refresh every 10 seconds - no audit needed
await dispatcher.Dispatch(
    new RefreshCacheTask(),
    recurring => recurring.Every(10).Seconds(),
    auditLevel: AuditLevel.None,
    taskKey: "cache-refresh");
```

**Warning**: No historical data available for debugging. Use only when audit data provides no value.

### Real-World Configuration Example

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    // Set conservative global default
    .SetDefaultAuditLevel(AuditLevel.Full))
    .AddSqlServerStorage(connectionString);

// Critical business tasks use global default (Full)
await dispatcher.Dispatch(new ProcessPaymentTask(orderId));

// High-frequency health checks - minimal audit
await dispatcher.Dispatch(
    new HealthCheckTask(),
    recurring => recurring.Every(5).Minutes(),
    auditLevel: AuditLevel.Minimal,
    taskKey: "health-check");

// Background email queue processing - errors only
await dispatcher.Dispatch(
    new ProcessEmailQueueTask(),
    recurring => recurring.Every(1).Minutes(),
    auditLevel: AuditLevel.ErrorsOnly,
    taskKey: "email-queue");

// Temporary cache warming task - no audit
await dispatcher.Dispatch(
    new WarmCacheTask(),
    recurring => recurring.Every(30).Seconds(),
    auditLevel: AuditLevel.None,
    taskKey: "cache-warmer");
```

### Performance Optimization Details

EverTask eliminates unnecessary database queries by passing `AuditLevel` through the execution pipeline:

1. **No SELECT Queries**: Audit level passed as parameter to storage methods (not queried from database)
2. **SQL Server Stored Procedure**: `usp_SetTaskStatus` conditionally creates audit records in T-SQL
3. **Single Roundtrip**: Status update + conditional audit insert in one database call
4. **50% Fewer Queries**: Reduced from 2 queries (SELECT + UPDATE/INSERT) to 1 (UPDATE/INSERT)

**SQL Server Example** (simplified):

```sql
CREATE PROCEDURE [EverTask].[usp_SetTaskStatus]
    @TaskId uniqueidentifier,
    @Status int,
    @Exception nvarchar(max) = NULL,
    @AuditLevel int = 0  -- Default: Full
AS
BEGIN
    -- Update task status
    UPDATE [EverTask].[QueuedTasks]
    SET Status = @Status, Exception = @Exception
    WHERE Id = @TaskId;

    -- Conditionally insert audit record based on AuditLevel
    IF (@AuditLevel = 0  -- Full
        OR (@AuditLevel = 1 AND (@Status = 3 OR @Status = 4 OR @Exception IS NOT NULL))  -- Minimal (errors)
        OR (@AuditLevel = 2 AND (@Status = 3 OR @Status = 4 OR @Exception IS NOT NULL))) -- ErrorsOnly
    BEGIN
        INSERT INTO [EverTask].[StatusAudit] (TaskId, Status, Exception, CreatedAtUtc)
        VALUES (@TaskId, @Status, @Exception, GETUTCDATE());
    END
END
```

### Migration Notes

- **Backward Compatible**: Null `AuditLevel` in database treated as `Full` (default)
- **Existing Tasks**: Tasks created before v1.7 continue with Full audit level
- **No Data Loss**: Changing audit level only affects future executions
- **Custom Storage**: Implementations must accept `AuditLevel` parameter in `SetStatus()` and `UpdateCurrentRun()`

### Recommendations by Task Type

| Task Type | Recommended Audit Level | Reason |
|-----------|------------------------|--------|
| Payment processing | **Full** | Compliance, dispute resolution |
| Order fulfillment | **Full** | Business-critical, customer service |
| Email sending | **ErrorsOnly** | Only care about delivery failures |
| Health checks (5-10 min) | **Minimal** | Track last run, audit errors |
| Cache refresh (< 1 min) | **None** or **ErrorsOnly** | High-frequency, low value |
| Data sync (hourly) | **Minimal** | Track sync status, audit errors |
| Cleanup tasks | **ErrorsOnly** | Only need failure alerts |
| Report generation | **Full** | Audit trail for generated reports |
| Background indexing | **Minimal** | Track progress, audit errors |

### Monitoring Audit Growth

Query audit table sizes to determine if audit levels need adjustment:

```sql
-- Check audit table row counts
SELECT
    'StatusAudit' AS TableName,
    COUNT(*) AS TotalRows,
    COUNT(*) / NULLIF(DATEDIFF(DAY, MIN(CreatedAtUtc), MAX(CreatedAtUtc)), 0) AS AvgRowsPerDay
FROM [EverTask].[StatusAudit]
UNION ALL
SELECT
    'RunsAudit' AS TableName,
    COUNT(*) AS TotalRows,
    COUNT(*) / NULLIF(DATEDIFF(DAY, MIN(ExecutedAtUtc), MAX(ExecutedAtUtc)), 0) AS AvgRowsPerDay
FROM [EverTask].[RunsAudit];
```

If audit tables grow too quickly (> 10,000 rows/day), consider:
1. Reducing audit level for high-frequency tasks
2. Implementing audit retention policies (see [Cleanup Old Tasks](#cleanup-old-tasks))
3. Archiving historical audit data to separate tables/database

## In-Memory Storage

In-memory storage is perfect for development and testing when you don't need task persistence.

### Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

### Characteristics

- ✅ Zero setup - works out of the box
- ✅ Fast performance
- ✅ No external dependencies
- ❌ Tasks lost on application restart
- ❌ Not suitable for production

### Use Cases

```csharp
// Development environment
#if DEBUG
    .AddMemoryStorage();
#else
    .AddSqlServerStorage(connectionString);
#endif

// Integration tests
public class TaskIntegrationTests
{
    [Fact]
    public async Task Should_Execute_Task()
    {
        var services = new ServiceCollection();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(GetType().Assembly))
                .AddMemoryStorage();

        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ITaskDispatcher>();

        var taskId = await dispatcher.Dispatch(new TestTask());

        // Assert task execution
    }
}
```

## SQL Server Storage

SQL Server provides enterprise-grade storage for production environments.

### Installation

```bash
dotnet add package EverTask.SqlServer
```

### Basic Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True;");
```

### Advanced Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(
    builder.Configuration.GetConnectionString("EverTaskDb")!,
    opt =>
    {
        opt.SchemaName = "EverTask";          // Custom schema (default: "EverTask")
        opt.AutoApplyMigrations = true;       // Auto-apply EF Core migrations
    });
```

### Schema Configuration

By default, EverTask creates a dedicated schema to keep task tables separate from your main database schema:

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    // Default behavior: creates "EverTask" schema
    opt.SchemaName = "EverTask";

    // Use main schema (not recommended)
    opt.SchemaName = null;

    // Custom schema
    opt.SchemaName = "Tasks";
});
```

#### Schema Contents

The schema contains:
- **QueuedTasks**: Main task table
- **TaskAudit**: Task execution history
- **__EFMigrationsHistory**: EF Core migrations table (also in custom schema)

### Migration Management

EverTask automatically applies migrations on startup by default. You can disable this behavior if you prefer to manage migrations manually:

```csharp
// Automatic migrations (default)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true; // Default behavior
});

// Manual migrations (if preferred)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = false;
});
```

If you choose to manage migrations manually, you can use EF Core tools:

```bash
# Apply migrations
dotnet ef database update --project YourProject --context TaskStoreDbContext

# Generate SQL script
dotnet ef migrations script --project YourProject --context TaskStoreDbContext --output migrations.sql
```

### Performance Optimizations (v2.0+)

Version 2.0 introduces significant performance improvements for SQL Server storage.

#### DbContext Pooling

DbContext pooling is automatically enabled in v2.0+, which reduces the overhead of creating new contexts and improves storage operation performance by 30-50%:

```csharp
.AddSqlServerStorage(connectionString)
```

#### Stored Procedures

The SetStatus operation now uses a stored procedure that atomically updates the task status and inserts an audit record in a single database roundtrip. This cuts database calls in half while guaranteeing transactional consistency.

### Connection String Configuration

```csharp
// appsettings.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Server=localhost;Database=EverTaskDb;User Id=evertask;Password=***;TrustServerCertificate=True"
  }
}

// Program.cs
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
```

### Characteristics

- ✅ Production-ready
- ✅ Highly scalable
- ✅ ACID transactions
- ✅ Stored procedures for performance
- ✅ Rich querying capabilities
- ❌ Requires SQL Server instance
- ❌ Additional infrastructure cost

## SQLite Storage

SQLite provides lightweight, file-based storage that works well for single-server deployments.

### Installation

```bash
dotnet add package EverTask.Sqlite
```

### Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqliteStorage("Data Source=evertask.db");
```

### Advanced Configuration

```csharp
.AddSqliteStorage(
    "Data Source=evertask.db;Cache=Shared;",
    opt =>
    {
        opt.SchemaName = null;               // SQLite doesn't support schemas
        opt.AutoApplyMigrations = true;
    });
```

### File Location

```csharp
// Current directory
.AddSqliteStorage("Data Source=evertask.db")

// Absolute path
.AddSqliteStorage("Data Source=/var/lib/myapp/evertask.db")

// App data folder
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MyApp",
    "evertask.db");
.AddSqliteStorage($"Data Source={dbPath}")
```

### Characteristics

- ✅ Simple setup - single file
- ✅ No server required
- ✅ Perfect for small-scale production
- ✅ Easy backups (copy file)
- ✅ Lower infrastructure cost
- ❌ Limited concurrent writes
- ❌ Single server only (no clustering)
- ⚠️ Provider limitation: EF Core cannot translate `DateTimeOffset` comparison operators for SQLite.  
  EverTask falls back to in-memory keyset filtering during recovery (`ProcessPendingAsync`), so avoid very large backlogs on SQLite or switch to SQL Server for heavy workloads.

### Use Cases

- Small to medium applications
- Single-server deployments
- Desktop applications
- IoT / edge computing

## Custom Storage

You can implement custom storage providers for Redis, MongoDB, PostgreSQL, or any other database by implementing the `ITaskStorage` interface.

### Implementing ITaskStorage

```csharp
public interface ITaskStorage
{
    // Basic CRUD
    Task<Guid> AddAsync(QueuedTask task, CancellationToken cancellationToken = default);
    Task<QueuedTask?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task UpdateAsync(QueuedTask task, CancellationToken cancellationToken = default);
    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    // Status management
    Task SetStatus(Guid id, TaskStatus status, CancellationToken cancellationToken = default);

    // Querying
    Task<List<QueuedTask>> GetPendingTasksAsync(CancellationToken cancellationToken = default);
    Task<List<QueuedTask>> GetScheduledTasksAsync(CancellationToken cancellationToken = default);

    // Task keys (for idempotent registration)
    Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken cancellationToken = default);

    // Audit
    Task AddAuditAsync(TaskAudit audit, CancellationToken cancellationToken = default);

    // Task execution log persistence (v3.0+)
    Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip = 0, int take = 1000, CancellationToken cancellationToken = default);
}
```

### Example: Redis Storage

```csharp
public class RedisTaskStorage : ITaskStorage
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;

    public RedisTaskStorage(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _db = redis.GetDatabase();
    }

    public async Task<Guid> AddAsync(QueuedTask task, CancellationToken cancellationToken = default)
    {
        var id = task.PersistenceId;
        var json = JsonConvert.SerializeObject(task);

        await _db.StringSetAsync($"task:{id}", json);

        // Add to pending set
        if (task.Status == TaskStatus.Pending)
        {
            await _db.SetAddAsync("tasks:pending", id.ToString());
        }

        return id;
    }

    public async Task<QueuedTask?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var json = await _db.StringGetAsync($"task:{id}");

        if (json.IsNullOrEmpty)
            return null;

        return JsonConvert.DeserializeObject<QueuedTask>(json!);
    }

    public async Task UpdateAsync(QueuedTask task, CancellationToken cancellationToken = default)
    {
        var json = JsonConvert.SerializeObject(task);
        await _db.StringSetAsync($"task:{task.PersistenceId}", json);
    }

    public async Task SetStatus(Guid id, TaskStatus status, CancellationToken cancellationToken = default)
    {
        var task = await GetAsync(id, cancellationToken);
        if (task != null)
        {
            task.Status = status;
            await UpdateAsync(task, cancellationToken);

            // Update indexes
            await _db.SetRemoveAsync($"tasks:{task.Status}", id.ToString());
            await _db.SetAddAsync($"tasks:{status}", id.ToString());
        }
    }

    public async Task<List<QueuedTask>> GetPendingTasksAsync(CancellationToken cancellationToken = default)
    {
        var ids = await _db.SetMembersAsync("tasks:pending");
        var tasks = new List<QueuedTask>();

        foreach (var id in ids)
        {
            var task = await GetAsync(Guid.Parse(id!), cancellationToken);
            if (task != null)
            {
                tasks.Add(task);
            }
        }

        return tasks;
    }

    // Implement other methods...
}

// Registration
builder.Services.AddSingleton<ITaskStorage, RedisTaskStorage>();
```

### Implementing ITaskStoreDbContextFactory (v2.0+)

If you're building an EF Core-based storage provider, implement the factory pattern to take advantage of DbContext pooling:

```csharp
public interface ITaskStoreDbContextFactory
{
    Task<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);
}

public class MyCustomDbContextFactory : ITaskStoreDbContextFactory
{
    private readonly IDbContextFactory<MyCustomDbContext> _factory;

    public MyCustomDbContextFactory(IDbContextFactory<MyCustomDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
    {
        return await _factory.CreateDbContextAsync(cancellationToken);
    }
}

// Registration
builder.Services.AddDbContextFactory<MyCustomDbContext>(options =>
    options.UseYourDatabase(connectionString));

builder.Services.AddSingleton<ITaskStoreDbContextFactory, MyCustomDbContextFactory>();
```

## Serialization

EverTask uses **Newtonsoft.Json** for task serialization because it handles polymorphism and inheritance well.

### Serialization Best Practices

#### ✅ Good Task Designs

```csharp
// Simple primitives
public record GoodTask1(int Id, string Name, DateTime Date) : IEverTask;

// Simple collections
public record GoodTask2(List<int> Ids, Dictionary<string, string> Metadata) : IEverTask;

// Simple nested objects
public record Address(string Street, string City);
public record GoodTask3(string Name, Address Address) : IEverTask;
```

#### ❌ Problematic Task Designs

```csharp
// ❌ Circular references
public class BadTask1 : IEverTask
{
    public BadTask1? Parent { get; set; }
    public List<BadTask1> Children { get; set; }
}

// ❌ Non-serializable types
public record BadTask2(DbContext Context, ILogger Logger) : IEverTask;

// ❌ Streams or delegates
public record BadTask3(Stream Data, Func<int> Callback) : IEverTask;

// ❌ Deep object graphs
public record BadTask4(ComplexObject WithManyNestedLevels) : IEverTask;
```

### Custom Serialization Settings

EverTask handles serialization internally. We don't recommend customizing this unless you have a specific need and understand the implications.

### Handling Serialization Failures

```csharp
try
{
    await dispatcher.Dispatch(new MyTask(data));
}
catch (JsonSerializationException ex)
{
    _logger.LogError(ex, "Failed to serialize task. Ensure task contains only serializable types.");
    // Handle error - simplify task data or use different approach
}
```

## Best Practices

### Storage Selection

Pick the right storage provider for your scenario:

1. **Development**: Use In-Memory storage
2. **Small Production Apps**: SQLite is sufficient
3. **Enterprise / Scale**: Use SQL Server
4. **Specific Needs**: Implement custom storage

### Connection Strings

```csharp
// ✅ Good: From configuration
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)

// ❌ Bad: Hardcoded
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;...")
```

### Schema Management

```csharp
// ✅ Good: Use dedicated schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
})

// ❌ Bad: Pollute main schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = null;
})
```

### Migration Strategy

```csharp
// Auto-apply migrations (default)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true;
});

// Disable auto-apply if you prefer manual control
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = false;
});
```

### Task Design for Serialization

```csharp
// ✅ Good: Simple, serializable task
public record ProcessOrderTask(
    int OrderId,
    string CustomerEmail,
    List<int> ItemIds) : IEverTask;

// ❌ Bad: Complex, non-serializable task
public record ProcessOrderTask(
    Order Order, // DbContext-tracked entity
    IOrderService OrderService, // Service dependency
    Func<bool> ValidationCallback) : IEverTask; // Delegate
```

### Backup and Recovery

#### SQL Server

```sql
-- Backup
BACKUP DATABASE EverTaskDb TO DISK = 'C:\Backups\EverTaskDb.bak'

-- Restore
RESTORE DATABASE EverTaskDb FROM DISK = 'C:\Backups\EverTaskDb.bak'
```

#### SQLite

```bash
# Backup (simple file copy)
cp evertask.db evertask.db.backup

# Restore
cp evertask.db.backup evertask.db
```

### Monitoring Storage Performance

```csharp
public class StorageMonitor
{
    private readonly ITaskStorage _storage;

    public async Task<StorageMetrics> GetMetrics()
    {
        var pending = await _storage.GetPendingTasksAsync();
        var scheduled = await _storage.GetScheduledTasksAsync();

        return new StorageMetrics
        {
            PendingTasksCount = pending.Count,
            ScheduledTasksCount = scheduled.Count
        };
    }
}
```

### Cleanup Old Tasks

Over time, completed tasks can pile up. Here's how to create a recurring cleanup task that runs daily:

```csharp
public record CleanupOldTasksTask : IEverTask;

public class CleanupOldTasksHandler : EverTaskHandler<CleanupOldTasksTask>
{
    private readonly ITaskStorage _storage;

    public override async Task Handle(CleanupOldTasksTask task, CancellationToken cancellationToken)
    {
        // Delete completed tasks older than 30 days
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-30);

        // Implementation depends on your storage provider
        // You may need direct database access for efficient bulk deletes
    }
}

// Schedule it to run daily at 2 AM
await dispatcher.Dispatch(
    new CleanupOldTasksTask(),
    r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0)),
    taskKey: "cleanup-old-tasks");
```

## Next Steps

- **[Configuration Reference](configuration-reference.md)** - All storage configuration options
- **[Architecture](architecture.md)** - How storage integrates with EverTask
- **[Getting Started](getting-started.md)** - Setup guide with storage configuration
- **[Monitoring](monitoring.md)** - Monitor storage performance
