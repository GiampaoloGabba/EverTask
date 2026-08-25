---
layout: default
title: Custom Storage
parent: Storage
nav_order: 8
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

## The Scheduling Clock

Every scheduling decision in EverTask resolves "now" from one `TimeProvider`, so a test can drive the whole
pipeline deterministically and a clock skew between the host and the database cannot make recovery disagree
with the scheduler. Two members carry that instant into storage:

```csharp
Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt, Guid? lastId,
                                   int take, CancellationToken ct = default);

Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                     CancellationToken ct = default);
```

Both are default members that delegate to the signatures above them, so a store written before they existed
keeps working and keeps its own implementation. It simply resolves the clock itself, so the deterministic
guarantee does not extend to it. Override them to honour `nowUtc` and you get that guarantee too.

The same holds for a provider that inherits `EfCoreTaskStorage`. Override only the older signatures and the
base hands the clock-carrying calls straight back to them instead of answering with its own query. Override
the `nowUtc` ones, as the in-box providers do, and yours win.

## Recovery Returns Two Kinds of Row

A recovery page is the union of two categories, and the distinction matters because they lead to opposite
actions:

- **Rows to execute**: `QueuedTask.IsRecoverableForExecution(now)`. Note the grouping of the temporal term:
  a recurring series whose `RunUntil` elapsed *during* the downtime still has the occurrence it had already
  scheduled before that boundary, and dropping it silently loses that run.
- **Series to finalize**: `QueuedTask.IsRecurringSeriesToFinalize()`. Its remaining slots all fall past
  `RunUntil`, or its run budget is spent. Nothing runs: the row is marked `Completed` with its cursor cleared.
  `Cancelled` is excluded, because a cancelled series is already terminal.

Both predicates live on `QueuedTask` and are the canonical, client-side definition. `RetrievePending` must
return their union; `TrySetQueuedIfRecoverable` must apply only the first, so a spent series is never handed
back to a worker queue.

Finalizing is conditional wherever the store can make it so. On a store that advertises
`SupportsScheduleVersioning` the recovery calls `TrySetRecurringSeriesCompleted`, compare-and-swapped on the
cursor, status and version of the row the page read. A `Cancel` that linearized in between therefore wins,
and the finalization reports the loss instead of overwriting it. A store without that capability keeps the
unconditional `SetRecurringSeriesCompleted`: refusing it there would turn a normal end of series into a
recovery failure, and after a few restarts into a poisoned task.

Finalizing a series writes `LastExecutionUtc` even though nothing ran. It is the same write every terminal
transition makes, and it has two consequences. The audit trail shows a `Queued → Completed` transition with no
`RunsAudit` row next to it, because there was no run. And the completed-task retention window is measured from
`LastExecutionUtc`, so it restarts at the finalization instead of at the series' last real run: a schedule
whose boundary elapsed during a long downtime is pruned relative to the restart that closed it.

## Durable Occurrences (optional)

A durable recurring schedule turns each due slot into its own child row, which needs operations no
non-atomic emulation can provide. They are default members that **throw `NotSupportedException`**, and two
capabilities say whether a store really has them:

```csharp
bool SupportsDurableOccurrences => false;
bool SupportsScheduleVersioning => false;
```

Capability and implementation are inseparable. A "best effort" version built from two separate writes is
exactly the crash window these operations exist to close (an occurrence inserted without its cursor advance,
or a cursor advanced with no occurrence), so a store either implements them atomically and advertises them,
or dispatching a durable schedule against it fails fast.

