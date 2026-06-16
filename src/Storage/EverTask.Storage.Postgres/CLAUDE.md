# EverTask.Storage.Postgres

## Purpose

PostgreSQL storage provider. Extends `EverTask.Storage.EfCore` via Npgsql. Multi-targets net8.0/net9.0/net10.0.

**Key fact:** Postgres is a fully relational provider like SQL Server (NOT like SQLite). Npgsql maps
`DateTimeOffset` → `timestamptz` and translates every ordering/keyset/cleanup comparison the base relies on
**server-side**, so the provider inherits `EfCoreTaskStorage` with **NO client-side overrides** (unlike SQLite,
which overrides 9 methods). Proven end-to-end: the full `EfCoreTaskStorageTestsBase` runs green on a real
Postgres container with zero overrides; captured SQL confirms the `Guid.CompareTo` keyset and the
`Take().ExecuteDelete` cleanup translate to server-side `uuid >` / `LIMIT`.

## DI Registration

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddPostgresStorage("Host=localhost;Database=evertask;Username=postgres;Password=...", opt =>
    {
        opt.SchemaName          = "evertask";  // Default: "evertask" (LOWERCASE — see gotcha)
        opt.AutoApplyMigrations = true;         // Default: true
    });
```

## Critical Gotchas

### Schema name MUST be lowercase
Default `"evertask"`. PostgreSQL folds **unquoted** identifiers to lowercase, but EF/Npgsql **always**
double-quotes generated identifiers — a mixed-case `"EverTask"` becomes permanently case-sensitive (every
hand-written `psql`/`search_path` query must quote it exactly). Validate custom values against `^[a-z_][a-z0-9_]*$`.

### Schema-aware migrations (Option B — full parity with SQL Server)
Schema is **runtime-configurable**: `DbSchemaAwareMigrationAssembly` (copied verbatim from SqlServer, incl.
`[SuppressMessage("Usage","EF1001")]`) injects `ITaskStoreDbContext` into the migration, and the hand-edited
`Initial` uses `_dbContext.Schema` everywhere (null/empty ⇒ `public`). `UseNpgsql(...).ReplaceService<IMigrationsAssembly, DbSchemaAwareMigrationAssembly>()`
plus `MigrationsHistoryTable(HistoryRepository.DefaultTableName, schema)` (REQUIRED — without the schema arg the
history table lands in `public`).

### GUID generator: `UUIDNext.Database.PostgreSql` — NEVER `.SqlServer`
Postgres sorts `uuid` byte-wise from byte 0 (like SQLite); `.PostgreSql` is the v7 family (same bytes as
`.SQLite`). `.SqlServer` is v8 with reordered bytes and would make `uuid` sort in non-temporal order, defeating
the recovery index / keyset.

### Recovery index = partial (Form B), not SQL Server's covering form
`IX_QueuedTasks_Recovery` is keyed `(CreatedAtUtc, Id)` (serves the keyset ORDER BY), INCLUDEs the runtime-predicate
columns, and has a **STATIC partial `WHERE`** that prunes the bulk terminal rows (Completed/Failed non-recurring).
**Never put `now()` in the predicate** (mutable ⇒ non-deterministic index): `RunUntil >= now` stays a runtime filter.

### Migrations are generated fresh, then hand-edited
`dotnet ef migrations add` (DEBUG-only `TaskStoreEfDbContextFactory`, `SchemaName="evertask"`). After generating,
hand-edit `Initial.cs`: add the `ITaskStoreDbContext` ctor, replace baked `"evertask"` with `_dbContext.Schema`,
append the recovery index via `migrationBuilder.Sql` (schema interpolated with a `public` fallback). Do NOT edit
the `.Designer.cs`. See `src/Storage/EverTask.Storage.SqlServer/CLAUDE.md` for the schema-aware pattern.

### Statistics filter normalization (in the EfCore base, not here)
`CountByStatusAsync`/`CountByQueueAndStatusAsync` normalize `createdAtOrAfterUtc` to UTC (`?.ToUniversalTime()`):
Npgsql requires `DateTimeOffset.Offset == 0` for `timestamptz`, so a `DateTimeOffset.Now` filter would throw.
No-op for SQL Server/SQLite. This keeps `PostgresTaskStorage` free of any statistics override.

## Phase 2 — writable-CTE optimizations (in `PostgresTaskStorage`)
`SetStatus`, `UpdateCurrentRun`, `CompleteRecurringRun` override the base with **single-statement data-modifying
CTEs** (Postgres' analog of the SQL Server stored procs): one statement = atomic, so audit insert + row update
commit together. No stored object, no migration (SQL lives in versioned C#).
- **Audit gate parity with `AuditPolicy`**: `SetStatus` decides the StatusAudit in C# (audited values are INPUTS).
  `UpdateCurrentRun` decides the RunsAudit **server-side in the CTE** (ErrorsOnly depends on the ROW's
  `Status`/`Exception`, read via `RETURNING` — faithful because the UPDATE never mutates them). `CompleteRecurringRun`
  audits CONSTANTS (`Completed`/null) so the gate is C#-computed from the level.
- **Propagation**: `SetStatus` swallows; `UpdateCurrentRun`/`CompleteRecurringRun` rethrow (Residual D). The
  run counter SATURATES at `int.MaxValue` (a `CASE` guard) instead of overflowing — uniform with the base and
  the other providers (see the run-count saturation note in `docs/recurring-tasks`).

## 🔗 Test Coverage
`test/EverTask.Tests.Storage/PostgresEfCoreTaskStorageTests.cs` (Testcontainers `postgres:16-alpine`, Respawn with
`DbAdapter.Postgres` + `SchemasToInclude=["public","evertask"]`). Inherits the full `EfCoreTaskStorageTestsBase`.

```bash
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName~Postgres"   # requires Docker
```
**Prerequisites**: Docker (Linux engine). The container is started once per `DatabaseTests` collection.
