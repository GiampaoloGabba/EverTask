# 07: Monitoring & logging

Three monitoring tiers (pick by need) + Serilog + persistent DB logs.

## Tier 1: In-code event subscription (no extra package)

Subscribe to the worker executor's event in a singleton service:

```csharp
public class TaskMonitoringService
{
    public TaskMonitoringService(IEverTaskWorkerExecutor executor, ILogger<TaskMonitoringService> logger)
    {
        executor.TaskEventOccurredAsync += eventData =>
        {
            if (eventData.Severity == nameof(SeverityLevel.Error))
                logger.LogError("Task {Id} failed: {Msg}\n{Ex}",
                    eventData.TaskId, eventData.Message, eventData.Exception);
            return Task.CompletedTask;
        };
    }
}
// builder.Services.AddSingleton<TaskMonitoringService>();
```

`EverTaskEventData(Guid TaskId, DateTimeOffset EventDateUtc, string Severity, string TaskType,
string TaskHandlerType, string TaskParameters, string Message, string? Exception = null,
IReadOnlyList<TaskExecutionLog>? ExecutionLogs = null)`: positional record; `Severity` is a string
(compare with `nameof(SeverityLevel.Error)` etc.). Events: Started/Completed/Failed/Cancelled/
Timeout/RecurringScheduled + rate-limit deferred/fail-open/rejected. Handler is
`Func<EverTaskEventData, Task>`; keep it fast (fire-and-forget slow work). Monitoring failures
never block task execution.

## Tier 2: SignalR events (`EverTask.Monitor.AspnetCore.SignalR`)

Four overloads (all on `EverTaskServiceBuilder`, all return it):

```csharp
.AddSignalRMonitoring()                                          // defaults
.AddSignalRMonitoring(Action<SignalRMonitoringOptions> monitoringConfiguration)  // e.g. o => o.IncludeExecutionLogs = true (off by default)
.AddSignalRMonitoring(Action<HubOptions> hubConfiguration)       // tune the SignalR hub (message size, timeouts, keep-alive)
.AddSignalRMonitoring(Action<HubOptions> hubConfiguration, Action<SignalRMonitoringOptions> monitoringConfiguration)  // both
```

**Required after `Build()`** (else no events reach clients):

```csharp
app.MapEverTaskMonitorHub();              // default route /evertask-monitoring/hub
app.MapEverTaskMonitorHub("/custom/path"); // standalone route IS configurable (pass any pattern)
app.MapEverTaskMonitorHub("/custom/path", hub => hub.TransportMaxBufferSize = 1024 * 1024); // + hub dispatcher options
```

> The custom-pattern overloads above apply to **standalone** SignalR. When the hub is mapped by `MapEverTaskApi()` (Tier 3), the route is **fixed** at `/evertask-monitoring/hub` (the read-only `EverTaskApiOptions.SignalRHubPath`) and cannot be changed.

Server-to-client only; client event name is **`"EverTaskEvent"`**. JS:

```javascript
const c = new signalR.HubConnectionBuilder().withUrl("/evertask-monitoring/hub").withAutomaticReconnect().build();
c.on("EverTaskEvent", e => { /* e.taskId, e.severity, e.message, ... */ });
await c.start();
```

Multi-server: add a backplane (`AddSignalR().AddAzureSignalR(...)` or `.AddStackExchangeRedis(...)`).

## Tier 3: Dashboard + REST API (`EverTask.Monitor.Api`)

Full embedded React dashboard + REST API; auto-registers SignalR. **ASP.NET Core only.**

