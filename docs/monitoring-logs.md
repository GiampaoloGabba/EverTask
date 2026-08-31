---
layout: default
title: Task Execution Logs
parent: Monitoring
nav_order: 5
---

# Task Execution Logs

**Available since:** v3.0

A built-in log capture system records logs written during task execution. It is a proxy that ALWAYS forwards logs to the standard ILogger infrastructure (console, file, Serilog, Application Insights, etc.) and optionally persists them to the database for audit trails.

## Why Use Log Capture?

- Review what a run did, retry attempts included, and investigate a failure with the context the run itself wrote
- Keep a permanent record of it in the database, when an audit trail or a compliance rule asks for one

## Basic Usage

Access the logger via the `Logger` property in your task handler:

```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    public override async Task Handle(ProcessOrderTask task, CancellationToken ct)
    {
        Logger.LogInformation("Processing order {OrderId}", task.OrderId);

        // Your business logic here
        await ProcessOrder(task.OrderId);

        Logger.LogInformation("Order {OrderId} processed successfully", task.OrderId);
    }
}
```

Logs are ALWAYS written to ILogger (console, file, etc.) regardless of persistence settings: leaving persistence off costs you the database rows, not the visibility.

## Structured Logging Support

The `Logger` property supports structured logging with message templates and parameters, exactly like a standard `ILogger`:

```csharp
public class DataProcessingHandler : EverTaskHandler<DataProcessingTask>
{
    public override async Task Handle(DataProcessingTask task, CancellationToken ct)
    {
        // Structured logging with parameters
        Logger.LogTrace("Processing step {Step}/{Total}", 1, task.TotalSteps);
        Logger.LogDebug("User {UserId} initiated processing at {Timestamp}", task.UserId, DateTimeOffset.UtcNow);
        Logger.LogInformation("Processing {Count} items from source {Source}", task.ItemCount, task.Source);

        try
        {
            await ProcessData(task);
        }
        catch (Exception ex)
        {
            // Exception overload - exception as first parameter
            Logger.LogError(ex, "Failed to process task {TaskId} at step {Step}", task.Id, currentStep);
            throw;
        }
    }
}
```

**Supported Overloads:**
- Simple messages: `Logger.LogInformation("message")`
- Structured parameters: `Logger.LogInformation("User {UserId} logged in", userId)`
- With exception: `Logger.LogError(exception, "Failed processing {TaskId}", taskId)`
- All log levels: `LogTrace`, `LogDebug`, `LogInformation`, `LogWarning`, `LogError`, `LogCritical`

**Important**: When persisted to the database, structured parameters are formatted into the final message (e.g., `"User john.doe logged in"`), while the original structured template and parameters are preserved in the ILogger infrastructure for Serilog, Application Insights, etc.

## Configuration

### Enable Database Persistence (Optional)

```csharp
services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .WithPersistentLogger(log => log           // Auto-enables persistent logging
        .SetMinimumLevel(LogLevel.Information) // Only persist Information+
        .SetMaxLogsPerTask(1000)))             // Limit logs per task
    .AddSqlServerStorage(connectionString);
```

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| `WithPersistentLogger` | Disabled | Auto-enables persistent logging. **Logs always go to ILogger regardless!** |
| `Disable()` | - | Disable database persistence (logs still go to ILogger) |
| `SetMinimumLevel()` | `Information` | Minimum log level to persist. Only affects database, not ILogger. |
| `SetMaxLogsPerTask()` | `1000` | Maximum logs to persist per task execution. `null` = unlimited. |

## How It Works

The log capture system uses a proxy pattern:

```
Handler.Logger.LogInformation("msg")
         ↓
   TaskLogCapture (proxy)
    ↙          ↘
ILogger        Database
(always)     (optional)
```

1. Every log call forwards to `ILogger<THandler>`, whatever else happens to it
2. With persistent logging enabled via `.WithPersistentLogger(log => log.Enable())`, the entry is also stored in the database
3. `SetMinimumLevel()` filters that database copy only, never the ILogger one

## Retrieving Persisted Logs

```csharp
// Get all logs for a task, ordered by sequence number
var logs = await storage.GetLogsAsync(taskId);

foreach (var log in logs)
{
    Console.WriteLine($"[{log.Level}] {log.TimestampUtc}: {log.Message}");
    if (log.ExceptionDetails != null)
        Console.WriteLine($"Exception: {log.ExceptionDetails}");
}

// Get a page (1-based page number, page size 1-1000)
var page = await storage.GetLogsAsync(taskId, pageNumber: 1, pageSize: 50);
```

