# EverTask.Storage.Sqlite

## Purpose

SQLite storage implementation. File-based or in-memory persistence for single-server deployments, development, testing.

**Target Frameworks**: net6.0, net7.0, net8.0, net9.0

**Use When**: Single-server, low-medium volume (< 10,000 concurrent tasks), zero infrastructure setup.

## Connection Strings

| Scenario | Connection String |
|----------|-------------------|
| **File (persistent)** | `Data Source=EverTask.db` |
| **Absolute path** | `Data Source=C:\\Data\\EverTask.db` |
| **WAL mode (recommended)** | `Data Source=EverTask.db;Mode=ReadWriteCreate;Cache=Shared` |
| **In-memory (testing)** | `Data Source=:memory:;Mode=Memory;Cache=Shared` |
| **Private in-memory** | `Data Source=:memory:` (single connection only) |

**WAL Mode Activation** (better concurrency):
```csharp
// Option 1: Connection string
.AddSqliteStorage("Data Source=EverTask.db;Mode=ReadWriteCreate;Cache=Shared")

// Option 2: PRAGMA (apply after opening connection)
// SQLite automatically uses WAL if supported, or configure via:
// PRAGMA journal_mode=WAL;
```

## DI Registration

```csharp
// Default (file-based)
.AddSqliteStorage()  // Uses "Data Source=EverTask.db"

// Custom path
.AddSqliteStorage("Data Source=C:\\MyApp\\tasks.db")

// In-memory (testing)
.AddSqliteStorage("Data Source=:memory:;Mode=Memory;Cache=Shared", opt =>
{
    opt.AutoApplyMigrations = true;  // Required for in-memory
});
```

## Critical Gotchas

### 1. No Schema Support
**CRITICAL**: `SqliteTaskStoreOptions.SchemaName` MUST be `""` (empty string).

**Reason**: SQLite has no schema concept (unlike SQL Server's `"EverTask"` schema).

### 2. Concurrency Limitations
**Write Concurrency**: SQLite = multiple readers OR single writer (not both).

**Mitigation**: WAL mode improves concurrency. EF Core handles connection pooling.

**Migrate to SQL Server when**:
- Multi-server deployment (web farms, Kubernetes)
- High task volume (> 10,000 concurrent tasks)
- High write concurrency needed

### 3. Data Type Mappings

| SQL Server | SQLite |
|------------|--------|
| `Guid` | `TEXT` |
| `DateTimeOffset` | `TEXT` (ISO 8601) |
| `bool` | `INTEGER` (0/1) |
| `string` | `TEXT` |

**DateTimeOffset Precision**: SQLite stores as text, may have ordering issues in some queries (see test notes).

## Migrations

**Auto-apply** (default):
```csharp
.AddSqliteStorage(connectionString, opt => opt.AutoApplyMigrations = true);
```

**Manual**:
```bash
cd src/Storage/EverTask.Storage.Sqlite/
dotnet ef migrations add MigrationName
dotnet ef database update
```

**Migration History**: `__EFMigrationsHistory` (no schema prefix).

## Migration Path to SQL Server

Schema compatible, but **manual data migration required**:

```csharp
// Before
.AddSqliteStorage("Data Source=EverTask.db")

// After
.AddSqlServerStorage("Server=localhost;Database=EverTask;...")
```

**Data migration**: Export from SQLite, import to SQL Server (manual process, schema matches).

## 🔗 Test Coverage

**Run SQLite tests**:
```bash
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqliteEfCoreTaskStorageTests"
```

**Location**: `test/EverTask.Tests.Storage/SqliteEfCoreTaskStorageTests.cs`

**Known Test Limitations**:
- DateTimeOffset ordering may require `.ToList().OrderBy()` workaround in some queries
- Some concurrent write tests may fail under heavy load (by design)
