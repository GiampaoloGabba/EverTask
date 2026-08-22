# EverTask.Monitor.Api

Refer to the root CLAUDE.md for project-wide rules.

REST API + embedded React dashboard, all under the fixed `/evertask-monitoring` prefix. Options live in
`Options/EverTaskApiOptions.cs` (XML-documented), exhaustively tabled in `docs/configuration-cheatsheet.md`.

## Entry points

- `AddMonitoringApi()` + `MapEverTaskApi()`. `AddMonitoringApi()` auto-calls `AddSignalRMonitoring()` unless
  a `SignalRTaskMonitor` is already registered as `ITaskMonitor`; `AddEverTaskMonitoringApiStandalone()`
  (no EverTask host) deliberately does NOT.
- OpenAPI: built-in ASP.NET Core generator, **net9+ only**, no Swashbuckle; isolated document at
  `/evertask-monitoring/openapi/evertask-monitoring.json`. Analyzer ET0008 warns when
  `EnableOpenApiDocument` is set on net8.0; `EnableSwagger` is an `[Obsolete]` no-op (#20).
- Scalar: `EverTask.Monitor.Api.Scalar` plugs in via `IMonitoringApiEndpointExtension` resolved in
  `MapEverTaskApi`; `AddMonitoringApiScalar()` throws unless it follows `AddMonitoringApi()`.

## Invariants and gotchas

- **Never mutate the host pipeline (#21)**: the JSON contract (camelCase, nulls omitted, enums as strings)
  is attached per-controller by `MonitoringJsonResultFilter` through `RoutePrefixConvention`, NEVER via
  `AddJsonOptions`, which rewrites the host's shared MVC `JsonOptions`. CORS likewise runs inside a
  `UseWhen` branch under `BasePath` (`Infrastructure/EverTaskApiStartupFilter.cs`).
- Middleware order: CORS branch, then `JwtAuthenticationMiddleware` — IP whitelist (403) BEFORE JWT (401 +
  `WWW-Authenticate`). Anonymous skips: `{ApiBasePath}/config|auth/login|auth/validate|auth/magic`.
- JWT only — no Basic Auth, and deliberately not `AddAuthentication().AddJwtBearer()`: the custom
  middleware must cover API + hub + UI and accept `?access_token=` for the SignalR handshake.
- `MagicLinkToken`: the UI reads the `/magic#token=` fragment (never sent to the server) and posts it to
  `POST /api/auth/magic`; `GET ?token=` is `[Obsolete]` and OpenAPI-deprecated because the query lands in
  request logs (#22). Both share the login rate-limit policy, use `FixedTimeEquals`, answer `no-store`.
- **`wwwroot/` is gitignored** (`.gitignore:292`) and shipped as `<EmbeddedResource>`: pack without first
  running the UI build (`pnpm run build` in `UI/`, never npm) and the dashboard answers "Dashboard UI not
  found". Served by `ManifestEmbeddedFileProvider`, falling back to `EmbeddedFileProvider` without a
  manifest, as in tests (`Extensions/EndpointRouteBuilderExtensions.cs`).
- `SignalRHubPath` is readonly `/evertask-monitoring/hub`; it cannot be reconfigured.
- Storage holds assembly-qualified type names; DTOs shorten them with `GetShortTypeName` EXCEPT
  `TaskDetailDto`, which returns `task.Type` as stored.
- `StatusAudits` and `RunsAudits` both come back **newest-first** (`OrderByDescending`).
- Average execution time is `Completed/Failed − InProgress` read from `StatusAudits`, over completed AND
  failed tasks (`Services/DashboardService.cs`) — not `LastExecutionUtc − CreatedAtUtc`.
- Tests: `test/EverTask.Tests.Monitoring/` — `WebApplicationFactory<TestProgram>` via
  `TestHelpers/MonitoringTestWebAppFactory.cs`; `API/HostIsolationTests.cs` pins the #21 invariants above.
