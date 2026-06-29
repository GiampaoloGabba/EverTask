# NuGet package matrix: feature → package

Resolve the package set from the user's storage choice + selected capabilities. The version is
governed lockstep by `Directory.Build.props` in this repo; for an external consumer, take the
latest published version of each package (they release in lockstep). Current repo package version:
**3.10.0**.

| Feature / choice | Package | Notes |
|---|---|---|
| Core (always) | `EverTask` | Dispatcher, worker, scheduler, in-memory storage. |
| Interfaces only (referenced by the task/handler library) | `EverTask.Abstractions` | `IEverTask`, `ITaskDispatcher`, `EverTaskHandler<T>`, retry/rate-limit types. **Bundles the ET0001–ET0007 analyzers.** |
| In-Memory storage | (none) | Built into `EverTask`; just call `.AddMemoryStorage()`. |
| SQL Server storage | `EverTask.Storage.SqlServer` | Pulls `EverTask.Storage.EfCore` + EF SqlServer. |
| PostgreSQL storage | `EverTask.Storage.Postgres` | Pulls `EverTask.Storage.EfCore` + Npgsql. |
| MySQL / MariaDB storage | `EverTask.Storage.MySql` | Pulls `EverTask.Storage.EfCore` + Microting MySQL provider. **net9.0/net10.0 only.** |
| SQLite storage | `EverTask.Storage.Sqlite` | Pulls `EverTask.Storage.EfCore` + EF Sqlite. |
| Audit retention/cleanup | `EverTask.Storage.EfCore` | `AddAuditCleanup(...)`; already transitive via any relational provider. |
| SignalR monitoring (events) | `EverTask.Monitor.AspnetCore.SignalR` | ASP.NET Core; `MapEverTaskMonitorHub()`. |
| Monitoring dashboard + REST API | `EverTask.Monitor.Api` | ASP.NET Core; `MapEverTaskApi()`. Auto-registers SignalR. |
| Serilog pipeline | `EverTask.Logging.Serilog` | Dedicated Serilog pipeline for EverTask's internal logs. |

## Package-management mechanics

1. **Check for Central Package Management**: a `Directory.Packages.props` at/above the project. If
   present: add a `<PackageVersion Include="X" Version="…" />` there and a versionless
   `<PackageReference Include="X" />` in the `.csproj`. (This repo uses CPM; do NOT put versions in
   `.csproj`.)
2. **Otherwise** use `dotnet add <proj> package X`.
3. The library that defines task records/handlers can reference just `EverTask.Abstractions` (keeps
   the analyzer active there); the startup/composition project references `EverTask` + the chosen
   storage/monitoring/logging packages.

## Typical sets

- **Dev/test (web or worker):** `EverTask` (+ `EverTask.Abstractions` if tasks live in a separate lib).
- **Production web app:** `EverTask` + `EverTask.Storage.Postgres` (or `.SqlServer`) +
  `EverTask.Monitor.Api` [+ `EverTask.Logging.Serilog`].
- **Worker service:** `EverTask` + `EverTask.Storage.SqlServer`/`.Postgres` (no `Monitor.Api`, no
  HTTP pipeline; use SignalR-to-external-hub or event subscription).
- **Desktop/edge:** `EverTask` + `EverTask.Storage.Sqlite`.

## Framework compatibility

All packages (core, storage, monitoring, SignalR, Serilog) multi-target **net8.0/net9.0/net10.0**
(inherited from `Directory.Build.props`; no project overrides this). net8.0 is the minimum.
**Exception:** `EverTask.Storage.MySql` targets **net9.0/net10.0 only** — its underlying Microting
provider has no EF Core 8 build.
