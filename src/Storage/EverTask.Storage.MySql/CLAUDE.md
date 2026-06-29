# EverTask.Storage.MySql

## Purpose

MySQL/MariaDB storage provider. Extends `EverTask.Storage.EfCore` via **Microting.EntityFrameworkCore.MySql**
(the maintained fork of the abandoned Pomelo). Multi-targets **net9.0/net10.0 ONLY**.

**Key fact:** like Postgres (and unlike SQLite), this is a fully relational provider. The Microting provider
maps `DateTimeOffset` → `datetime(6)` (UTC) and translates every ordering/keyset/cleanup comparison
**server-side**, so it inherits `EfCoreTaskStorage` with **one** override (`CleanupCompletedTasks`). Proven by
the full `EfCoreTaskStorageTestsBase` running green on a real MariaDB 10.11 container.

## DI Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMySqlStorage("Server=localhost;Database=evertask;User=root;Password=...", opt =>
    {
        opt.AutoApplyMigrations = true;                               // Default: true
        opt.ServerVersion = new MariaDbServerVersion(new Version(10, 11)); // Optional: skip auto-detect
    });
```

## Critical Gotchas

### net8.0 is NOT supported
Microting publishes EF Core 9 (`9.0.x`) and EF Core 10 (`10.0.x`) but **no EF Core 8 build**. The `.csproj`
overrides `Directory.Build.props` with `<TargetFrameworks>net9.0;net10.0</TargetFrameworks>`. The core library
still multi-targets net8/9/10; only this package drops net8. The test project references it with
`Condition="'$(TargetFramework)' != 'net8.0'"` and the test class is guarded `#if !NET8_0`. When Microting (or
a successor) ships EF Core 8, net8 can be added back.

### No schema (schema == database)
`SchemaName` defaults to `""` and must stay empty (mirrors SQLite). MySQL/MariaDB have no sub-database schema —
a "schema" IS a database, selected by the connection string. So: **no `DbSchemaAwareMigrationAssembly`**, **no
schema arg to `MigrationsHistoryTable`**, `UseEverTaskSchema("")`. `__EFMigrationsHistory` lives in the
connection's database. The Initial migration and the hot-write procs use UNQUALIFIED names, so a non-empty
`SchemaName` would break at the first query — `AddMySqlStorage` therefore **throws `ArgumentException`** on a
non-empty value rather than failing silently later.

### Stored procs must be deployed (proc-not-found → double execution)
Same operational footgun as SQL Server: the hot writes `CALL` `usp_SetTaskStatus` / `usp_UpdateCurrentRun` /
`usp_CompleteRecurringRun`. If those procs are absent (e.g. `AutoApplyMigrations = false` and the
`AddHotWriteStoredProcedures` migration was never applied), every hot write throws "PROCEDURE … does not exist".
`SetStatus` **swallows** that (logs critical) and the row stays in a recoverable status, so startup recovery
re-dispatches it → double execution. With `AutoApplyMigrations = false`, apply ALL migrations (tables AND the
proc migration) before the app handles tasks.

### GUID generator: `UUIDNext.Database.PostgreSql` — NEVER `.SqlServer`
`Guid` maps to `char(36)` (collation `ascii_general_ci`). A UUIDv7 canonical string sorts in temporal order, so
the `(CreatedAtUtc, Id)` keyset / recovery index stay efficient. `.SqlServer` (v8) reorders bytes and would
break the string ordering.

### `CleanupCompletedTasks` override (the ONE override)
On MySQL a `Take(n).ExecuteDelete()` → `DELETE ... LIMIT` does NOT reliably honor a correlated `EXISTS` guard in
its `WHERE`: the `preserveTasksWithLogs` guard (`!TaskExecutionLogs.Any(...)`) was dropped and a completed task
that still owned logs got purged (covered by two base tests). The override resolves the matching ids with a
`SELECT` (the `EXISTS` subqueries and the `DateTimeOffset` cutoff translate server-side) and deletes by primary
key in batches (`DeleteByIdsAsync`). The other `Cleanup*` methods have no `EXISTS` guard and inherit the base.

### Recovery index is a plain composite
`IX_QueuedTasks_Recovery` on `(CreatedAtUtc, Id)` serves the keyset ORDER BY. MySQL/MariaDB support neither
`INCLUDE` columns (SQL Server) nor partial/filtered indexes (Postgres), so the recoverable-status predicate is a
runtime filter, not index-pruned. It is hand-added in the Initial migration via `CreateIndex` (kept out of the
model, like the Postgres recovery index — confirmed by `dotnet ef migrations has-pending-model-changes`).

### `lower_case_table_names` is OS-dependent
Linux defaults to `0` (case-sensitive table names), Windows to `1` (folded to lowercase). The migrations create
PascalCase tables (`QueuedTasks`, …); on a case-sensitive server they stay PascalCase. This only bites if the
same database files are moved across OSes — rare, but relevant before any future stored procedures (Phase 2).

### Phase 2: hot-write stored procedures (`AddHotWriteStoredProcedures` migration)
MySQL/MariaDB have read-only CTEs and no `UPDATE ... RETURNING`, so the single-roundtrip optimization Postgres
does with writable CTEs and SQL Server with stored procedures here uses **stored procedures** too:
`usp_SetTaskStatus`, `usp_UpdateCurrentRun`, `usp_CompleteRecurringRun`. Each runs one `START TRANSACTION ... COMMIT`
with an `EXIT HANDLER FOR SQLEXCEPTION` that rolls back and `RESIGNAL`s (atomic; a mid-statement failure persists
nothing). Invariants, identical to the base/SqlServer/Postgres: `SetStatus` audit gate + terminal-stamp computed
in C# from the INPUT (audited values are inputs) and **swallows**; `UpdateCurrentRun` ErrorsOnly RunsAudit gate
decided **server-side** from the row's `Status`/`Exception` (read `FOR UPDATE`), `NOT FOUND` handler makes a missing
task a no-op, **rethrows** (Residual D); `CompleteRecurringRun` audits the constants `Completed`/null so both gates
are C#-computed, `NextRunUtc` assigned unconditionally, **rethrows**. The counter SATURATES at `int.MaxValue`.
Gotchas: the proc params take the GUID as `CHAR(36)` (the C# overrides pass `taskId.ToString()`, matching the
`char(36)` storage / `ascii_general_ci` collation); `DROP` + `CREATE PROCEDURE` are separate `Sql(..., suppressTransaction: true)`
calls (MySQL DDL implicitly commits); no `DELIMITER` is needed (that is a CLI-only construct — the driver sends the
whole `CREATE PROCEDURE` as one statement). The procs are unqualified (schema is the connection's database).

## Migrations

```bash
cd src/Storage/EverTask.Storage.MySql/
dotnet ef migrations add MigrationName --framework net9.0
```

The DEBUG-only `TaskStoreEfDbContextFactory` hardcodes `new MariaDbServerVersion(new Version(10, 11))`
(scaffolding does not connect, but `UseMySql` requires a `ServerVersion`).

## 🔗 Test Coverage

`test/EverTask.Tests.Storage/MySqlEfCoreTaskStorageTests.cs` (Testcontainers `mariadb:10.11`, Respawn
`DbAdapter.MySql` with `SchemasToInclude=[<database>]`). Inherits the full `EfCoreTaskStorageTestsBase`.

```bash
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~MySql"   # requires Docker
```
**Prerequisites**: Docker (Linux engine). The container is started once per `DatabaseTests` collection.
