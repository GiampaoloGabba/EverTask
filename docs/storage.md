---
layout: default
title: Storage
nav_order: 6
---

# Storage Configuration

EverTask supports multiple storage providers for task persistence. This guide covers all available options and how to implement custom storage.

## Table of Contents

- [Storage Overview](#storage-overview)
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

#### Automatic Migrations

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true; // Applies migrations on startup
});
```

#### Manual Migrations

For production environments, we recommend manually controlling when migrations run:

```bash
# Create a migration
dotnet ef migrations add InitialCreate --project YourProject --context TaskStoreDbContext

# Apply migrations
dotnet ef database update --project YourProject --context TaskStoreDbContext
```

```csharp
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = false;
});
```

You can generate SQL scripts and apply them through your normal deployment pipeline:

```bash
# Generate SQL script
dotnet ef migrations script --project YourProject --context TaskStoreDbContext --output migrations.sql

# Review and apply via SQL Management Studio or deployment pipeline
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
// Development: Auto-apply
#if DEBUG
    opt.AutoApplyMigrations = true;
#else
    opt.AutoApplyMigrations = false; // Manual in production
#endif
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
