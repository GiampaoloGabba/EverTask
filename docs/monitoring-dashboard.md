---
layout: default
title: Monitoring Dashboard
parent: Monitoring
nav_order: 2
---

# Monitoring Dashboard

EverTask provides a comprehensive monitoring dashboard with REST API endpoints and an embedded React UI for real-time task monitoring, analytics, and management.

![Dashboard Overview]({{ '/assets/screenshots/1.png' | relative_url }})

## Table of Contents

- [Overview](#overview)
- [Installation](#installation)
- [Quick Start](#quick-start)
- [Configuration](#configuration)
- [Authentication](#authentication)
- [Advanced Scenarios](#advanced-scenarios)
- [Swagger Integration](#swagger-integration)
- [Real-Time Monitoring](#real-time-monitoring)
- [Security Best Practices](#security-best-practices)
- [Integration Examples](#integration-examples)
- [Documentation Resources](#documentation-resources)

## Overview

The EverTask Monitoring API provides:

- **REST API** for querying tasks, viewing statistics, and analyzing performance
- **Embedded React Dashboard** with modern UI for visual monitoring
- **Real-Time Updates** via SignalR integration with intelligent throttling
- **Task History** with detailed execution logs and status changes
- **Analytics** including success rate trends, execution times, and task distribution
- **Queue Metrics** for multi-queue monitoring
- **JWT Authentication** for secure access

The monitoring system can be used in two modes:
- **Full Mode** (default): API + embedded dashboard UI
- **API-Only Mode**: REST API only, for custom frontend integrations

### Version 3.3 - Feature Complete (Read-Only Monitoring)

The dashboard and API are **feature complete for read-only monitoring** in version 3.3. This release provides comprehensive observability and analytics capabilities for complete visibility into your task execution pipeline.

**Current Capabilities (v3.3):**
- ✅ Complete read-only monitoring and observability
- ✅ Real-time task status updates via SignalR with event-driven cache invalidation
- ✅ Comprehensive analytics (success rates, execution times, task distribution)
- ✅ Detailed execution logs visualization with filtering and export
- ✅ Multi-queue monitoring and advanced task filtering
- ✅ Audit trail visualization (status history, execution runs)
- ✅ Terminal-style log viewer with color-coded severity levels

**Future Releases:**
- ⏳ Task management operations (stop, restart, cancel running tasks)
- ⏳ Runtime parameter modification for queued/scheduled tasks
- ⏳ Queue management operations (pause/resume queues)
- ⏳ Task retry/requeue functionality
- ⏳ Bulk task operations

> **Note**: Both the REST API and embedded dashboard currently operate in **read-only mode**. You can view, analyze, and export all task data, but cannot modify task execution or queue behavior through the UI or API. Task management capabilities will be introduced in future releases.

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

    // Dashboard auto-refresh debounce (milliseconds)
    options.EventDebounceMs = 1000;                // Default: 1000 (1 second)

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
| `EventDebounceMs` | int | `1000` | Debounce time in milliseconds for SignalR event-driven cache invalidation in the dashboard. Higher values reduce API load during task bursts but introduce slight UI update delays |

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

**With JWT Authentication** (when `EnableAuthentication = true`):\
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

### 8. Rate Limiting

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

## Documentation Resources

The monitoring documentation is organized into specialized guides:

### 📊 [API Reference](monitoring-api-reference.md)

Complete REST API documentation with all endpoints:
- Tasks endpoints (list, details, history)
- Dashboard endpoints (overview, activity)
- Queue endpoints (metrics, health)
- Statistics endpoints (trends, analytics)
- Request/response examples and query parameters

### 🎨 [Dashboard UI Guide](monitoring-dashboard-ui.md)

Visual interface documentation with screenshots:
- Overview page (metrics, charts, activity feed)
- Task list view (filtering, sorting, pagination)
- Task detail view (parameters, history, logs)
- Queue metrics (distribution, success rates)
- Statistics & analytics (trends, performance)
- Complete screenshot gallery

### 🔌 [Custom Event Monitoring](monitoring-events.md)

Build custom integrations using the event system:
- Task lifecycle events
- DIY SignalR integration
- Third-party monitoring (Application Insights, Prometheus)
- Custom alerts (Slack, email, PagerDuty)
- Serilog integration

## Next Steps

- **[API Reference](monitoring-api-reference.md)** - Complete REST API documentation
- **[Dashboard UI Guide](monitoring-dashboard-ui.md)** - UI features and screenshots
- **[Custom Event Monitoring](monitoring-events.md)** - Event-based monitoring and custom integrations
- **[Configuration Reference](configuration-reference.md)** - All configuration options
- **[Architecture](architecture.md)** - How monitoring works internally