The two capabilities are independent, and so is what
[`ITaskScheduleManager`](../recurring-tasks/managing-tasks.md#changing-a-schedule-while-it-runs) asks of them.
`Reschedule`, `ReevaluateSchedule` and `ResumeSchedule` rewrite a schedule row and need
`SupportsScheduleVersioning`: without a real compare-and-swap a reschedule could report success while a run
finishing at the same moment overwrote it, so a store that returns `false` is refused rather than emulated.
`RequeueFailedOccurrence` addresses a child row and needs `SupportsDurableOccurrences` instead — a store with
versioning but no occurrences has nothing to requeue. `CancelSchedule` needs neither: it writes a
cancellation. So a store that implements neither capability keeps working for everything else, including
ending a schedule on purpose. Its schedules just cannot be changed while they run.

| Operation | What must be atomic |
|-----------|---------------------|
| `MaterializeOccurrence` | Insert the child AND advance the schedule cursor, guarded by a compare-and-swap on version + cursor. A null new cursor ends the series in the same commit. |
| `TryAdvanceScheduleCursor` | Move the cursor with NO occurrence, guarded by version + cursor |
| `TrySetRecurringSeriesCompleted` | Finalize only while the expected cursor, status and version still hold |
| `CancelSchedule` | Cancel the schedule and its still-waiting occurrences together |
| `RequeueTerminal` | Put a `Failed`/`Cancelled` row back to `Queued`, keeping its identity and audits |
| `TryRequeueStaleOccurrence` | Compare-and-swap requeue of an occurrence stranded in a known status |
| `UpdateSchedule` | Replace the definition and bump the version, guarded by version + cursor and refused on a `Cancelled` row. A finished series expects a `null` cursor, so the guard has to read that as IS NULL; a cancel touches neither the version nor the cursor, so only the status can refuse it |
| `TryHaltSchedule` | Write the halted marker, guarded by version + cursor + status |
| `UpdateCurrentRun` / `CompleteRecurringRun` (version overloads) | Advance only while the schedule version matches |

`MaterializeOccurrence` must also classify what it finds the way every built-in store does, in this order:

1. the schedule row is gone, cancelled, or already has no cursor: `ParentInactive`;
2. its version differs from the expected one: `VersionMismatch`;
3. its cursor differs from the expected one: `CursorMoved`. An expected cursor of `null` lands here too,
   since a live schedule always has one;
4. the slot is already materialized: `AlreadyExists`.

Only `Created` writes anything. Miss the `null` case and a caller that retried with the cursor it just read
back inserts an occurrence on a finished series and hands it a cursor again.

The child row has one shape, whatever the backend, and `QueuedTask.ApplyOccurrenceContract(scheduleId,
scheduleVersion)` is that shape. It writes a fresh one-shot: `WaitingQueue`, run count 0, at the version it
was materialized against, with the definition, the cursor, the bounds and the task key cleared. What the
caller supplies survives untouched: id, creation time, slot, type, payload, handler, queue, audit level,
runtime info. The in-box providers that build the `INSERT` by hand write those columns and nothing else; a
store that persists the entity it was handed calls the method first. Skip it and the same
`MaterializeOccurrence(...)` call stores a different row on your backend than on every other one.

`TryAdvanceScheduleCursor` is how a slot is SKIPPED. Every slot that survives the misfire policy carries the
cursor forward inside `MaterializeOccurrence` — the occurrence is written at the slot that survives while the
cursor jumps over the ones that did not — so a skip usually costs no write of its own. This is the write for
the case where nothing survives at all: a whole backlog outside the age window, or a stale slot under the skip
policy. It counts no run and writes no audit, because nothing executed, and a cursor that would move to `null`
goes through `TrySetRecurringSeriesCompleted` instead.

`CancelSchedule` cancels exactly the set startup recovery would put back in a queue: `WaitingQueue`, `Queued`,
`Pending` and `ServiceStopped`. Leave one of them out and an occurrence of a cancelled schedule comes back at
the next restart and runs. Occurrences already `InProgress` own a live delivery and are left to end on their
own. It also touches only rows that exist: cancelling a schedule someone else has already removed is a no-op,
not an error, and it leaves no audit row for a task that is gone.

**If your backend admits concurrent writers, derive the audited set from the cancelling statement itself** —
`OUTPUT`, `RETURNING`, or whatever your engine offers — never from a second read. Under READ COMMITTED a
re-read can attribute to this call an occurrence another writer cancelled, so the audit trail would claim a
transition your transaction never made. The three optimized in-box providers take it from the statement; the
EF Core base re-reads inside the transaction, which is exact only while writers are serialized, as they are
on SQLite.

The two read helpers (`GetOccurrences`, `CountActiveOccurrences`) carry no atomicity contract, so their
defaults are a correct query over `Get`. Override them for an indexed one.

Three columns back all of this: `ParentTaskId` (with a restrict self-foreign-key, a unique index on
`(ParentTaskId, ScheduledExecutionUtc)` named `UX_QueuedTasks_Occurrence`, and a check constraint that an
occurrence always names its slot), `RuntimeInfo` (opaque JSON) and `ScheduleVersion` (int, default 0).

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
    ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default);

    // Synchronous path for the places that have no async hook, such as the scoped
    // ITaskStoreDbContext DI registration. The default waits on CreateDbContextAsync() as a Task.
    ITaskStoreDbContext CreateDbContext() => CreateDbContextAsync().AsTask().GetAwaiter().GetResult();
}

public class MyCustomDbContextFactory(IDbContextFactory<MyCustomDbContext> factory) : ITaskStoreDbContextFactory
{
    public async ValueTask<ITaskStoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await factory.CreateDbContextAsync(cancellationToken);

    // IDbContextFactory has a synchronous CreateDbContext, so override and skip the wait entirely
    public ITaskStoreDbContext CreateDbContext() => factory.CreateDbContext();
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

### MySQL / MariaDB

MySQL and MariaDB are also **built-in**: use [`EverTask.Storage.MySql`](mysql-storage.md) (`AddMySqlStorage(...)`) instead of writing your own.

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
