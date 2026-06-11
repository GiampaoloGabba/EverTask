# EverTask.Storage.SqlServer

## Purpose

SQL Server storage implementation. Extends `EverTask.Storage.EfCore` with schema-aware migrations and SQL Server optimizations.

**Target Frameworks**: net6.0, net7.0, net8.0, net9.0

## Quick Start

**Docker (testing)**:
```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong@Passw0rd" \
  -p 1433:1433 --name sqlserver-evertask \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

**Connection Strings**:

| Scenario | Connection String |
|----------|-------------------|
| Docker/TCP | `Server=localhost,1433;Database=EverTaskDb;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True` |
| Integrated Security | `Server=localhost;Database=EverTaskDb;Integrated Security=True;TrustServerCertificate=True` |
| Named Instance | `Server=localhost\SQLEXPRESS;Database=EverTaskDb;Integrated Security=True;TrustServerCertificate=True` |
| LocalDB | `Server=(localdb)\mssqllocaldb;Database=EverTaskDb;Integrated Security=True` |

**CI/CD Environment Variables** (recommended for pipelines):
```bash
# Azure DevOps / GitHub Actions
EVERTASK_SQL_CONNECTION_STRING="Server=..."
EVERTASK_SQL_PASSWORD="$(SecretVariable)"  # Use secret management

# appsettings.json reference
"ConnectionStrings": {
  "EverTask": "Server=...;Password=${EVERTASK_SQL_PASSWORD};..."
}
```

## DI Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString, opt =>
    {
        opt.SchemaName = "EverTask";        // Default: "EverTask"
        opt.AutoApplyMigrations = true;     // Default: true
    });
```

## Database Schema

**Default Schema**: `EverTask` (configurable via `SqlServerTaskStoreOptions.SchemaName`)

**SQL Server Optimizations**:
- `uniqueidentifier` for Guid PKs
- `datetimeoffset` for UTC timestamps (preserves timezone)
- `nvarchar(max)` for JSON task requests
- `nvarchar(500)` for Type/Handler names (indexed)
- `nvarchar(15)` for Status enum
- `bigint IDENTITY` for audit table PKs

## Schema-Aware Migrations

**CRITICAL**: ALL migrations MUST accept `ITaskStoreDbContext` constructor parameter for custom schema support.

**Pattern**:
```csharp
public partial class MigrationName : Migration
{
    private readonly ITaskStoreDbContext _dbContext;

    public MigrationName(ITaskStoreDbContext dbContext)  // REQUIRED
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!string.IsNullOrEmpty(_dbContext.Schema))
            migrationBuilder.EnsureSchema(name: _dbContext.Schema);

        migrationBuilder.CreateTable(
            name: "TableName",
            schema: _dbContext.Schema,  // Use everywhere
            // ...
        );
    }
}
```

**Enforcement**: `DbSchemaAwareMigrationAssembly` injects `ITaskStoreDbContext` via reflection.

**Raw SQL migrations** (stored procedures, indexes with INCLUDE): interpolate the schema into every object reference, with `dbo` fallback:
```csharp
var schema = string.IsNullOrEmpty(_dbContext.Schema) ? "dbo" : _dbContext.Schema;
migrationBuilder.Sql($@"CREATE PROCEDURE [{schema}].[usp_Name] ... [{schema}].[QueuedTasks] ...");
```
`Down()` must restore the PREVIOUS version of a stored procedure (copy it from the prior migration), not just drop it. Reference examples: `20260111103208_FixStoredProcedureUtcTimezone.cs`, `20260611064213_AddRecoveryIndexAndUpdateRunProcedure.cs`.

## Generating Migrations

**Commands**:
```bash
cd src/Storage/EverTask.Storage.SqlServer/
dotnet ef migrations add MigrationName --framework net9.0
dotnet ef migrations remove              # Remove last (if not applied)
dotnet ef migrations script              # Generate SQL
dotnet ef database update                # Apply manually
```

**Prerequisites**: `TaskStoreEfDbContextFactory` must exist (DEBUG builds only).

**CRITICAL — hand-edit after generation**: `dotnet ef migrations add` generates the migration WITHOUT the `ITaskStoreDbContext` constructor and with hardcoded schema. After generating, ALWAYS:
1. Add the `ITaskStoreDbContext` constructor (see pattern above) to the main `.cs` file — keep the class `partial`
2. Replace any hardcoded `schema: "EverTask"` with `schema: _dbContext.Schema` (or the `[{schema}]` interpolation for raw SQL)
3. Do NOT edit the `.Designer.cs` (auto-generated, no constructor needed — injection targets the migration class)
4. Compare with the previous migration to confirm the pattern matches before committing

## Common Issues

| Issue | Solution |
|-------|----------|
| "Invalid object name 'QueuedTasks'" | Verify connection string permissions + schema name matches migrations |
| "Migrations configuration type not found" | Ensure `TaskStoreEfDbContextFactory` exists (DEBUG builds), rebuild project |
| Custom schema not applied | Verify `SchemaName` set in options, regenerate migrations if schema changed after initial migration |
| "Login failed" in tests | Ensure SQL Server container running, use `TrustServerCertificate=True` for self-signed certs |
| Slow startup with AutoApplyMigrations | Set `AutoApplyMigrations = false`, apply migrations via deployment script or database initializer |

## 🔗 Test Coverage

**Run all storage tests** (requires SQL Server):
```bash
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj
```

**Exclude SQL Server tests**:
```bash
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"
```

**Location**: `test/EverTask.Tests.Storage/SqlServerEfCoreTaskStorageTests.cs`
