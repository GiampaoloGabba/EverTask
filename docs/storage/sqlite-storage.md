---
layout: default
title: SQLite Storage
parent: Storage
nav_order: 5
---

# SQLite Storage

SQLite provides lightweight, file-based storage that works well for single-server deployments.

## Installation

```bash
dotnet add package EverTask.Storage.Sqlite
```

## Configuration

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqliteStorage("Data Source=evertask.db");
```

## Advanced Configuration

```csharp
.AddSqliteStorage(
    "Data Source=evertask.db;Cache=Shared;",
    opt =>
    {
        opt.SchemaName = null;               // SQLite doesn't support schemas
        opt.AutoApplyMigrations = true;
    });
```

## File Location

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

## Characteristics

- Simple setup - single file
- No server required
- Perfect for small-scale production
- Easy backups (copy file)
- Lower infrastructure cost
- Limited concurrent writes
- Single server only (no clustering)
- Provider limitation: EF Core cannot translate `DateTimeOffset` comparison operators for SQLite. EverTask falls back to in-memory keyset filtering during recovery (`ProcessPendingAsync`), so avoid very large backlogs on SQLite or switch to SQL Server for heavy workloads.

## Use Cases

- Small to medium applications
- Single-server deployments
- Desktop applications
- IoT / edge computing

## Best Practices

### File Permissions

Ensure the application has read/write permissions to the database file and its directory (SQLite needs to create temporary files).

### Connection String Options

```csharp
// Recommended for ASP.NET Core applications
.AddSqliteStorage("Data Source=evertask.db;Cache=Shared;")
```

The `Cache=Shared` option allows multiple connections to share the same cache, which can improve performance in multi-threaded applications.

### File Location

Store the database file in a persistent location:

```csharp
// Good: Persistent location
var dbPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "MyApp",
    "evertask.db");

// Bad: Temp directory (may be cleared)
var dbPath = Path.Combine(Path.GetTempPath(), "evertask.db");
```

## Performance Considerations

### Concurrent Writes

SQLite supports multiple concurrent readers but only one writer at a time. For high-concurrency scenarios with many concurrent task executions, consider SQL Server instead.

### Large Backlogs

Due to the `DateTimeOffset` limitation, SQLite uses in-memory filtering when recovering pending tasks. If you expect to have thousands of pending tasks, SQL Server is a better choice.

### Write-Ahead Logging (WAL)

SQLite's WAL mode can improve concurrent read/write performance:

```csharp
.AddSqliteStorage("Data Source=evertask.db;Cache=Shared;Pooling=True;")
```

Then enable WAL in your database:

```sql
PRAGMA journal_mode=WAL;
```

## When to Use

Use SQLite storage when:
- Running a small application
- Single-server deployment
- Limited infrastructure budget
- Desktop or edge applications
- Need simple file-based persistence
- Concurrent writes are moderate

Consider alternatives when:
- High concurrency requirements (use SQL Server)
- Multi-server clustering (use SQL Server)
- Very large backlogs (> 10,000 pending tasks)
- Need stored procedures and advanced features

## Next Steps

- **[Storage Overview](overview.md)** - Compare storage providers
- **[SQL Server Storage](sql-server-storage.md)** - Enterprise storage alternative
- **[Audit Configuration](audit-configuration.md)** - Optimize database with audit levels
- **[Best Practices](best-practices.md)** - Storage optimization strategies
