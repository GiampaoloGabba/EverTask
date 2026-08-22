# EverTask.Storage.Postgres

Refer to the root CLAUDE.md for project-wide rules.

Npgsql maps `DateTimeOffset` → `timestamptz` and translates every ordering/keyset/cleanup comparison the base
relies on server-side, so `PostgresTaskStorage` carries **no client-side override** (contrast SQLite, which
overrides 9).

- **Schema name must be lowercase** (default `evertask`). PostgreSQL folds unquoted identifiers to lowercase
  but EF/Npgsql always double-quotes what it generates, so a mixed-case `"EverTask"` becomes permanently
  case-sensitive in every hand-written `psql`/`search_path` query. Keep custom values matching
  `^[a-z_][a-z0-9_]*$` — **nothing validates this at registration** (unlike `AddMySqlStorage`, which throws).
- The schema is runtime-configurable: `DbSchemaAwareMigrationAssembly` (copied from SqlServer) injects
  `ITaskStoreDbContext` into the migration, which uses `_dbContext.Schema` everywhere (null/empty ⇒ `public`).
  `MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema)` is **required** — without the schema
  argument the history table lands in `public`.
- **GUID generator: `UUIDNext.Database.PostgreSql`, NEVER `.SqlServer`.** Postgres sorts `uuid` byte-wise from
  byte 0; `.SqlServer` is v8 with reordered bytes and would make `uuid` sort non-temporally, defeating the
  recovery index and the keyset.
- `IX_QueuedTasks_Recovery` is partial: keyed `(CreatedAtUtc, Id)`, `INCLUDE`s the runtime-predicate columns,
  and its **static** `WHERE` prunes the bulk terminal rows. **Never put `now()` in the predicate** (mutable ⇒
  non-deterministic index); `RunUntil >= now` stays a runtime filter. That `WHERE` hardcodes the
  recoverable-status list — one of the four copies tracked in `../EverTask.Storage.EfCore/CLAUDE.md`.
- Migrations are generated fresh (DEBUG-only `TaskStoreEfDbContextFactory`, `SchemaName="evertask"`) then
  hand-edited exactly as in `../EverTask.Storage.SqlServer/CLAUDE.md`, plus one delta: append the recovery
  index via `migrationBuilder.Sql`, schema interpolated with a `public` fallback.
- Phase 2 — `SetStatus`, `UpdateCurrentRun` and `CompleteRecurringRun` override the base with
  single-statement data-modifying CTEs (one statement = atomic, so the audit insert and the row update commit
  together; no stored object, no migration). Audit gates: `SetStatus` decides in C#, `UpdateCurrentRun` decides
  **server-side** from the row's `Status`/`Exception` read via `RETURNING`, `CompleteRecurringRun` audits
  constants. `SetStatus` swallows, the other two rethrow. The run counter saturates at `int.MaxValue`.
- Tests: `test/EverTask.Tests.Storage/PostgresEfCoreTaskStorageTests.cs` (Testcontainers `postgres:16-alpine`,
  Respawn `DbAdapter.Postgres` with `SchemasToInclude=["public","evertask"]` — without `evertask` Respawn
  silently cleans nothing).
