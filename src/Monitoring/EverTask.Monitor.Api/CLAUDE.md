# CLAUDE.md - EverTask.Monitor.Api

REST API + embedded React dashboard for monitoring EverTask background tasks. Provides read-only endpoints for querying tasks, statistics, and real-time SignalR events.

## Project Overview

**Package Name**: `EverTask.Monitor.Api`
**Type**: .NET Class Library (multi-targeted: net6.0, net7.0, net8.0)
**Purpose**: General-purpose monitoring API (standalone or with embedded UI)

## Key Features

- REST API for task querying, statistics, and analytics
- Embedded React SPA dashboard (optional, can be disabled)
- Real-time monitoring via SignalR (auto-configured)
- Basic Authentication middleware
- CORS support for custom frontends
- Extension methods for easy integration (`.AddEverTaskApi()`, `.MapEverTaskApi()`)

## Project Structure

```
EverTask.Monitor.Api/
├── UI/                              # React dashboard source (see UI/CLAUDE.md)
│   └── CLAUDE.md                    # UI-specific documentation
├── wwwroot/                         # React build output (embedded in NuGet)
├── Controllers/                     # API controllers
│   ├── TasksController.cs           # GET /tasks, /tasks/{id}, /tasks/{id}/status-audit, /tasks/{id}/runs-audit
│   ├── DashboardController.cs       # GET /dashboard/overview, /dashboard/recent-activity
│   ├── QueuesController.cs          # GET /queues, /queues/{name}/tasks
│   ├── StatisticsController.cs      # GET /statistics/*
│   └── ConfigController.cs          # GET /config (no auth)
├── Services/                        # Business logic
│   ├── ITaskQueryService.cs + TaskQueryService.cs
│   ├── IDashboardService.cs + DashboardService.cs
│   └── IStatisticsService.cs + StatisticsService.cs
├── DTOs/                            # Data Transfer Objects
│   ├── Tasks/                       # TaskListDto, TaskDetailDto, StatusAuditDto, RunsAuditDto, etc.
│   ├── Dashboard/                   # OverviewDto, TasksOverTimeDto, QueueSummaryDto, etc.
│   ├── Queues/                      # QueueMetricsDto
│   └── Statistics/                  # SuccessRateTrendDto, ExecutionTimeDto, etc.
├── Middleware/
│   └── BasicAuthenticationMiddleware.cs
├── Options/
│   └── EverTaskApiOptions.cs        # Configuration options
├── Extensions/
│   ├── ServiceCollectionExtensions.cs   # .AddEverTaskApi()
│   └── EndpointRouteBuilderExtensions.cs # .MapEverTaskApi()
├── EverTask.Monitor.Api.csproj
└── README.md
```

## Build Commands

```bash
# Restore dependencies
dotnet restore

# Build
dotnet build

# Build release
dotnet build -c Release

# Create NuGet package
dotnet pack -o ../../nupkg

# Run tests (if test project exists)
dotnet test
```

## UI Build (Separate Process)

React dashboard lives in `UI/` subfolder. See `UI/CLAUDE.md` for details.

```bash
cd UI
npm install
npm run build  # Outputs to ../wwwroot/
```

UI is embedded in NuGet package via `<EmbeddedResource Include="wwwroot\**\*" />`.

## Configuration

**EverTaskApiOptions.cs** properties:

```csharp
BasePath: "/evertask"                  // Base path for API and UI
EnableUI: true                          // Enable/disable embedded dashboard
ApiBasePath: "{BasePath}/api"           // API endpoint path (auto-derived)
UIBasePath: "{BasePath}"                // UI path (auto-derived)
Username: "admin"                       // Basic Auth username
Password: "admin"                       // Basic Auth password
SignalRHubPath: "/evertask/monitor"    // SignalR hub path
RequireAuthentication: true             // Enable/disable Basic Auth
AllowAnonymousReadAccess: false         // Allow read endpoints without auth
EnableCors: true                        // Enable CORS
CorsAllowedOrigins: []                  // CORS origins (empty = allow all)
```

## Usage Examples

### Minimal Setup (API + UI)

```csharp
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString)
    .AddEverTaskApi();

var app = builder.Build();
app.MapEverTaskApi();
app.Run();

// UI: http://localhost:5000/evertask
// API: http://localhost:5000/evertask/api
```

### Custom Configuration

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.BasePath = "/monitoring";
    options.EnableUI = true;
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USER") ?? "admin";
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASS") ?? "changeme";
    options.SignalRHubPath = "/realtime/tasks";
    options.RequireAuthentication = !builder.Environment.IsDevelopment();
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[] { "https://myapp.com" };
});

app.MapEverTaskApi();

// UI: http://localhost:5000/monitoring
// API: http://localhost:5000/monitoring/api
```

### API-Only Mode (No UI)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.BasePath = "/api/evertask";
    options.EnableUI = false;  // Disable embedded UI
    options.RequireAuthentication = false;  // Open API for custom frontend
});

app.MapEverTaskApi();

// API: http://localhost:5000/api/evertask
// No UI served
```

