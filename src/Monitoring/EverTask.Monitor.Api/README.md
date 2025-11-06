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
    // Note: BasePath is now fixed to "/monitoring" and cannot be changed
    // SignalRHubPath is fixed to "/monitoring/hub"
    options.Username = "admin";
    options.Password = "secret";
    options.EnableUI = true;  // Default: true
});

var app = builder.Build();

// Map API endpoints and serve UI
app.MapEverTaskApi();

app.Run();
```

- **Dashboard UI**: `http://localhost:5000/monitoring`
- **API Endpoints**: `http://localhost:5000/monitoring/api/*`
- **SignalR Hub**: `http://localhost:5000/monitoring/hub` (fixed)

### Mode 2: API-Only (No UI)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    // BasePath is fixed to "/monitoring", so API will be at /monitoring/api
    options.EnableUI = false;  // Disable embedded UI
    options.RequireAuthentication = true;
});

var app = builder.Build();
app.MapEverTaskApi();
app.Run();
```

- **API Endpoints**: `http://localhost:5000/monitoring/api/*`
- **No UI served** - build your own frontend or use as a standalone API

### Mode 3: Development Mode (No Authentication)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.RequireAuthentication = false;  // Open access for development
    options.EnableCors = true;  // Allow cross-origin requests
    options.EnableUI = true;  // Keep UI enabled
});
```

### Advanced Configuration

```csharp
builder.Services.AddEverTaskApi(options =>
{
    // Note: BasePath and SignalRHubPath are now fixed:
    // - BasePath: "/monitoring" (cannot be changed)
    // - SignalRHubPath: "/monitoring/hub" (cannot be changed)
    options.EnableUI = true;  // Enable embedded dashboard UI
    options.Username = "api_user";
    options.Password = Environment.GetEnvironmentVariable("API_PASSWORD") ?? "changeme";
    options.RequireAuthentication = true;
    options.AllowAnonymousReadAccess = false;
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[] { "https://myapp.com", "http://localhost:3000" };
});
```

**Fixed Path Structure:**
- UI: `/monitoring` (when EnableUI = true)
- API: `/monitoring/api/*`
- SignalR Hub: `/monitoring/hub`

## Swagger Integration

If your application already uses Swagger/OpenAPI, you should configure **separate Swagger documents** to avoid mixing your application's endpoints with EverTask monitoring endpoints.

```csharp
builder.Services.AddSwaggerGen(c =>
{
    c.EnableAnnotations();

    // Create separate Swagger documents
    c.SwaggerDoc("v1", new() { Title = "My Application API", Version = "v1" });
    c.SwaggerDoc("monitoring", new() { Title = "EverTask Monitoring API", Version = "v1" });

    // Filter controllers by namespace
    c.DocInclusionPredicate((docName, apiDesc) =>
    {
        if (apiDesc.ActionDescriptor is not Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor controllerActionDescriptor)
            return false;

        var controllerNamespace = controllerActionDescriptor.ControllerTypeInfo.Namespace ?? string.Empty;

        return docName switch
        {
            "v1" => !controllerNamespace.StartsWith("EverTask.Monitor.Api"),
            "monitoring" => controllerNamespace.StartsWith("EverTask.Monitor.Api"),
            _ => false
        };
    });
});

// Configure SwaggerUI with both documents
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "My Application API");
        c.SwaggerEndpoint("/swagger/monitoring/swagger.json", "EverTask Monitoring API");
    });
}
```

Now you'll see a **dropdown in Swagger UI** to switch between your application API and EverTask Monitoring API.

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

**Recommended production configurations:**

#### 1. Disable UI in Production (API-only)
```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.EnableUI = false;  // No public dashboard
    options.RequireAuthentication = true;
    // Use strong credentials from secrets/environment
    options.Username = builder.Configuration["Monitoring:Username"];
    options.Password = builder.Configuration["Monitoring:Password"];
});
```

#### 2. Use Reverse Proxy with IP Whitelisting (Recommended)

**Nginx example** - Allow only internal IPs:
```nginx
location /monitoring {
    # Only allow access from internal network
    allow 10.0.0.0/8;
    allow 172.16.0.0/12;
    allow 192.168.0.0/16;
    deny all;

    proxy_pass http://localhost:5000/monitoring;
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
    options.RequireAuthentication = false;  // Safe on localhost
});
```

Access monitoring via SSH tunnel:
```bash
ssh -L 5001:localhost:5001 user@yourserver.com
# Then visit http://localhost:5001/monitoring
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

**Basic Auth is NOT sufficient for public internet exposure:**
- Credentials transmitted in base64 (easily decoded)
- No rate limiting or brute-force protection
- No session management or token expiration

**For public exposure, consider:**
- Running behind a reverse proxy with OAuth2/OIDC
- Using API Gateway with proper authentication
- **Best option: Don't expose publicly at all**

### Summary

| Environment | Recommended Configuration |
|-------------|--------------------------|
| **Development** | `RequireAuthentication = false`, `EnableUI = true` |
| **Staging (internal)** | Basic Auth, IP whitelisting |
| **Production (public app)** | `EnableUI = false`, reverse proxy + IP whitelist, or VPN-only |
| **Production (internal)** | Basic Auth or no auth if network-isolated |

## API Endpoints

All endpoints are prefixed with `/monitoring/api` (fixed)

### Tasks

- `GET /tasks` - Get paginated task list with filters
  - Query params: `status`, `type`, `search`, `page`, `pageSize`, `sortBy`, `sortDirection`
- `GET /tasks/{id}` - Get task details
- `GET /tasks/{id}/status-audit` - Get status audit history
- `GET /tasks/{id}/runs-audit` - Get runs audit history

### Dashboard

- `GET /dashboard/overview?range=Today|Week|Month|All` - Get overview statistics
- `GET /dashboard/recent-activity?limit=50` - Get recent activity

### Queues

- `GET /queues` - Get all queue metrics
- `GET /queues/{name}/tasks` - Get tasks in specific queue

### Statistics

- `GET /statistics/success-rate-trend?period=Last7Days|Last30Days|Last90Days`
- `GET /statistics/task-types?range=Today|Week|Month|All`
- `GET /statistics/execution-times?range=Today|Week|Month|All`

### Config

- `GET /config` - Get runtime configuration (no auth required)
  - Returns: `{ apiBasePath, uiBasePath, signalRHubPath, requireAuthentication, uiEnabled }`

### SignalR Hub

- `/monitoring/hub` (fixed)
  - Event: `EverTaskEvent` - Real-time task events

## Configuration Options

```csharp
public class EverTaskApiOptions
{
    // Base path for API and UI (fixed: "/monitoring", readonly)
    public string BasePath => "/monitoring";

    // Enable embedded dashboard UI (default: true)
    // Set to false for API-only mode
    public bool EnableUI { get; set; } = true;

    // API base path (fixed: "/monitoring/api", readonly, derived)
    public string ApiBasePath => $"{BasePath}/api";

    // UI base path (fixed: "/monitoring", readonly, only used when EnableUI is true)
    public string UIBasePath => BasePath;

    // Basic Authentication credentials (default: "admin"/"admin")
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";

    // SignalR hub path (fixed: "/monitoring/hub", readonly)
    public string SignalRHubPath => "/monitoring/hub";

    // Enable Basic Authentication (default: true)
    public bool RequireAuthentication { get; set; } = true;

    // Allow anonymous read access (default: false)
    public bool AllowAnonymousReadAccess { get; set; } = false;

    // Enable CORS (default: true)
    public bool EnableCors { get; set; } = true;

    // CORS allowed origins (default: allow all)
    public string[] CorsAllowedOrigins { get; set; } = Array.Empty<string>();
}
```

## Authentication

The API uses HTTP Basic Authentication by default. To authenticate:

### Using cURL

```bash
curl -u admin:admin https://yourapp.com/evertask/api/tasks
```

### Using JavaScript/Fetch

```javascript
const response = await fetch('https://yourapp.com/evertask/api/tasks', {
    headers: {
        'Authorization': 'Basic ' + btoa('admin:admin')
    }
});
const tasks = await response.json();
```

### Disable Authentication (Development Only)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.RequireAuthentication = false;
});
```

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
  "executedAtUtc": "2025-10-19T10:30:01Z",
  "executionTimeMs": 250
}
```

## Example: Custom React Dashboard

```javascript
import { useEffect, useState } from 'react';

function TaskMonitor() {
    const [tasks, setTasks] = useState([]);

    useEffect(() => {
        fetch('https://yourapp.com/evertask/api/tasks?status=Running', {
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
    .withUrl('https://yourapp.com/monitoring/hub')
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
