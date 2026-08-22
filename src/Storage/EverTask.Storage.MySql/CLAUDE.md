# EverTask.Storage.MySql

Refer to the root CLAUDE.md for project-wide rules.

MySQL/MariaDB provider on **Microting.EntityFrameworkCore.MySql** (the maintained fork of the abandoned
Pomelo), which publishes EF Core 9 and 10 but **no EF Core 8 build** — hence the csproj override
`<TargetFrameworks>net9.0;net10.0</TargetFrameworks>`, the test project's
`Condition="'$(TargetFramework)' != 'net8.0'"` and the `#if !NET8_0` guard on the test class.

- **Stored procs must be deployed, or tasks execute twice.** The hot writes `CALL usp_SetTaskStatus`,
  `usp_UpdateCurrentRun`, `usp_CompleteRecurringRun`. If the `AddHotWriteStoredProcedures` migration was never
  applied (e.g. `AutoApplyMigrations = false`), every hot write throws "PROCEDURE … does not exist";
  `SetStatus` **swallows** it, the row stays in a recoverable status and startup recovery re-dispatches it →
  **double execution**. With `AutoApplyMigrations = false`, apply ALL migrations before the app handles tasks.
- **No schema**: a MySQL "schema" IS a database, chosen by the connection string. `SchemaName` must stay `""`,
  so there is no `DbSchemaAwareMigrationAssembly` and no schema argument to `MigrationsHistoryTable`; tables
  and procs use unqualified names. `AddMySqlStorage` **throws `ArgumentException`** on a non-empty value
  rather than failing at the first query.
- **GUID generator: `UUIDNext.Database.PostgreSql`, NEVER `.SqlServer`.** `Guid` maps to `char(36)`
  (`ascii_general_ci`); a UUIDv7 canonical string sorts temporally, keeping the `(CreatedAtUtc, Id)` keyset and
  the recovery index efficient. `.SqlServer` (v8) reorders bytes and breaks that string ordering.
- `CleanupCompletedTasks` is the one cleanup-path override: a `DELETE … LIMIT` does not reliably honor a
  correlated `EXISTS` in its `WHERE`, so the `preserveTasksWithLogs` guard was dropped and completed tasks that
  still owned logs got purged. The override resolves the ids with a `SELECT` and deletes by primary key in
  `CleanupBatchSize` batches (`DeleteByIdsAsync`). The other `Cleanup*` methods inherit the base.
- Phase 2 (`Migrations/20260629214027_AddHotWriteStoredProcedures.cs`): MySQL has read-only CTEs and no
  `UPDATE … RETURNING`, so the three hot writes are stored procedures, each a single
  `START TRANSACTION … COMMIT` with an `EXIT HANDLER FOR SQLEXCEPTION` that rolls back and `RESIGNAL`s.
  MySQL-specific mechanics: the proc params take the GUID as `CHAR(36)` (the C# overrides pass
  `taskId.ToString()`); `DROP` and `CREATE PROCEDURE` are separate `Sql(…, suppressTransaction: true)` calls
  (MySQL DDL implicitly commits); no `DELIMITER` is needed — that is a CLI-only construct, the driver sends
  the whole `CREATE PROCEDURE` as one statement.
- `IX_QueuedTasks_Recovery` is a plain composite on `(CreatedAtUtc, Id)`: MySQL/MariaDB support neither
  `INCLUDE` columns nor partial indexes, so the recoverable-status predicate is a runtime filter. It is
  hand-added in the Initial migration via `CreateIndex`, kept out of the model.
- Generate with `dotnet ef migrations add <Name> --framework net9.0`; the DEBUG-only
  `TaskStoreEfDbContextFactory` hardcodes `new MariaDbServerVersion(new Version(10, 11))` because `UseMySql`
  requires a `ServerVersion` even though scaffolding never connects.
- Tests: `test/EverTask.Tests.Storage/MySqlEfCoreTaskStorageTests.cs` (Testcontainers `mariadb:10.11`, Respawn
  `DbAdapter.MySql` with `SchemasToInclude=[<database>]`).
