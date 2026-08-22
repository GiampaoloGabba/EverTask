# EverTask.Tests.Storage

Refer to the root CLAUDE.md for project-wide rules. Integration tests for the four EF Core providers against
real engines.

- **`EfCore/EfCoreTaskStorageTestsBase.cs` is the contract suite** (~106 facts): add a test there ONCE and it
  runs on all four providers. Subclasses only implement `CreateDbContext()` / `GetStorage()` and may override
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
- SQLite cannot `ORDER BY` a `DateTimeOffset` — materialize with `ToList()` first. And floor a
  `DateTimeOffset` through `FloorToMicroseconds` before persisting whenever the test asserts an exact
  round-trip: Postgres `timestamptz` keeps microseconds, .NET ticks are 100 ns.
