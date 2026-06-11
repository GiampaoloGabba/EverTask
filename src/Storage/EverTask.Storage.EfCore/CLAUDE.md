# EverTask.Storage.EfCore

## Purpose

Base EF Core storage for EverTask. **Cannot be used standalone** — extended by provider-specific packages (SqlServer, Sqlite).

**Target Frameworks**: net6.0, net7.0, net8.0, net9.0

## Key Components

| Component | Purpose |
|-----------|---------|
| `TaskStoreEfDbContext<T>` | Abstract generic DbContext (`QueuedTasks`, `StatusAudit`, `RunsAudit`) |
| `EfCoreTaskStorage` | `ITaskStorage` implementation (singleton wrapping scoped DbContext factory) |
| `ITaskStoreDbContext` | Interface exposing `DbSet<>` properties + `Schema` property |

**Database Schema**:
- `QueuedTasks` — Main queue (PK: `Id` Guid)
- `StatusAudit` — Status history (PK: auto-increment, FK: `QueuedTaskId`, cascade delete)
- `RunsAudit` — Execution history (PK: auto-increment, FK: `QueuedTaskId`, cascade delete)
- **Indexes**: `QueuedTasks.Status`, `StatusAudit.QueuedTaskId`, `RunsAudit.QueuedTaskId` (non-unique)

## Critical Gotchas

### DbContext Scoping (Thread Safety)
**CRITICAL**: `EfCoreTaskStorage` is **singleton** but creates **scoped DbContext** per operation.

**WHY**: DbContext is NOT thread-safe. Each concurrent task needs its own instance.

**HOW**: `IServiceScopeFactory.CreateScope()` wraps every storage operation.

**CONSISTENCY**: Same pattern as `WorkerExecutor` (see `src/EverTask/CLAUDE.md`).

### No Change Tracking on Reads
All read queries use `.AsNoTracking()` — tasks are read, executed externally, status updated separately. No tracking = less memory + faster queries.

### RetrievePending = Startup Recovery Filter (no task loss)
`RetrievePending` defines what survives a restart. Recoverable statuses: `WaitingQueue`, `Queued`, `Pending`, `InProgress`, `ServiceStopped`, **plus** recurring tasks (`IsRecurring && NextRunUtc != null`) in `Completed`/`Failed` (revives recurring tasks between runs without re-registration). `WaitingQueue` is essential — it's the status of every task persisted but not yet delivered to a channel (delayed tasks parked in the scheduler, tasks dropped by a full queue); excluding it silently loses them on restart.

**CRITICAL**: the filter is duplicated in `SqliteTaskStorage` (override) and `MemoryTaskStorage` — change all three together. Covered by the recovery-filter section in `EfCoreTaskStorageTestsBase.cs` (runs on all providers).

### Schema-Aware Migrations
SQL Server supports custom schemas via `DbSchemaAwareMigrationAssembly`. Migrations inject `ITaskStoreDbContext` to access `Schema` property dynamically.

**Note**: Sqlite doesn't support schemas (uses empty string).

## Checklist: New EF Core Provider

When creating a new provider (e.g., PostgreSQL, MySQL):

- [ ] **DbContext**: Extend `TaskStoreEfDbContext<TYourContext>`
- [ ] **Options**: Implement `ITaskStoreOptions` with provider defaults
- [ ] **DI Extension**: Create `AddYourProviderStorage()` registering DbContext + ITaskStoreDbContext + ITaskStorage
- [ ] **Migrations**: Generate via `dotnet ef migrations add Initial`
- [ ] **Design-Time Factory**: Implement `IDesignTimeDbContextFactory<TYourContext>` (optional, for EF tools)
- [ ] **Tests**: Add to `test/EverTask.Tests.Storage/` inheriting from `EfCoreTaskStorageTestsBase`

**Example**: See `EverTask.Storage.SqlServer/` for complete reference implementation.

## 🔗 Test Coverage

**When modifying EfCoreTaskStorage**:
- **MUST run ALL provider tests** (verify no regressions):
  ```bash
  dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~InMemoryEfCoreTaskStorageTests"
  dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqliteEfCoreTaskStorageTests"
  dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~SqlServerEfCoreTaskStorageTests"
  ```

**When adding new ITaskStorage method**:
- Update: `test/EverTask.Tests.Storage/EfCore/EfCoreTaskStorageTestsBase.cs`
- Test automatically runs for all providers (InMemory, Sqlite, SqlServer)

**Location**: `test/EverTask.Tests.Storage/`
