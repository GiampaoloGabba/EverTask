# EverTask.Monitor.Api

**REST API with embedded dashboard UI for EverTask monitoring.** Provides endpoints for querying tasks, statistics, and real-time monitoring via SignalR. Includes a built-in React dashboard that can be enabled or disabled based on your needs.

## Use Cases

- **Full-Featured Dashboard**: Use the embedded React UI for complete monitoring (default)
- **API-Only Mode**: Disable UI and build your own custom frontend or integrate with third-party tools
- **Mobile Apps**: Query task status and statistics from mobile applications
- **Custom Integrations**: Integrate EverTask monitoring into existing systems
- **Flexible Deployment**: Single package for both API and UI, runs on a single Kestrel instance

## Features

- **Embedded Dashboard UI**: Modern React dashboard with real-time updates (can be disabled)
- **Task Query API**: Paginated task lists with filtering, sorting, and search
- **Dashboard Statistics**: Overview metrics, recent activity, task distribution
- **Queue Metrics**: Per-queue statistics and task lists
- **Analytics**: Success rate trends, execution times, task type distribution
- **Real-time Monitoring**: SignalR integration for live task events
- **Basic Authentication**: Optional HTTP Basic Auth protection
- **CORS Support**: Configurable cross-origin request handling
- **Flexible Modes**: Use with embedded UI (default) or API-only mode

## Installation

```bash
dotnet add package EverTask.Monitor.Api
```

## Quick Start

### Mode 1: Full Dashboard (API + UI) - Default

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add EverTask with Monitoring API + UI
builder.Services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString)
    .AddSignalRMonitoring();  // Required for real-time monitoring

builder.Services.AddEverTaskApi(options =>
{
    // Note: BasePath is fixed to "/evertask-monitoring" and cannot be changed
    // SignalRHubPath is fixed to "/evertask-monitoring/hub"
    options.Username = "admin";
    options.Password = "secret";
    options.EnableAuthentication = true;  // Default: true
    options.EnableUI = true;  // Default: true
});

var app = builder.Build();

// Map API endpoints and serve UI
app.MapEverTaskApi();

app.Run();
```

- **Dashboard UI**: `http://localhost:5000/evertask-monitoring`
- **API Endpoints**: `http://localhost:5000/evertask-monitoring/api/*`
- **SignalR Hub**: `http://localhost:5000/evertask-monitoring/hub` (fixed)

### Mode 2: API-Only (No UI)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    // BasePath is fixed to "/evertask-monitoring", so API will be at /evertask-monitoring/api
    options.EnableUI = false;  // Disable embedded UI
    options.EnableAuthentication = true;
});

var app = builder.Build();
app.MapEverTaskApi();
app.Run();
```

- **API Endpoints**: `http://localhost:5000/evertask-monitoring/api/*`
- **No UI served** - build your own frontend or use as a standalone API

