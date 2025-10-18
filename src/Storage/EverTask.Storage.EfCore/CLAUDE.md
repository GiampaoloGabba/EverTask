# EverTask.Storage.EfCore

Base EF Core storage implementation for EverTask. This package provides abstract infrastructure for relational database persistence, extended by provider-specific implementations (SqlServer, Sqlite).

## Project Purpose

EverTask.Storage.EfCore implements the `ITaskStorage` interface using Entity Framework Core for relational databases. It serves as the foundation for provider-specific implementations, handling:

- Task persistence with full audit trails (status changes and execution runs)
- Task retrieval with status-based filtering
- Thread-safe database operations via scoped DbContext per operation
- Schema-aware migrations for multi-tenant scenarios

This is a base library - it cannot be used standalone. Applications must use provider-specific packages (EverTask.Storage.SqlServer or EverTask.Storage.Sqlite).

## Architecture

### Core Files

**EfCoreTaskStorage.cs** - Main storage implementation
- Implements `ITaskStorage` interface from EverTask.Abstractions
- Creates a new service scope and DbContext instance per operation (thread-safe)
- Uses `IServiceScopeFactory` to resolve scoped `ITaskStoreDbContext` for each database operation
- All queries use `.AsNoTracking()` for read performance (no change tracking overhead)
- Handles task persistence, status updates, and auditing

**TaskStoreEfDbContext.cs** - Abstract base DbContext
- Generic abstract class: `TaskStoreEfDbContext<T> where T : DbContext`
- Configures entity mappings using Fluent API in `OnModelCreating`
- Supports schema customization via `ITaskStoreOptions.SchemaName`
- Enforces string conversion for enums (stores `QueuedTaskStatus` as `nvarchar(15)`)
- Configures cascade deletes for audit tables
- Creates indexes on `Status` and foreign key columns for query performance

**ITaskStoreDbContext.cs** - DbContext abstraction interface
- Exposes `DbSet<QueuedTask>`, `DbSet<StatusAudit>`, `DbSet<RunsAudit>`
- Provides `Schema` property for schema-aware migrations
- Allows provider-specific DbContext implementations to be used interchangeably

**ITaskStoreOptions.cs** - Configuration interface
- `AutoApplyMigrations`: Whether to run `Database.Migrate()` on startup
- `SchemaName`: Database schema name (defaults to null for default schema)

## Database Schema

### Entities

**QueuedTask** - Primary task entity (defined in `src/EverTask/Storage/QueuedTask.cs`)
```csharp
public class QueuedTask
{
    Guid Id                        // Primary key
    DateTimeOffset CreatedAtUtc    // Task creation timestamp
    DateTimeOffset? LastExecutionUtc // Last completed/failed execution
    DateTimeOffset? ScheduledExecutionUtc // Delayed execution time
    string Type                    // AssemblyQualifiedName of IEverTask implementation (max 500 chars)
    string Request                 // JSON-serialized task request (nvarchar(max))
    string Handler                 // AssemblyQualifiedName of handler (max 500 chars)
    string? Exception              // Detailed exception string from last failure
    bool IsRecurring               // Whether task repeats
    string? RecurringTask          // JSON-serialized RecurringTask configuration
    string? RecurringInfo          // Human-readable schedule description
    int? CurrentRunCount           // Execution counter
    int? MaxRuns                   // Maximum execution limit
    DateTimeOffset? RunUntil       // Task expiration time
    DateTimeOffset? NextRunUtc     // Next scheduled execution for recurring tasks
    QueuedTaskStatus Status        // Current status (stored as string)

    ICollection<StatusAudit> StatusAudits   // Audit trail of status changes
    ICollection<RunsAudit> RunsAudits       // Audit trail of executions
}
```

**StatusAudit** - Status change audit trail
```csharp
public class StatusAudit
{
    long Id                        // Auto-increment primary key
    Guid QueuedTaskId              // Foreign key to QueuedTask
    DateTimeOffset UpdatedAtUtc    // Status change timestamp
    QueuedTaskStatus NewStatus     // New status value (stored as string)
    string? Exception              // Exception details if status change due to error

    QueuedTask QueuedTask          // Navigation property
}
```

**RunsAudit** - Execution run audit trail
```csharp
public class RunsAudit
{
    long Id                        // Auto-increment primary key
    Guid QueuedTaskId              // Foreign key to QueuedTask
    DateTimeOffset ExecutedAt      // Execution timestamp
    QueuedTaskStatus Status        // Status after execution (stored as string)
    string? Exception              // Exception details if execution failed

    QueuedTask QueuedTask          // Navigation property
}
```

