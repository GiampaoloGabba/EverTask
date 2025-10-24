# EverTask.Tests.Storage

## Purpose

Integration tests for all EverTask storage implementations (InMemory, Sqlite, SqlServer). Verifies `ITaskStorage` contract for task persistence, status transitions, scheduling, audit trails.

## Test Architecture

**Base Class**: `EfCore/EfCoreTaskStorageTestsBase.cs` — Defines comprehensive test suite running identically across all providers.

**Provider Test Classes** (inherit from base):

| Class | Provider | Prerequisites | Cleanup Strategy |
|-------|----------|---------------|------------------|
| `InMemoryEfCoreTaskStorageTests` | EF Core InMemory | None (fastest) | New DB per test class |
| `SqliteEfCoreTaskStorageTests` | SQLite file | None | Manual `RemoveRange()` |
| `SqlServerEfCoreTaskStorageTests` | SQL Server LocalDB | SQL Server installed | Respawn library |

## Quick Commands

**All storage tests**:
```bash
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj
```

**Specific provider**:
```bash
# InMemory (fastest, no dependencies)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~InMemoryEfCoreTaskStorageTests"

# SQLite (file-based, minimal setup)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqliteEfCoreTaskStorageTests"

# SQL Server (production-like, schema support)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqlServerEfCoreTaskStorageTests"

# Exclude SQL Server (no Docker/LocalDB available)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"
```

## Connection String Configuration

**SQL Server Tests**: Use connection string from:
1. Environment variable: `EVERTASK_SQL_CONNECTION_STRING`
2. Fallback: `Server=(localdb)\\mssqllocaldb;Database=EverTaskTests;Integrated Security=True`

**Set via environment variable**:
```bash
# Windows (PowerShell)
$env:EVERTASK_SQL_CONNECTION_STRING="Server=localhost,1433;Database=EverTaskTests;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"

# Linux/macOS
export EVERTASK_SQL_CONNECTION_STRING="Server=localhost,1433;Database=EverTaskTests;User Id=sa;Password=YourStrong@Passw0rd;TrustServerCertificate=True"
```

## Adding Tests

**When adding new `ITaskStorage` method**:

- [ ] Add test to `EfCoreTaskStorageTestsBase.cs` using `[Fact]` attribute
- [ ] Test automatically runs for ALL providers (no changes needed in provider-specific classes)
- [ ] Verify with: `dotnet test test/EverTask.Tests.Storage/`

**Example**:
```csharp
// In EfCoreTaskStorageTestsBase.cs
[Fact]
public async Task Should_get_pending_tasks()
{
    // Arrange
    var task = QueuedTasks.First();
    await Storage.AddTask(task);

    // Act
    var pending = await Storage.GetPendingTasks();

    // Assert
    pending.ShouldContain(t => t.Id == task.Id);
}
```

**When adding provider-specific behavior test**:
- Add to specific provider class (e.g., `SqlServerEfCoreTaskStorageTests.cs` for schema verification)

## IMPORTANT: Updating Base Class

**When adding new `ITaskStorage` method**, MUST update `EfCoreTaskStorageTestsBase` to avoid orphaned tests.

**Checklist**:
- [ ] Add method to `ITaskStorage` interface
- [ ] Implement in `EfCoreTaskStorage`
- [ ] Add test to `EfCoreTaskStorageTestsBase`
- [ ] Run all provider tests to verify

## Known Limitations

| Provider | Limitation | Workaround |
|----------|------------|------------|
| **SQLite** | DateTimeOffset ordering issues in some queries | Use `.ToList().OrderBy()` instead of `.OrderBy()` in LINQ |
| **SQLite** | Concurrent write tests may fail under heavy load | Expected behavior (single writer limitation) |
| **InMemory** | No schema support | Tests using schema-specific features skip InMemory |

## Test Data

**Sample Data**: `QueuedTasks` property provides 2 test tasks in base class.

**Cleanup**: Each test class handles cleanup differently (see table above).
