# EverTask.Storage.SqlServer

SQL Server-specific storage implementation for EverTask background task library.

## Purpose

Provides persistent task storage using SQL Server with Entity Framework Core. This package extends `EverTask.Storage.EfCore` to add SQL Server-specific features, migrations, and configuration.

## Architecture

**Inheritance Chain:**
- `SqlServerTaskStoreContext` extends `TaskStoreEfDbContext<T>` (from EverTask.Storage.EfCore)
- `TaskStoreEfDbContext<T>` extends `DbContext` and implements `ITaskStoreDbContext`

**Key Components:**
- `SqlServerTaskStoreContext`: DbContext for SQL Server provider
- `ServiceCollectionExtensions`: DI registration via `AddSqlServerStorage()`
- `DbSchemaAwareMigrationAssembly`: Custom migration assembly supporting schema-aware migrations
- `TaskStoreEfDbContextFactory`: Design-time factory for migration generation (DEBUG only)
- `SqlServerTaskStoreOptions`: Configuration options (schema name, auto-apply migrations)

## Database Schema

**Default Schema:** `EverTask` (configurable via `SqlServerTaskStoreOptions.SchemaName`)

**Tables:**
- `QueuedTasks`: Main task queue (Id, Type, Handler, Request, Status, CreatedAtUtc, LastExecutionUtc, Exception)
- `StatusAudit`: Task status change history (Id, QueuedTaskId, NewStatus, UpdatedAtUtc, Exception)
- `RunsAudit`: Task execution history (Id, QueuedTaskId, Status, StartedAtUtc, CompletedAtUtc, Exception, RunUntil)

**Indexes:**
- `IX_QueuedTasks_Status` on `QueuedTasks.Status` (non-unique)
- `IX_QueuedTaskStatusAudit_QueuedTaskId` on `StatusAudit.QueuedTaskId` (non-unique)
- `IX_RunsAudit_QueuedTaskId` on `RunsAudit.QueuedTaskId` (non-unique)

**Foreign Keys:**
- `StatusAudit.QueuedTaskId` → `QueuedTasks.Id` (CASCADE delete)
- `RunsAudit.QueuedTaskId` → `QueuedTasks.Id` (CASCADE delete)

## DI Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString, opt =>
    {
        opt.SchemaName = "EverTask";              // Default: "EverTask"
        opt.AutoApplyMigrations = true;           // Default: true
    });
```

**Registration Details:**
- Registers `SqlServerTaskStoreContext` as scoped DbContext
- Registers `ITaskStoreDbContext` pointing to `SqlServerTaskStoreContext`
- Registers `ITaskStorage` as singleton `EfCoreTaskStorage` (from EverTask.Storage.EfCore)
- Configures SQL Server provider with `UseSqlServer()`
- Replaces `IMigrationsAssembly` with `DbSchemaAwareMigrationAssembly` for schema-aware migrations
- Sets migrations history table to `__EFMigrationsHistory` in configured schema

**Auto-Apply Migrations:**
If `AutoApplyMigrations = true` (default), migrations are applied automatically during service registration by calling `dbContext.Database.Migrate()`.

## Connection String Requirements

**Format:** Standard SQL Server connection string

**Examples:**
```csharp
// LocalDB
"Server=(localdb)\\mssqllocaldb;Database=EverTaskDb;Trusted_Connection=True;MultipleActiveResultSets=true"

// SQL Server instance
"Server=localhost;Database=EverTaskDb;User Id=sa;Password=YourPassword;TrustServerCertificate=True"

// Azure SQL
"Server=tcp:yourserver.database.windows.net,1433;Database=EverTaskDb;User ID=yourusername;Password=yourpassword;Encrypt=True"
```

**Required Permissions:**
- Database create (if auto-creating database)
- Schema create (if using custom schema)
- Table create/alter (for migrations)
- Read/write on QueuedTasks, StatusAudit, RunsAudit tables

## Migrations

### Existing Migrations
1. `20231111185846_Initial` - Initial schema (QueuedTasks, StatusAudit tables)
2. `20231116013050_AddScheduledDateTimeOffset` - Add scheduled task support
3. `20231121002815_AddRunsAudits` - Add RunsAudit table for execution history
4. `20231123234359_RenameStatusAudit` - Rename StatusAudit table
5. `20240408232125_AddRunUntil` - Add RunUntil column to RunsAudit

### Generating New Migrations

**Prerequisites:**
- Navigate to project directory: `src/Storage/EverTask.Storage.SqlServer/`
- Ensure `TaskStoreEfDbContextFactory` is enabled (DEBUG builds only)

**Commands:**
```bash
# Add new migration
dotnet ef migrations add MigrationName

# Remove last migration (if not applied)
dotnet ef migrations remove

# Generate SQL script for migration
dotnet ef migrations script

# Apply migrations manually
dotnet ef database update
```

**Schema-Aware Migration Pattern:**
All migrations MUST include a constructor accepting `ITaskStoreDbContext` to support custom schema names:

```csharp
public partial class MigrationName : Migration
{
    private readonly ITaskStoreDbContext _dbContext;