### Mode 3: Development Mode (No Authentication)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.EnableAuthentication = false;  // Open access for development
    options.EnableCors = true;  // Allow cross-origin requests
    options.EnableUI = true;  // Keep UI enabled
});
```

### Advanced Configuration

```csharp
builder.Services.AddEverTaskApi(options =>
{
    // Note: BasePath and SignalRHubPath are now fixed:
    // - BasePath: "/evertask-monitoring" (cannot be changed)
    // - SignalRHubPath: "/evertask-monitoring/hub" (cannot be changed)
    options.EnableUI = true;  // Enable embedded dashboard UI
    options.Username = "api_user";
    options.Password = Environment.GetEnvironmentVariable("API_PASSWORD") ?? "changeme";
    options.EnableAuthentication = true;
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[] { "https://myapp.com", "http://localhost:3000" };
});
```

**Fixed Path Structure:**
- UI: `/evertask-monitoring` (when EnableUI = true)
- API: `/evertask-monitoring/api/*`
- SignalR Hub: `/evertask-monitoring/hub`

## OpenAPI Document and Scalar UI

The monitoring API can serve its own OpenAPI document (net9.0+, built-in ASP.NET Core generator),
fully isolated from your application's OpenAPI/Swagger/Scalar setup:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableOpenApiDocument = true; // /evertask-monitoring/openapi/evertask-monitoring.json
});
```

For an interactive API reference, install `EverTask.Monitor.Api.Scalar` and chain one call
(this also enables the document automatically):

```csharp
.AddMonitoringApi(options => { /* ... */ })
.AddMonitoringApiScalar(); // /evertask-monitoring/scalar
```

The monitoring controllers carry their own ApiExplorer group (`evertask-monitoring`), so they
never show up in your application's OpenAPI or Swagger documents, and your endpoints never show
up in the monitoring document. See the
[monitoring documentation](https://github.com/GiampaoloGabba/EverTask/blob/master/docs/monitoring-dashboard.md#openapi-document-and-scalar-ui)
for the optional recipe to surface the monitoring API inside your own Swagger UI.

## ⚠️ Security Considerations

**CRITICAL: EverTask Monitor API runs on the SAME Kestrel server as your host application.**

This means:
- ✅ Shares the same **IP address and port** as your application
- ✅ No separate server/process created
- ⚠️ **If your application is public, the monitoring dashboard is also public!**

### Production Security Best Practices

**DO NOT expose the monitoring dashboard on the public internet without proper security measures.**

The monitoring API exposes sensitive information:
- Task details, parameters, and payloads
- Exception stack traces with internal code paths
- Queue names and infrastructure details
- Execution statistics and patterns

**And it can be given a write surface.** `EnableManagementEndpoints` turns on three routes that requeue an
occurrence, resume a halted catch-up or cancel a schedule — operations that put handlers with side effects
back into execution. It is `false` by default and the prefix does not exist until it is set, but if you do
set it: treat `ManagementUsername` / `ManagementPassword` as an operator credential, keep it apart from the
dashboard one, and remember that a config binder reading environment variables can turn the flag on without
anyone editing code. `ManagementAuthorization` is there when the decision belongs to your own principal.

**Recommended production configurations:**

#### 1. Disable UI in Production (API-only)
```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.EnableUI = false;  // No public dashboard
    options.EnableAuthentication = true;
    // Use strong credentials from secrets/environment
    options.Username = builder.Configuration["Monitoring:Username"];
    options.Password = builder.Configuration["Monitoring:Password"];
});
```

#### 2. Use Reverse Proxy with IP Whitelisting (Recommended)

**Nginx example** - Allow only internal IPs:
```nginx
location /evertask-monitoring {
    # Only allow access from internal network
    allow 10.0.0.0/8;
    allow 172.16.0.0/12;
    allow 192.168.0.0/16;
    deny all;

    proxy_pass http://localhost:5000/evertask-monitoring;
}
```

#### 3. Bind Kestrel to Multiple Endpoints

**Public app on 5000, monitoring on localhost:5001:**
```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);        // Public application
    options.ListenLocalhost(5001);    // Monitoring (localhost only)
});

builder.Services.AddEverTaskApi(options =>
{
    options.EnableUI = true;
    options.EnableAuthentication = false;  // Safe on localhost
});
```

Access monitoring via SSH tunnel:
```bash
ssh -L 5001:localhost:5001 user@yourserver.com
# Then visit http://localhost:5001/evertask-monitoring
```

#### 4. VPN-Only Access

Deploy your application normally, but require VPN connection to access monitoring endpoints.

#### 5. Separate Deployment (Most Secure)

Create a dedicated monitoring application on an internal network:
```csharp
// monitoring-app/Program.cs (internal network only)
var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddEverTaskMonitoringApiStandalone(options => { /* ... */ })
    .AddSqlServerStorage(connectionString);  // Same database