Additive by design (4.0+): route prefix, JSON contract (camelCase/string enums), CORS, SPA
fallback and OpenAPI document all apply to the monitoring endpoints only. The host's controllers
keep their routes and MVC JsonOptions, and a host SPA fallback keeps working (issue #21).

```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI             = true;     // default
    options.EnableAuthentication = true;     // default (JWT)
    options.Username             = Environment.GetEnvironmentVariable("MONITOR_USER") ?? "admin";
    options.Password             = Environment.GetEnvironmentVariable("MONITOR_PASS")!;  // CHANGE from "admin"
    options.JwtSecret            = Environment.GetEnvironmentVariable("MONITOR_JWT");     // set for multi-instance
    options.JwtIssuer            = "EverTask.Monitor.Api";  // default
    options.JwtAudience          = "EverTask.Monitor.Api";  // default
    options.JwtExpirationHours   = 8;        // default
    options.EnableCors           = true;     // default
    options.CorsAllowedOrigins   = new[] { "https://myapp.com" };   // empty = allow all
    options.AllowedIpAddresses   = new[] { "10.0.0.0/8" };          // empty = allow all; CIDR ok
    options.MagicLinkToken       = null;     // set a 32+ char token to enable /magic#token=... (see below)
    options.EnableOpenApiDocument = false;   // default; true serves the OpenAPI doc at
                                             // /evertask-monitoring/openapi/evertask-monitoring.json (net9+)
    options.EventDebounceMs      = 1000;     // dashboard cache-invalidation debounce
    // Write surface (4.0+), OFF by default. Enabling it is not enough: a caller must also carry the
    // operate role, which ONLY the second credential below grants (never Username/Password, never a
    // magic link). Or replace the role check with the host's own authorization.
    options.EnableManagementEndpoints = false;
    // Registration THROWS if only one half is set, or if the password equals Password or MagicLinkToken.
    options.ManagementUsername        = Environment.GetEnvironmentVariable("MONITOR_OPERATE_USER");
    options.ManagementPassword        = Environment.GetEnvironmentVariable("MONITOR_OPERATE_PASS");
    // Func<HttpContext, Task<bool>>; when set it REPLACES the role check. Runs inside routing, after the
    // host's UseAuthentication, so ctx.User is the app's own principal.
    options.ManagementAuthorization   = null;
});
```

Optional Scalar API reference (`EverTask.Monitor.Api.Scalar` package, net9+): chain
`.AddMonitoringApiScalar()` after `AddMonitoringApi()` to serve an interactive API reference at
`/evertask-monitoring/scalar` (auto-enables the OpenAPI document). Everything stays under the
monitoring base path: the host's own OpenAPI/Swagger/Scalar setup is never touched.
(`EnableSwagger` is an obsolete no-op since 4.0.0.)

**Required after `Build()`:** `app.MapEverTaskApi();` (maps hub + controllers + SPA). It also accepts
an optional `Action<HttpConnectionDispatcherOptions>` to tune the SignalR hub connection.

> CORS (4.0+): `EnableCors = true` applies the `EverTaskMonitoringApi` policy to requests under
> `/evertask-monitoring` automatically; the host pipeline is untouched and nothing needs wiring.
> Login rate limit (4.0+): the `evertask-monitoring-login` policy (5 attempts/15 min per IP, 429)
> covers `/api/auth/login` and both `/api/auth/magic` forms; it is registered by the package but
> enforced only if the host runs `app.UseRateLimiter()` after `UseRouting()`. (`BasePath`,
> `ApiBasePath`, `UIBasePath`, `SignalRHubPath` are read-only computed properties; don't try to
> set them.)

Fixed paths: dashboard `/evertask-monitoring`, API `/evertask-monitoring/api`, hub
`/evertask-monitoring/hub`. Auth is a custom JWT scheme (IP whitelist first → JWT via
`Authorization: Bearer`, or `?access_token=` on the hub alone). Login: `POST /evertask-monitoring/api/auth/login`
`{username,password}`. Default creds `admin`/`admin`: **always change in production.**
Enforced inside routing since 4.0+, so the whitelist, the JWT and the hub handshake all hold under
`app.UsePathBase(...)` — before that a path base skipped every one of them (issue #46). CORS is the
exception: its branch still keys off the pre-`UsePathBase` path.

`AllowedIpAddresses` compares `Connection.RemoteIpAddress` and **does not read `X-Forwarded-For`**
(breaking in 4.0: it used to trust the header, so anyone could spoof a whitelisted address — issue #47).
Behind a reverse proxy, wire the framework's own middleware and let it rewrite the address:
`Configure<ForwardedHeadersOptions>` with `ForwardedHeaders.XForwardedFor` + `KnownProxies`/`KnownNetworks`,
then `app.UseForwardedHeaders()` before `UseRouting()`. Without `KnownProxies` nothing is forwarded, which
is the point: only a peer you named may be believed.

Magic link when `MagicLinkToken` is set (4.0+): hand users
`https://host/evertask-monitoring/magic#token=<MagicLinkToken>`. The fragment never reaches the
server; the dashboard exchanges it with `POST /api/auth/magic` `{token}`. Never generate a
`?token=` URL or call `GET /api/auth/magic?token=` (deprecated, kept for compat): the query string
is written verbatim by Serilog `UseSerilogRequestLogging()` (`RawTarget`), reverse proxies and
browser history, and the token never expires (issue #22). When the host app links to the
dashboard, redirect server-side to the fragment URL instead of embedding the token in the
frontend. If a legacy `?token=` link must stay, branch request logging around the monitoring path:
`app.UseWhen(ctx => !ctx.Request.Path.StartsWithSegments("/evertask-monitoring"), b => b.UseSerilogRequestLogging());`.

REST endpoints (under `/evertask-monitoring/api`): `GET /tasks` (filter status/queue/type/date,
paged), `/tasks/{id}` (+ `/status-audit`, `/runs-audit`, `/execution-logs`, `/occurrences`),
`/tasks/counts`, `/dashboard/overview`, `/dashboard/recent-activity`, `/queues`,
`/queues/{name}/tasks`, `/statistics/{success-rate-trend|task-types|execution-times}`,
`/rate-limits` (per-key parked count, next slot, tracked keys, fail-open count; in-memory,
single-node), `/config` (no auth). Every endpoint is **read-only** except the three management ones
below; from application code, changing a schedule at runtime is still `ITaskScheduleManager`, behind
your own authorization (`05-scheduling.md`).

The two audit trails are **paged** (4.0+): `GET /tasks/{id}/status-audit` and `/runs-audit` take
`skip`/`take` (default 0/100) and answer `{audits, totalCount, skip, take}` — not a bare array. The
detail's `statusAudits`/`runsAudits` blocks carry only the FIRST page and report
`statusAuditsTotalCount`/`runsAuditsTotalCount`: a long-lived recurring row records one transition per
state per run, so nothing serves the whole history at once any more.

Management endpoints (4.0+, `POST`, no body, task id in the path):
`/management/tasks/{id}/requeue` (a terminal occurrence back in the queue),
`/management/tasks/{id}/resume` (release a halted catch-up, cursor and backlog KEPT),
`/management/tasks/{id}/cancel` (cancel the schedule and every pending occurrence, terminal). They
answer `{status, message, taskId, nextRunUtc?, releasedHalt}` with 200 / 404 / 409 / 501 / 503. 404 on
every route while `EnableManagementEndpoints` is false; 403 for a read-only session. `resume` and
`cancel` resolve the row's `taskKey`, so a schedule dispatched without one answers 409. No CSRF token
is needed (Bearer header, never a cookie) — keep the dashboard token out of cookies. The gate is an MVC
authorization filter on those routes, so it holds under `app.UsePathBase(...)` as well.

Durable schedules (`.WithDurableOccurrences()` / `.OnMisfire(...)`, see `05-scheduling.md`) show up
in three places. Task DTOs carry `parentTaskId`, `occurrenceMode`, `misfirePolicy`, `timeZoneId`,
`scheduleVersion`, `nominalSlotUtc` and `misfireKind` (nulls omitted, and they are null TOGETHER on a
task that belongs to no schedule — `scheduleVersion` included, so a plain one-shot carries none of
them rather than a version of 0); `/tasks/{id}` adds an `occurrence` block on a child and a `halt`
block on a schedule whose catch-up stopped itself over its cap; `/tasks/{id}/occurrences` lists what
a schedule materialized, newest slot first, paged by the storage itself. Filter the list with `parentTaskId`, `onlyOccurrences` and
`onlyCatchUp`. `/dashboard/overview` adds `catchUpBacklog`: the occurrences of every durable
schedule by state (pending / active / failed / skipped / completed), the oldest slot that has not
started, how far behind it is, and how many schedules are halted — a halt never releases itself, so
that counter is the one to alert on, and it counts only the schedules that are still live: a halted
series someone cancelled keeps its marker, since nothing clears it, but stops being reported. Slots a
schedule DROPPED never became rows and are not in those counts; they arrive as `OccurrenceSkipped`
events, which always name the rule that dropped them.

To show how late a delivery is, use `startedAtUtc` (when its run began) and never `lastExecutionUtc`,
which is written on terminal transitions and so says when the run ENDED — a punctual occurrence with
a three-minute handler would read as three minutes late. It is read from the row's `InProgress`
transition in the audit trail, so a run still in flight answers for itself at `AuditLevel.Full`;
below that level a run that FINISHED is derived from its end less its measured duration, and
everything else — a row that never ran, a failure or a finalization that measured no duration, a row
waiting for its next delivery — reports nothing rather than an instant nobody measured.

The two audit trails (`/status-audit`, `/runs-audit`, and the `statusAudits`/`runsAudits` blocks of
`/tasks/{id}`) are read from the audit tables, so they answer the same history whatever storage is
behind the API, and `avgExecutionTimeMs` on the overview is the mean of the durations completions
measured — the same column `executionTimeMs` reports per task, with the runs nobody measured left out
rather than counted as zero.

Events from the durable side reach the same `TaskEventOccurredAsync` channel, carrying
`ScheduledAtUtc` (the nominal slot) and `ScheduleVersion`, plus `ParentTaskId` on the events of an
OCCURRENCE — a schedule-level event is about the schedule row itself, so there its own `TaskId` is
the schedule id: occurrence materialized, occurrence skipped, stale occurrence requeued, catch-up
started and completed (the two ends of one replay), catch-up halted (rate-limited to one per schedule
every five minutes), schedule rescheduled, re-park failed, occurrence-provider evaluation failed, and
the two `Error` events for a row this build cannot rebuild (a schedule that materializes nothing, an
occurrence marked `Failed`).

Standalone (no `EverTaskServiceBuilder`): `services.AddEverTaskMonitoringApiStandalone(...)`;
then you must register `ITaskStorage` yourself. It does **not** auto-register SignalR monitoring,
and there is no `IServiceCollection` overload of `AddSignalRMonitoring` (that method exists only on
`EverTaskServiceBuilder`). `MapEverTaskApi()` still maps the hub endpoint, but without the monitor
subscription no live events are pushed; for live dashboard updates use
`AddEverTask(...).AddMonitoringApi(...)`.

## Serilog (`EverTask.Logging.Serilog`)

Replaces EverTask's internal `IEverTaskLogger<T>` with a **dedicated** Serilog pipeline (separate
from the host's `ILogger`).

```csharp
.AddSerilog()                                  // Console sink default
.AddSerilog(c => c.MinimumLevel.Information().WriteTo.Console()
    .WriteTo.File("logs/evertask-.txt", rollingInterval: RollingInterval.Day).Enrich.FromLogContext())
.AddSerilog(c => c.ReadFrom.Configuration(builder.Configuration,
    new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }))   // from appsettings
```

Without it, EverTask logs flow to the host's `ILogger<T>` automatically (no setup). LogLevel maps
Trace→Verbose, Critical→Fatal.

Per-task lifecycle lines (task started/completed, storage status transitions) are logged at `Debug`;
set the `EverTask` category to `Debug` to see one line per task. Warnings and errors stay visible at
the default level; the monitoring events (SignalR/dashboard) are not affected by the log level.

## Persistent execution logs (DB)

```csharp
opt.WithPersistentLogger(log => log.SetMinimumLevel(LogLevel.Information).SetMaxLogsPerTask(1000))
```

Handler `Logger.Log*` calls are persisted (and always also sent to `ILogger`). Retrieve via the
ergonomic extension `storage.GetLogsAsync(taskId[, pageNumber, pageSize])` (1-based paging, optional
`CancellationToken`), or the raw `storage.GetExecutionLogsAsync(taskId[, skip, take], ct)` (the `ct`
is required). Pair with `AddAuditCleanup(...)` retention
(`03-storage.md`) to bound growth. Surfaced in the dashboard's Execution Logs tab and (if
`IncludeExecutionLogs`) in SignalR events.

## Wizard decision points

1. Dashboard? → `EverTask.Monitor.Api` + `MapEverTaskApi()` (web only). Set auth creds.
   If the app runs durable schedules, point the operator at the overview's `catchUpBacklog` and at
   the schedule detail's Occurrences tab: that is where a halted catch-up and a growing backlog are
   visible.
2. Only real-time events, no dashboard? → SignalR + `MapEverTaskMonitorHub()`.
3. Only react in code (log/alert/forward to APM)? → subscribe `TaskEventOccurredAsync`.
4. Need execution logs streamed? → `IncludeExecutionLogs = true`.
5. Dedicated Serilog pipeline? → `.AddSerilog(...)`. Else host `ILogger` is used automatically.
6. Persist handler logs to DB? → `.WithPersistentLogger(...)` + retention cleanup.
7. Multi-server SignalR? → add a backplane.
