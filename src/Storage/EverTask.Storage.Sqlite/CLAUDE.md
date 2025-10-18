# CLAUDE.md - EverTask.Storage.Sqlite

SQLite-specific storage implementation for EverTask. Provides file-based or in-memory persistence for task execution state.

## Project Purpose

Implements `ITaskStorage` using SQLite via Entity Framework Core. Suitable for:
- Single-server deployments
- Development/testing environments
- Embedded applications
- Low-to-medium task volumes (< 10,000 concurrent tasks)
- Applications requiring zero infrastructure setup

## Architecture

Inherits from `EverTask.Storage.EfCore` base implementation:

```
SqliteTaskStoreContext (DbContext)
  └─ TaskStoreEfDbContext<T> (base class in EfCore)
      └─ ITaskStoreDbContext (interface)
```

Uses `EfCoreTaskStorage` (from base package) for actual storage operations.

## Database File Management

### Default File Location
- **Default**: `EverTask.db` in application working directory
- File is created automatically if it doesn't exist (when `AutoApplyMigrations = true`)

### Connection String Patterns

**File-based (persistent):**
```csharp
// Relative path (current directory)
"Data Source=EverTask.db"

// Absolute path
"Data Source=C:\\Data\\EverTask.db"

// With WAL mode (recommended for concurrency)
"Data Source=EverTask.db;Cache=Shared;Mode=ReadWriteCreate"
```

**In-memory (testing):**
```csharp
// Shared in-memory (accessible across connections)
"Data Source=:memory:;Mode=Memory;Cache=Shared"

// Private in-memory (single connection only)
"Data Source=:memory:"
```

### SQLite-Specific Considerations

**WAL Mode**: SQLite's Write-Ahead Logging improves concurrency but is NOT enabled by default. Enable via connection string or pragma.

**Thread Safety**: SQLite supports multiple readers OR single writer. For high-concurrency scenarios, consider SQL Server storage.

**File Locking**: SQLite uses file-level locking. Multiple processes accessing the same database file can cause contention.

## DI Registration

### Basic Registration
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqliteStorage(); // Uses default "Data Source=EverTask.db"
```

### Custom Connection String
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqliteStorage("Data Source=C:\\MyApp\\tasks.db");
```

### With Configuration Options
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqliteStorage("Data Source=EverTask.db", opt =>
    {
        opt.AutoApplyMigrations = true;  // Apply migrations on startup (default: true)
        opt.SchemaName = "";              // SQLite doesn't support schemas (must be empty)
    });
```

### In-Memory for Testing
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqliteStorage("Data Source=:memory:;Mode=Memory;Cache=Shared", opt =>
    {
        opt.AutoApplyMigrations = true; // Required for in-memory
    });
```

## Migrations

### Auto-Apply vs Manual

**Auto-Apply (default):**
```csharp
.AddSqliteStorage(connectionString, opt => opt.AutoApplyMigrations = true);
```
- Migrations run automatically during `AddSqliteStorage()` call
- Suitable for development, testing, single-server production
- WARNING: Blocks startup if migration fails

**Manual:**
```csharp
.AddSqliteStorage(connectionString, opt => opt.AutoApplyMigrations = false);
```
- Apply migrations manually via EF CLI or custom code
- Required for multi-instance deployments (prevents race conditions)

### Generating New Migrations

Migrations are created in DEBUG mode only (see `TaskStoreEfDbContextFactory.cs`):

```bash
# From solution root
dotnet ef migrations add MigrationName --project src/Storage/EverTask.Storage.Sqlite

# Apply manually
dotnet ef database update --project src/Storage/EverTask.Storage.Sqlite
```

### Migration History Table

SQLite stores migration history in `__EFMigrationsHistory` table (no schema prefix, unlike SQL Server).

## Schema Differences from SQL Server

### No Schema Support
- `SqliteTaskStoreOptions.SchemaName` must be `""` (empty string)
- Unlike SQL Server (`SchemaName = "EverTask"`), SQLite does not support schemas
- All tables exist in default namespace

### Data Type Mappings
```csharp
// SQL Server         SQLite
Guid                  TEXT
DateTimeOffset        TEXT  (ISO 8601 format)
bool                  INTEGER (0 = false, 1 = true)
int                   INTEGER
string                TEXT
```