var app = builder.Build();
app.MapEverTaskApi();
app.Run("http://internal-monitor.local:5000");
```

### Authentication Considerations

**JWT Authentication provides:**
- Token-based authentication with expiration (default: 8 hours)
- Stateless authentication (no server-side sessions)
- Support for Bearer token scheme

**However, for public internet exposure:**
- Use HTTPS in production (JWT tokens are credentials)
- Consider additional security layers (rate limiting, IP whitelisting)
- **Best option: Don't expose publicly at all** - use VPN or internal network

### Summary

| Environment | Recommended Configuration |
|-------------|--------------------------|
| **Development** | `EnableAuthentication = false`, `EnableUI = true` |
| **Staging (internal)** | JWT Auth, IP whitelisting |
| **Production (public app)** | `EnableUI = false`, reverse proxy + IP whitelist, or VPN-only |
| **Production (internal)** | JWT Auth or no auth if network-isolated |

## API Endpoints

All endpoints are prefixed with `/evertask-monitoring/api` (fixed). **Every one of them is read-only except
the three under `/management`**, which do not exist unless a host turns them on (`EnableManagementEndpoints`,
off by default) and which need a second credential — the dashboard credentials are a read credential shared
by everyone who looks at it. Changing a schedule from application code is still `ITaskScheduleManager`,
called behind your own authorization.

### Tasks

- `GET /tasks` - Get paginated task list with filters
  - Query params: `statuses`, `taskType`, `queueName`, `searchTerm`, `isRecurring`, `createdAfter`,
    `createdBefore`, `page`, `pageSize`, `sortBy`, `sortDescending`
  - Durable schedules: `parentTaskId` (the occurrences of one schedule), `onlyOccurrences`, `onlyCatchUp`
    (only the rows that stand for missed work)
- `GET /tasks/{id}` - Get task details
- `GET /tasks/{id}/status-audit?skip=&take=` - Status transition history, newest first, as
  `{ audits, totalCount, skip, take }`
- `GET /tasks/{id}/runs-audit?skip=&take=` - Recorded runs of an inline recurring task, same shape
- `GET /tasks/{id}/execution-logs?skip=&take=&level=` - Get the captured handler logs of a task
- `GET /tasks/{id}/occurrences?nonTerminalOnly=&skip=&take=` - The materialized occurrences of a durable
  schedule, newest slot first, paged by the storage itself
- `GET /tasks/counts` - Task counts by category (all, standard, recurring, failed, occurrences)

Task DTOs carry the schedule context of a row: `parentTaskId`, `occurrenceMode`, `misfirePolicy`,
`timeZoneId`, `scheduleVersion`, `nominalSlotUtc`, `misfireKind` and `startedAtUtc` (when the last run began —
`lastExecutionUtc` is stamped when it ENDED; it is read from the row's `InProgress` audit, so a run still in
flight has one, and it is absent wherever nothing measured a start). The schedule fields are null together on
a task that belongs to no schedule.

### Dashboard

- `GET /dashboard/overview?range=Today|Week|Month|All` - Get overview statistics, including `catchUpBacklog`:
  the occurrences of every durable schedule by state, how far behind the oldest pending slot is, and how many
  schedules stopped themselves over their catch-up cap
- `GET /dashboard/recent-activity?limit=50` - Get recent activity

### Queues

- `GET /queues` - Get all queue metrics
- `GET /queues/{name}/tasks` - Get tasks in specific queue

### Statistics

- `GET /statistics/success-rate-trend?period=Last7Days|Last30Days|Last90Days`
- `GET /statistics/task-types?range=Today|Week|Month|All`
- `GET /statistics/execution-times?range=Today|Week|Month|All`

### Rate limits

- `GET /rate-limits` - Per-key parked count, next slot, tracked keys and fail-open count. In-memory, so it
  reports this process only.

### Management (opt-in, the only endpoints that write)

Absent unless `EnableManagementEndpoints` is `true`: while it is false the whole prefix answers `404` and the
routes are not in the OpenAPI document. Once enabled, a call must carry the **operate** role — the token
returned by logging in with `ManagementUsername` / `ManagementPassword` — or satisfy the
`ManagementAuthorization` hook, which replaces the role check. The dashboard credential and every magic link
are read-only and get `403`, and turning authentication off does not open the surface.

- `POST /management/tasks/{id}/requeue` - Put a `Failed`/`Cancelled` occurrence back in the queue, keeping
  its id, history and audit trail
- `POST /management/tasks/{id}/resume` - Release a schedule that halted its own catch-up
- `POST /management/tasks/{id}/cancel` - Cancel a schedule and the occurrences of it still pending

All three take the id in the path, no body, and answer
`{ status, message, taskId, nextRunUtc?, releasedHalt }`. `501` means no EverTask host is registered on this
process (a standalone monitoring host has no scheduler to command).

### Config

- `GET /config` - Get runtime configuration (no auth required)
  - Returns: `{ apiBasePath, uiBasePath, signalRHubPath, requireAuthentication, uiEnabled, managementEnabled }`

### SignalR Hub

- `/evertask-monitoring/hub` (fixed)
  - Event: `EverTaskEvent` - Real-time task events

## Configuration Options

```csharp
public class EverTaskApiOptions
{
    // Base path for API and UI (fixed: "/evertask-monitoring", readonly)
    public string BasePath => "/evertask-monitoring";

    // Enable embedded dashboard UI (default: true)
    // Set to false for API-only mode
    public bool EnableUI { get; set; } = true;

    // API base path (fixed: "/evertask-monitoring/api", readonly, derived)
    public string ApiBasePath => $"{BasePath}/api";

    // Management (write) base path (fixed: "/evertask-monitoring/api/management", readonly, derived)
    public string ManagementBasePath => $"{ApiBasePath}/management";

    // UI base path (fixed: "/evertask-monitoring", readonly, only used when EnableUI is true)
    public string UIBasePath => BasePath;

    // JWT Authentication credentials (default: "admin"/"admin")
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";

    // SignalR hub path (fixed: "/evertask-monitoring/hub", readonly)
    public string SignalRHubPath => "/evertask-monitoring/hub";

    // Enable JWT Authentication (default: true)
    public bool EnableAuthentication { get; set; } = true;

    // Expose the three write endpoints under ManagementBasePath (default: false)
    // While false the whole prefix answers 404 and the routes are absent from the OpenAPI document
    public bool EnableManagementEndpoints { get; set; }

    // The OPERATE credential, separate from the read credential above (default: null = nobody operates)
    // Only a token issued for this pair carries the operate role
    public string? ManagementUsername { get; set; }
    public string? ManagementPassword { get; set; }