### QueuedTaskStatus Enum

Stored as string (max 15 chars) in database:
- `WaitingQueue` - Task dispatched but not yet persisted
- `Queued` - Persisted and ready for execution
- `InProgress` - Currently executing
- `Pending` - Scheduled for future execution
- `Cancelled` - Cancelled by user
- `Completed` - Successfully executed
- `Failed` - Execution failed
- `ServiceStopped` - Service shutdown during execution

### Indexes

- `QueuedTask.Status` - Non-unique index for status-based queries
- `StatusAudit.QueuedTaskId` - Non-unique index for audit queries
- `RunsAudit.QueuedTaskId` - Non-unique index for audit queries

### Relationships

- `QueuedTask` 1-to-many `StatusAudit` (cascade delete)
- `QueuedTask` 1-to-many `RunsAudit` (cascade delete)

## Serialization Strategy

### Task Serialization

Tasks are serialized using **Newtonsoft.Json** (not System.Text.Json) for robust polymorphism support.

**Serialization Point**: `TaskHandlerExecutor.ToQueuedTask()` in `src/EverTask/Handler/TaskHandlerExecutor.cs`

```csharp
var request = JsonConvert.SerializeObject(executor.Task);  // IEverTask instance
var requestType = executor.Task.GetType().AssemblyQualifiedName;
var handlerType = executor.Handler.GetType().AssemblyQualifiedName;
```

**Deserialization Point**: `WorkerExecutor.cs` retrieves `QueuedTask.Request` (JSON string) and deserializes using type information from `QueuedTask.Type`.

**Storage Format**:
- `QueuedTask.Type`: `"MyApp.Tasks.SendEmailTask, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"`
- `QueuedTask.Request`: `{"To":"user@example.com","Subject":"Hello"}`
- `QueuedTask.Handler`: `"MyApp.Handlers.SendEmailHandler, MyApp, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"`

### Recurring Task Serialization

If task is recurring, `RecurringTask` object is serialized:
- `QueuedTask.RecurringTask`: JSON-serialized `RecurringTask` configuration (cron expression, interval, etc.)
- `QueuedTask.RecurringInfo`: Human-readable string (e.g., "Every 5 minutes")
- `QueuedTask.NextRunUtc`: Calculated next execution time

### Exception Serialization

Exceptions are serialized using `ExceptionExtensions.ToDetailedString()` which produces a formatted multi-line string containing:
- Exception type full name
- Message
- Stack trace
- Data dictionary
- Inner exceptions (nested with indentation)
- Aggregate exception inner exceptions

Stored in `QueuedTask.Exception`, `StatusAudit.Exception`, and `RunsAudit.Exception` as `nvarchar(max)`.

## Thread Safety and DbContext Scoping

### Per-Operation Scoping

Every database operation creates a new service scope and resolves a fresh `ITaskStoreDbContext`:

```csharp
public async Task Persist(QueuedTask taskEntity, CancellationToken ct = default)
{
    using var scope = serviceScopeFactory.CreateScope();
    await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

    dbContext.QueuedTasks.Add(taskEntity);
    await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
}
```

**Why**: EF Core DbContext is not thread-safe. Since `WorkerExecutor` executes tasks concurrently, each task execution must use its own DbContext instance. See `src/EverTask/Worker/WorkerExecutor.cs:25` where each task handler gets a scoped service provider.

### No Change Tracking on Reads

All read queries use `.AsNoTracking()` to disable EF Core's change tracking:

```csharp
return await dbContext.QueuedTasks
    .AsNoTracking()
    .Where(where)
    .ToArrayAsync(ct)
    .ConfigureAwait(false);
```

**Why**: Tasks are read, executed externally, then status is updated in a separate operation. No need to track changes from read to update - reduces memory overhead and improves query performance.

## Key Operations

### Persist Task

```csharp
public async Task Persist(QueuedTask taskEntity, CancellationToken ct = default)
```

Inserts new task into database. Called by `Dispatcher` after creating `TaskHandlerExecutor.ToQueuedTask()`.

### Retrieve Pending Tasks

```csharp
public async Task<QueuedTask[]> RetrievePending(CancellationToken ct = default)
```

Retrieves tasks eligible for execution based on:
- `MaxRuns == null || CurrentRunCount <= MaxRuns` - Not exceeded run limit
- `RunUntil == null || RunUntil >= DateTimeOffset.UtcNow` - Not expired
- Status is `Queued`, `Pending`, `ServiceStopped`, or `InProgress`

