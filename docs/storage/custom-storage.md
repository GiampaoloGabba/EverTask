---
layout: default
title: Custom Storage
parent: Storage
nav_order: 7
---

# Custom Storage

You can implement custom storage providers for Redis, MongoDB, or any other database by implementing the `ITaskStorage` interface.

## Implementing ITaskStorage

```csharp
public interface ITaskStorage
{
    // Querying
    Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default);
    Task<QueuedTask[]> GetAll(CancellationToken ct = default);
    Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default);

    // Persistence
    Task Persist(QueuedTask executor, CancellationToken ct = default);
    Task UpdateTask(QueuedTask task, CancellationToken ct = default);
    Task Remove(Guid taskId, CancellationToken ct = default);

    // Recovery: keyset-paginated page of pending tasks ordered by creation timestamp
    Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default);

    // Status transitions (each carries the AuditLevel so the audit row is written without a SELECT)
    Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default);
    Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default);
    Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel);
    Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel);
    Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel);
    Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                   double? executionTimeMs = null, CancellationToken ct = default);

    // Recurring run accounting
    Task<int> GetCurrentRunCount(Guid taskId);
    Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel);

    // Task execution log persistence (v3.0+)
    Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken);
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken);
}
```

The interface also exposes default-implemented members that built-in providers override with atomic
writes: `TrySetQueuedIfRecoverable`, `CompleteRecurringRun`, `SetRecurringSeriesCompleted`,
`SetRecurringTaskPoisoned`, and the recovery-failure counter (`IncrementRecoveryFailure` /
`ClearRecoveryFailure`). A custom store inherits the non-atomic fallbacks; override them only
if your backend can make the check-and-set atomic. See `src/EverTask/Storage/ITaskStorage.cs` for the
full contract and the per-member rationale.

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

    public async Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        var id = executor.Id;
        var json = JsonSerializer.Serialize(executor);

        await _db.StringSetAsync($"task:{id}", json);

        // Index by status so SetStatus and recovery can find the row
        await _db.SetAddAsync($"tasks:{executor.Status}", id.ToString());
    }

    private async Task<QueuedTask?> Read(Guid id)
    {
        var json = await _db.StringGetAsync($"task:{id}");
        return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<QueuedTask>(json!);
    }

    public async Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(task);
        await _db.StringSetAsync($"task:{task.Id}", json);
    }

    public async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception,
                                AuditLevel auditLevel, double? executionTimeMs = null,
                                CancellationToken ct = default)
    {
        var task = await Read(taskId);
        if (task is null)
            return;

        await _db.SetRemoveAsync($"tasks:{task.Status}", taskId.ToString());

        task.Status = status;
        task.Exception = exception?.ToString();
        await UpdateTask(task, ct);

        await _db.SetAddAsync($"tasks:{status}", taskId.ToString());

        // Write a StatusAudit row when the audit level requires it (mirror AuditPolicy)
        if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception))
        {
            // persist a StatusAudit record keyed by QueuedTaskId = taskId
        }
    }

    // RetrievePending: return a keyset-paginated page of recoverable tasks
    // ordered by CreatedAtUtc (then Id), starting after (lastCreatedAt, lastId).

    // Implement the remaining members (Get, GetAll, the other status setters,
    // UpdateCurrentRun, execution-log persistence, ...).
}

// Registration
builder.Services.AddSingleton<ITaskStorage, RedisTaskStorage>();
```

## Implementing ITaskStoreDbContextFactory (v2.0+)

If you're building an EF Core-based storage provider, implement the factory pattern and register it with
`AddPooledDbContextFactory<T>` (NOT `AddDbContextFactory<T>`, which is not pooled) to take advantage of
DbContext pooling. Because pooling requires a single `DbContextOptions<T>` constructor, route any schema
through the options (e.g. `optionsBuilder.UseEverTaskSchema(schemaName)`) rather than a constructor dependency:

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
builder.Services.AddPooledDbContextFactory<MyCustomDbContext>(options =>
    options.UseYourDatabase(connectionString)
           .UseEverTaskSchema(schemaName));

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

1. **Index Key Fields**: Ensure `Status`, `CreatedAtUtc`, and `TaskKey` are indexed for fast queries
2. **Optimize the Recovery Query**: `RetrievePending()` runs on every startup - keyset pagination on `(CreatedAtUtc, Id)` keeps it fast
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
    public async Task Should_Persist_And_Retrieve_Task()
    {
        var task = new QueuedTask
        {
            Id = Guid.NewGuid(),
            Type = "TestTask",
            Status = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await _storage.Persist(task);
        var retrieved = (await _storage.Get(t => t.Id == task.Id)).FirstOrDefault();

        retrieved.ShouldNotBeNull();
        retrieved.Type.ShouldBe("TestTask");
    }

    [Fact]
    public async Task Should_Update_Task_Status()
    {
        var task = new QueuedTask
        {
            Id = Guid.NewGuid(),
            Status = QueuedTaskStatus.Queued
        };

        await _storage.Persist(task);
        await _storage.SetStatus(task.Id, QueuedTaskStatus.Completed, null, AuditLevel.Full);

        var retrieved = (await _storage.Get(t => t.Id == task.Id)).First();
        retrieved.Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_Retrieve_Pending_Tasks()
    {
        var task1 = new QueuedTask { Id = Guid.NewGuid(), Status = QueuedTaskStatus.Queued };
        var task2 = new QueuedTask { Id = Guid.NewGuid(), Status = QueuedTaskStatus.Completed };

        await _storage.Persist(task1);
        await _storage.Persist(task2);

        var pending = await _storage.Get(t => t.Status == QueuedTaskStatus.Queued);

        pending.Length.ShouldBe(1);
        pending[0].Id.ShouldBe(task1.Id);
    }
}
```

## Common Custom Storage Scenarios

### PostgreSQL

PostgreSQL is now a **built-in** provider: use [`EverTask.Storage.Postgres`](postgres-storage.md) (`AddPostgresStorage(...)`) instead of writing your own. The scenarios below remain useful for stores EverTask does not ship.

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

    public async Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        await _tasks.InsertOneAsync(executor, cancellationToken: ct);
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
