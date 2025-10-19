---
layout: default
title: Monitoring
nav_order: 10
---

# Monitoring

EverTask gives you visibility into your background tasks through a flexible event system that tracks execution, failures, and performance.

## Table of Contents

- [Task Events](#task-events)
- [Event Data Structure](#event-data-structure)
- [Basic Event Monitoring](#basic-event-monitoring)
- [SignalR Real-Time Monitoring](#signalr-real-time-monitoring)
- [Custom Monitoring Integrations](#custom-monitoring-integrations)
- [Logging Integration](#logging-integration)
- [Best Practices](#best-practices)

## Task Events

EverTask publishes events for all significant task lifecycle moments through the `TaskEventOccurredAsync` event on `IEverTaskWorkerExecutor`. Subscribe to these events to build monitoring dashboards, send alerts, or integrate with external systems.

### Event Types

- **Task Started**: When a task begins execution
- **Task Completed**: When a task finishes successfully
- **Task Failed**: When a task fails after all retry attempts
- **Task Cancelled**: When a task is cancelled
- **Task Timeout**: When a task exceeds its timeout
- **Recurring Task Scheduled**: When a recurring task is scheduled for next execution

### Severity Levels

```csharp
public enum SeverityLevel
{
    Information,    // Normal operation (started, completed, scheduled)
    Warning,        // Non-critical issues (cancelled, timeout)
    Error           // Failures and exceptions
}
```

## Event Data Structure

Each event includes everything you need to track what happened:

```csharp
public record EverTaskEventData(
    Guid TaskId,
    DateTimeOffset EventDateUtc,
    string Severity,
    string TaskType,
    string TaskHandlerType,
    string TaskParameters,
    string Message,
    string? Exception = null);
```

### Example Event Data

```json
{
  "TaskId": "dc49351d-476d-49f0-a1e8-3e2a39182d22",
  "EventDateUtc": "2024-10-19T16:10:20+00:00",
  "Severity": "Information",
  "TaskType": "MyApp.Tasks.SendEmailTask",
  "TaskHandlerType": "MyApp.Handlers.SendEmailHandler",
  "TaskParameters": "{\"Email\":\"user@example.com\",\"Subject\":\"Welcome\"}",
  "Message": "Task with id dc49351d-476d-49f0-a1e8-3e2a39182d22 was completed.",
  "Exception": null
}
```

## Basic Event Monitoring

The simplest way to monitor tasks is to subscribe to events in your services:

### Simple Event Subscription

```csharp
public class TaskMonitoringService
{
    private readonly ILogger<TaskMonitoringService> _logger;

    public TaskMonitoringService(
        IEverTaskWorkerExecutor executor,
        ILogger<TaskMonitoringService> logger)
    {
        _logger = logger;

        executor.TaskEventOccurredAsync += OnTaskEventAsync;
    }

    private Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        _logger.LogInformation(
            "Event from EverTask: [{Severity}] {Message}",
            eventData.Severity,
            eventData.Message);

        return Task.CompletedTask;
    }
}

// Register in Program.cs
builder.Services.AddSingleton<TaskMonitoringService>();
```

### Filtering by Severity

```csharp
private Task OnTaskEventAsync(EverTaskEventData eventData)
{
    switch (eventData.Severity)
    {
        case nameof(SeverityLevel.Error):
            _logger.LogError(
                eventData.Exception,
                "Task {TaskId} of type {TaskType} failed: {Message}",
                eventData.TaskId,
                eventData.TaskType,
                eventData.Message);
            break;

        case nameof(SeverityLevel.Warning):
            _logger.LogWarning(
                "Task {TaskId} warning: {Message}",
                eventData.TaskId,
                eventData.Message);
            break;

        case nameof(SeverityLevel.Information):
            _logger.LogInformation(
                "Task {TaskId}: {Message}",
                eventData.TaskId,
                eventData.Message);
            break;
    }

    return Task.CompletedTask;
}
```

### Tracking Specific Tasks

Sometimes you only care about certain types of tasks. Here's how to filter for specific task types:

```csharp
private Task OnTaskEventAsync(EverTaskEventData eventData)
{
    // Only monitor critical tasks
    if (eventData.TaskType.Contains("Payment") ||
        eventData.TaskType.Contains("Order"))
    {
        _logger.LogInformation(
            "Critical task event: {@EventData}",
            eventData);

        // Send to external monitoring
        _telemetry.TrackEvent("CriticalTaskEvent", new Dictionary<string, string>
        {
            ["TaskId"] = eventData.TaskId.ToString(),
            ["TaskType"] = eventData.TaskType,
            ["Severity"] = eventData.Severity,
            ["Message"] = eventData.Message
        });
    }

    return Task.CompletedTask;
}
```

## SignalR Real-Time Monitoring

If you're building an ASP.NET Core application, you can watch tasks execute in real-time using the SignalR integration. This is especially useful for admin dashboards or debugging during development.

### Installation

```bash
dotnet add package EverTask.Monitor.AspnetCore.SignalR
```

### Configuration

```csharp
using EverTask.Monitor.AspnetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddSignalRMonitoring(); // Add SignalR monitoring

var app = builder.Build();

// Map the monitoring hub
app.MapEverTaskMonitorHub(); // Default: /evertask/monitoring

// Or with custom URL
app.MapEverTaskMonitorHub("/task-monitoring");

app.Run();
```

### JavaScript Client

```html
<!DOCTYPE html>
<html>
<head>
    <title>EverTask Monitor</title>
    <script src="https://cdn.jsdelivr.net/npm/@microsoft/signalr@latest/dist/browser/signalr.min.js"></script>
</head>
<body>
    <h1>EverTask Real-Time Monitor</h1>
    <div id="events"></div>

    <script>
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/evertask/monitoring")
            .withAutomaticReconnect()
            .build();

        connection.on("TaskEvent", (eventData) => {
            console.log("Task event received:", eventData);

            const eventDiv = document.createElement("div");
            eventDiv.className = `event event-${eventData.severity.toLowerCase()}`;
            eventDiv.innerHTML = `
                <strong>${eventData.severity}</strong>:
                ${eventData.taskType} -
                ${eventData.message}
                <small>(${new Date(eventData.eventDateUtc).toLocaleString()})</small>
            `;

            document.getElementById("events").prepend(eventDiv);
        });

        connection.start()
            .then(() => console.log("Connected to EverTask monitoring"))
            .catch(err => console.error("Connection error:", err));
    </script>

    <style>
        .event { padding: 10px; margin: 5px 0; border-left: 4px solid #ccc; }
        .event-information { border-left-color: #007bff; }
        .event-warning { border-left-color: #ffc107; }
        .event-error { border-left-color: #dc3545; }
    </style>
</body>
</html>
```

### .NET Client

```csharp
using Microsoft.AspNetCore.SignalR.Client;

public class EverTaskMonitorClient
{
    private readonly HubConnection _connection;
    private readonly ILogger<EverTaskMonitorClient> _logger;

    public EverTaskMonitorClient(string hubUrl, ILogger<EverTaskMonitorClient> logger)
    {
        _logger = logger;

        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        _connection.On<EverTaskEventData>("TaskEvent", OnTaskEvent);
    }

    public async Task StartAsync()
    {
        await _connection.StartAsync();
        _logger.LogInformation("Connected to EverTask monitoring hub");
    }

    private void OnTaskEvent(EverTaskEventData eventData)
    {
        _logger.LogInformation(
            "Received task event: [{Severity}] {TaskType} - {Message}",
            eventData.Severity,
            eventData.TaskType,
            eventData.Message);
    }

    public async Task StopAsync()
    {
        await _connection.StopAsync();
    }
}

// Usage
var client = new EverTaskMonitorClient("https://localhost:5001/evertask/monitoring", logger);
await client.StartAsync();
```

### Filtering Events on Client

You can filter events client-side to avoid cluttering your UI with informational messages:

```javascript
connection.on("TaskEvent", (eventData) => {
    // Only show errors and warnings
    if (eventData.severity === "Error" || eventData.severity === "Warning") {
        displayEvent(eventData);

        // Play alert sound for errors
        if (eventData.severity === "Error") {
            playAlertSound();
        }
    }

    // Track metrics
    updateMetrics(eventData);
});

function updateMetrics(eventData) {
    // Update dashboard metrics
    if (eventData.severity === "Error") {
        incrementErrorCount();
    }
    if (eventData.message.includes("completed")) {
        incrementCompletedCount();
    }
}
```

## Custom Monitoring Integrations

Want to integrate with your existing monitoring tools? Here are examples for popular platforms.

### Application Insights

```csharp
public class ApplicationInsightsMonitor
{
    private readonly TelemetryClient _telemetry;

    public ApplicationInsightsMonitor(
        IEverTaskWorkerExecutor executor,
        TelemetryClient telemetry)
    {
        _telemetry = telemetry;
        executor.TaskEventOccurredAsync += OnTaskEventAsync;
    }

    private Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        var properties = new Dictionary<string, string>
        {
            ["TaskId"] = eventData.TaskId.ToString(),
            ["TaskType"] = eventData.TaskType,
            ["TaskHandlerType"] = eventData.TaskHandlerType,
            ["Severity"] = eventData.Severity
        };

        switch (eventData.Severity)
        {
            case nameof(SeverityLevel.Error):
                _telemetry.TrackException(
                    new Exception(eventData.Exception ?? eventData.Message),
                    properties);
                break;

            case nameof(SeverityLevel.Warning):
            case nameof(SeverityLevel.Information):
                _telemetry.TrackEvent(
                    $"EverTask.{eventData.Severity}",
                    properties);
                break;
        }

        return Task.CompletedTask;
    }
}
```

### Prometheus Metrics

```csharp
using Prometheus;

public class PrometheusMonitor
{
    private static readonly Counter TasksStarted = Metrics
        .CreateCounter("evertask_tasks_started_total", "Total tasks started");

    private static readonly Counter TasksCompleted = Metrics
        .CreateCounter("evertask_tasks_completed_total", "Total tasks completed");

    private static readonly Counter TasksFailed = Metrics
        .CreateCounter("evertask_tasks_failed_total", "Total tasks failed",
            new CounterConfiguration { LabelNames = new[] { "task_type" } });

    private static readonly Histogram TaskDuration = Metrics
        .CreateHistogram("evertask_task_duration_seconds", "Task execution duration");

    public PrometheusMonitor(IEverTaskWorkerExecutor executor)
    {
        executor.TaskEventOccurredAsync += OnTaskEventAsync;
    }

    private Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        if (eventData.Message.Contains("started"))
        {
            TasksStarted.Inc();
        }
        else if (eventData.Message.Contains("completed"))
        {
            TasksCompleted.Inc();
        }
        else if (eventData.Severity == nameof(SeverityLevel.Error))
        {
            TasksFailed.WithLabels(eventData.TaskType).Inc();
        }

        return Task.CompletedTask;
    }
}
```

### Email Alerts

```csharp
public class EmailAlertMonitor
{
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailAlertMonitor> _logger;

    public EmailAlertMonitor(
        IEverTaskWorkerExecutor executor,
        IEmailService emailService,
        ILogger<EmailAlertMonitor> logger)
    {
        _emailService = emailService;
        _logger = logger;
        executor.TaskEventOccurredAsync += OnTaskEventAsync;
    }

    private async Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        // Only alert on critical task failures
        if (eventData.Severity == nameof(SeverityLevel.Error) &&
            IsCriticalTask(eventData.TaskType))
        {
            try
            {
                await _emailService.SendAlertAsync(
                    to: "ops@example.com",
                    subject: $"Critical Task Failure: {eventData.TaskType}",
                    body: $@"
                        Task ID: {eventData.TaskId}
                        Task Type: {eventData.TaskType}
                        Time: {eventData.EventDateUtc}
                        Message: {eventData.Message}
                        Exception: {eventData.Exception}
                    ");

                _logger.LogInformation(
                    "Alert sent for failed task {TaskId}",
                    eventData.TaskId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send alert email");
            }
        }
    }

    private bool IsCriticalTask(string taskType)
    {
        return taskType.Contains("Payment") ||
               taskType.Contains("Order") ||
               taskType.Contains("Billing");
    }
}
```

### Slack Notifications

```csharp
public class SlackMonitor
{
    private readonly HttpClient _httpClient;
    private readonly string _webhookUrl;

    public SlackMonitor(
        IEverTaskWorkerExecutor executor,
        IConfiguration configuration)
    {
        _httpClient = new HttpClient();
        _webhookUrl = configuration["Slack:WebhookUrl"];
        executor.TaskEventOccurredAsync += OnTaskEventAsync;
    }

    private async Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        if (eventData.Severity == nameof(SeverityLevel.Error))
        {
            var payload = new
            {
                text = $"❌ Task Failed: {eventData.TaskType}",
                attachments = new[]
                {
                    new
                    {
                        color = "danger",
                        fields = new[]
                        {
                            new { title = "Task ID", value = eventData.TaskId.ToString(), @short = true },
                            new { title = "Time", value = eventData.EventDateUtc.ToString(), @short = true },
                            new { title = "Message", value = eventData.Message },
                            new { title = "Exception", value = eventData.Exception ?? "N/A" }
                        }
                    }
                }
            };

            await _httpClient.PostAsJsonAsync(_webhookUrl, payload);
        }
    }
}
```

## Logging Integration

EverTask uses standard .NET logging, so it works with whatever logging setup you already have:

### Basic Logging

```csharp
// EverTask automatically logs to ILogger
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
});

// Logs appear in your configured sinks (Console, File, etc.)
```

### Serilog Integration

```bash
dotnet add package EverTask.Serilog
```

```csharp
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(connectionString)
.AddSerilog(opt =>
    opt.ReadFrom.Configuration(
        builder.Configuration,
        new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

appsettings.json:

```json
{
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/evertask-.txt",
          "rollingInterval": "Day"
        }
      },
      {
        "Name": "Seq",
        "Args": {
          "serverUrl": "http://localhost:5341"
        }
      }
    ]
  }
}
```

## Best Practices

### 1. Monitor Critical Tasks

You don't need to alert on everything. Focus on tasks that matter most to your business:

```csharp
private Task OnTaskEventAsync(EverTaskEventData eventData)
{
    if (IsCriticalTask(eventData.TaskType) &&
        eventData.Severity == nameof(SeverityLevel.Error))
    {
        // Immediate alerting for critical failures
        SendImmediateAlert(eventData);
    }

    return Task.CompletedTask;
}
```

### 2. Aggregate Metrics

Instead of just logging individual events, track aggregate metrics to spot trends and performance issues:

```csharp
public class MetricsAggregator
{
    private int _completedCount;
    private int _failedCount;
    private readonly ConcurrentDictionary<string, int> _failuresByType = new();

    private Task OnTaskEventAsync(EverTaskEventData eventData)
    {
        if (eventData.Message.Contains("completed"))
        {
            Interlocked.Increment(ref _completedCount);
        }
        else if (eventData.Severity == nameof(SeverityLevel.Error))
        {
            Interlocked.Increment(ref _failedCount);
            _failuresByType.AddOrUpdate(eventData.TaskType, 1, (_, count) => count + 1);
        }

        return Task.CompletedTask;
    }

    public (int Completed, int Failed, Dictionary<string, int> FailuresByType) GetMetrics()
    {
        return (_completedCount, _failedCount, new Dictionary<string, int>(_failuresByType));
    }
}
```

### 3. Avoid Blocking Operations

Keep event handlers fast. If you need to do something slow (like sending an alert), fire and forget it:

```csharp
// ✅ Good: Fire-and-forget alerting
private Task OnTaskEventAsync(EverTaskEventData eventData)
{
    if (eventData.Severity == nameof(SeverityLevel.Error))
    {
        _ = Task.Run(() => SendAlertAsync(eventData)); // Fire-and-forget
    }

    return Task.CompletedTask;
}

// ❌ Bad: Blocking the event pipeline
private async Task OnTaskEventAsync(EverTaskEventData eventData)
{
    if (eventData.Severity == nameof(SeverityLevel.Error))
    {
        await SendAlertAsync(eventData); // This blocks other events from processing
    }
}
```

### 4. Handle Event Handler Failures

Wrap your event handlers in try-catch blocks so a failing monitor doesn't bring down your entire application:

```csharp
private async Task OnTaskEventAsync(EverTaskEventData eventData)
{
    try
    {
        await ProcessEventAsync(eventData);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error in event handler");
        // Don't let event handler failures crash the application
    }
}
```

### 5. Use Structured Logging

Always use structured logging with named parameters rather than string interpolation. This makes your logs queryable and easier to analyze:

```csharp
private Task OnTaskEventAsync(EverTaskEventData eventData)
{
    _logger.LogInformation(
        "Task event: {TaskId} {TaskType} {Severity} {Message}",
        eventData.TaskId,
        eventData.TaskType,
        eventData.Severity,
        eventData.Message);

    return Task.CompletedTask;
}
```

## Future Monitoring Options

We're planning to add:

- **Sentry Crons** - Automatic cron monitoring for recurring tasks
- **OpenTelemetry** - Distributed tracing and metrics
- **Health Checks** - Built-in health check endpoints
- **Admin Dashboard** - Web-based monitoring and management UI

Stay tuned for updates!

## Next Steps

- **[Resilience](resilience.md)** - Configure retry policies and error handling
- **[Storage](storage.md)** - Query task status and history from storage
- **[Configuration Reference](configuration-reference.md)** - All configuration options
- **[Architecture](architecture.md)** - How monitoring works internally