## API Endpoints

All endpoints relative to `{BasePath}/api` (default: `/evertask/api`):

### Tasks
- `GET /tasks` - Paginated task list with filters
  - Query params: `TaskFilter` + `PaginationParams`
  - Returns: `TasksPagedResponse`
- `GET /tasks/{id}` - Task details
  - Returns: `TaskDetailDto` or 404
- `GET /tasks/{id}/status-audit` - Status change history
  - Returns: `List<StatusAuditDto>`
- `GET /tasks/{id}/runs-audit` - Execution history
  - Returns: `List<RunsAuditDto>`

### Dashboard
- `GET /dashboard/overview?range={Today|Week|Month|All}` - Overview statistics
  - Returns: `OverviewDto`
- `GET /dashboard/recent-activity?limit=50` - Recent activity
  - Returns: `List<RecentActivityDto>`

### Queues
- `GET /queues` - All queue metrics
  - Returns: `List<QueueMetricsDto>`
- `GET /queues/{name}/tasks` - Tasks in specific queue
  - Returns: `TasksPagedResponse`

### Statistics
- `GET /statistics/success-rate-trend?period={Last7Days|Last30Days|Last90Days}`
  - Returns: `SuccessRateTrendDto`
- `GET /statistics/task-types?range={Today|Week|Month|All}`
  - Returns: `Dictionary<string, int>`
- `GET /statistics/execution-times?range={Today|Week|Month|All}`
  - Returns: `List<ExecutionTimeDto>`

### Config
- `GET /config` - Runtime configuration (no auth required)
  - Returns: `{ apiBasePath, uiBasePath, signalRHubPath, requireAuthentication, uiEnabled }`

### SignalR Hub
- `{SignalRHubPath}` (default: `/evertask/monitor`)
  - Event: `EverTaskEvent` → `EverTaskEventData`

## Services Implementation

### ITaskQueryService

Query tasks from `ITaskStorage` with filtering, pagination, sorting:

```csharp
Task<TasksPagedResponse> GetTasksAsync(TaskFilter filter, PaginationParams pagination, CancellationToken ct = default);
Task<TaskDetailDto?> GetTaskDetailAsync(Guid id, CancellationToken ct = default);
Task<List<StatusAuditDto>> GetStatusAuditAsync(Guid id, CancellationToken ct = default);
Task<List<RunsAuditDto>> GetRunsAuditAsync(Guid id, CancellationToken ct = default);
```

**Implementation notes**:
- Use `ITaskStorage.Get()` with LINQ expressions
- Apply filters, then pagination (Skip/Take)
- Sort by `SortBy` property (default: `CreatedAtUtc`)
- Project to DTOs (use short type names via `Type.GetType()?.Name`)

### IDashboardService

Aggregate dashboard statistics:

```csharp
Task<OverviewDto> GetOverviewAsync(DateRange range, CancellationToken ct = default);
Task<List<RecentActivityDto>> GetRecentActivityAsync(int limit = 50, CancellationToken ct = default);
```

**Implementation notes**:
- Convert `DateRange` enum to DateTime filters
- Calculate success rate: `(Completed / (Completed + Failed)) * 100`
- Calculate avg execution time: `Avg(LastExecutionUtc - CreatedAtUtc)` for completed tasks
- Group tasks by hour for `TasksOverTime`
- Group tasks by queue and status for `QueueSummaries`

### IStatisticsService

Advanced analytics:

```csharp
Task<SuccessRateTrendDto> GetSuccessRateTrendAsync(TimePeriod period, CancellationToken ct = default);
Task<List<QueueMetricsDto>> GetQueueMetricsAsync(CancellationToken ct = default);
Task<Dictionary<string, int>> GetTaskTypeDistributionAsync(DateRange range, CancellationToken ct = default);
Task<List<ExecutionTimeDto>> GetExecutionTimesAsync(DateRange range, CancellationToken ct = default);
```

**Implementation notes**:
- Convert `TimePeriod` to date range and interval (daily/weekly buckets)
- Calculate success rate per bucket/queue
- Use `GroupBy` for aggregations

## DTOs and JSON Serialization

All DTOs use `record` types for immutability. JSON serialization configured in `AddControllers()`:

```csharp
services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
```

**Result**: C# `TaskListDto` → JSON `taskListDto` (camelCase).

## Middleware

**BasicAuthenticationMiddleware** protects API endpoints:

- Checks `Authorization: Basic {base64(username:password)}` header
- Returns 401 with `WWW-Authenticate` challenge if invalid
- Skips if `RequireAuthentication = false`
- Always allows `/config` endpoint (no auth)
- Allows anonymous read access if `AllowAnonymousReadAccess = true` (GET/HEAD only)

## Extension Methods

### AddEverTaskApi()