Called by `WorkerExecutor` on startup and periodically to load scheduled tasks.

### Set Status

```csharp
public async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null, CancellationToken ct = default)
```

Updates task status and creates audit record:
1. Calls `Audit()` to insert `StatusAudit` record
2. Updates `QueuedTask.Status`, `QueuedTask.LastExecutionUtc`, `QueuedTask.Exception`
3. `LastExecutionUtc` is set to `DateTimeOffset.UtcNow` for terminal states (Completed, Failed, ServiceStopped)

**Note**: Uses separate `SaveChangesAsync()` calls for audit vs task update. Audit failures are logged as warnings but don't fail the operation.

### Update Current Run

```csharp
public async Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun)
```

Increments run counter for recurring tasks:
1. Creates `RunsAudit` record with current execution details
2. Updates `QueuedTask.NextRunUtc` with calculated next execution time
3. Increments `QueuedTask.CurrentRunCount`

Called by `WorkerExecutor` after each recurring task execution.

## Query Patterns

### Status-Based Filtering

```csharp
var tasks = await storage.Get(t => t.Status == QueuedTaskStatus.Failed);
```

Uses indexed `Status` column for efficient filtering.

### Lambda Expression Support

`Get()` method accepts `Expression<Func<QueuedTask, bool>>` for flexible querying:

```csharp
public async Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
```

### GetAll

```csharp
public async Task<QueuedTask[]> GetAll(CancellationToken ct = default)
```

Retrieves all tasks without filtering. Use sparingly - no pagination support.

## Migration Strategy

### Provider-Specific Migrations

Migrations are stored in provider-specific projects (SqlServer, Sqlite), not in this base project.

**Why**: SQL syntax differs between providers (e.g., `uniqueidentifier` vs `TEXT` for Guid, `IDENTITY` vs `AUTOINCREMENT` for auto-increment).

### Schema-Aware Migrations (SQL Server Only)

SQL Server supports schema-aware migrations via `DbSchemaAwareMigrationAssembly` (see `src/Storage/EverTask.Storage.SqlServer/DbSchemaAwareMigrationAssembly.cs`).

**Mechanism**:
1. `DbSchemaAwareMigrationAssembly` extends `MigrationsAssembly`
2. Overrides `CreateMigration()` to inject `ITaskStoreDbContext` into migration constructor
3. Migrations accept `ITaskStoreDbContext` constructor parameter to access `Schema` property
4. Migration's `Up()` method uses `_dbContext.Schema` for dynamic schema names

**Example** (from Initial migration):
```csharp
public partial class Initial : Migration
{
    private readonly ITaskStoreDbContext _dbContext;

    public Initial(ITaskStoreDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!string.IsNullOrEmpty(_dbContext.Schema))
            migrationBuilder.EnsureSchema(name: _dbContext.Schema);

        migrationBuilder.CreateTable(
            name: "QueuedTasks",
            schema: _dbContext.Schema,  // Dynamic schema
            // ...
        );
    }
}
```

**Why Sqlite Doesn't Support This**: Sqlite doesn't have schema support. All tables use default schema.

### Auto-Apply Migrations

Controlled by `ITaskStoreOptions.AutoApplyMigrations`:

```csharp
if (storeOptions.AutoApplyMigrations)
{
    using var scope = builder.Services.BuildServiceProvider().CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<SqlServerTaskStoreContext>();
    dbContext.Database.Migrate();
}
```

Runs during `AddSqlServerStorage()` registration in `ServiceCollectionExtensions.cs`.

**Warning**: Auto-migration during startup can cause issues in multi-instance deployments. Recommended to run migrations manually in production.

## Extension Points for Provider Implementations

### 1. Create Provider-Specific DbContext

```csharp
public class SqlServerTaskStoreContext(
    DbContextOptions<SqlServerTaskStoreContext> options,
    IOptions<ITaskStoreOptions> storeOptions)
    : TaskStoreEfDbContext<SqlServerTaskStoreContext>(options, storeOptions);
```

Inherit from `TaskStoreEfDbContext<T>` with concrete type parameter. No additional configuration needed - base class handles all entity mappings.

### 2. Create Provider-Specific Options

```csharp
public class SqlServerTaskStoreOptions : ITaskStoreOptions
{
    public bool AutoApplyMigrations { get; set; } = false;
    public string? SchemaName { get; set; } = "EverTask";
}
```

