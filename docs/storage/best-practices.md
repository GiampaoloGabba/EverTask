---
layout: default
title: Best Practices
parent: Storage
nav_order: 8
---

# Storage Best Practices

## Storage Selection

Pick the right storage provider for your scenario:

1. **Development**: Use In-Memory storage
2. **Small Production Apps**: SQLite is sufficient
3. **Enterprise / Scale**: Use SQL Server
4. **Specific Needs**: Implement custom storage

### Selection Criteria

**Choose In-Memory when:**
- Developing locally
- Running integration tests
- Prototyping features
- Tasks don't need to survive restarts

**Choose SQLite when:**
- Small to medium applications
- Single-server deployments
- Limited infrastructure budget
- Desktop or edge applications

**Choose SQL Server when:**
- Production at scale
- High availability requirements
- Need robust querying
- Enterprise-grade reliability needed

**Choose Custom Storage when:**
- Specific database requirements
- Integration with existing data stores
- Cloud-native databases (CosmosDB, DynamoDB)
- Specialized storage features needed

## Connection Strings

### Store in Configuration

```csharp
// Good: From configuration
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)

// Bad: Hardcoded
.AddSqlServerStorage("Server=localhost;Database=EverTaskDb;...")
```

### Environment-Specific Configuration

```json
// appsettings.Development.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Data Source=evertask-dev.db"
  }
}

// appsettings.Production.json
{
  "ConnectionStrings": {
    "EverTaskDb": "Server=prod-sql;Database=EverTaskDb;User Id=evertask;Password=***"
  }
}
```

### Secure Secrets

```csharp
// Use environment variables for sensitive data
var connectionString = builder.Configuration.GetConnectionString("EverTaskDb")
    ?? throw new InvalidOperationException("Connection string not configured");

// Or use Azure Key Vault, AWS Secrets Manager, etc.
```

## Schema Management

### Use Dedicated Schema (SQL Server)

```csharp
// Good: Use dedicated schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = "EverTask";
})

// Bad: Pollute main schema
.AddSqlServerStorage(connectionString, opt =>
{
    opt.SchemaName = null;
})
```

**Benefits:**
- Clear separation of concerns
- Easier to manage permissions
- Simpler backup/restore operations
- No naming conflicts with application tables

## Migration Strategy

### Automatic Migrations (Recommended for Most Cases)

```csharp
// Auto-apply migrations (default)
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = true;
});
```

**Good for:**
- Development environments
- Small applications
- Simple deployment processes

### Manual Migrations (Enterprise)

```csharp
// Disable auto-apply if you prefer manual control
.AddSqlServerStorage(connectionString, opt =>
{
    opt.AutoApplyMigrations = false;
});
```

Then apply migrations manually:

```bash
# Generate SQL script
dotnet ef migrations script --project YourProject --context TaskStoreDbContext --output migrations.sql

# Review and apply script manually
```

**Good for:**
- Enterprise environments
- DBA-controlled deployments
- Compliance requirements
- Staged rollouts

## Backup and Recovery

### SQL Server

```sql
-- Backup
BACKUP DATABASE EverTaskDb TO DISK = 'C:\Backups\EverTaskDb.bak'

-- Restore
RESTORE DATABASE EverTaskDb FROM DISK = 'C:\Backups\EverTaskDb.bak'
```

### SQLite

```bash
# Backup (simple file copy)
cp evertask.db evertask.db.backup

# Restore
cp evertask.db.backup evertask.db
```

### Backup Strategy

**Recommended approach:**
1. **Regular backups**: Daily full backups, hourly incrementals (SQL Server)
2. **Test restores**: Verify backups work before you need them
3. **Off-site storage**: Store backups in different location/region
4. **Retention policy**: Keep 30 days of backups, longer for compliance

## Monitoring Storage Performance

### Track Storage Metrics

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

### Monitor Database Size

**SQL Server:**
```sql
SELECT
    DB_NAME(database_id) AS DatabaseName,
    (size * 8.0 / 1024) AS SizeMB
FROM sys.master_files
WHERE database_id = DB_ID('EverTaskDb');
```

**SQLite:**
```bash
ls -lh evertask.db
```

## Cleanup Old Tasks

Over time, completed tasks can pile up. Here's how to create a recurring cleanup task that runs daily:

### Create Cleanup Task

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

### SQL Server Cleanup

```sql
-- Delete completed tasks older than 30 days
DELETE FROM [EverTask].[QueuedTasks]
WHERE Status = 2 -- Completed
  AND CompletedAtUtc < DATEADD(DAY, -30, GETUTCDATE());

-- Clean up orphaned audit records
DELETE FROM [EverTask].[StatusAudit]
WHERE TaskId NOT IN (SELECT Id FROM [EverTask].[QueuedTasks]);

DELETE FROM [EverTask].[RunsAudit]
WHERE TaskId NOT IN (SELECT Id FROM [EverTask].[QueuedTasks]);
```

