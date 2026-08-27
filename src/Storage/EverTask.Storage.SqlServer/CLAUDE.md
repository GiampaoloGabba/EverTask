# EverTask.Storage.SqlServer

Refer to the root CLAUDE.md for project-wide rules.

- **The three hot writes are stored procedures**, not EF: `SqlServerTaskStorage` overrides `SetStatus`,
  `UpdateCurrentRun` and `CompleteRecurringRun` to `EXEC` `usp_SetTaskStatus`, `usp_UpdateCurrentRun`,
  `usp_CompleteRecurringRun`. If a proc is missing (`AutoApplyMigrations = false` with the proc migrations
  unapplied), `SetStatus` **swallows** the error, the row stays in a recoverable status and startup recovery
  re-dispatches it — **double execution**.
- **Four MORE procs since durable occurrences**: `usp_MaterializeOccurrence`, `usp_CancelSchedule`,
  `usp_UpdateCurrentRunCas` and `usp_CompleteRecurringRunCas` (`Migrations/…_AddDurableOccurrences.cs`). They
  are NEW names, so their `Down()` only drops them — the three pre-existing procs are untouched by that
  migration. Each reports its outcome through an **OUTPUT parameter** (`@Outcome` / `@Applied`), read back
  from the `SqlParameter` after `ExecuteSqlRawAsync`. `usp_MaterializeOccurrence` holds the schedule row with
  `UPDLOCK, HOLDLOCK`: that is what serializes two materializers reading the same cursor.
  `usp_CancelSchedule` audits the schedule row only when its UPDATE found one (`@@ROWCOUNT`, read on the very
  next statement) — cancelling a schedule a concurrent `Remove` already deleted is a silent no-op on every
  other provider, while an audit row for a missing task violates `FK_StatusAudit → QueuedTasks`.
- **The four new procs open with `SET XACT_ABORT ON`, and any new one must too.** With it OFF (the default)
  SQL Server aborts only the statement that failed: a constraint violation on the INSERT inside
  `usp_MaterializeOccurrence` leaves the procedure running, the cursor advance executes and `COMMIT` commits a
  schedule that moved on without its occurrence. The three pre-existing procs predate this rule and are
  single-write, so they are not retrofitted. Pinned by the fault-injection facts in
  `EfCoreTaskStorageTestsBase` and by the procedure-body check in `SqlServerEfCoreTaskStorageTests`.
- **The occurrence unique index is FILTERED** (`WHERE [ParentTaskId] IS NOT NULL AND …`): SQL Server treats
  NULLs as equal in a unique index and every ordinary row has a null `ParentTaskId`. EF's convention adds the
  filter automatically for unique indexes over nullable columns — do not remove it.
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
- **Every read of this provider re-runs itself when SQL Server names it the deadlock victim** (error 1205,
  `SqlServerTaskStorage`, at most 3 attempts). Reads through a nonclustered index take locks in the opposite
  order to writes, so a read racing status updates (recovery above all) can be picked as the victim — and
  letting 1205 out on the recovery path aborts the whole startup recovery (`WorkerService` logs
  `RecoveryFailed`), leaving the backlog for the next restart. Writes are deliberately NOT re-run: each is a
  compare-and-swap or a single-transaction procedure whose caller already knows what a lost race means. Only
  SQL Server needs this — PostgreSQL and MySQL answer plain reads from a snapshot. Guard test:
  `SqlServerEfCoreTaskStorageTests.Should_rerun_a_read_that_sql_server_picked_as_the_deadlock_victim`.
- Only the **clock-carrying** `RetrievePending` overload is overridden here: overriding the legacy one would
  make `EfCoreTaskStorage`'s reflection probe take this provider for a pre-4.0 storage and route every call
  through it, dropping the caller's `nowUtc`.
- `IX_QueuedTasks_Recovery` is a non-filtered covering index (`(CreatedAtUtc, Id)` plus `INCLUDE`), added by
  raw SQL; the recoverable-status predicate stays a runtime filter, so it needs no edit when that list changes
  (unlike Postgres — see `../EverTask.Storage.EfCore/CLAUDE.md`).