Registers:
- `EverTaskApiOptions` singleton
- Auto-registers `SignalRMonitoring` if not already registered
- All services (`ITaskQueryService`, `IDashboardService`, `IStatisticsService`)
- Controllers with JSON camelCase serialization
- CORS policy (if enabled)

### MapEverTaskApi()

Maps:
- SignalR hub at `SignalRHubPath`
- All controllers
- Conditionally serves embedded UI if `EnableUI = true`:
  - Static files from `ManifestEmbeddedFileProvider` at `UIBasePath`
  - SPA fallback routing (serves `index.html` for non-API routes)

## Dependencies

- **EverTask.Monitor.AspnetCore.SignalR** (auto-installed)
- **EverTask.Abstractions** (for `ITaskStorage`, enums)
- **Microsoft.AspNetCore.App** (FrameworkReference)

## Data Access

All services depend on `ITaskStorage` (injected via DI). Use LINQ expressions for querying:

```csharp
// Example: Filter by status
var tasks = await _storage.Get(t => t.Status == QueuedTaskStatus.Completed);

// Example: Filter by date range
var tasks = await _storage.Get(t => t.CreatedAtUtc >= startDate && t.CreatedAtUtc <= endDate);

// Example: Include related entities
var task = await _storage.Get(t => t.Id == id, includeAudits: true);
```

## Type Name Handling

Backend stores full assembly-qualified type names:

```
Kv.Workers.Emails.ContactFormEmailTaskHandler, Kv.Workers, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
```

**For display (DTOs)**:
- **Short name**: Use `Type.GetType(fullName)?.Name` → `ContactFormEmailTaskHandler`
- **Full name**: Use as-is for detail views

**In TaskListDto**: Use short name (table display)
**In TaskDetailDto**: Use full name (detail view)

## Recurring Tasks

Recurring tasks have additional fields:

```csharp
IsRecurring: bool
RecurringTask: string?        // JSON serialized recurring config
RecurringInfo: string?        // Human-readable (e.g., "Every 5 minutes")
MaxRuns: int?
RunUntil: DateTimeOffset?
NextRunUtc: DateTimeOffset?
CurrentRunCount: int?
```

Services should populate `RecurringInfo` with human-readable schedule when available.

## Audit Tables

**StatusAudits**: All status changes (Queued → InProgress → Completed/Failed)
**RunsAudits**: Execution attempts (especially for recurring tasks)

DTOs return both, ordered appropriately:
- StatusAudits: Oldest → Newest (lifecycle flow)
- RunsAudits: Newest → Oldest (recent executions first)

## SignalR Integration

Auto-configured via `AddSignalRMonitoring()` if not already registered. Hub path must match `SignalRHubPath` option.

Event published to clients:

```csharp
public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    string Severity,         // "Information" | "Warning" | "Error"
    string TaskType,
    string TaskHandlerType,
    string TaskParameters,   // JSON
    string Message,
    string? Exception
);
```

Clients subscribe via hub method: `EverTaskEvent`

## Testing

**Unit tests**: Mock `ITaskStorage`, test service logic
**Integration tests**: Use in-memory storage, test controllers end-to-end
**Manual testing**: Use Postman/Swagger to test endpoints

## Performance Considerations

- **Database indexes**: Ensure indexes on `Status`, `CreatedAtUtc`, `QueueName`, `IsRecurring`
- **Pagination**: Always use Skip/Take at database level
- **Projections**: Select only needed fields, avoid loading full entities
- **Caching**: Consider caching frequently accessed data (e.g., queue metrics)

## Security

- **Basic Auth**: Simple username/password (use HTTPS in production)
- **No write operations**: API is read-only (monitoring only)
- **CORS**: Configure `CorsAllowedOrigins` for production
- **Config endpoint**: Always accessible without auth (frontend needs it)

## Troubleshooting

**Build errors**: Ensure `EverTask.Monitor.AspnetCore.SignalR` project reference exists
**UI not served**: Check `EnableUI = true` and `wwwroot/` folder exists (build UI first)
**401 errors**: Check `RequireAuthentication` and credentials
**SignalR not working**: Ensure hub path matches in both API and SignalR config
**Type errors**: Ensure DTOs match backend models (camelCase in JSON)

## Related Files

- **UI Documentation**: `UI/CLAUDE.md`
- **UI Build Note**: `UI/BUILD_NOTE.md`
- **README**: `README.md`
- **Project File**: `EverTask.Monitor.Api.csproj`

## NuGet Package

**Package ID**: `EverTask.Monitor.Api`
**Description**: REST API for EverTask monitoring with optional embedded dashboard
**Tags**: evertask, monitoring, api, rest, signalr, dashboard

Install:
```bash
dotnet add package EverTask.Monitor.Api
```

Published package includes:
- All compiled .NET assemblies
- Embedded `wwwroot/` resources (React build)
- XML documentation for IntelliSense
