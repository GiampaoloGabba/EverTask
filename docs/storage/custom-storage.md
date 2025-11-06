---
layout: default
title: Custom Storage
parent: Storage
nav_order: 6
---

# Custom Storage

You can implement custom storage providers for Redis, MongoDB, PostgreSQL, or any other database by implementing the `ITaskStorage` interface.

## Implementing ITaskStorage

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

## Example: Redis Storage

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

## Implementing ITaskStoreDbContextFactory (v2.0+)

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

## Implementation Guidelines

### Required Functionality

Your custom storage implementation must:

1. **Persist Tasks**: Store task data durably
2. **Support Queries**: Retrieve pending and scheduled tasks efficiently
3. **Handle Concurrent Access**: Support multiple workers reading/writing simultaneously
4. **Implement Task Keys**: Support idempotent task registration via `GetByTaskKey()`
5. **Support Audit Trails**: Store audit records for task execution history
6. **Handle Execution Logs**: Store and retrieve task execution logs (v3.0+)

### Performance Considerations

1. **Index Key Fields**: Ensure `Status`, `ScheduledTime`, and `TaskKey` are indexed for fast queries
2. **Optimize Pending Tasks Query**: `GetPendingTasksAsync()` is called frequently - make it fast
3. **Use Transactions**: Ensure atomic updates where necessary (status changes + audit records)
4. **Connection Pooling**: Reuse database connections efficiently
5. **Batch Operations**: Consider batch operations for audit records if your storage supports it

### Error Handling

Your implementation should:

1. **Throw on Critical Failures**: Let EverTask handle retry logic
2. **Handle Transient Errors**: Implement retry logic for network errors
3. **Log Errors**: Log storage errors for debugging
4. **Validate Input**: Check for null/invalid parameters

### Testing Your Implementation

```csharp
public class CustomStorageTests
{
    private readonly ITaskStorage _storage;

    public CustomStorageTests()
    {
        _storage = new YourCustomStorage(/* configuration */);
    }

    [Fact]
    public async Task Should_Add_And_Retrieve_Task()
    {
        var task = new QueuedTask
        {
            PersistenceId = Guid.NewGuid(),
            TaskType = "TestTask",
            Status = TaskStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _storage.AddAsync(task);
        var retrieved = await _storage.GetAsync(task.PersistenceId);

        retrieved.ShouldNotBeNull();
        retrieved.TaskType.ShouldBe("TestTask");
    }

    [Fact]
    public async Task Should_Update_Task_Status()
    {
        var task = new QueuedTask
        {
            PersistenceId = Guid.NewGuid(),
            Status = TaskStatus.Pending
        };

        await _storage.AddAsync(task);
        await _storage.SetStatus(task.PersistenceId, TaskStatus.Completed);

        var retrieved = await _storage.GetAsync(task.PersistenceId);
        retrieved!.Status.ShouldBe(TaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Retrieve_Pending_Tasks()
    {
        var task1 = new QueuedTask { PersistenceId = Guid.NewGuid(), Status = TaskStatus.Pending };
        var task2 = new QueuedTask { PersistenceId = Guid.NewGuid(), Status = TaskStatus.Completed };

        await _storage.AddAsync(task1);
        await _storage.AddAsync(task2);

        var pending = await _storage.GetPendingTasksAsync();

        pending.Count.ShouldBe(1);
        pending[0].PersistenceId.ShouldBe(task1.PersistenceId);
    }
}
```

## Common Custom Storage Scenarios

### PostgreSQL

Implement using Npgsql and EF Core with PostgreSQL provider. Follow the same pattern as SQL Server storage.

### MongoDB

```csharp
public class MongoDbTaskStorage : ITaskStorage
{
    private readonly IMongoCollection<QueuedTask> _tasks;

    public MongoDbTaskStorage(IMongoClient client)
    {
        var database = client.GetDatabase("EverTask");
        _tasks = database.GetCollection<QueuedTask>("Tasks");
    }

    public async Task<Guid> AddAsync(QueuedTask task, CancellationToken cancellationToken = default)
    {
        await _tasks.InsertOneAsync(task, cancellationToken: cancellationToken);
        return task.PersistenceId;
    }

    // Implement other methods...
}
```

### CosmosDB

Use the Cosmos SDK with proper partitioning strategy based on task execution patterns.

### DynamoDB

Use AWS SDK with appropriate table design and secondary indexes for queries.

## Next Steps

- **[Storage Overview](overview.md)** - Compare with built-in providers
- **[Serialization](serialization.md)** - Handle task serialization
- **[Best Practices](best-practices.md)** - Storage optimization strategies
- **[SQL Server Storage](sql-server-storage.md)** - Reference implementation
