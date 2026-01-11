---
layout: default
title: Monitoring
nav_order: 7
has_children: true
---

# Monitoring

EverTask provides comprehensive monitoring capabilities to give you full visibility into your background tasks. Whether you need a ready-to-use web dashboard or want to build custom integrations, EverTask has you covered.

## Two Approaches to Monitoring

### 1. Complete Dashboard Solution (Recommended)

**[→ Monitoring Dashboard Guide](monitoring-dashboard.md)**

A complete, production-ready monitoring solution with minimal setup:

- **Embedded React Dashboard** - Modern UI with real-time updates
- **REST API** - Full-featured API for programmatic access
- **Real-Time Events** - SignalR integration with intelligent throttling
- **Authentication** - JWT-based security
- **Ready in Minutes** - Add `.AddMonitoringApi()` and you're done

**Perfect for:**
- Quick setup and immediate visibility
- Production monitoring dashboards
- Teams that want a complete solution out of the box
- Applications that need both UI and API access

**Quick Start:**
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddMonitoringApi(options =>
{
    options.EnableUI = true;
    options.Username = "admin";
    options.Password = "admin";
});

var app = builder.Build();
app.MapEverTaskApi();

// Dashboard: http://localhost:5000/evertask-monitoring
// API: http://localhost:5000/evertask-monitoring/api
```

**Resources:**
- [Monitoring Dashboard Overview](monitoring-dashboard.md) - Installation, configuration, security
- [API Reference](monitoring-api-reference.md) - Complete REST API documentation
- [Dashboard UI Guide](monitoring-dashboard-ui.md) - UI features and screenshots

---

### 2. Custom Event Monitoring

**[→ Custom Event Monitoring Guide](monitoring-events.md)**

Build your own monitoring integrations using EverTask's event system:

- **Event Lifecycle Hooks** - Subscribe to task events (Started, Completed, Failed, etc.)
- **Custom SignalR Integration** - Build real-time dashboards from scratch
- **Third-Party Integrations** - Application Insights, Prometheus, Slack, email alerts
- **Serilog Integration** - Structured logging with custom sinks

**Perfect for:**
- Custom monitoring dashboards
- Integration with existing monitoring tools (Application Insights, Prometheus, Grafana)
- Specialized alerting workflows (Slack, PagerDuty, email)
- Advanced analytics and custom metrics

**Quick Example:**
```csharp
public class TaskMonitor
{
    public TaskMonitor(IEverTaskWorkerExecutor executor, ILogger<TaskMonitor> logger)
    {
        executor.TaskEventOccurredAsync += async eventData =>
        {
            if (eventData.Severity == "Error")
            {
                logger.LogError("Task {TaskId} failed: {Message}",
                    eventData.TaskId, eventData.Message);

                await SendSlackAlert(eventData);
            }
        };
    }
}
```

**Resources:**
- [Custom Event Monitoring Guide](monitoring-events.md) - Event system, SignalR, integrations

---

### 3. Task Execution Logs

**[→ Task Execution Logs Guide](monitoring-logs.md)**

Capture and persist logs written during task execution for debugging and audit trails:

- **Proxy Logger** - Logs ALWAYS go to ILogger, optionally persisted to database
- **Structured Logging** - Full support for message templates and parameters
- **Retry Tracking** - Logs accumulate across all retry attempts
- **Dashboard Integration** - View logs in the terminal-style log viewer
- **Audit Compliance** - Permanent record of task execution for regulatory requirements

**Perfect for:**
- Debugging failed tasks with full execution context
- Regulatory compliance and audit trails
- Root cause analysis of production issues
- Tracking execution history across retries

**Quick Example:**
```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    public override async Task Handle(ProcessOrderTask task, CancellationToken ct)
    {
        Logger.LogInformation("Processing order {OrderId}", task.OrderId);

        // Logs always go to ILogger
        // Optionally persisted to database with .WithPersistentLogger()

        await ProcessOrder(task.OrderId);

        Logger.LogInformation("Order {OrderId} completed", task.OrderId);
    }
}
```

**Resources:**
- [Task Execution Logs Guide](monitoring-logs.md) - Configuration, retrieval, best practices

---

## Comparison Table

| Feature | Dashboard Solution | Custom Event Monitoring |
|---------|-------------------|------------------------|
| **Setup Time** | Minutes | Hours |
| **UI Included** | ✅ Yes (React) | ❌ Build your own |
| **REST API** | ✅ Full-featured | ❌ Build your own |
| **Real-Time Updates** | ✅ SignalR integrated | ✅ DIY SignalR |
| **Authentication** | ✅ JWT built-in | ⚠️ Implement yourself |
| **Task Filtering** | ✅ Advanced filters | ❌ Build your own |
| **Analytics** | ✅ Built-in charts | ❌ Build your own |
| **Custom Integrations** | ⚠️ API-based | ✅ Full control |
| **Alerting** | ❌ Not yet | ✅ DIY (Slack, email, etc.) |
| **Customization** | ⚠️ Limited | ✅ Unlimited |

## Monitoring Best Practices

### 1. Start with the Dashboard

For most applications, the monitoring dashboard provides everything you need:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableUI = true;
    options.EnableAuthentication = true;
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME") ?? "admin";
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD") ?? "admin";
});
```

