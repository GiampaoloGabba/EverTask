# EverTask.Tests.Storage

Refer to the root CLAUDE.md for project-wide rules. Integration tests for the four EF Core providers against
real engines.

- **`EfCore/EfCoreTaskStorageTestsBase.cs` is the contract suite** (~140 facts, including the durable-occurrence
  region and the execution-vs-finalization recovery filter): add a test there ONCE and it runs on all four
  providers — which is the only way the four very different implementations of the same operation (EF base for
  SQLite, procedures for SQL Server and MySQL, writable CTEs for Postgres) are held to one contract.
  Subclasses only implement `CreateDbContext()` / `GetStorage()` and may override
  `Initialize()` / `GetGuidForProvider()`; provider-specific behaviour (schema, index, Respawn proof) goes in
  the provider class. `QueuedTasks` (base) yields exactly 2 fresh tasks — one `InProgress`, one `Queued`.

| Provider class | Engine | Cleanup |
|---|---|---|
| `SqliteEfCoreTaskStorageTests` (file `SqLiteEfCoreTaskStorageTests.cs`, capital **L**) | SQLite file, no Docker | manual `RemoveRange()` |
| `SqlServerEfCoreTaskStorageTests` | `mcr.microsoft.com/mssql/server:2022-latest` | Respawn |
| `PostgresEfCoreTaskStorageTests` | `postgres:16-alpine` | Respawn, `SchemasToInclude = ["public", "evertask"]` |
| `MySqlEfCoreTaskStorageTests` | `mariadb:10.11`, **net9/net10 only** (`Condition="'$(TargetFramework)' != 'net8.0'"` on package AND project reference) | Respawn `DbAdapter.MySql`, schema = database |

- Docker is available on this dev machine and the images are pre-pulled — check `docker info` before reporting
  it missing; only a first pull of the ~1.7 GB mssql image is slow.
- Every container-backed class carries `[Collection("DatabaseTests")]` and there is **no
  `[CollectionDefinition]`** anywhere: the bare attribute is the only thing serializing them against
  `parallelizeTestCollections: true` in `xunit.runner.json`, so the shared static containers are never started
  concurrently. Put it on any new one.
- Four classes start a SQL Server container, not just the storage suite: `SqlServerEfCoreTaskStorageTests`,
  `SqlServerRecoveryIntegrationTests`, `AuditLevelIntegrationTests`, `SqlServerRecurringPoisonRecoveryTests`
  (in `RecurringPoisonRecoveryIntegrationTests.cs`).
- **Schema is asserted from the CATALOG, per provider** (`Should_have_the_durable_occurrence_schema_on_queued_tasks`
  in each provider class): the unique index and its SQL Server-only filter, `IX_QueuedTasks_ParentTaskId`,
  `CK_QueuedTasks_OccurrenceSlot`, the non-cascading self FK, and the four new procedures on SQL Server and
  MySQL. Behaviour alone passes on a table missing any of them.
- **Fault injection is two abstract pairs of SQL strings**, one trigger each, because the four
  implementations order the two writes of a materialization differently and no single point is "after the
  insert" on all of them.
  `InstallStatusAuditInsertFaultSql` fails every `StatusAudit` INSERT — the one point sitting AFTER the
  occurrence INSERT on all four (EF stages the audit behind it in the same `SaveChanges`, the two procedures
  write it last, the Postgres CTE last), so the shared suite can prove an already-inserted occurrence rolls
  back. `InstallScheduleAdvanceFaultSql` fails every `QueuedTasks` UPDATE, which is the window the plan names
  (after the child insert, before the schedule advance) on the three that insert first, and one step earlier
  on the EF base, which advances first. A check constraint would not do for either: it is validated against
  the rows already in the table. Install with `await using` — a leaked trigger breaks every later test in the
  collection.
- **`EfCore/MigrationSqlSnapshot`** pins the SQL each `AddDurableOccurrences` migration emits against
  `MigrationSnapshots/<Provider>.AddDurableOccurrences.sql` — a released migration is frozen, and the model
  snapshot notices none of what lives in raw SQL (procedure bodies, index filters, delete rules). A missing
  snapshot is written and the test fails, so a new baseline is reviewed in the diff; a mismatch also writes
  `<name>.actual.sql` next to it.
- SQLite cannot `ORDER BY` a `DateTimeOffset` — materialize with `ToList()` first. And floor a
  `DateTimeOffset` through `FloorToMicroseconds` before persisting whenever the test asserts an exact
  round-trip: Postgres `timestamptz` keeps microseconds, .NET ticks are 100 ns.
