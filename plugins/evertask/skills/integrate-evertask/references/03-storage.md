# 03: Storage / persistence

Exactly one storage call is mandatory after `AddEverTask(...)`.

## Decision matrix

| Scenario | Provider | Why |
|---|---|---|
| Local dev, unit/integration tests | **In-Memory** (or SQLite `:memory:`) | Zero infra; tasks lost on restart. |
| Desktop / edge / small single-server app | **SQLite** | File-based, zero infra. |
| Production, scale-out, high write concurrency, multi-instance | **SQL Server** or **PostgreSQL** | ACID, server-side queries, clustering. |
| Existing SQL Server / enterprise DBA stack | **SQL Server** | Stored procs, existing skills. |
| Greenfield OSS stack, no license cost | **PostgreSQL** | Full SQL-Server parity, writable-CTE optimizations. |
| Very large backlogs (>~10k pending) | **SQL Server / PostgreSQL** | SQLite recovery falls back to client-side keyset. |
| Redis / Mongo / Cosmos / DynamoDB | **Custom `ITaskStorage`** | Not built-in; implement + register singleton. |

Per-provider constraints: SQLite = no schema, single writer, client-side `DateTimeOffset`
filtering; Postgres `SchemaName` lowercase only; MySQL/MariaDB = no schema (a "schema" is a
database), net9.0/net10.0 only; In-Memory = no audit, no persistence, no cleanup.

## In-Memory (core `EverTask` package, no NuGet)

```csharp
.AddMemoryStorage()   // no options
```

Singleton `MemoryTaskStorage`. Lost on restart; not for production, durable scheduling, or
multi-instance.

## SQL Server: `EverTask.Storage.SqlServer`

```csharp
.AddSqlServerStorage(string connectionString, Action<SqlServerTaskStoreOptions>? configure = null)
```

| Option | Default | Notes |
|---|---|---|
| `SchemaName` | `"EverTask"` | Any case. `null` → `dbo` (discouraged). Changing post-migration needs manual DB update. |
| `AutoApplyMigrations` | `true` | Calls `Database.Migrate()` at startup. `false` for DBA/staged control. |

Connection string examples: Docker `Server=localhost,1433;Database=EverTaskDb;User Id=sa;Password=…;TrustServerCertificate=True`;
LocalDB `Server=(localdb)\mssqllocaldb;Database=EverTaskDb;Integrated Security=True`. Add
`Encrypt=True;TrustServerCertificate=False` for prod TLS.

Pooled DbContext factory + stored proc `usp_SetTaskStatus` (status + conditional audit in one
round-trip). Schema is runtime-configurable. Tables: `QueuedTasks`, `StatusAudit`, `RunsAudit`,
`TaskExecutionLogs`, `__EFMigrationsHistory` (all under `SchemaName`).

## PostgreSQL: `EverTask.Storage.Postgres`

```csharp
.AddPostgresStorage(string connectionString, Action<PostgresTaskStoreOptions>? configure = null)
```

| Option | Default | Notes |
|---|---|---|
| `SchemaName` | `"evertask"` | **Lowercase only** (`^[a-z_][a-z0-9_]*$`): identifiers are double-quoted, so mixed-case becomes permanently case-sensitive. `null` → `public`. |
| `AutoApplyMigrations` | `true` | Same as SQL Server. |

Connection: `Host=localhost;Database=evertask;Username=evertask;Password=***`. All
`DateTimeOffset` → `timestamptz` (UTC). Pooled factory + writable-CTE single-statement status/run
updates (no stored DB objects). UUID v7 ids.

## MySQL / MariaDB: `EverTask.Storage.MySql`  (net9.0/net10.0 only)

```csharp
.AddMySqlStorage(string connectionString, Action<MySqlTaskStoreOptions>? configure = null)
```

| Option | Default | Notes |
|---|---|---|
| `AutoApplyMigrations` | `true` | Same as the others. |
| `ServerVersion` | `null` | `null` → `ServerVersion.AutoDetect(cs)`; set `new MariaDbServerVersion(new Version(10,11))` to skip the probe. |
| `SchemaName` | `""` | **Must stay `""`** (MySQL/MariaDB have no sub-database schema; a "schema" is a database). |