    public MigrationName(ITaskStoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!string.IsNullOrEmpty(_dbContext.Schema))
            migrationBuilder.EnsureSchema(name: _dbContext.Schema);

        migrationBuilder.CreateTable(
            name: "TableName",
            schema: _dbContext.Schema,  // Use _dbContext.Schema everywhere
            columns: table => new { /* ... */ }
        );
    }
}
```

This pattern is enforced by `DbSchemaAwareMigrationAssembly`, which intercepts migration creation and injects `ITaskStoreDbContext` via reflection.

### Migration Workflow

1. **Auto-Apply (Production):** Set `AutoApplyMigrations = true` (default). Migrations apply on app startup.
2. **Manual Apply (Production):** Set `AutoApplyMigrations = false`. Use `dotnet ef database update` or call `dbContext.Database.Migrate()` manually.
3. **CI/CD:** Generate migration SQL scripts via `dotnet ef migrations script` and apply via deployment pipelines.

## SQL Server Optimizations

**Provider-Specific Features:**
- Uses `uniqueidentifier` (GUID) for primary keys
- Uses `datetimeoffset` for UTC timestamps (preserves timezone info)
- Uses `nvarchar(max)` for JSON serialized task requests
- Uses `nvarchar(500)` for Type/Handler names (indexed)
- Uses `nvarchar(15)` for Status enum (string conversion with max length)
- Uses `bigint` identity columns for audit table primary keys
- Supports `MultipleActiveResultSets=true` for concurrent operations

**Indexing Strategy:**
- Status column indexed for efficient queue queries (e.g., finding Pending tasks)
- Foreign key columns indexed for cascade delete performance
- Non-unique indexes to support multiple tasks with same status

**Concurrency:**
- DbContext is scoped per HTTP request or per task execution
- No optimistic concurrency tokens (tasks are single-write after creation)
- Relies on SQL Server row-level locking for queue dequeue operations

## Testing Considerations

### Docker Container for Testing

SQL Server storage tests require a running SQL Server instance. Use Docker for local testing:

```bash
# Start SQL Server container
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong@Passw0rd" \
  -p 1433:1433 --name sqlserver-evertask \
  -d mcr.microsoft.com/mssql/server:2022-latest

# Connection string for tests
"Server=localhost,1433;Database=EverTaskTestDb;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"
```

**LocalDB Alternative:**
For Windows environments, use LocalDB (no Docker required):

```bash
# Connection string
"Server=(localdb)\\mssqllocaldb;Database=EverTaskTestDb;Trusted_Connection=True;MultipleActiveResultSets=true"
```

### Test Structure

See `test/EverTask.Tests.Storage/SqlServerEfCoreTaskStorageTests.cs`:

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(SqlServerEfCoreTaskStorageTests).Assembly))
    .AddSqlServerStorage(connectionString, opt => opt.AutoApplyMigrations = true);
```

**Test Database Cleanup:**
- Use Respawn library to reset database between tests (preserves `__EFMigrationsHistory`)
- Or manually remove entities via `DbContext.RemoveRange()`

### Running Tests

```bash
# Run all storage tests (requires SQL Server running)
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj

# Exclude SQL Server tests
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj \
  --filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"
```

## Configuration Examples

### Basic Registration
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True");
```

### Custom Schema
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True", opt =>
    {
        opt.SchemaName = "BackgroundTasks";  // Use custom schema instead of "EverTask"
    });
```

### Manual Migrations
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage("Server=localhost;Database=EverTaskDb;Trusted_Connection=True", opt =>
    {
        opt.AutoApplyMigrations = false;  // Don't auto-apply migrations on startup
    });

// Apply migrations manually elsewhere
var dbContext = serviceProvider.GetRequiredService<SqlServerTaskStoreContext>();
dbContext.Database.Migrate();
```

## Package Dependencies

- `Microsoft.EntityFrameworkCore.SqlServer` - SQL Server provider
- `Microsoft.EntityFrameworkCore.Relational` - Relational database abstractions
- `Microsoft.EntityFrameworkCore.Tools` - EF Core CLI tools (for migrations)
- `EverTask.Storage.EfCore` (project reference) - Base EF Core storage implementation
- `EverTask` (project reference) - Core library

## Target Frameworks

- .NET 6.0
- .NET 7.0
- .NET 8.0

Package versions managed via `Directory.Packages.props` with conditional versioning per target framework.

## Common Issues

**Issue:** Migration fails with "Invalid object name 'QueuedTasks'"
**Solution:** Ensure connection string has permissions to create tables. Check schema name matches migrations.

**Issue:** "The migrations configuration type 'XYZ' was not found"
**Solution:** Ensure `TaskStoreEfDbContextFactory` exists and is enabled (DEBUG builds). Rebuild project.

**Issue:** Custom schema not applied
**Solution:** Verify `SchemaName` is set in options. Regenerate migrations if changing schema after initial migration.

**Issue:** "Login failed for user" in tests
**Solution:** Ensure SQL Server container/instance is running. Verify connection string credentials. Use `TrustServerCertificate=True` for self-signed certs.

**Issue:** Slow startup with AutoApplyMigrations
**Solution:** Set `AutoApplyMigrations = false` and apply migrations separately (e.g., via deployment script or database initializer).
