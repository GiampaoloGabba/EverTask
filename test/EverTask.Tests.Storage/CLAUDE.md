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
- **MariaDB is ready twice.** Its entrypoint answers the module's in-container wait strategy with the
  temporary server it starts to initialize the data directory, then restarts the real one, and a connection
  from the HOST landing in that window is refused ("Unable to connect to any of the specified MySQL hosts").
  It costs whichever test ran first and passes on the re-run, so it reads as flakiness.
  `MySqlEfCoreTaskStorageTests.WaitUntilServerAcceptsConnections` closes it by probing the mapped port — the
  endpoint the tests actually use — before the migrations run.
- Every container-backed class carries `[Collection("DatabaseTests")]` and there is **no
  `[CollectionDefinition]`** anywhere: the bare attribute is the only thing serializing them against
  `parallelizeTestCollections: true` in `xunit.runner.json`, so the shared static containers are never started
  concurrently. Put it on any new one.
- **One SQL Server container per test process, `SqlServerTestContainer`** — five classes need SQL Server
  (`SqlServerEfCoreTaskStorageTests`, `SqlServerRecoveryIntegrationTests`, `AuditLevelIntegrationTests`,
  `SqlServerRecurringPoisonRecoveryTests` in `RecurringPoisonRecoveryIntegrationTests.cs`, and
  `SqlServerDurableOccurrencesMultiHostTests`) and all five take the connection string from that holder.
  A new one MUST do the same, never `new MsSqlBuilder(...)` of its own: an instance reserves ~5120 kernel
  aio contexts at boot out of the 65536 a default `fs.aio-max-nr` gives the whole Docker VM, and a
  solution-wide `dotnet test` runs the three target frameworks at once — one container per class was 12
  instances (61440, under by a hair) and the fifth class took it to 15 (76800, over). Past the budget an
  instance does not slow down, it aborts mid-boot ("Unable to create a new asynchronous I/O context") and
  Testcontainers reports the wait strategy failing on an exited container, on whichever suite happened to
  start last — it reads as flakiness, it is arithmetic. Sharing is safe because the collection serializes
  the classes and each Respawns before every test.
- **The two durable-occurrence suites here answer questions a single host cannot.**
  `CatchUpRecoveryIntegrationTests` (SQLite, no Docker) seeds a downtime and lets the REAL startup recovery
  replay it, so the unique index, the check constraint and the self foreign key are all in the loop. Its
  schedules are on an **hourly** grid, not a minute one: on a real clock only an hour-wide grid lets a test
  name the exact slots it expects, because the age window's boundary then sits half an hour from a slot
  instead of half a minute, and an assertion that has to survive a slow run is an assertion that cannot be
  exact. It covers each policy after a real downtime (catch-up window, `FireOnce` collapsing into one row,
  `Skip` behaving as it does inline), the `RunUntil` that elapsed while the host was down, a restart
  mid-replay, six schedules draining under one global materialization budget, and the retention proof that a
  pruned occurrence does not come back (the CURSOR drives materialization, not the rows).
  `SqlServerDurableOccurrencesMultiHostTests` builds TWO hosts by hand (the base class holds one) on one
  database: the first test pins that materialization is idempotent across them, the second pins the
  single-active-host LIMIT — two hosts really do deliver the same occurrence twice. That second assertion is
  the contract of 4.0 written down, and it is what the distributed-execution-lease epic will invert; do not
  "fix" it.
- **`SqlServerEfCoreTaskStorageTests.Should_rerun_a_read_that_sql_server_picked_as_the_deadlock_victim`
  builds the collision, it does not simulate it**: three `RetrievePending` loops (nonclustered index, then
  clustered key lookup) against four `SetStatus` loops (clustered row, then the two indexes carrying
  `Status`), plus two plain `Get` polls standing in for the caller that gets picked as the victim without
  being part of the cycle. The proof the run really collided is the storage's own EventId 2025, read through
  a `RecordingLogger<SqlServerTaskStorage>` registered over the DI logger — the test stops as soon as it
  counts two, so it costs a second or two, and a run where nothing collided FAILS rather than passing
  vacuously. The status writes pass `CancellationToken.None` on purpose: `usp_SetTaskStatus` owns a
  transaction, and cancelling one mid-flight strands it open on a pooled connection, holding the very locks
  the Respawn cleanup then waits 30 s for.
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
