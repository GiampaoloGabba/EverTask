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
- [Swagger Integration](#swagger-integration)
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
- **JWT Authentication** for secure access

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
    options.BasePath = "/evertask-monitoring";
    options.EnableUI = true;
    options.Username = "admin";
    options.Password = "admin";
    options.EnableAuthentication = true;
});

var app = builder.Build();

// Map monitoring endpoints
app.MapEverTaskApi();

app.Run();
```

Access the dashboard:
- **Dashboard UI**: `http://localhost:5000/evertask-monitoring`
- **API Endpoints**: `http://localhost:5000/evertask-monitoring/api`
- **Credentials**: `admin` / `admin`

## Configuration

### EverTaskApiOptions

All configuration is done through the `EverTaskApiOptions` class passed to `AddMonitoringApi()`:

```csharp
.AddMonitoringApi(options =>
{
    // Base path for API and UI (default: "/evertask-monitoring")
    options.BasePath = "/evertask-monitoring";

    // Enable/disable embedded dashboard (default: true)
    options.EnableUI = true;

    // JWT Authentication credentials
    options.Username = "admin";         // Default: "admin"
    options.Password = "admin";         // Default: "admin"

    // Authentication settings
    options.EnableAuthentication = true;          // Default: true

    // SignalR hub path for real-time updates (fixed path)
    // Note: SignalRHubPath is now fixed to "/evertask-monitoring/hub" and cannot be changed

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
| `BasePath` | string | `"/evertask-monitoring"` | Base path for API and UI |
| `EnableUI` | bool | `true` | Enable embedded dashboard UI |
| `EnableSwagger` | bool | `false` | Enable Swagger/OpenAPI documentation |
| `ApiBasePath` | string | `"{BasePath}/api"` | API base path (readonly, derived) |
| `UIBasePath` | string | `"{BasePath}"` | UI base path (readonly, derived) |
| `Username` | string | `"admin"` | JWT authentication username |
| `Password` | string | `"admin"` | JWT authentication password |
| `JwtSecret` | string? | auto-generated | Secret key for signing JWT tokens (min 256 bits recommended) |
| `JwtIssuer` | string | `"EverTask.Monitor.Api"` | JWT token issuer |
| `JwtAudience` | string | `"EverTask.Monitor.Api"` | JWT token audience |
| `JwtExpirationHours` | int | `8` | JWT token expiration time in hours |
| `SignalRHubPath` | string | `"/evertask-monitoring/hub"` | SignalR hub path (readonly, fixed) |
| `EnableAuthentication` | bool | `true` | Enable JWT authentication |
| `EnableCors` | bool | `true` | Enable CORS |
| `CorsAllowedOrigins` | string[] | `[]` | CORS allowed origins (empty = allow all) |
| `AllowedIpAddresses` | string[] | `[]` | IP whitelist (empty = allow all IPs). Supports IPv4/IPv6 and CIDR notation |

## API Endpoints

All endpoints are relative to `{BasePath}/api` (default: `/evertask-monitoring/api`).

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
  "apiBasePath": "/evertask-monitoring/api",
  "uiBasePath": "/evertask-monitoring",
  "signalRHubPath": "/evertask-monitoring/hub",
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

The monitoring API uses JWT (JSON Web Token) authentication for secure access.

### Default Credentials

```
Username: admin
Password: admin
```

> **Warning:** Always change the default credentials in production!

### Authentication Flow

1. **Login**: POST credentials to `/evertask-monitoring/api/auth/login` to obtain a JWT token
2. **Use Token**: Include the token in the `Authorization: Bearer {token}` header for all API requests
3. **Token Expiration**: Tokens expire after 8 hours by default (configurable via `JwtExpirationHours`)

### Configuration

```csharp
.AddMonitoringApi(options =>
{
    // Enable authentication (default: true)
    options.EnableAuthentication = true;

    // Set custom credentials
    options.Username = "monitor_user";
    options.Password = "secure_password_123";

    // JWT configuration
    options.JwtSecret = "your-256-bit-secret-key-here";  // Auto-generated if not provided
    options.JwtExpirationHours = 8;                       // Default: 8 hours
    options.JwtIssuer = "MyApp";                          // Default: "EverTask.Monitor.Api"
    options.JwtAudience = "MyApp";                        // Default: "EverTask.Monitor.Api"
});
```

### Environment Variables

Store credentials and secrets securely using environment variables:

```csharp
.AddMonitoringApi(options =>
{
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? "admin";
    options.JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");  // Auto-generated if null
});
```

```bash
# Set environment variables
export MONITOR_USERNAME=admin
export MONITOR_PASSWORD=my_secure_password
export JWT_SECRET=your-strong-random-secret-min-256-bits
```

### Login Endpoint

POST to `/evertask-monitoring/api/auth/login` to obtain a JWT token:

**Request:**
```json
{
  "username": "admin",
  "password": "admin"
}
```

**Response:**
```json
{
  "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
  "expiresAt": "2025-01-16T02:00:00Z",
  "username": "admin"
}
```

### Using the Token

Include the token in the `Authorization` header for all API requests:

```bash
curl -H "Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..." \
     http://localhost:5000/evertask-monitoring/api/tasks