### Autoincrement Identity
```csharp
// RunsAudit.Id and StatusAudit.Id
.Annotation("Sqlite:Autoincrement", true)  // SQLite-specific
```

## Database Schema

### Tables
- **QueuedTasks**: Task definitions, scheduling info, status
- **RunsAudit**: Execution history per task run
- **StatusAudit**: Task status change history

See `Migrations/20231123230302_Initial.cs` for full schema definition.

## Provider-Specific Limitations

### SQLite Limitations
1. **No concurrent writes**: Single writer at a time (readers can run concurrently)
2. **No true DateTimeOffset**: Stored as TEXT, parsed on read
3. **Limited ALTER TABLE**: Some schema changes require table recreation
4. **No schemas**: All tables in default namespace
5. **File size**: Practical limit ~1TB, but performance degrades before that

### Workarounds
- **Concurrency**: Enable WAL mode, use connection pooling (handled by EF)
- **Performance**: Index on `QueuedTasks.Status` (created by migration)
- **Scaling**: Migrate to SQL Server storage when task volume exceeds SQLite capabilities

## Use Cases

### Use SQLite When:
- Single-server deployment (ASP.NET Core, Console app, Windows Service)
- Development/testing environments
- Embedded scenarios (desktop apps, IoT devices)
- Task volume < 10,000 concurrent tasks
- Infrastructure simplicity is priority
- Zero database server setup required

### Use SQL Server When:
- Multi-server deployments (web farms, Kubernetes)
- High task volume (> 10,000 concurrent tasks)
- High write concurrency requirements
- Enterprise environments with existing SQL Server infrastructure
- Advanced features needed (partitioning, replication, Always On)

### Migration Path
Switch from SQLite to SQL Server by changing DI registration:
```csharp
// Before
.AddSqliteStorage("Data Source=EverTask.db")

// After
.AddSqlServerStorage("Server=localhost;Database=EverTask;...")
```

WARNING: Schema is compatible, but manual data migration required.

## Key Files

- **ServiceCollectionExtensions.cs**: DI registration entry point
- **SqliteTaskStoreOptions.cs**: Configuration options (AutoApplyMigrations, SchemaName)
- **SqliteTaskStoreContext.cs**: DbContext implementation (thin wrapper around base)
- **TaskStoreEfDbContextFactory.cs**: Design-time factory for migrations (DEBUG only)
- **Migrations/**: EF Core migration files

## Dependencies

- **Microsoft.EntityFrameworkCore.Sqlite**: SQLite provider for EF Core
- **EverTask.Storage.EfCore**: Base EF Core storage implementation
- **EverTask**: Core task execution library

## Testing

Test implementation at `test/EverTask.Tests.Storage/SqLiteEfCoreTaskStorageTests.cs`:
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqliteEfCoreTaskStorageTests).Assembly))
    .AddSqliteStorage("Data Source=EverTask.db", opt => opt.AutoApplyMigrations = true);
```

## Performance Notes

- SQLite is fast for reads (multiple concurrent readers)
- Write performance limited by file I/O and locking
- WAL mode significantly improves concurrency vs rollback journal
- Index on `QueuedTasks.Status` optimizes task polling queries
- In-memory mode is fastest but non-persistent (testing only)

## Common Patterns

### Development Setup
```csharp
#if DEBUG
    .AddSqliteStorage("Data Source=EverTask.db", opt => opt.AutoApplyMigrations = true);
#else
    .AddSqlServerStorage(Configuration.GetConnectionString("EverTask"));
#endif
```

### Testing Setup
```csharp
[TestInitialize]
public void Setup()
{
    var services = new ServiceCollection();
    services.AddEverTask(opt => opt.RegisterTasksFromAssembly(GetType().Assembly))
        .AddSqliteStorage("Data Source=:memory:;Mode=Memory;Cache=Shared",
            opt => opt.AutoApplyMigrations = true);
    _serviceProvider = services.BuildServiceProvider();
}
```

## No Subdirectory Documentation Needed

- **Migrations/**: Auto-generated EF Core migrations (self-documenting via code)