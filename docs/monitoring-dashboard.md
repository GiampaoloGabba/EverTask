---
layout: default
title: Monitoring Dashboard
nav_order: 11
---

# Monitoring Dashboard

EverTask provides a comprehensive monitoring dashboard with REST API endpoints and an embedded React UI for real-time task monitoring, analytics, and management.

## Table of Contents

- [Overview](#overview)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [API Endpoints](#api-endpoints)
- [Dashboard Features](#dashboard-features)
- [Authentication](#authentication)
- [Advanced Scenarios](#advanced-scenarios)
- [Real-Time Monitoring](#real-time-monitoring)
- [Security Best Practices](#security-best-practices)
- [Integration Examples](#integration-examples)

## Overview

The EverTask Monitoring API provides:

- **REST API** for querying tasks, viewing statistics, and analyzing performance
- **Embedded React Dashboard** with modern UI for visual monitoring
- **Real-Time Updates** via SignalR integration
- **Task History** with detailed execution logs and status changes
- **Analytics** including success rate trends, execution times, and task distribution
- **Queue Metrics** for multi-queue monitoring
- **Basic Authentication** for secure access

The monitoring system can be used in two modes:
- **Full Mode** (default): API + embedded dashboard UI
- **API-Only Mode**: REST API only, for custom frontend integrations

## Installation

Install the monitoring API package:

```bash
dotnet add package EverTask.Monitor.Api
```

The package automatically includes:
- REST API controllers
- Embedded React dashboard
- SignalR monitoring integration

## Quick Start

Add monitoring to your application with just a few lines of code:

```csharp
using EverTask;
using EverTask.Monitor.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Configure EverTask with monitoring
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddMonitoringApi(options =>
{
    options.BasePath = "/monitoring";
    options.EnableUI = true;
    options.Username = "admin";
    options.Password = "admin";
    options.RequireAuthentication = true;
});

var app = builder.Build();

// Map monitoring endpoints
app.MapEverTaskApi();

app.Run();
```

Access the dashboard:
- **Dashboard UI**: `http://localhost:5000/monitoring`
- **API Endpoints**: `http://localhost:5000/monitoring/api`
- **Credentials**: `admin` / `admin`

## Configuration

### EverTaskApiOptions

All configuration is done through the `EverTaskApiOptions` class passed to `AddMonitoringApi()`:

```csharp
.AddMonitoringApi(options =>
{
    // Base path for API and UI (default: "/monitoring")
    options.BasePath = "/monitoring";

    // Enable/disable embedded dashboard (default: true)
    options.EnableUI = true;

    // Basic Authentication credentials
    options.Username = "admin";         // Default: "admin"
    options.Password = "admin";         // Default: "admin"

    // Authentication settings
    options.RequireAuthentication = true;          // Default: true
    options.AllowAnonymousReadAccess = false;      // Default: false

    // SignalR hub path for real-time updates (fixed path)
    // Note: SignalRHubPath is now fixed to "/monitoring/hub" and cannot be changed

    // CORS settings
    options.EnableCors = true;                     // Default: true
    options.CorsAllowedOrigins = new[] {           // Default: empty (allow all)
        "https://myapp.com"
    };
});
```

### Configuration Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `BasePath` | string | `"/monitoring"` | Base path for API and UI |
| `EnableUI` | bool | `true` | Enable embedded dashboard UI |
| `ApiBasePath` | string | `"{BasePath}/api"` | API base path (readonly, derived) |
| `UIBasePath` | string | `"{BasePath}"` | UI base path (readonly, derived) |
| `Username` | string | `"admin"` | Basic Auth username |
| `Password` | string | `"admin"` | Basic Auth password |
| `SignalRHubPath` | string | `"/monitoring/hub"` | SignalR hub path (readonly, fixed) |
| `RequireAuthentication` | bool | `true` | Enable Basic Auth |
| `AllowAnonymousReadAccess` | bool | `false` | Allow read-only access without auth |
| `EnableCors` | bool | `true` | Enable CORS |
| `CorsAllowedOrigins` | string[] | `[]` | CORS allowed origins (empty = allow all) |

## API Endpoints

All endpoints are relative to `{BasePath}/api` (default: `/monitoring/api`).

### Tasks Endpoints

#### GET /tasks

Get paginated list of tasks with filtering and sorting.

**Query Parameters:**
- `status` (optional): Filter by status (`Queued`, `InProgress`, `Completed`, `Failed`, `Cancelled`)
- `queueName` (optional): Filter by queue name
- `taskType` (optional): Filter by task type (partial match)
- `isRecurring` (optional): Filter recurring tasks (`true`/`false`)
- `createdFrom` (optional): Filter by creation date (from)
- `createdTo` (optional): Filter by creation date (to)
- `sortBy` (optional): Sort field (default: `CreatedAtUtc`)
- `sortDescending` (optional): Sort direction (default: `true`)
- `page` (optional): Page number (default: `1`)
- `pageSize` (optional): Page size (default: `20`, max: `100`)

**Response:**
```json
{
  "tasks": [
    {
      "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
      "taskType": "SendEmailTask",
      "handlerType": "SendEmailHandler",
      "status": "Completed",
      "queueName": "default",
      "createdAtUtc": "2025-01-15T10:00:00Z",
      "lastExecutionUtc": "2025-01-15T10:00:05Z",
      "isRecurring": false,
      "nextRunUtc": null
    }
  ],
  "totalCount": 150,
  "page": 1,
  "pageSize": 20,
  "totalPages": 8
}
```

#### GET /tasks/{id}

Get detailed information about a specific task.

**Response:**
```json
{
  "id": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
  "taskType": "MyApp.Tasks.SendEmailTask",
  "handlerType": "MyApp.Handlers.SendEmailHandler",
  "status": "Completed",
  "queueName": "default",
  "parameters": "{\"Email\":\"user@example.com\",\"Subject\":\"Welcome\"}",
  "errorDetails": null,
  "createdAtUtc": "2025-01-15T10:00:00Z",
  "scheduledAtUtc": null,
  "lastExecutionUtc": "2025-01-15T10:00:05Z",
  "completedAtUtc": "2025-01-15T10:00:05Z",
  "isRecurring": false,
  "recurringInfo": null,
  "maxRuns": null,
  "currentRunCount": null,
  "nextRunUtc": null,
  "runUntil": null
}
```

#### GET /tasks/{id}/status-audit

Get status change history for a task.

**Response:**
```json
[
  {
    "id": 1,
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "oldStatus": "Queued",
    "newStatus": "InProgress",
    "changedAtUtc": "2025-01-15T10:00:00Z",
    "errorDetails": null
  },
  {
    "id": 2,
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "oldStatus": "InProgress",
    "newStatus": "Completed",
    "changedAtUtc": "2025-01-15T10:00:05Z",
    "errorDetails": null
  }
]
```

#### GET /tasks/{id}/runs-audit

Get execution history for a task (especially useful for recurring tasks).

**Response:**
```json
[
  {
    "id": 1,
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "executionStartedUtc": "2025-01-15T10:00:00Z",
    "executionCompletedUtc": "2025-01-15T10:00:05Z",
    "status": "Completed",
    "errorDetails": null
  }
]
```

### Dashboard Endpoints

#### GET /dashboard/overview

Get overview statistics for the dashboard.

**Query Parameters:**
- `range` (optional): Time range (`Today`, `Week`, `Month`, `All`) - default: `Today`

**Response:**
```json
{
  "totalTasks": 1234,
  "completedTasks": 1150,
  "failedTasks": 45,
  "activeTasks": 39,
  "successRate": 96.2,
  "averageExecutionTime": 1234.56,
  "activeQueues": 3,
  "recurringTasks": 12,
  "tasksOverTime": [
    {
      "timestamp": "2025-01-15T00:00:00Z",
      "completed": 100,
      "failed": 5
    }
  ],
  "queueSummaries": [
    {
      "queueName": "default",
      "totalTasks": 800,
      "activeTasks": 20,
      "completedTasks": 750,
      "failedTasks": 30
    }
  ]
}
```

#### GET /dashboard/recent-activity

Get recent task activity.

**Query Parameters:**
- `limit` (optional): Maximum number of activities to return (default: `50`, max: `100`)

**Response:**
```json
[
  {
    "taskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
    "taskType": "SendEmailTask",
    "status": "Completed",
    "timestamp": "2025-01-15T10:00:05Z",
    "message": "Task completed successfully"
  }
]
```

### Queue Endpoints

#### GET /queues

Get metrics for all queues.

**Response:**
```json
[
  {
    "queueName": "default",
    "totalTasks": 800,
    "queuedTasks": 10,
    "inProgressTasks": 5,
    "completedTasks": 750,
    "failedTasks": 30,
    "cancelledTasks": 5,
    "successRate": 96.2
  }
]
```

#### GET /queues/{name}/tasks

Get tasks for a specific queue (same response format as `GET /tasks`).

### Statistics Endpoints

#### GET /statistics/success-rate-trend

Get success rate trend over time.

**Query Parameters:**
- `period` (optional): Time period (`Last7Days`, `Last30Days`, `Last90Days`) - default: `Last7Days`

**Response:**
```json
{
  "period": "Last7Days",
  "dataPoints": [
    {
      "timestamp": "2025-01-15T00:00:00Z",
      "successRate": 96.5,
      "totalTasks": 120,
      "successfulTasks": 116,
      "failedTasks": 4
    }
  ]
}
```

#### GET /statistics/task-types

Get task distribution by type.

**Query Parameters:**
- `range` (optional): Time range (`Today`, `Week`, `Month`, `All`) - default: `Today`

**Response:**
```json
{
  "SendEmailTask": 450,
  "ProcessPaymentTask": 320,
  "GenerateReportTask": 150
}
```

#### GET /statistics/execution-times

Get execution time statistics by task type.

**Query Parameters:**
- `range` (optional): Time range (`Today`, `Week`, `Month`, `All`) - default: `Today`

**Response:**
```json
[
  {
    "taskType": "SendEmailTask",
    "averageExecutionTime": 1234.56,
    "minExecutionTime": 500.0,
    "maxExecutionTime": 3000.0,
    "taskCount": 450
  }
]
```

### Configuration Endpoint

#### GET /config

Get runtime configuration (no authentication required - needed for dashboard initialization).

**Response:**
```json
{
  "apiBasePath": "/monitoring/api",
  "uiBasePath": "/monitoring",
  "signalRHubPath": "/monitoring/hub",
  "requireAuthentication": true,
  "uiEnabled": true
}
```

## Dashboard Features

The embedded React dashboard provides a modern, responsive interface for monitoring your tasks.

### Overview Page

The main dashboard shows:
- **Total Tasks** count with breakdown by status
- **Success Rate** percentage with visual indicator
- **Active Queues** count
- **Average Execution Time** in milliseconds
- **Tasks Over Time** chart showing completed vs failed tasks
- **Queue Summaries** with status breakdown per queue
- **Recent Activity** feed with latest task updates

### Task List View

Browse and filter tasks with:
- **Status Filters**: Quick filters for Queued, In Progress, Completed, Failed, Cancelled
- **Queue Filter**: Filter by queue name
- **Task Type Filter**: Search by task type
- **Recurring Filter**: Show only recurring tasks
- **Date Range Filter**: Filter by creation date
- **Sorting**: Sort by any column
- **Pagination**: Navigate through large datasets

### Task Detail View

Click any task to view:
- **Task Information**: Type, handler, status, queue, parameters
- **Timing Information**: Created, scheduled, last execution, completed timestamps
- **Recurring Information**: Schedule, max runs, current run count, next run time
- **Status History**: Complete audit trail of status changes
- **Execution History**: All execution attempts with timestamps and outcomes
- **Error Details**: Full stack traces for failed tasks

### Queue Metrics

Monitor queue health with:
- **Task Distribution**: Breakdown by status per queue
- **Success Rate**: Per-queue success percentage
- **Active Tasks**: Currently executing tasks
- **Queue Capacity**: Visual indicators

### Statistics & Analytics

Analyze performance with:
- **Success Rate Trends**: Historical success rate over 7/30/90 days
- **Task Type Distribution**: See which tasks run most frequently
- **Execution Time Analysis**: Identify slow tasks and performance bottlenecks
- **Time Range Filters**: Analyze Today, Week, Month, or All time

### Real-Time Updates

The dashboard automatically updates when:
- Tasks are dispatched
- Tasks start executing
- Tasks complete or fail
- Status changes occur

Updates are pushed via SignalR with no page refresh required.

## Authentication

The monitoring API uses Basic Authentication for security.

### Default Credentials

```
Username: admin
Password: admin
```

> **Warning:** Always change the default credentials in production!

### Configuration

```csharp
.AddMonitoringApi(options =>
{
    // Enable authentication (default: true)
    options.RequireAuthentication = true;

    // Set custom credentials
    options.Username = "monitor_user";
    options.Password = "secure_password_123";

    // Allow read-only access without authentication (optional)
    options.AllowAnonymousReadAccess = false;
});
```

### Environment Variables

Store credentials securely using environment variables:

```csharp
.AddMonitoringApi(options =>
{
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? "admin";
});
```

```bash
# Set environment variables
export MONITOR_USERNAME=admin
export MONITOR_PASSWORD=my_secure_password
```

### Anonymous Read Access

Allow reading task data without authentication (write operations still require auth):

```csharp
.AddMonitoringApi(options =>
{
    options.RequireAuthentication = true;
    options.AllowAnonymousReadAccess = true;  // GET/HEAD requests don't need auth
});
```

### Disable Authentication

For development environments only:

```csharp
.AddMonitoringApi(options =>
{
    options.RequireAuthentication = false;  // No authentication required
});
```

## Advanced Scenarios

### API-Only Mode

Disable the embedded UI to use only the REST API:

```csharp
.AddMonitoringApi(options =>
{
    options.BasePath = "/api/evertask";
    options.EnableUI = false;  // Disable embedded dashboard
});
```

This is useful when:
- Building a custom frontend
- Integrating with existing monitoring systems
- Mobile app integration
- Third-party dashboard integration

### Custom Base Path

Configure a custom base path for your monitoring:

```csharp
.AddMonitoringApi(options =>
{
    options.BasePath = "/admin/tasks";
});

// UI:  http://localhost:5000/admin/tasks
// API: http://localhost:5000/admin/tasks/api
```

### CORS Configuration

Configure CORS for custom frontend applications:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[]
    {
        "https://myapp.com",
        "https://dashboard.myapp.com"
    };
});
```

Allow all origins (development only):

```csharp
.AddMonitoringApi(options =>
{
    options.EnableCors = true;
    options.CorsAllowedOrigins = Array.Empty<string>();  // Allow all origins
});
```

### Environment-Specific Configuration

Adjust configuration based on environment:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddMonitoringApi(options =>
{
    options.BasePath = "/monitoring";
    options.EnableUI = true;

    if (builder.Environment.IsDevelopment())
    {
        // Development: Disable authentication
        options.RequireAuthentication = false;
        options.EnableCors = true;
        options.CorsAllowedOrigins = Array.Empty<string>();
    }
    else
    {
        // Production: Strict security
        options.RequireAuthentication = true;
        options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
        options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? throw new InvalidOperationException("MONITOR_PASSWORD not set");
        options.EnableCors = true;
        options.CorsAllowedOrigins = new[] { "https://myapp.com" };
    }
});
```

### Custom SignalR Hub Path

Configure a custom SignalR hub path:

```csharp
.AddMonitoringApi(options =>
{
    options.SignalRHubPath = "/realtime/tasks";
});
```

### Standalone API Registration

Use the monitoring API without EverTask integration (for custom scenarios):

```csharp
// In Program.cs
builder.Services.AddEverTaskMonitoringApiStandalone(options =>
{
    options.BasePath = "/monitoring";
    options.EnableUI = false;
});

// Note: You must register ITaskStorage manually
builder.Services.AddSingleton<ITaskStorage, MyCustomStorage>();
```

## Real-Time Monitoring

The dashboard integrates seamlessly with EverTask's SignalR monitoring.

### Automatic Configuration

SignalR monitoring is automatically configured when you add the monitoring API.
The hub path is fixed to `/monitoring/hub` and cannot be changed.

If SignalR monitoring wasn't previously registered, it's added automatically.

### Manual SignalR Configuration

If you want more control over SignalR settings:

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddSignalRMonitoring(opt =>
{
    opt.IncludeExecutionLogs = true;  // Include logs in SignalR events
})
.AddMonitoringApi(options =>
{
    options.BasePath = "/monitoring";
});
```

### SignalR Events

The dashboard receives real-time events for:
- **Task Started**: When a task begins execution
- **Task Completed**: When a task finishes successfully
- **Task Failed**: When a task fails
- **Task Cancelled**: When a task is cancelled
- **Task Timeout**: When a task exceeds its timeout

### Custom SignalR Client

Connect to the SignalR hub from your own application:

```javascript
import * as signalR from '@microsoft/signalr';

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/monitoring/hub')
    .withAutomaticReconnect()
    .build();

connection.on('EverTaskEvent', (eventData) => {
    console.log('Task event:', eventData);
    // eventData contains: TaskId, EventDateUtc, Severity, TaskType, Message, etc.
});

await connection.start();
```

## Security Best Practices

### 1. Change Default Credentials

Always change the default username and password:

```csharp
.AddMonitoringApi(options =>
{
    options.Username = "your_username";
    options.Password = "strong_password_here";
});
```

### 2. Use HTTPS in Production

Always use HTTPS for monitoring endpoints in production:

```csharp
var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.MapEverTaskApi();
```

### 3. Restrict CORS Origins

Don't allow all origins in production:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableCors = true;
    options.CorsAllowedOrigins = new[]
    {
        "https://app.example.com",
        "https://dashboard.example.com"
    };
});
```

### 4. Use Environment Variables

Never hardcode credentials:

```csharp
.AddMonitoringApi(options =>
{
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME")
        ?? throw new InvalidOperationException("MONITOR_USERNAME not set");
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD")
        ?? throw new InvalidOperationException("MONITOR_PASSWORD not set");
});
```

### 5. Limit Network Access

Use firewall rules or network policies to restrict access:

```csharp
// In appsettings.json
{
  "Kestrel": {
    "Endpoints": {
      "Monitoring": {
        "Url": "http://localhost:5001",  // Only accessible locally
        "Protocols": "Http1"
      }
    }
  }
}
```

### 6. Disable in Production (Optional)

If monitoring is only needed for development:

```csharp
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddEverTask(opt => ...)
        .AddMonitoringApi();
}
```

### 7. Rate Limiting

Consider adding rate limiting to monitoring endpoints:

```csharp
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("monitoring", opt =>
    {
        opt.Window = TimeSpan.FromSeconds(10);
        opt.PermitLimit = 100;
    });
});

app.UseRateLimiter();
```

## Integration Examples

### Console Application

```csharp
using EverTask;
using EverTask.Monitor.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage()
.AddMonitoringApi(options =>
{
    options.BasePath = "/monitoring";
    options.Username = "admin";
    options.Password = "admin";
});

var app = builder.Build();
app.MapEverTaskApi();
await app.StartAsync();

Console.WriteLine("Dashboard: http://localhost:5000/monitoring");
Console.WriteLine("Credentials: admin / admin");

// Your application logic...
Console.ReadKey();
await app.StopAsync();
```

### ASP.NET Core Web Application

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
.AddMonitoringApi(options =>
{
    options.BasePath = "/admin/tasks";
    options.RequireAuthentication = !builder.Environment.IsDevelopment();
});

var app = builder.Build();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapEverTaskApi();  // Add monitoring endpoints

app.Run();
```

### Worker Service

```csharp
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb")!)
.AddMonitoringApi(options =>
{
    options.BasePath = "/monitoring";
});

// Add web server for monitoring
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

builder.Services.AddSingleton<IHostedService>(sp =>
{
    var webApp = WebApplication.CreateBuilder().Build();
    webApp.MapEverTaskApi();
    return new WebHostingService(webApp);
});

var host = builder.Build();
host.Run();
```

### Custom Frontend Integration

Use the REST API with your own frontend:

```typescript
// TypeScript example
import axios from 'axios';

const API_BASE = 'http://localhost:5000/monitoring/api';
const AUTH = { username: 'admin', password: 'admin' };

// Get tasks
const response = await axios.get(`${API_BASE}/tasks`, {
    auth: AUTH,
    params: {
        status: 'Completed',
        page: 1,
        pageSize: 20
    }
});

console.log('Tasks:', response.data.tasks);
console.log('Total:', response.data.totalCount);

// Get task details
const task = await axios.get(`${API_BASE}/tasks/${taskId}`, { auth: AUTH });
console.log('Task:', task.data);

// Get dashboard overview
const overview = await axios.get(`${API_BASE}/dashboard/overview`, {
    auth: AUTH,
    params: { range: 'Today' }
});

console.log('Total tasks:', overview.data.totalTasks);
console.log('Success rate:', overview.data.successRate);
```

## Next Steps

- **[Monitoring Events](monitoring.md)** - Event-based monitoring and custom integrations
- **[Configuration Reference](configuration-reference.md)** - Complete configuration documentation
- **[Advanced Features](advanced-features.md)** - Multi-queue and advanced scenarios
- **[Architecture](architecture.md)** - How monitoring works internally