Implement `ITaskStoreOptions` with provider-specific defaults.

### 3. Register with DI

```csharp
public static EverTaskServiceBuilder AddSqlServerStorage(
    this EverTaskServiceBuilder builder,
    string connectionString,
    Action<SqlServerTaskStoreOptions>? configure = null)
{
    // Configure options
    builder.Services.Configure<SqlServerTaskStoreOptions>(options => { /* ... */ });

    // Register options as ITaskStoreOptions
    builder.Services.AddTransient<IOptions<ITaskStoreOptions>>(sp =>
        sp.GetRequiredService<IOptions<SqlServerTaskStoreOptions>>());

    // Register DbContext with provider
    builder.Services.AddDbContext<SqlServerTaskStoreContext>((_, opt) =>
        opt.UseSqlServer(connectionString, /* migration options */));

    // Register as ITaskStoreDbContext
    builder.Services.AddScoped<ITaskStoreDbContext>(provider =>
        provider.GetRequiredService<SqlServerTaskStoreContext>());

    // Register storage implementation
    builder.Services.TryAddSingleton<ITaskStorage, EfCoreTaskStorage>();

    return builder;
}
```

### 4. Generate Migrations

```bash
# From provider project directory
dotnet ef migrations add MigrationName --context SqlServerTaskStoreContext
```

For schema-aware migrations (SQL Server), migrations must accept `ITaskStoreDbContext` constructor parameter.

### 5. Create Design-Time DbContext Factory (Optional)

For `dotnet ef` tooling support:

```csharp
public class TaskStoreEfDbContextFactory : IDesignTimeDbContextFactory<SqlServerTaskStoreContext>
{
    public SqlServerTaskStoreContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<SqlServerTaskStoreContext>();
        optionsBuilder.UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=EverTask");

        var storeOptions = Options.Create<ITaskStoreOptions>(new SqlServerTaskStoreOptions());
        return new SqlServerTaskStoreContext(optionsBuilder.Options, storeOptions);
    }
}
```

## Common Patterns When Working With Storage

### Adding New Entity Properties

1. Update `QueuedTask`, `StatusAudit`, or `RunsAudit` class in `src/EverTask/Storage/QueuedTask.cs`
2. Update entity configuration in `TaskStoreEfDbContext.OnModelCreating()` if constraints needed
3. Generate migration in each provider project:
   ```bash
   cd src/Storage/EverTask.Storage.SqlServer
   dotnet ef migrations add AddNewProperty --context SqlServerTaskStoreContext

   cd ../EverTask.Storage.Sqlite
   dotnet ef migrations add AddNewProperty --context SqliteTaskStoreContext
   ```

### Adding Indexes

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
{
    base.OnModelCreating(modelBuilder);

    modelBuilder.Entity<QueuedTask>()
        .HasIndex(q => q.CreatedAtUtc)
        .IsUnique(false);
}
```

Then generate migration.

### Custom Queries

Add methods to `EfCoreTaskStorage`:

```csharp
public async Task<QueuedTask[]> GetFailedTasksOlderThan(DateTimeOffset threshold)
{
    using var scope = serviceScopeFactory.CreateScope();
    await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

    return await dbContext.QueuedTasks
        .AsNoTracking()
        .Where(t => t.Status == QueuedTaskStatus.Failed
                 && t.LastExecutionUtc < threshold)
        .ToArrayAsync()
        .ConfigureAwait(false);
}
```

Add corresponding interface method to `ITaskStorage` if needed by consumers.

## Testing Notes

- Use InMemory storage (`EverTask.Storage.Memory`) for unit tests - no database required
- Use Sqlite for integration tests - file-based, no container required
- Use SQL Server for integration tests requiring specific SQL Server features (requires Docker container)
- Test project excludes SQL Server tests by default: `dotnet test --filter FullyQualifiedName!~SqlServerEfCoreTaskStorageTests`

## Dependencies

- **Microsoft.EntityFrameworkCore** - Core EF abstraction
- **Microsoft.EntityFrameworkCore.Relational** - Relational database support
- **Microsoft.Extensions.DependencyInjection.Abstractions** - DI container abstractions
- **EverTask** - Core library (ITaskStorage, QueuedTask entities, logging)

## Related Projects

- **EverTask.Storage.SqlServer** - SQL Server implementation with schema-aware migrations
- **EverTask.Storage.Sqlite** - Sqlite implementation for file-based or in-memory databases
- **EverTask** - Core library defining `ITaskStorage` interface and entity models
