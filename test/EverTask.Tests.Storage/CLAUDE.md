# EverTask.Tests.Storage

## Purpose

Integration tests for all EverTask storage implementations (InMemory, Sqlite, SqlServer). Verifies `ITaskStorage` contract for task persistence, status transitions, scheduling, audit trails.

**End-to-end recovery against real SQL Server**: `SqlServerRecoveryIntegrationTests.cs` (Docker, `DatabaseTests` collection) exercises the **concurrent recovery flow** (WorkerService + consumers + dispatcher + scheduler) against real storage — backlog > capacity without deadlock/loss, `WaitingQueue` recovery across restart, recurring revival preserving `NextRunUtc`. This is the real-DB counterpart of the memory-backed `QueueResilienceIntegrationTests` in `EverTask.Tests`. The `RetrievePending` recoverable-status filter is covered for all three providers by the recovery-filter section in `EfCoreTaskStorageTestsBase.cs`.

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

### Docker IS available on this Windows dev machine — verify, don't assume

Do **NOT** report "Docker is missing" without checking first. This box runs Docker Desktop (WSL2, **Linux engine**), which is exactly what the SQL Server image needs.

**Verify before running** (all should succeed):
```pwsh
docker info --format '{{.OSType}}'   # -> linux  (engine in Linux-container mode)
docker images mcr.microsoft.com/mssql/server:2022-latest   # image pre-pulled (~1.67 GB)
```

- `mcr.microsoft.com/mssql/server:2022-latest` is a **Linux** container — runs fine because the engine is in Linux mode. No "Windows containers" switch needed.
- Active Docker context is `desktop-linux` (npipe `//./pipe/dockerDesktopLinuxEngine`). **Testcontainers auto-discovers it** from `~/.docker/config.json` — do **NOT** set `DOCKER_HOST`.
- First run pulls the image (slow, ~1.67 GB); subsequent runs reuse it. Pre-pull with `docker pull mcr.microsoft.com/mssql/server:2022-latest` if needed.

**Verified run** (net9.0, filtered by class to dodge the blame-hang; ~14 s for 66 + ~26 s for 3):
```pwsh
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj -c Release -f net9.0 --filter "FullyQualifiedName~SqlServer"
```

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