Connection: `Server=localhost;Database=evertask;User=evertask;Password=***`. Built on the maintained
Microting fork of Pomelo; MySQL 8.0+ / MariaDB 10.11+. All `DateTimeOffset` → `datetime(6)` (UTC),
server-side like Postgres. Pooled factory + UUID v7 ids (stored as `char(36)`). One read-path override
(completed-task purge: a MySQL `DELETE ... LIMIT` ignores a correlated `EXISTS` guard). Hot writes
(SetStatus/UpdateCurrentRun/CompleteRecurringRun) use stored procedures (single-statement, atomic; the
ErrorsOnly runs-audit gate decided server-side), the analog of SQL Server's procs / Postgres' CTEs.

## SQLite: `EverTask.Storage.Sqlite`

```csharp
.AddSqliteStorage(string connectionString = "Data Source=EverTask.db", Action<SqliteTaskStoreOptions>? configure = null)
```

`connectionString` has a default, so `.AddSqliteStorage()` is valid.

| Option | Default | Notes |
|---|---|---|
| `SchemaName` | `""` | **Must stay `""`** (SQLite has no schemas). |
| `AutoApplyMigrations` | `true` | Critical for `:memory:` (schema re-applied every start). |

Connection options: file `Data Source=evertask.db`; WAL `Data Source=evertask.db;Mode=ReadWriteCreate;Cache=Shared`;
in-memory `Data Source=:memory:;Mode=Memory;Cache=Shared`.

Caveats: single writer (multiple readers OK); EF can't translate `DateTimeOffset` ordering, so
`SqliteTaskStorage` overrides ~9 methods with client-side filtering; large backlogs (>~10k pending)
load more into memory on recovery. Use SQL Server/Postgres for high write concurrency.

## Audit levels

Global default `SetDefaultAuditLevel(AuditLevel.Full)`; per-dispatch override via the `auditLevel`
parameter on every `Dispatch(...)`.

| Level | StatusAudit | RunsAudit (recurring) | Use for |
|---|---|---|---|
| `Full` (default) | all status transitions | every run | critical tasks |
| `Minimal` | errors only | **every run** (tracks last run) + `LastExecutionUtc` | high-frequency recurring |
| `ErrorsOnly` | errors only | errors only (exception recorded or status `Failed`) | fire-and-forget |
| `None` | never | never | extreme frequency |

The single source of truth for these decisions is `AuditPolicy.cs` (`ShouldCreateStatusAudit` /
`ShouldCreateRunsAudit`). Key distinction: `Minimal` vs `ErrorsOnly` differ **only** in RunsAudit:
`Minimal` still writes a RunsAudit row for *every* recurring run (so you keep run-frequency history),
while `ErrorsOnly` writes RunsAudit only for a run with a non-empty exception string or status `Failed`.

Every audit row is a synchronous write: drop the level for high-frequency or heavily-throttled
task types to improve throughput.

## Audit retention / cleanup (EF Core providers only)

Registered on **`IServiceCollection`** (not the builder), after the storage call:

```csharp
services.AddAuditCleanup(AuditRetentionPolicy retentionPolicy, int cleanupIntervalHours = 24);
```

Factories: `AuditRetentionPolicy.WithUniformRetention(days)`,
`AuditRetentionPolicy.WithErrorPriority(successRetentionDays, errorRetentionDays)`. Individually
settable: `StatusAuditRetentionDays`, `RunsAuditRetentionDays`, `ErrorAuditRetentionDays`,
`ExecutionLogRetentionDays`, `MaxExecutionLogsPerTask`, `OccurrenceRetentionDays`,
`DeleteCompletedTasksAfterRetention` (all `null` = unlimited). `OccurrenceRetentionDays` prunes the
finished occurrences of a durable recurring schedule in ANY terminal state (Completed, Failed and
Cancelled), which `DeleteCompletedTasksAfterRetention` does not: that one only removes completed rows
with no audit trail left. Both skip a row that still owns execution logs while a log-retention
window/cap is active, so a short occurrence window never cascade-deletes logs a long log window kept; the
occurrence pass skips a row whose StatusAudit rows are still inside their window whenever
`StatusAuditRetentionDays` is set, and its RunsAudit rows whenever `RunsAuditRetentionDays` is, for the same
reason. One guard per trail: a knob left unset prunes nothing and so holds nothing back.
The cleanup service also exposes `AuditCleanupOptions.CleanupInterval` (default
24h, from the `cleanupIntervalHours` arg) and `InitialDelay` (default 1 min before the first sweep).
Requires an EF Core storage; warns + disables itself for custom non-EF storage.

## Inspecting tasks at runtime: the `QueuedTask` shape