```

### Disable Authentication

For development environments only:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableAuthentication = false;  // No authentication required
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
    options.BasePath = "/evertask-monitoring";
    options.EnableUI = true;

    if (builder.Environment.IsDevelopment())
    {
        // Development: Disable authentication
        options.EnableAuthentication = false;
        options.EnableCors = true;
        options.CorsAllowedOrigins = Array.Empty<string>();
    }
    else
    {
        // Production: Strict security
        options.EnableAuthentication = true;
        options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
        options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? throw new InvalidOperationException("MONITOR_PASSWORD not set");
        options.JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET") ?? throw new InvalidOperationException("JWT_SECRET not set");
        options.EnableCors = true;
        options.CorsAllowedOrigins = new[] { "https://myapp.com" };
    }
});
```

### SignalR Hub Path

The SignalR hub path is fixed to `/evertask-monitoring/hub` and cannot be customized. This ensures consistent integration between the API and the embedded dashboard UI.

### Standalone API Registration

Use the monitoring API without EverTask integration (for custom scenarios):

```csharp
// In Program.cs
builder.Services.AddEverTaskMonitoringApiStandalone(options =>
{
    options.BasePath = "/evertask-monitoring";
    options.EnableUI = false;
});

// Note: You must register ITaskStorage manually
builder.Services.AddSingleton<ITaskStorage, MyCustomStorage>();
```

## Swagger Integration

EverTask Monitoring API provides automatic Swagger/OpenAPI documentation generation with complete separation from your application's API documentation.

### Enable Swagger Documentation

Enable Swagger for the monitoring API in your configuration:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI = true;
    options.EnableSwagger = true;  // Enable separate Swagger document
    // ... other options
});
```

### Configure SwaggerUI

Add both endpoints to your SwaggerUI configuration:

```csharp
// Configure your application's Swagger document
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "My Application API", Version = "v1" });
});