### Cleanup Strategy

**Consider:**
- **Retention period**: How long to keep completed tasks (30, 60, 90 days)
- **Audit requirements**: Legal/compliance requirements for task history
- **Failed tasks**: Keep failed tasks longer for debugging
- **Archive vs Delete**: Archive old tasks to separate table/database instead of deleting

### Archive Strategy

```sql
-- Create archive table
CREATE TABLE [EverTask].[QueuedTasks_Archive] (
    -- Same schema as QueuedTasks
);

-- Archive old tasks
INSERT INTO [EverTask].[QueuedTasks_Archive]
SELECT * FROM [EverTask].[QueuedTasks]
WHERE Status = 2 -- Completed
  AND CompletedAtUtc < DATEADD(DAY, -90, GETUTCDATE());

-- Delete archived tasks
DELETE FROM [EverTask].[QueuedTasks]
WHERE Status = 2
  AND CompletedAtUtc < DATEADD(DAY, -90, GETUTCDATE());
```

## Audit Configuration

Configure audit levels to prevent database bloat from high-frequency tasks:

```csharp
// Set global default
builder.Services.AddEverTask(opt => opt
    .SetDefaultAuditLevel(AuditLevel.Full)) // Conservative default
    .AddSqlServerStorage(connectionString);

// Override per task
await dispatcher.Dispatch(
    new HighFrequencyTask(),
    recurring => recurring.Every(5).Minutes(),
    auditLevel: AuditLevel.Minimal); // 75% reduction in audit records
```

**See:** [Audit Configuration](audit-configuration.md) for detailed guidance

## Task Design

Design tasks for optimal serialization:

```csharp
// Good: Simple, serializable task
public record ProcessOrderTask(
    int OrderId,
    string CustomerEmail,
    List<int> ItemIds) : IEverTask;

// Bad: Complex, non-serializable task
public record ProcessOrderTask(
    Order Order, // DbContext-tracked entity
    IOrderService OrderService, // Service dependency
    Func<bool> ValidationCallback) : IEverTask; // Delegate
```

**See:** [Serialization](serialization.md) for detailed guidance

## Performance Optimization

### SQL Server

1. **Enable DbContext Pooling**: Automatic in v2.0+
2. **Use Stored Procedures**: Automatic in v2.0+ for SetStatus
3. **Configure Audit Levels**: Use Minimal for high-frequency tasks
4. **Index Custom Queries**: Add indexes if you add custom queries

### SQLite

1. **Enable WAL Mode**: Better concurrent performance
2. **Use Shared Cache**: `Cache=Shared` in connection string
3. **Avoid Large Backlogs**: Switch to SQL Server for heavy workloads
4. **File Location**: Use SSD for better performance

### In-Memory

1. **Limited by RAM**: Monitor memory usage
2. **No Optimization Needed**: Already fastest option

## Security

### Connection String Security

```csharp
// Good: Store in secure configuration
var connectionString = builder.Configuration.GetConnectionString("EverTaskDb");

// Bad: Hardcoded credentials
var connectionString = "Server=prod;User=admin;Password=admin123";
```

### Database Permissions

**SQL Server:**
```sql
-- Create dedicated user with minimal permissions
CREATE USER evertask WITH PASSWORD = '***';
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::EverTask TO evertask;
```

**SQLite:**
```bash
# Ensure file permissions restrict access
chmod 600 evertask.db
chown appuser:appuser evertask.db
```

### Encrypt Connections

```csharp
// SQL Server: Encrypt connection
.AddSqlServerStorage(
    "Server=prod;Database=EverTaskDb;Encrypt=True;TrustServerCertificate=False;...")
```

## Troubleshooting

### Task Not Persisting

1. Check connection string is correct
2. Verify database exists
3. Ensure migrations have been applied
4. Check application has database permissions
5. Look for serialization errors in logs

### Slow Performance

1. Check database server resources (CPU, memory, disk)
2. Monitor audit table sizes (implement cleanup)
3. Review audit levels (use Minimal for high-frequency)
4. Check for missing indexes (if custom queries added)
5. Consider upgrading storage provider (SQLite → SQL Server)

### Database Growing Too Large

1. Implement cleanup task for old completed tasks
2. Reduce audit levels for high-frequency tasks
3. Archive old data to separate table/database
4. Implement retention policies

## Next Steps

- **[Audit Configuration](audit-configuration.md)** - Control database bloat
- **[Serialization](serialization.md)** - Task design best practices
- **[Storage Overview](overview.md)** - Compare storage providers
- **[Monitoring](../monitoring.md)** - Monitor storage performance
