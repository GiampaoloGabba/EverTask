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
- **The two audit trails are READ from the storage** (`ITaskStorage.GetStatusAudits` / `GetRunsAudits`), for
  the same reason as `GetLastRunStarts`: nothing populates `row.StatusAudits` / `row.RunsAudits`, so the two
  endpoints and the detail's two blocks answered the whole history over the in-memory store and `[]` over all
  four relational ones — for a task whose audit tables hold every transition it ever made. The detail and the
  endpoints share one reader each (`ReadStatusAuditsAsync` / `ReadRunsAuditsAsync`), so they cannot drift.
  Both come back **newest-first**, ordered by the storage on the audit IDENTITY (insertion order; SQLite
  refuses a `DateTimeOffset` in an `ORDER BY`); so does `GET /tasks/{id}/occurrences`, on the nominal slot.
- **Schedule facts come out of two JSON columns, read through EverTask's own internals.** `OccurrenceMode`,
  `MisfirePolicy` and `TimeZoneId` live inside the serialized `RecurringTask`; the occurrence metadata and the
  catch-up halt are the two shapes of `RuntimeInfo`, told apart by `ParentTaskId`. `Services/TaskScheduleFacts`
  is the ONE reader — it uses `EverTaskJson` and the internal `OccurrenceRuntimeInfo`/`ScheduleRuntimeInfo`
  (hence `InternalsVisibleTo("EverTask.Monitor.Api")` in `src/EverTask/GlobalUsings.cs`), parses once per row,
  and never throws on what a column holds: a row nobody can parse loses a badge, it does not fail the endpoint.
- **New DTO fields are `init` properties in the record body, never appended positional parameters** (X4): the
  primary constructor and `Deconstruct` signatures of every public DTO stay what they were.
- **The schedule fields are absent TOGETHER on a row that belongs to no schedule**, `ScheduleVersion`
  included — hence `int?` and `ScheduleVersionOf`, which answers from the row's shape
  (`IsRecurring || ParentTaskId != null`) and not from the column, which is 0 on every row in the store. The
  docs promise "nulls are omitted, so a one-shot carries none of them", and a non-nullable field made that
  promise false for one of the six.
- **`StartedAtUtc` is the only term lateness may be measured against** (`Services/TaskRunTiming`).
  `LastExecutionUtc` is written by storage on TERMINAL transitions, so it is when a run ENDED: the UI badge
  built on it reported a punctual occurrence with a three-minute handler as three minutes late. The start is
  the row's own `InProgress` transition, **read from the storage** (`ITaskStorage.GetLastRunStarts`, one query
  per page): `Get`/`GetAll`/`GetOccurrencesPage` populate no navigation, so walking `row.StatusAudits` here
  answered only over the in-memory store — the same row got a different answer per backend, and the source
  the docs call the only one that can speak for a run still in flight was dead on all four relational
  providers.
  - **Derive a start only from a run that MEASURED one** (`TaskRunTiming.MeasuredDurationMs`: `Completed`
    AND `ExecutionTimeMs > 0`), because a completion is the only write that stamps the end and the duration
    together. Everything else stamps the end and leaves the duration at 0, so counting it back returns the END
    as the start — the whole execution reported as tardiness, the very defect the term exists to remove. That
    is a failure (`SetStatus` with no duration), a row failed without ever reaching a handler, and the two
    finalizations that close a row NO handler ran for: the materialization that spends a durable schedule's
    last run, and recovery finalizing a series whose remaining slots fall past its bound. 0 means unmeasured,
    never instant.
  - **A row WAITING for its delivery reports no start**, whatever the audit trail holds: an earlier attempt's
    start is not the start of the run the row now stands for. That is a recurring row between two runs and a
    requeued occurrence, both of which used to report the previous attempt.
- **A halt is reported only while the series could still run** (`TaskScheduleFacts.HasStandingHalt`, used by
  the detail and by the overview counter alike). Nothing clears the marker when a series ends — a cancel and
  the recovery finalization both leave `RuntimeInfo` exactly as they found it, having nothing left to halt —
  so reading the column alone kept a cancelled schedule alarming for ever on the one counter the docs tell
  operators to alert on.
- **`GET /tasks/{id}/occurrences` pages in the STORAGE** (`ITaskStorage.GetOccurrencesPage`), never in the
  service: a schedule with a long retention behind it holds hundreds of thousands of occurrence rows, and
  ordering and slicing them here meant reading all of them to show a hundred. `skip`/`take` are clamped to
  non-negative before the call — they end up in an OFFSET/FETCH clause, where a negative is an error rather
  than an empty page.
- `OverviewDto.CatchUpBacklog` counts occurrence ROWS by state over the WHOLE store, ignoring the selected
  range — a backlog is what is owed now. Slots a schedule dropped never became rows and are absent by
  construction; they are reported by the `OccurrenceSkipped` monitoring event instead.
- **Every average execution time in the API is `TaskRunTiming.AverageMeasuredDurationMs`** — the mean of the
  durations completions really measured, never `LastExecutionUtc − CreatedAtUtc` (that is queue time too) and
  no longer `Completed − InProgress` off `StatusAudits`, which made the overview's tile answer `0.0` on every
  relational store and on every audit level below `Full`. The overview, the queue metrics and the queue
  configurations share the one rule, so the same rows cannot produce three different means.
- Tests: `test/EverTask.Tests.Monitoring/` — `WebApplicationFactory<TestProgram>` via
  `TestHelpers/MonitoringTestWebAppFactory.cs`; `API/HostIsolationTests.cs` pins the #21 invariants above.
  That factory registers `AddMemoryStorage()`, the ONE store that keeps the audits on the row object, so a
  reader that walks a navigation passes the whole suite while answering nothing in production:
  `API/Services/RelationalAuditReadTests.cs` runs the services over a real EF Core store (SQLite, migrations
  applied) for exactly that class of defect.
