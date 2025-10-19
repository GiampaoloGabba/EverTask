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
    options.BasePath = "/monitoring";  // UI at /monitoring, API at /monitoring/api
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
- **SignalR Hub**: `http://localhost:5000/monitoring/monitor` (default)

### Mode 2: API-Only (No UI)

```csharp
builder.Services.AddEverTaskApi(options =>
{
    options.BasePath = "/api/monitoring";
    options.EnableUI = false;  // Disable embedded UI
    options.RequireAuthentication = true;
});

var app = builder.Build();
app.MapEverTaskApi();
app.Run();
```

- **API Endpoints**: `http://localhost:5000/api/monitoring/*`
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
    options.BasePath = "/evertask";  // Base path for both UI and API
    options.EnableUI = true;  // Enable embedded dashboard UI
    options.Username = "api_user";
    options.Password = Environment.GetEnvironmentVariable("API_PASSWORD") ?? "changeme";
    options.SignalRHubPath = "/realtime/tasks";
    options.RequireAuthentication = true;
    options.AllowAnonymousReadAccess = false;
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[] { "https://myapp.com", "http://localhost:3000" };
});
```

**Path Structure:**
- When `EnableUI = true`: UI at `{BasePath}/`, API at `{BasePath}/api/*`
- When `EnableUI = false`: API at `{BasePath}/*`

## API Endpoints

All endpoints are prefixed with `{ApiBasePath}`:
- When `EnableUI = true`: `{BasePath}/api` (e.g., `/evertask/api`)
- When `EnableUI = false`: `{BasePath}` (e.g., `/api/monitoring`)

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

- `{SignalRHubPath}` (default: `/evertask/monitor`)
  - Event: `EverTaskEvent` - Real-time task events

## Configuration Options

```csharp
public class EverTaskApiOptions
{
    // Base path for API and UI (default: "/evertask")
    // When EnableUI is true: UI at {BasePath}/, API at {BasePath}/api/*
    // When EnableUI is false: API at {BasePath}/*
    public string BasePath { get; set; } = "/evertask";

    // Enable embedded dashboard UI (default: true)
    // Set to false for API-only mode
    public bool EnableUI { get; set; } = true;

    // API base path (derived from BasePath and EnableUI)
    public string ApiBasePath => EnableUI ? $"{BasePath}/api" : BasePath;

    // UI base path (only used when EnableUI is true)
    public string UIBasePath => BasePath;

    // Basic Authentication credentials (default: "admin"/"admin")
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "admin";

    // SignalR hub path (default: "/evertask/monitor")
    public string SignalRHubPath { get; set; } = "/evertask/monitor";

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
    .withUrl('https://yourapp.com/evertask/monitor')
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
