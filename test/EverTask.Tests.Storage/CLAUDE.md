# CLAUDE.md - EverTask.Tests.Storage

AI coding agent documentation for the EverTask storage implementation test suite.

## Project Purpose

This project contains integration tests for all EverTask storage implementations: SqlServer, Sqlite, and InMemory. Tests verify that each storage provider correctly implements the `ITaskStorage` contract for task persistence, status transitions, scheduling, and audit trails.

## Test Architecture

### Base Test Class Pattern

All storage tests inherit from `EfCore/EfCoreTaskStorageTestsBase.cs`, which defines a comprehensive test suite that runs identically across all storage providers. This ensures consistent behavior regardless of the underlying database.

**Abstract methods required by implementations:**
- `CreateDbContext()`: Returns provider-specific ITaskStoreDbContext
- `GetStorage()`: Returns provider-specific ITaskStorage instance
- `Initialize()`: Sets up DI container with storage provider
- `CleanUpDatabase()`: Removes test data between test runs

### Test Classes

**InMemoryEfCoreTaskStorageTests.cs**
- Uses EF Core InMemory provider via `TestDbContext`
- Fastest execution, no external dependencies
- Run these tests during rapid development cycles
- No cleanup needed (database recreated per test run)

**SqliteEfCoreTaskStorageTests.cs**
- Uses SQLite file database (`EverTask.db`)
- Tests against real SQL engine with minimal setup
- Implements `IDisposable` to clean up database file
- No schema support (schema property returns empty string)
- Connection: `Data Source=EverTask.db`

**SqlServerEfCoreTaskStorageTests.cs**
- Uses SQL Server LocalDB: `(localdb)\mssqllocaldb`
- Tests full production-like SQL Server behavior
- Verifies schema support (default: "EverTask")
- Uses Respawn library for efficient database cleanup
- Database: `EverTaskTestDb`
- **Note**: While Testcontainers.MsSql is referenced, current implementation uses LocalDB

## Test Coverage

All base tests verify:

**Core Operations:**
- `Get()`: Filter tasks by predicate
- `GetAll()`: Retrieve all tasks
- `Persist()`: Create new queued task
- `RetrievePending()`: Get tasks ready for execution

**Status Transitions (with StatusAudit tracking):**
- `SetQueued()`: Mark task as queued
- `SetInProgress()`: Mark task as executing
- `SetCompleted()`: Mark task as successfully completed
- `SetCancelledByUser()`: User-initiated cancellation
- `SetCancelledByService()`: Service-initiated cancellation with exception storage

**Scheduling:**
- `GetCurrentRunCount()`: Retrieve execution count for recurring tasks
- `UpdateCurrentRun()`: Increment run count and set next scheduled execution

**Audit Trails:**
- StatusAudit entries created on each status change
- RunsAudit entries tracked via `ITaskStoreDbContext`

## Running Tests

### All Storage Tests
```bash
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj
```

### Specific Provider
```bash
# InMemory (fastest)
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj --filter FullyQualifiedName~InMemoryEfCoreTaskStorageTests

# SQLite
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj --filter FullyQualifiedName~SqliteEfCoreTaskStorageTests

# SQL Server
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj --filter FullyQualifiedName~SqlServerEfCoreTaskStorageTests
```

### Prerequisites

**SQL Server Tests:**
- Requires SQL Server LocalDB installed
- Database auto-created via migrations (`AutoApplyMigrations = true`)
- Respawn handles cleanup between tests (preserves `__EFMigrationsHistory`)

**SQLite Tests:**
- No prerequisites (embedded database)
- Database file `EverTask.db` created in test execution directory
- File deleted during test disposal

**InMemory Tests:**
- No prerequisites
- Fastest option for CI/CD pipelines

## Database Test Patterns

### Test Isolation

Each test follows this pattern:
1. Create test data using `QueuedTasks` property (returns 2 sample tasks)
2. Add to DbContext and SaveChanges
3. Execute storage operation under test
4. Assert expected results
5. CleanUp called between tests (not automatic - manual removal in base class)

### Sample Test Data

`QueuedTasks` property provides:
- **Task 1**: InProgress status, Type1/Handler1, scheduled for tomorrow
- **Task 2**: Queued status, Type2/Handler2, scheduled for tomorrow + 1 minute

### Cleanup Strategies

**InMemory**: New database instance per test class (no cleanup needed)