// In the pipeline
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "My Application API");
    c.SwaggerEndpoint("/swagger/evertask-monitoring/swagger.json", "EverTask Monitoring API");
});
```

### How It Works

When `EnableSwagger = true`:
- EverTask creates a separate Swagger document at `/swagger/evertask-monitoring/swagger.json`
- The document includes **only** EverTask monitoring endpoints (`/evertask-monitoring/api/*`)
- Your application's Swagger document (`v1`) **excludes** EverTask endpoints automatically
- No namespace filtering or custom predicates required in your application

Result: A dropdown appears in Swagger UI to switch between your application API and EverTask Monitoring API, with complete separation of endpoints.

## Real-Time Monitoring

The dashboard integrates seamlessly with EverTask's SignalR monitoring.

### Automatic Configuration

SignalR monitoring is automatically configured when you add the monitoring API.
The hub path is fixed to `/evertask-monitoring/hub` and cannot be changed.

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
    options.BasePath = "/evertask-monitoring";
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

**With JWT Authentication** (when `EnableAuthentication = true`):
```javascript
import * as signalR from '@microsoft/signalr';

// First, obtain JWT token via login
const loginResponse = await fetch('/evertask-monitoring/api/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username: 'admin', password: 'admin' })
});
const { token } = await loginResponse.json();

// Connect to SignalR hub with JWT token
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/evertask-monitoring/hub', {
        accessTokenFactory: () => token  // Pass JWT token for authentication
    })
    .withAutomaticReconnect()
    .build();

connection.on('EverTaskEvent', (eventData) => {
    console.log('Task event:', eventData);
    // eventData contains: TaskId, EventDateUtc, Severity, TaskType, Message, etc.
});

await connection.start();
```

**Without Authentication** (when `EnableAuthentication = false` or IP whitelist only):
```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/evertask-monitoring/hub')
    .withAutomaticReconnect()
    .build();

connection.on('EverTaskEvent', (eventData) => {
    console.log('Task event:', eventData);
});

await connection.start();
```

**Note:** The embedded React dashboard automatically handles JWT authentication. This is only needed for custom client integrations.

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

### 5. Configure IP Whitelist

Restrict access to specific IP addresses or CIDR ranges. This protects both the API and SignalR hub:

```csharp
.AddMonitoringApi(options =>
{
    // Only allow access from specific IPs
    options.AllowedIpAddresses = new[]
    {
        "192.168.1.100",           // Specific IP
        "10.0.0.0/8",              // Private network (CIDR notation)
        "172.16.0.0/12",           // Another private range
        "::1"                      // IPv6 localhost
    };
});
```

**Important notes:**
- When `AllowedIpAddresses` is empty (default), **all IPs are allowed**
- Supports IPv4, IPv6, and CIDR notation (e.g., `192.168.0.0/24`)
- Checks `X-Forwarded-For` header first (reverse proxy scenarios)
- Returns **403 Forbidden** if IP is not in whitelist
- IP check happens **before** authentication (more efficient)

**Reverse proxy example:**
```csharp
// If behind nginx/IIS, client IP comes from X-Forwarded-For header
options.AllowedIpAddresses = new[]
{
    "203.0.113.0/24"  // Allow only from specific public IP range
};
```

### 6. Limit Network Access

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

### 7. Disable in Production (Optional)

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
    options.BasePath = "/evertask-monitoring";
    options.Username = "admin";
    options.Password = "admin";
});

var app = builder.Build();
app.MapEverTaskApi();
await app.StartAsync();

Console.WriteLine("Dashboard: http://localhost:5000/evertask-monitoring");
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
    options.EnableAuthentication = !builder.Environment.IsDevelopment();
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
    options.BasePath = "/evertask-monitoring";
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

const API_BASE = 'http://localhost:5000/evertask-monitoring/api';

// Login to get JWT token
const loginResponse = await axios.post(`${API_BASE}/auth/login`, {
    username: 'admin',
    password: 'admin'
});

const token = loginResponse.data.token;
console.log('Token expires at:', loginResponse.data.expiresAt);

// Create axios instance with JWT token
const api = axios.create({
    baseURL: API_BASE,
    headers: {
        'Authorization': `Bearer ${token}`
    }
});

// Get tasks
const response = await api.get('/tasks', {
    params: {
        status: 'Completed',
        page: 1,
        pageSize: 20
    }
});

console.log('Tasks:', response.data.tasks);
console.log('Total:', response.data.totalCount);

// Get task details
const task = await api.get(`/tasks/${taskId}`);
console.log('Task:', task.data);

// Get dashboard overview
const overview = await api.get('/dashboard/overview', {
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
