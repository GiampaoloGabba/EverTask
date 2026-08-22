# EverTask.Storage.SqlServer

Refer to the root CLAUDE.md for project-wide rules.

- **The three hot writes are stored procedures**, not EF: `SqlServerTaskStorage` overrides `SetStatus`,
  `UpdateCurrentRun` and `CompleteRecurringRun` to `EXEC` `usp_SetTaskStatus`, `usp_UpdateCurrentRun`,
  `usp_CompleteRecurringRun`. If a proc is missing (`AutoApplyMigrations = false` with the proc migrations
  unapplied), `SetStatus` **swallows** the error, the row stays in a recoverable status and startup recovery
  re-dispatches it — **double execution**.
- The runtime schema falls back to `dbo` on an empty `SchemaName` (`SqlServerTaskStorage.cs`) and MUST match
  the same `dbo` fallback used by the migrations. An older `?? "EverTask"` made `EXEC` target a schema the
  procs did not live in, producing exactly the double execution above.
- **Every migration MUST take an `ITaskStoreDbContext` ctor parameter** and use `_dbContext.Schema` on every
  object — `DbSchemaAwareMigrationAssembly` injects it by reflection. Copy the shape from the previous
  migration.
- Raw-SQL migrations (procs, indexes with `INCLUDE`) interpolate the schema into every object reference with a
  `dbo` fallback, and `Down()` must **restore the PREVIOUS body** of a stored procedure (copy it from the
  prior migration), not just drop it. Reference:
  `Migrations/20260611064213_AddRecoveryIndexAndUpdateRunProcedure.cs`,
  `Migrations/20260616181803_SaturateRunCounter.cs`.
- Generate with `dotnet ef migrations add <Name> --framework net9.0` from this folder; it needs the DEBUG-only
  `TaskStoreEfDbContextFactory`. The generated file has no ctor and a hardcoded schema, so always hand-edit:
  add the `ITaskStoreDbContext` ctor (keep the class `partial`) and replace every `schema: "EverTask"` with
  `schema: _dbContext.Schema`. Do NOT edit the `.Designer.cs` — injection targets the migration class.
- `IX_QueuedTasks_Recovery` is a non-filtered covering index (`(CreatedAtUtc, Id)` plus `INCLUDE`), added by
  raw SQL; the recoverable-status predicate stays a runtime filter, so it needs no edit when that list changes
  (unlike Postgres — see `../EverTask.Storage.EfCore/CLAUDE.md`).
