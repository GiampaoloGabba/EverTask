# EverTask.Storage.EfCore

Refer to the root CLAUDE.md for project-wide rules.

Base EF Core storage for every relational provider; never used standalone.

- **Contexts come from a POOLED factory** (`ITaskStoreDbContextFactory` over `AddPooledDbContextFactory`), one
  leased per operation, never shared. A pooled context admits only the single `DbContextOptions<T>` ctor
  parameter, so the schema travels in the options via `UseEverTaskSchema` (`EverTaskSchemaExtension`) instead
  of being injected. No `IServiceScopeFactory.CreateScope()` exists in this layer.
- `RetrievePending` is the startup recovery filter — whatever it excludes is silently lost on restart, so
  `WaitingQueue` (persisted, not yet handed to a channel) and recurring `Completed`/`Failed` rows with
  `NextRunUtc != null` must stay in it.
- **A new recoverable status must be added in FOUR places**: `QueuedTask.IsRecoverable` (canonical, in
  `src/EverTask/Storage/QueuedTask.cs`), `EfCoreTaskStorage.RecoverableQuery` (EF-translatable mirror), the
  inline list in `SqliteTaskStorage.RetrievePending`, and a NEW Postgres migration — the partial index
  `IX_QueuedTasks_Recovery` bakes `WHERE Status IN (…)` into `Migrations/20260616162141_Initial.cs`, so a
  missing status makes Postgres recovery skip those rows silently. `MemoryTaskStorage` keeps no copy.
- **The base is never constrained by SQLite**: it always carries the optimized server-side form
  (`BatchDeleteAsync` bounded by `CleanupBatchSize`, `GROUP BY`/`HAVING`, ordered offsets). What SQLite cannot
  translate (`DateTimeOffset` ordering comparisons) is worked around by an `override` there — 9 today — never
  pushed down here. SQL Server and Postgres override 3 hot writes, MySQL those 3 plus `CleanupCompletedTasks`.
- `AuditCleanupHostedService` only interprets the retention policy — any knob `<= 0` is disabled, and an
  active log retention makes `CleanupCompletedTasks` preserve tasks that still own execution logs.
- A new `ITaskStorage` method goes in `test/EverTask.Tests.Storage/EfCore/EfCoreTaskStorageTestsBase.cs` and
  then runs on all four providers. New provider: use the `new-relational-storage-provider` skill.
