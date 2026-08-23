---
layout: default
title: SQLite Storage
parent: Storage
nav_order: 7
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
        opt.SchemaName = "";                 // SQLite doesn't support schemas; keep it empty (the default)
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

## Durable-Occurrence Columns

A recurring schedule can materialize each due slot as its own child row, so `QueuedTasks` carries three
extra columns:

| Column | Type | Purpose |
|--------|------|---------|
| `ParentTaskId` | nullable TEXT | The schedule an occurrence belongs to; null on every ordinary row |
| `RuntimeInfo` | nullable TEXT | Opaque JSON: occurrence metadata on a child, schedule runtime state on a schedule row |
| `ScheduleVersion` | INTEGER, default 0 | Bumped by a runtime reschedule; advances compare-and-swap against it |

They come with a **restrict** self-referencing foreign key `ParentTaskId → Id`, a unique index
`UX_QueuedTasks_Occurrence` on `(ParentTaskId, ScheduledExecutionUtc)` and the check constraint
`CK_QueuedTasks_OccurrenceSlot`. SQLite cannot add a foreign key or a check constraint to an existing table,
so the `AddDurableOccurrences` migration rebuilds the table — EF Core generates that rebuild, but it is worth
knowing when you look at the migration SQL. No index filter is needed: SQLite treats NULLs as distinct in a
unique index.

The occurrence operations inherit the EF Core base, where each is a conditional UPDATE inside a transaction —
which SQLite executes atomically like any other write.

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
- **[PostgreSQL Storage](postgres-storage.md)** - Open-source relational alternative
- **[MySQL / MariaDB Storage](mysql-storage.md)** - MySQL/MariaDB alternative
- **[Audit Configuration](audit-configuration.md)** - Optimize database with audit levels
- **[Best Practices](best-practices.md)** - Storage optimization strategies
