---
layout: default
title: Audit Configuration
parent: Storage
nav_order: 2
---

# Audit Configuration

EverTask provides configurable audit trail levels to control database bloat from high-frequency tasks. By default, every task execution creates audit records in `StatusAudit` and `RunsAudit` tables. For tasks running every few minutes, this can generate thousands of records per day.

## Audit Levels

Control audit trail verbosity with the `AuditLevel` enum:

| Level | StatusAudit | RunsAudit | Use Case |
|-------|-------------|-----------|----------|
| **Full** (default) | All status transitions | All executions | Critical tasks requiring complete history |
| **Minimal** | Errors only | All executions | High-frequency recurring tasks (tracks last run + errors) |
| **ErrorsOnly** | Errors only | Errors only | Tasks where only failures matter |
| **None** | Never | Never | Extremely high-frequency tasks, no audit needed |

## Database Impact

**Example** (100 tasks running every 5 minutes):

| Audit Level | Daily Audit Records | Storage Reduction |
|-------------|---------------------|-------------------|
| Full | ~2,304 records/day | Baseline |
| Minimal | ~576 records/day | 75% reduction |
| ErrorsOnly | ~903 records/day* | 60% reduction |
| None | 0 records/day | 100% reduction |

*Assuming typical failure rates. Only errors generate audit records.

## Global Default Configuration

Set the default audit level for all tasks:

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetDefaultAuditLevel(AuditLevel.Minimal)) // Default: AuditLevel.Full
    .AddSqlServerStorage(connectionString);
```

## Per-Task Override

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

## Audit Level Behavior

### Full (Default)

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

### Minimal

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

### ErrorsOnly

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

### None

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

## Real-World Configuration Example

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

## Performance Optimization Details

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

## Migration Notes

- **Backward Compatible**: Null `AuditLevel` in database treated as `Full` (default)
- **Existing Tasks**: Tasks created before v1.7 continue with Full audit level
- **No Data Loss**: Changing audit level only affects future executions
- **Custom Storage**: Implementations must accept `AuditLevel` parameter in `SetStatus()` and `UpdateCurrentRun()`

## Recommendations by Task Type

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

## Monitoring Audit Growth

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
2. Implementing audit retention policies (see [Best Practices](best-practices.md#cleanup-old-tasks))
3. Archiving historical audit data to separate tables/database

## Next Steps

- **[Best Practices](best-practices.md)** - Storage optimization and cleanup strategies
- **[SQL Server Storage](sql-server-storage.md)** - Stored procedure optimization
- **[Monitoring](../monitoring.md)** - Track task execution and audit growth