### 2. Add Custom Alerts

Combine the dashboard with custom event monitoring for critical alerts:

```csharp
public class CriticalTaskMonitor
{
    public CriticalTaskMonitor(IEverTaskWorkerExecutor executor)
    {
        executor.TaskEventOccurredAsync += async eventData =>
        {
            if (eventData.Severity == "Error" && IsCritical(eventData.TaskType))
            {
                await SendPagerDutyAlert(eventData);
            }
        };
    }
}
```

### 3. Secure Your Monitoring

Always use strong credentials and HTTPS in production:

```csharp
.AddMonitoringApi(options =>
{
    options.EnableAuthentication = true;
    options.Username = Environment.GetEnvironmentVariable("MONITOR_USERNAME")
        ?? throw new InvalidOperationException("MONITOR_USERNAME not set");
    options.Password = Environment.GetEnvironmentVariable("MONITOR_PASSWORD")
        ?? throw new InvalidOperationException("MONITOR_PASSWORD not set");
    options.JwtSecret = Environment.GetEnvironmentVariable("JWT_SECRET");
});
```

### 4. Monitor What Matters

Focus on critical tasks and error conditions:

```csharp
executor.TaskEventOccurredAsync += async eventData =>
{
    // Only alert on payment/order failures
    if (eventData.Severity == "Error" &&
        (eventData.TaskType.Contains("Payment") || eventData.TaskType.Contains("Order")))
    {
        await AlertOpsTeam(eventData);
    }
};
```

## What to Monitor

### Essential Metrics

- **Task Success Rate** - Track completion vs failure ratio
- **Execution Times** - Identify slow or degraded tasks
- **Error Patterns** - Group failures by type/cause
- **Queue Health** - Monitor queue depths and processing rates
- **Recurring Tasks** - Ensure scheduled tasks run on time

### Warning Signs

Watch for these indicators of problems:

- **High Failure Rates** (>5%) - May indicate issues with task logic or external dependencies
- **Increasing Execution Times** - Could signal resource contention or performance degradation
- **Queue Buildup** - Tasks queuing faster than processing capacity
- **Recurring Task Delays** - Tasks missing their schedule consistently
- **Repeated Timeout Errors** - Tasks exceeding configured timeouts frequently

## Integration Examples

### Application Insights + Dashboard

```csharp
// Dashboard for visual monitoring
builder.Services.AddEverTask(opt => ...)
    .AddMonitoringApi();

// Application Insights for metrics
builder.Services.AddSingleton<ITaskMonitor, ApplicationInsightsMonitor>();
```

### Prometheus + Custom SignalR

```csharp
// Prometheus for metrics
builder.Services.AddEverTask(opt => ...)
    .AddSignalRMonitoring();

builder.Services.AddSingleton<ITaskMonitor, PrometheusMonitor>();
```

### Dashboard + Slack Alerts

```csharp
// Dashboard for visual monitoring
builder.Services.AddEverTask(opt => ...)
    .AddMonitoringApi();

// Slack for critical alerts
builder.Services.AddSingleton<ITaskMonitor, SlackAlertMonitor>();
```

## Next Steps

**Ready to start monitoring?**

1. **Quick Start** → [Monitoring Dashboard Guide](monitoring-dashboard.md)
2. **API Integration** → [API Reference](monitoring-api-reference.md)
3. **Custom Integrations** → [Custom Event Monitoring](monitoring-events.md)
4. **UI Overview** → [Dashboard UI Guide](monitoring-dashboard-ui.md)

**Related Topics:**

- [Configuration Reference](configuration-reference.md) - All configuration options
- [Resilience](resilience.md) - Retry policies and error handling
- [Architecture](architecture.md) - How monitoring works internally