    // Decide for yourself instead of using the operate role (default: null = use the role)
    // Runs inside routing, after the host's UseAuthentication, so context.User is your own principal
    public Func<HttpContext, Task<bool>>? ManagementAuthorization { get; set; }

    // Enable CORS (default: true)
    public bool EnableCors { get; set; } = true;

    // CORS allowed origins (default: allow all)
    public string[] CorsAllowedOrigins { get; set; } = Array.Empty<string>();

    // Magic link token for instant authentication (default: null = disabled)
    public string? MagicLinkToken { get; set; }
}
```

## Authentication

The API uses JWT Authentication. To authenticate:

### Step 1: Login to Get JWT Token

```bash
curl -X POST https://yourapp.com/evertask-monitoring/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"admin","password":"admin"}'
```

Response:
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "username": "admin",
  "expiresAt": "2025-01-07T10:00:00Z"
}
```

### Step 2: Use Token in Requests

```bash
curl -H "Authorization: Bearer YOUR_JWT_TOKEN" \
  https://yourapp.com/evertask-monitoring/api/tasks
```

### Using JavaScript/Fetch

```javascript
// Login
const loginResponse = await fetch('https://yourapp.com/evertask-monitoring/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: 'admin' })
});
const { token } = await loginResponse.json();

// Use token for API calls
const response = await fetch('https://yourapp.com/evertask-monitoring/api/tasks', {
    headers: {
        'Authorization': `Bearer ${token}`
    }
});
const tasks = await response.json();
```

### Disable Authentication (Development Only)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.EnableAuthentication = false;
});
```

### Magic Link (External Integration)

Enable instant access via URL token for embedding in other dashboards or portals:

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.MagicLinkToken = "your-32-char-secret-token-here";
});
```

Access URL: `https://yourapp.com/evertask-monitoring/magic#token=your-32-char-secret-token-here`

Put the token in the URL fragment (`#token=`): the fragment never reaches the server, and the dashboard exchanges it with `POST /api/auth/magic` (token in the body), so it stays out of request logs. The older `?token=` query form still works but is written verbatim by anything that logs request URLs (Serilog request logging, reverse proxies, browser history), and the token never expires. See [Magic Link Access](../../../docs/monitoring-dashboard.md#magic-link-access).

The magic link validates the token and returns a standard JWT session. If `MagicLinkToken` is not configured, the endpoint returns 404.

## CORS Configuration

For cross-origin requests (e.g., separate frontend, mobile apps):

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[]
    {
        "https://dashboard.myapp.com",
        "https://mobile.myapp.com",
        "http://localhost:3000"  // Development
    };
});
```

## JSON Serialization

All responses use `camelCase` property names:

```json
{
  "id": "123e4567-e89b-12d3-a456-426614174000",
  "type": "SendEmailTask",
  "status": "Completed",
  "createdAtUtc": "2025-10-19T10:30:00Z",
  "lastExecutionUtc": "2025-10-19T10:30:01Z",
  "executionTimeMs": 250
}
```

## Example: Custom React Dashboard

```javascript
import { useEffect, useState } from 'react';

function TaskMonitor() {
    const [tasks, setTasks] = useState([]);

    useEffect(() => {
        fetch('https://yourapp.com/evertask/api/tasks?statuses=InProgress', {
            headers: {
                'Authorization': 'Basic ' + btoa('admin:admin')
            }
        })
        .then(res => res.json())
        .then(data => setTasks(data.items));
    }, []);

    return (
        <div>
            <h1>Running Tasks</h1>
            {tasks.map(task => (
                <div key={task.id}>
                    {task.type} - {task.status}
                </div>
            ))}
        </div>
    );
}
```

## Example: SignalR Real-Time Monitoring

```javascript
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
    .withUrl('https://yourapp.com/evertask-monitoring/hub')
    .build();

connection.on('EverTaskEvent', (event) => {
    console.log(`Task ${event.taskId} - ${event.eventType}`);
    // Update your UI in real-time
});

await connection.start();
```

## Building the UI (Development)

The embedded UI is built from the React source in the `UI/` folder:

```bash
# Navigate to the UI folder
cd src/Monitoring/EverTask.Monitor.Api/UI

# Install dependencies
npm install

# Development (with hot reload)
npm run dev
# Frontend: http://localhost:5173 (proxies to backend at localhost:5000)

# Production build
npm run build
# Outputs to: ../wwwroot/
```

The `wwwroot/` folder is embedded into the NuGet package automatically.

## Dependencies

- `EverTask.Monitor.AspnetCore.SignalR` - Real-time monitoring
- `EverTask.Abstractions` - Core interfaces
- `Microsoft.AspNetCore.App` - ASP.NET Core framework

## Target Frameworks

- .NET 6.0
- .NET 7.0
- .NET 8.0

## License

See the LICENSE file in the repository root.
