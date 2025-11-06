---
layout: default
title: Task Execution Logs
parent: Monitoring
nav_order: 5
---

# Task Execution Logs

**Available since:** v3.0

EverTask provides a built-in log capture system that records logs written during task execution. The logger acts as a **proxy** that ALWAYS forwards logs to the standard ILogger infrastructure (console, file, Serilog, Application Insights, etc.) and **optionally** persists them to the database for audit trails.

## Why Use Log Capture?

- **Debugging**: Review exactly what happened during task execution, including retry attempts
- **Audit Trails**: Keep a permanent record of task execution logs in the database
- **Compliance**: Meet regulatory requirements for task execution logging
- **Root Cause Analysis**: Investigate failures with full execution context

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

**Key Point**: Logs are ALWAYS written to ILogger (console, file, etc.) regardless of persistence settings. This ensures you never lose visibility into task execution.

## Structured Logging Support

The `Logger` property fully supports **structured logging** with message templates and parameters, just like standard `ILogger`:

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

The log capture system uses a **proxy pattern**:

```
Handler.Logger.LogInformation("msg")
         ↓
   TaskLogCapture (proxy)
    ↙          ↘
ILogger        Database
(always)     (optional)
```

1. **Always Log to ILogger**: Every log call forwards to `ILogger<THandler>` for standard logging infrastructure
2. **Conditional Persistence**: If persistent logging is enabled via `.WithPersistentLogger(log => log.Enable())`, logs are also stored in database
3. **Filtered Persistence**: `SetMinimumLevel()` filters only database persistence, not ILogger

## Retrieving Persisted Logs

```csharp
// Get all logs for a task
var logs = await storage.GetExecutionLogsAsync(taskId);

foreach (var log in logs)
{
    Console.WriteLine($"[{log.Level}] {log.TimestampUtc}: {log.Message}");
    if (log.ExceptionDetails != null)
        Console.WriteLine($"Exception: {log.ExceptionDetails}");
}

// Get paginated logs
var page = await storage.GetExecutionLogsAsync(taskId, skip: 0, take: 50);
```

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

This is **intentional** - it provides complete visibility into all execution attempts.

## Performance Considerations

### When Disabled
- **Zero overhead** — JIT optimizations eliminate all log capture code paths
- Single `if` check per log call (negligible performance impact)

### When Enabled
- **Minimal impact** — ~5-10ms overhead for typical tasks
- ~100 bytes per log in memory, single bulk INSERT after task completion
- **Logs persist even on failure** — Captured in the finally block for debugging failed tasks

### Always
- **ILogger Always Invoked** — Standard Microsoft.Extensions.Logging overhead applies regardless of persistence settings

## Best Practices

1. **Use Standard Log Levels**: `LogInformation` for normal flow, `LogWarning` for issues, `LogError` for failures
2. **Include Context**: Log task parameters and key decision points
3. **Set Reasonable Limits**: Default 1000 logs per task prevents unbounded growth
4. **Use for Debugging**: Don't rely on persisted logs for real-time monitoring (use ILogger infrastructure)
5. **Clean Up Old Logs**: Implement retention policies to prevent database bloat

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