`GetLogsAsync` is an extension over `ITaskStorage` and validates its arguments; the raw
`GetExecutionLogsAsync(taskId, skip, take)` it delegates to is the storage-contract member (see
[Custom Storage](storage/custom-storage.md)).

## Log Retention

Persisted logs don't clean up on their own. Without a policy they stay until their parent task is deleted, so a long-running service (recurring tasks above all) keeps piling them up. Two opt-in settings on `AuditRetentionPolicy` trim them independently of the task, enforced by the cleanup service:

```csharp
var policy = new AuditRetentionPolicy
{
    ExecutionLogRetentionDays = 30,   // drop logs older than 30 days
    MaxExecutionLogsPerTask   = 1000  // keep at most the latest 1000 logs per task
};

services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .WithPersistentLogger(log => log.SetMinimumLevel(LogLevel.Information)))
    .AddSqlServerStorage(connectionString);

// AddAuditCleanup is the single entry-point that applies the policy.
services.AddAuditCleanup(policy, cleanupIntervalHours: 24);
```

| Option | Default | Description |
|--------|---------|-------------|
| `ExecutionLogRetentionDays` | `null` (unlimited) | Delete logs older than this many days (by `TimestampUtc`) |
| `MaxExecutionLogsPerTask` | `null` (disabled) | Keep at most the latest N logs per task, across all of its runs |

When both are set, a log is removed if it breaks either rule. Both default to off, so turning on persistent logging never starts deleting logs by itself. The cleanup runs only when you register `AddAuditCleanup()`.

**This is not the same as `SetMaxLogsPerTask`.** `SetMaxLogsPerTask` caps a *single* execution while it runs: past the limit it stops capturing and records how many entries it dropped. The retention settings above work across *every* past run, after the fact. Use the per-execution cap to keep one chatty run in check; use retention to stop the table growing forever.

## Retry Attempt Tracking

Logs accumulate across ALL retry attempts:

```csharp
public class RetryTaskHandler : EverTaskHandler<RetryTask>
{
    public override async Task Handle(RetryTask task, CancellationToken ct)
    {
        Logger.LogInformation("Attempt started");

        // If this fails and retries, each attempt logs "Attempt started"
        // Database will contain: ["Attempt started", "Attempt started", "Attempt started", ...]
    }
}
```

This is **intentional**: it keeps the full record of every execution attempt.

## Performance Considerations

### When Disabled
- One `if` per log call; the JIT drops the rest of the capture path

### When Enabled
- ~5-10ms over a typical task, ~100 bytes per log in memory, single bulk INSERT after completion
- Logs are written in the finally block, so a failed task keeps the ones that explain why

### Always
- The ILogger call happens either way, with the usual Microsoft.Extensions.Logging cost

## Best Practices

1. `LogInformation` for normal flow, `LogWarning` for something odd, `LogError` for a failure
2. Log the task parameters and the decisions the run made, not just that it started and ended
3. Bound both ends: the default cap of 1000 caps a single execution, and `ExecutionLogRetentionDays` /
   `MaxExecutionLogsPerTask` (see [Log Retention](#log-retention)) keep the table from growing forever
4. Read persisted logs after the fact; for real-time monitoring, watch the ILogger side

## Example: Audit Trail

```csharp
public class PaymentProcessorHandler : EverTaskHandler<ProcessPaymentTask>
{
    public override async Task Handle(ProcessPaymentTask task, CancellationToken ct)
    {
        Logger.LogInformation("Payment processing started for amount {Amount}", task.Amount);

        // Audit critical steps
        Logger.LogInformation("Validating payment method");
        await ValidatePaymentMethod(task.PaymentMethodId);

        Logger.LogInformation("Charging payment gateway");
        var result = await ChargePaymentGateway(task);

        if (result.IsSuccess)
        {
            Logger.LogInformation("Payment succeeded with transaction ID {TransactionId}", result.TransactionId);
        }
        else
        {
            Logger.LogError("Payment failed: {ErrorMessage}", result.ErrorMessage);
            throw new PaymentException(result.ErrorMessage);
        }
    }
}
```

With persistent logging enabled (`.WithPersistentLogger(...)`), all these logs are stored in the database and queryable by `taskId`.

## Next Steps

- **[Monitoring Dashboard](monitoring-dashboard.md)** - View execution logs in the web UI
- **[Dashboard UI Guide](monitoring-dashboard-ui.md)** - Terminal-style log viewer with color-coded severity levels
- **[Custom Event Monitoring](monitoring-events.md)** - Build custom monitoring integrations
- **[Configuration Reference](configuration-reference.md)** - All log capture configuration options
