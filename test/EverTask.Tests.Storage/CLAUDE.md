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
| `SqlServerEfCoreTaskStorageTests` | SQL Server Testcontainers | Docker | Respawn library |

## Prerequisites

**SQL Server Tests**: Require Docker to run Testcontainers. The container is started automatically and shared across tests in the same collection.

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

# SQL Server (requires Docker)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqlServerEfCoreTaskStorageTests"

# Exclude SQL Server (no Docker available)
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"
```

## Test Collections

| Collection | Tests | Purpose |
|------------|-------|---------|
| `DatabaseTests` | `SqlServerEfCoreTaskStorageTests`, `SqliteEfCoreTaskStorageTests`, `AuditLevelIntegrationTests` | Serialized DB tests sharing Testcontainers |
| `TimingSensitiveTests` | Scheduled/recurring task tests | Serialized to avoid CPU contention |

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

**Cleanup**: Each test class handles cleanup differently (see table above). SQL Server uses Respawn for fast cleanup between tests.