**SQLite**: Manual removal of all records via `CleanUpDatabase()`:
```csharp
_dbContext.RunsAudit.RemoveRange(_dbContext.RunsAudit.ToList());
_dbContext.StatusAudit.RemoveRange(_dbContext.StatusAudit.ToList());
_dbContext.QueuedTasks.RemoveRange(_dbContext.QueuedTasks.ToList());
await _dbContext.SaveChangesAsync(CancellationToken.None);
```

**SQL Server**: Respawn library for efficient truncation (preserves schema):
```csharp
await Respawner.CreateAsync(_connectionString, new RespawnerOptions
{ TablesToIgnore = new Respawn.Graph.Table[] { "__EFMigrationsHistory" } });
```

## Adding Tests for New Storage Features

When adding new `ITaskStorage` methods:

1. **Add test to EfCoreTaskStorageTestsBase.cs**
   - Use `[Fact]` attribute
   - Access storage via `_storage` field
   - Access DbContext via `_mockedDbContext` field
   - Create test data using `QueuedTasks` or custom data
   - Use Shouldly assertions for readability

2. **Test automatically runs for all providers**
   - InMemory, SQLite, and SQL Server tests inherit the new test
   - No changes needed in provider-specific test classes

3. **Provider-specific tests (rare)**
   - Add to individual test class only if testing provider-specific behavior
   - Example: Schema verification in SqlServerEfCoreTaskStorageTests

### Test Example Template

```csharp
[Fact]
public async Task Should_PerformNewOperation()
{
    // Arrange
    var queued = QueuedTasks[0];
    await _storage.Persist(queued);

    // Act
    var result = await _storage.NewMethod(queued.Id);

    // Assert
    result.ShouldNotBeNull();
    result.SomeProperty.ShouldBe(expectedValue);

    // Verify persistence if needed
    var persisted = await _storage.Get(x => x.Id == queued.Id);
    persisted[0].Status.ShouldBe(QueuedTaskStatus.Expected);
}
```

## EfCore Subdirectory

### EfCore/EfCoreTaskStorageTestsBase.cs
Abstract base class defining complete test suite for all storage providers. Contains 15 tests covering all ITaskStorage operations.

### EfCore/TestDbContext.cs
Minimal `DbContext` implementation for InMemory tests. Implements `ITaskStoreDbContext` with hardcoded schema "EverTask" (not used by InMemory provider).

### EfCore/ExceptionExtensionsTests.cs
Unit tests for `ExceptionExtensions.ToDetailedString()` method used to serialize exceptions during task failures. Tests null handling and formatted output.

## Key Dependencies

**Testing Frameworks:**
- xunit: Test runner
- Shouldly: Fluent assertions
- Moq: Mocking (currently unused but available)

**Database:**
- Microsoft.EntityFrameworkCore.SqlServer: SQL Server provider
- Microsoft.EntityFrameworkCore.InMemory: In-memory provider
- Testcontainers.MsSql: Docker-based SQL Server (referenced but not actively used)
- Respawn: Fast database cleanup for integration tests

**Project References:**
- EverTask.Storage.EfCore: Base storage implementation
- EverTask.Storage.SqlServer: SQL Server-specific storage
- EverTask.Storage.Sqlite: SQLite-specific storage

## Notes for AI Agents

**When modifying storage logic:**
1. Run all storage tests to verify cross-provider compatibility
2. SQLite has limitations (e.g., DateTimeOffset ordering requires `.ToList().OrderBy()`)
3. Schema support varies: SQL Server uses "EverTask", SQLite uses empty string
4. InMemory tests are fastest for rapid iteration
5. SQL Server tests verify production-like behavior with migrations and schemas

**When adding storage features:**
1. Update `ITaskStorage` interface in core library
2. Implement in `EverTask.Storage.EfCore/EfCoreTaskStorage.cs`
3. Add test to `EfCoreTaskStorageTestsBase.cs`
4. Test runs automatically for all three providers
5. Verify provider-specific behavior if needed (especially SQL Server vs SQLite)

**Test data creation:**
- Use `QueuedTasks` property for standard scenarios
- Create custom `QueuedTask` instances for edge cases
- Avoid hardcoding GUIDs (use `Guid.NewGuid()`)
- Use `DateTimeOffset.UtcNow` for timestamps (storage uses UTC)

## Subdirectory Documentation

The `EfCore/` subdirectory does not require a separate CLAUDE.md file. It contains only helper classes and the base test class, all documented above.
