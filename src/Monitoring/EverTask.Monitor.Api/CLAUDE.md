# EverTask.Monitor.Api

Refer to the root CLAUDE.md for project-wide rules.

REST API + embedded React dashboard, all under the fixed `/evertask-monitoring` prefix. Options live in
`Options/EverTaskApiOptions.cs` (XML-documented), exhaustively tabled in `docs/configuration-cheatsheet.md`.

## Entry points

- `AddMonitoringApi()` + `MapEverTaskApi()`. The first auto-calls `AddSignalRMonitoring()` unless a
  `SignalRTaskMonitor` is already registered as `ITaskMonitor`; `AddEverTaskMonitoringApiStandalone()` (no host)
  deliberately does NOT. `SignalRHubPath` is readonly.
- OpenAPI: the built-in ASP.NET Core generator, **net9+ only**, no Swashbuckle, at
  `/evertask-monitoring/openapi/evertask-monitoring.json`. ET0008 warns when `EnableOpenApiDocument` is set on net8.0;
  `EnableSwagger` is an `[Obsolete]` no-op (#20).
- Scalar: `EverTask.Monitor.Api.Scalar` plugs in via `IMonitoringApiEndpointExtension` resolved in `MapEverTaskApi`;
  `AddMonitoringApiScalar()` throws unless it follows `AddMonitoringApi()`.

## Invariants and gotchas

- **`Infrastructure/MonitoringAccessPolicy` is the ONE place that decides access** — which surface a path is
  (`None`/`Ui`/`Api`/`Hub`, matched with `StartsWithSegments`, never a string prefix), the IP whitelist (403) BEFORE
  the JWT (401 + `WWW-Authenticate`), and the anonymous skips
  (`{ApiBasePath}/config|auth/login|auth/validate|auth/magic`). Its two callers — `MonitoringAccessFilter`
  (controllers), `MonitoringEndpointGuard` (the rest) — never re-implement a path test.
- **The client address is `Connection.RemoteIpAddress`, never a header (#47)** — trusting `X-Forwarded-For` made the
  whitelist advisory. A proxied host makes it true with `UseForwardedHeaders` + `KnownProxies`; BREAKING in 4.0.0,
  documented on `AllowedIpAddresses`.
- **The policy is enforced ONLY INSIDE routing**, because both its inputs are the host's to change: the path after
  `UsePathBase` (#46) and the address after `UseForwardedHeaders` (#47) come from middleware running after the startup
  filter's. Deciding earlier judges a path the surface does not have (anonymous 200s, no whitelist, an anonymous hub
  negotiate) and, now, the proxy's address. So it is `MonitoringAccessFilter` on every monitoring controller (via
  `RoutePrefixConvention`, ordered before the management gate) and `MonitoringEndpointGuard` on everything else;
  `JwtAuthenticationMiddleware` only 404s the disabled management prefix.
  - The cost: an endpoint under the prefix that EverTask did not map is not covered. The policy protects the endpoints
    we map, not the prefix.
  - The guard is a convention on the **route group with an empty prefix** `MapEverTaskApi` maps the hub, the dashboard
    files, the OpenAPI document and the companion packages into (a group because `MapEverTaskMonitorHub` exposes no
    convention builder; empty so no route changes). It wraps only endpoints whose ROUTE is under the base path, so
    `MapControllers` leaves the host's own untouched.
  - Known limitation, functional and not security: the CORS branch in `EverTaskApiStartupFilter` still tests the
    pre-`UsePathBase` path, so under a path base the monitoring CORS policy is not applied.
- **Never mutate the host pipeline (#21)**: the JSON contract (camelCase, nulls omitted, enums as strings) is attached
  per-controller by `MonitoringJsonResultFilter` through `RoutePrefixConvention`, NEVER via `AddJsonOptions`, which
  rewrites the host's shared MVC `JsonOptions`. CORS likewise runs in a `UseWhen` branch under `BasePath`.
- JWT only — no Basic Auth, and deliberately not `AddAuthentication().AddJwtBearer()`: the policy must cover API + hub
  + UI and accept `?access_token=` for the SignalR handshake, which no host scheme would look at; that query token is
  HUB-only. Expiry on a live connection is enforced by `MonitoringTokenExpirationHubFilter`, which re-validates the
  connection's own token in `OnConnectedAsync` — self-contained, because long polling CLONES the HttpContext and drops
  custom features. Registered via `AddSignalR().AddHubOptions<TaskMonitorHub>` (a bare `Configure<HubOptions<THub>>`
  is never consumed), only when `EnableAuthentication` is on.
- `MagicLinkToken`: the UI reads the `/magic#token=` fragment (never sent to the server) and posts it to `POST
  /api/auth/magic`; `GET ?token=` is `[Obsolete]` and OpenAPI-deprecated (the query lands in request logs, #22). Both
  share the login rate-limit policy, use `FixedTimeEquals`, answer `no-store`.
- **The management endpoints are the ONE write surface** (#42). `EnableManagementEndpoints` is off by default and,
  while it is, every path under `{ApiBasePath}/management` answers **404**. Once enabled the session must carry the
  **operate** role (`MonitoringRoles`, a `role` claim on the JWT), granted ONLY by the second credential
  `ManagementUsername`/`ManagementPassword` — the dashboard credential is shared and a magic link is a URL, so both
  stay read. The host's `ManagementAuthorization` hook REPLACES the role check when set, including when
  `EnableAuthentication` is false, where the answer without a hook is 403, never "open".
- **The authoritative gate is `Infrastructure/ManagementAuthorizationFilter`, an MVC authorization filter, NOT the
  middleware**, which reads `Request.Path` before `UsePathBase` moved the prefix out of it. That placement also lets
  the hook read `HttpContext.User`, since it runs after the host's `UseAuthentication`. `RoutePrefixConvention`
  attaches the filter by ROUTE, never by controller type, so a controller added under the prefix inherits the gate;
  evaluate the hook there ONLY, or authorization runs twice.
- **The operate credential is validated at registration** (`ValidateManagementCredentials`): half a pair, or a
  `ManagementPassword` equal to `Password` or `MagicLinkToken`, throws — a username is not a secret, so an equal
  password would promote the shared read credential to operate.
- Cross-site browser requests are rejected before authorization: `Sec-Fetch-Site` allows only
  same-origin/same-site/none, with an Origin-versus-request-origin fallback when fetch metadata is absent; requests
  with neither header pass as non-browser clients.
- `ITaskScheduleManager` is OPTIONAL in `ManagementService` (standalone has no EverTask host, and 501 is the honest
  answer); `resume`/`cancel` resolve the row's `TaskKey`, so a keyless schedule answers 409.
- **The two audit trails are READ from the storage, a PAGE at a time** (`ITaskStorage.GetStatusAuditsPage` /
  `GetRunsAuditsPage`, #44), like `GetLastRunStarts`: nothing populates `row.StatusAudits`/`row.RunsAudits`, so a
  navigation walk answers everything over the in-memory store and `[]` over all four relational ones. The detail and
  the endpoints share one reader each (`ReadStatusAuditsAsync`/`ReadRunsAuditsAsync`) so they cannot drift. Both come
  back **newest-first**, ordered by the storage on the audit IDENTITY (SQLite refuses a `DateTimeOffset` in an `ORDER
  BY`); `GET /tasks/{id}/occurrences` orders on the nominal slot and pages in the storage too. `skip` is clamped
  non-negative and `take` to `0..500` before every such call, since both reach an OFFSET/FETCH clause. The endpoints
  answer `{audits, totalCount, skip, take}`, NOT a bare array; the detail carries the first page plus the two total
  counts, with `ITaskQueryService.DefaultAuditPageSize` the shared default. The in-memory store assigns audits no
  identity (every `Id` is 0).
- **Schedule facts come out of two JSON columns, read through EverTask's own internals.** `OccurrenceMode`,
  `MisfirePolicy` and `TimeZoneId` live inside the serialized `RecurringTask`; the occurrence metadata and the
  catch-up halt are the two shapes of `RuntimeInfo`, told apart by `ParentTaskId`. `Services/TaskScheduleFacts` is the
  ONE reader (`EverTaskJson` + internal `OccurrenceRuntimeInfo`/`ScheduleRuntimeInfo`, hence
  `InternalsVisibleTo("EverTask.Monitor.Api")` in `src/EverTask/GlobalUsings.cs`), parsing once per row and never
  throwing on a column's contents: an unparsable row loses a badge, it does not fail the endpoint.
- **New DTO fields are `init` properties in the record body, never appended positional parameters**: the primary
  constructor and `Deconstruct` signatures of every public DTO stay what they were.
- **The schedule fields are absent TOGETHER on a row that belongs to no schedule**, `ScheduleVersion` included — hence
  `int?` and `ScheduleVersionOf`, which answers from the row's shape (`IsRecurring || ParentTaskId != null`) and not
  from the column, which is 0 on every row. The docs promise a one-shot carries none of them.
- Storage holds assembly-qualified type names; DTOs shorten them with `GetShortTypeName` EXCEPT `TaskDetailDto`, which
  returns `task.Type` as stored.
- **`StartedAtUtc` is the only term lateness may be measured against** (`Services/TaskRunTiming`): `LastExecutionUtc`
  is written on TERMINAL transitions, so it is when a run ENDED. The start is the row's own `InProgress` transition,
  read from the storage (`ITaskStorage.GetLastRunStarts`, one query per page).
  - **Derive a start only from a run that MEASURED one** (`MeasuredDurationMs`: `Completed` AND `ExecutionTimeMs >
    0`), since a completion is the only write stamping end and duration together; everything else leaves the duration
    at 0, so counting back returns the END as the start. 0 means unmeasured, never instant. **A row WAITING for its
    delivery reports no start**, whatever the audit trail holds: an earlier attempt's start is not the start of the
    run the row now stands for.
- **Every average execution time in the API is `TaskRunTiming.AverageMeasuredDurationMs`** — the mean of the durations
  completions really measured, never `LastExecutionUtc − CreatedAtUtc` (queue time too) and no longer `Completed −
  InProgress` off `StatusAudits`, which answered `0.0` on every relational store and below audit level `Full`.
  Overview, queue metrics and queue configurations share it.
- **A halt is reported only while the series could still run** (`TaskScheduleFacts.HasStandingHalt`, detail and
  overview counter alike): nothing clears the marker when a series ends, so the column alone would keep a cancelled
  schedule alarming for ever.
- `OverviewDto.CatchUpBacklog` counts occurrence ROWS by state over the WHOLE store, ignoring the selected range — a
  backlog is what is owed now. Dropped slots never became rows and are reported by `OccurrenceSkipped` instead.
- **`wwwroot/` is gitignored** (`.gitignore:292`) and shipped as `<EmbeddedResource>`: pack without first running the
  UI build (`pnpm run build` in `UI/`, never npm) and the dashboard answers "Dashboard UI not found". Served by
  `ManifestEmbeddedFileProvider`, falling back to `EmbeddedFileProvider` without a manifest.
- `test/EverTask.Tests.Monitoring/` — `WebApplicationFactory<TestProgram>` via
  `TestHelpers/MonitoringTestWebAppFactory.cs`; `API/HostIsolationTests.cs` pins the #21 invariants. That factory
  registers `AddMemoryStorage()`, the ONE store keeping audits on the row object, so a navigation walk passes the
  whole suite while answering nothing in production — hence `API/Services/RelationalAuditReadTests.cs`, over SQLite.