`ITaskStorage.Get(...)` / `GetByTaskKey(...)` return `EverTask.Storage.QueuedTask`, the materialized
row. Useful public read members for building status/inspection logic without reading source:
`Status`, `CurrentRunCount`, `MaxRuns`, `NextRunUtc`, `RunUntil`, `TaskKey`, `QueueName`,
`LastExecutionUtc`, `ExecutionTimeMs`, the `StatusAudits` / `RunsAudits` / `ExecutionLogs`
collections, and the recovery predicates `IsRecoverableForExecution(now)` /
`IsRecurringSeriesToFinalize()` (`IsRecoverable(now)` is the former under its historical name).
Durable recurring schedules add `ParentTaskId` (null on every ordinary row), `RuntimeInfo` and
`ScheduleVersion`.

## Custom storage

Implement `ITaskStorage` and register: `services.AddSingleton<ITaskStorage, MyStorage>();`.
Key surface: `Get/GetAll/Persist/UpdateTask/Remove`, `RetrievePending` (keyset recovery),
`GetByTaskKey`, the `Set*` status transitions (all take `AuditLevel`), `GetCurrentRunCount` /
`UpdateCurrentRun`, recurring helpers (`CompleteRecurringRun`, `SetRecurringSeriesCompleted`,
`SetRecurringTaskPoisoned`), recovery guards (`TrySetQueuedIfRecoverable`,
`IncrementRecoveryFailure`, `ClearRecoveryFailure`), and execution logs (`SaveExecutionLogsAsync`,
`GetExecutionLogsAsync`).

Critical: make `TrySetQueuedIfRecoverable` an **atomic conditional UPDATE** (the default fallback
is non-atomic read-then-write → recovery double-execution); make the recurring helpers
single-transaction; forward `AuditLevel`; match recoverable statuses to
`QueuedTask.IsRecoverableForExecution` (`WaitingQueue, Queued, Pending, InProgress, ServiceStopped` +
recurring tasks with a next run). Optionally implement `ITaskStorageStatistics` to avoid O(backlog)
reads in the dashboard.

`RetrievePending` must return TWO categories: rows still to EXECUTE
(`QueuedTask.IsRecoverableForExecution(now)`) and recurring series that only need FINALIZING
(`QueuedTask.IsRecurringSeriesToFinalize()` — every remaining slot past `RunUntil`, or the run budget
spent). `TrySetQueuedIfRecoverable` applies only the first, so a spent series is never handed back to a
worker queue. Both members also have a `nowUtc` overload that the core always calls: the defaults
delegate to the legacy signatures, so an existing storage keeps working but resolves the clock itself.

Durable recurring schedules need atomic operations that no non-atomic emulation can provide
(`MaterializeOccurrence`, `TryAdvanceScheduleCursor`, `CancelSchedule`, `RequeueTerminal`,
`TryRequeueStaleOccurrence`, `UpdateSchedule`, `TryHaltSchedule`, `TrySetRecurringSeriesCompleted` and the
compare-and-swap overloads of `UpdateCurrentRun` / `CompleteRecurringRun`). `TryAdvanceScheduleCursor` is
the write a SKIPPED slot needs — a compare-and-swap on version plus cursor that moves the cursor and
nothing else: no run counted, no audit row. They default to throwing `NotSupportedException`, gated
by `SupportsDurableOccurrences` / `SupportsScheduleVersioning` (both `false` by default). Implement them
atomically before flipping either flag — a "best effort" version built from two writes is exactly the
crash window they exist to close.

> To add a new **EF Core relational** provider package (MySQL, Oracle, …), use the separate
> `new-relational-storage-provider` skill: it has the mandatory per-DB verification matrix.

**Advanced, custom ID generation:** the persistence-id generator is `IGuidGenerator` (default
`DefaultGuidGenerator` emitting DB-friendly temporally-ordered UUIDs), registered via
`TryAddSingleton`. Register your own with `services.AddSingleton<IGuidGenerator, MyGen>()` **before**
`AddEverTask` to control index-fragmentation behavior for a specific DB engine. Niche; defaults are fine.

## Serialization note

All providers use the isolated System.Text.Json serializer; payload survival across recovery is
governed by the contract in `08-payload-contract.md`. Serialization happens at dispatch (writes
`Request`/`RecurringTask` columns) and recovery (reads them back). Legacy Newtonsoft rows
(pre-v3.9) are read leniently; no row migration needed.
