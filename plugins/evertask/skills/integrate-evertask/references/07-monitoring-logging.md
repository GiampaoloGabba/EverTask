# 07 — Monitoring & logging

Three monitoring tiers (pick by need) + Serilog + persistent DB logs.

## Tier 1 — In-code event subscription (no extra package)

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
IReadOnlyList<TaskExecutionLog>? ExecutionLogs = null)` — positional record; `Severity` is a string
(compare with `nameof(SeverityLevel.Error)` etc.). Events: Started/Completed/Failed/Cancelled/
Timeout/RecurringScheduled + rate-limit deferred/fail-open/rejected. Handler is
`Func<EverTaskEventData, Task>`; keep it fast (fire-and-forget slow work). Monitoring failures
never block task execution.

## Tier 2 — SignalR events — `EverTask.Monitor.AspnetCore.SignalR`

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

## Tier 3 — Dashboard + REST API — `EverTask.Monitor.Api`

Full embedded React dashboard + REST API; auto-registers SignalR. **ASP.NET Core only.**

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
    options.MagicLinkToken       = null;     // set a 32+ char token to enable /auth/magic
    options.EnableSwagger        = false;    // default
    options.EventDebounceMs      = 1000;     // dashboard cache-invalidation debounce
});
```

**Required after `Build()`:** `app.MapEverTaskApi();` (maps hub + controllers + SPA). It also accepts
an optional `Action<HttpConnectionDispatcherOptions>` to tune the SignalR hub connection.

> ⚠ **CORS is registered but NOT applied.** `EnableCors = true` only **registers** a named CORS policy
> (`EverTaskMonitoringApi`) — EverTask's pipeline never calls `UseCors`/`RequireCors`, so the policy has
> no effect until **you** apply it in your app (e.g. `app.UseCors("EverTaskMonitoringApi")`). Setting
> `CorsAllowedOrigins` alone does nothing for the monitoring endpoints. (`BasePath`, `ApiBasePath`,
> `UIBasePath`, `SignalRHubPath` are read-only computed properties — don't try to set them.)

Fixed paths: dashboard `/evertask-monitoring`, API `/evertask-monitoring/api`, hub
`/evertask-monitoring/hub`. Auth is a custom JWT middleware (IP whitelist first → JWT via
`Authorization: Bearer` or `?access_token=`). Login: `POST /evertask-monitoring/api/auth/login`
`{username,password}`. Default creds `admin`/`admin` — **always change in production.** Magic link
when `MagicLinkToken` set: `GET .../api/auth/magic?token=...`.

REST endpoints (under `/evertask-monitoring/api`): `GET /tasks` (filter status/queue/type/date,
paged ≤100), `/tasks/{id}` (+ `/status-audit`, `/runs-audit`, `/execution-logs`),
`/dashboard/overview`, `/dashboard/recent-activity`, `/queues`, `/queues/{name}/tasks`,
`/statistics/{success-rate-trend|task-types|execution-times}`, `/rate-limits` (per-key parked count,
next slot, tracked keys, fail-open count — in-memory, single-node), `/config` (no auth).

Standalone (no `EverTaskServiceBuilder`): `services.AddEverTaskMonitoringApiStandalone(...)` —
then you must register `ITaskStorage` yourself. It does **not** auto-register SignalR monitoring,
and there is no `IServiceCollection` overload of `AddSignalRMonitoring` (that method exists only on
`EverTaskServiceBuilder`). `MapEverTaskApi()` still maps the hub endpoint, but without the monitor
subscription no live events are pushed; for live dashboard updates use
`AddEverTask(...).AddMonitoringApi(...)`.

## Serilog — `EverTask.Logging.Serilog`

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
2. Only real-time events, no dashboard? → SignalR + `MapEverTaskMonitorHub()`.
3. Only react in code (log/alert/forward to APM)? → subscribe `TaskEventOccurredAsync`.
4. Need execution logs streamed? → `IncludeExecutionLogs = true`.
5. Dedicated Serilog pipeline? → `.AddSerilog(...)`. Else host `ILogger` is used automatically.
6. Persist handler logs to DB? → `.WithPersistentLogger(...)` + retention cleanup.
7. Multi-server SignalR? → add a backplane.
