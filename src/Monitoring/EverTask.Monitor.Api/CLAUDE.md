# CLAUDE.md - EverTask.Monitor.Api

REST API + embedded React dashboard for EverTask monitoring. Read-only endpoints for task query, statistics, real-time SignalR events.

## Quick Facts

- **Package**: `EverTask.Monitor.Api` (multi-target: net8.0, net9.0, net10.0 from `Directory.Build.props`)
- **Type**: Class library (embedded resources: `wwwroot/`)
- **Auth**: JWT only (no Basic Auth)
- **UI**: Optional React SPA (see `UI/CLAUDE.md`)
- **Entry points**: `.AddMonitoringApi()`, `.MapEverTaskApi()`
- **OpenAPI**: built-in ASP.NET Core generator (net9+ only, NO Swashbuckle), isolated document
  `/evertask-monitoring/openapi/evertask-monitoring.json`; controllers carry ApiExplorer group
  `evertask-monitoring` via `RoutePrefixConvention` (which touches ONLY this assembly's controllers)
- **Scalar**: sibling package `EverTask.Monitor.Api.Scalar` plugs in via `IMonitoringApiEndpointExtension`
  (resolved in `MapEverTaskApi`); `AddMonitoringApiScalar()` must follow `AddMonitoringApi()` and
  force-enables `EnableOpenApiDocument`

## Build

```bash
dotnet build -c Release
dotnet pack -o ../../nupkg

# UI build (separate)
cd UI && npm run build  # → ../wwwroot/
```

## Critical Configuration

**EverTaskApiOptions.cs**:
- `EnableOpenApiDocument` (bool, default: false) - OpenAPI doc, net9+ only (net8 no-op, analyzer ET0008 warns)
- `EnableSwagger` - OBSOLETE no-op since 3.12 (Swashbuckle removed, issue #20)
- `EnableAuthentication` (bool, default: true) - JWT auth on/off
- `EnableUI` (bool, default: true) - Serve embedded React dashboard
- `Username/Password` (string, default: "admin"/"admin") - JWT login credentials
- `JwtSecret` (string?, auto-generated if null) - Signing key (256-bit min recommended)
- `JwtExpirationHours` (int, default: 8) - Token TTL
- `SignalRHubPath` (readonly: "/evertask-monitoring/hub") - Fixed, cannot change
- `AllowedIpAddresses` (string[], default: empty = allow all) - IP whitelist (CIDR supported)
- `MagicLinkToken` (string?, default: null) - Static token for instant auth via `/api/auth/magic?token=...`

## Architecture

### Controllers → Services → ITaskStorage
- **Controllers**: Map to `/api/*` (derived from BasePath)
- **Services**: `ITaskQueryService`, `IDashboardService`, `IStatisticsService`
- **Storage**: Depends on `ITaskStorage` (injected via DI)

### Middleware Chain
0. Scoped CORS branch (`UseWhen` path under BasePath → `UseCors(EverTaskApiOptions.CorsPolicyName)`) when `EnableCors` — host pipeline untouched (#21)
1. IP whitelist check (if configured) → 403 if blocked
2. JWT authentication (if `EnableAuthentication = true`)
   - Skips: `/api/config`, `/api/auth/login`, `/api/auth/validate`, `/api/auth/magic`
   - Validates: `Authorization: Bearer <token>`
   - Returns 401 + WWW-Authenticate challenge if invalid

### JSON Serialization
- camelCase properties (`JsonNamingPolicy.CamelCase`)
- Null values omitted (`JsonIgnoreCondition.WhenWritingNull`)
- Enums as strings (`JsonStringEnumConverter`)
- Applied ONLY to this package's controllers via `MonitoringJsonResultFilter` (scoped
  `SystemTextJsonOutputFormatter` attached per-controller by `RoutePrefixConvention`) — never via
  `AddJsonOptions`, which would rewrite the host's shared MVC JsonOptions (#21)

## Key Gotchas

1. **JWT Only**: No Basic Auth. Deleted `BasicAuthenticationMiddleware` in favor of `JwtAuthenticationMiddleware`.
2. **Fixed Paths**: `SignalRHubPath` is readonly `/evertask-monitoring/hub` (cannot be changed).
3. **UI Embed**: `wwwroot/` files are `<EmbeddedResource>` in `.csproj`, served via `ManifestEmbeddedFileProvider`.
4. **Auto SignalR**: `.AddEverTaskApi()` auto-registers SignalR monitoring if not already added.
5. **Type Names**: Backend stores full assembly-qualified names. DTOs use short names (`Type.GetType()?.Name`).
6. **Recurring Tasks**: Check `IsRecurring` field, populate `RecurringInfo` (human-readable schedule).
7. **Audit Tables**: `StatusAudits` (Oldest→Newest), `RunsAudits` (Newest→Oldest).

## Operational Checklist

### Adding New API Endpoint
1. Add method to service interface + implementation
2. Add controller endpoint with `[HttpGet]` / `[HttpPost]`
3. Add DTO in `DTOs/` (camelCase JSON)
4. Update `UI/src/services/api.ts` if UI needs it
5. Update XML docs on controller methods

### Modifying Authentication
- **Auth logic**: `Middleware/JwtAuthenticationMiddleware.cs`
- **Token generation**: `Controllers/AuthController.cs`
- **Options**: `Options/EverTaskApiOptions.cs`
- **Service registration**: `Extensions/ServiceCollectionExtensions.cs`

### Testing
- **Test project**: `test/EverTask.Tests.Monitoring/`
- **Auth tests**: `API/Middleware/JwtAuthenticationMiddlewareTests.cs`
- **Controller tests**: Use `WebApplicationFactory<T>`

## Service Implementation Notes

### ITaskQueryService
- Use `ITaskStorage.Get()` with LINQ expressions
- Apply filters → pagination (Skip/Take) → project to DTOs
- Sort by `SortBy` property (default: `CreatedAtUtc`)

### IDashboardService
- Convert `DateRange` enum to DateTime filters
- Success rate: `(Completed / (Completed + Failed)) * 100`
- Avg execution time: `Avg(LastExecutionUtc - CreatedAtUtc)` for completed tasks
- Group by hour for `TasksOverTime`, by queue+status for `QueueSummaries`

### IStatisticsService
- Convert `TimePeriod` to date range + interval (daily/weekly buckets)
- Calculate success rate per bucket/queue
- Use `GroupBy` for aggregations

## Related Files

- **UI Code**: `UI/` (see `UI/CLAUDE.md`)
- **User Docs**: `README.md`, `docs/monitoring-dashboard.md`
- **Tests**: `test/EverTask.Tests.Monitoring/`
