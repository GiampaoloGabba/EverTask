using EverTask.Abstractions;

namespace EverTask.Example.AspnetCore;

/// <summary>
/// Demo task that generates rich logging at multiple levels for testing the execution logs UI
/// </summary>
public record DemoLoggingTask(
    string TaskName,
    int LogCount = 20,
    bool ShouldFail = false
) : IEverTask;

public class DemoLoggingTaskHandler : EverTaskHandler<DemoLoggingTask>
{
    public override async Task Handle(DemoLoggingTask task, CancellationToken cancellationToken)
    {
        Logger.LogTrace("TRACE: Starting execution of task '{TaskName}' with {LogCount} log messages",
            task.TaskName, task.LogCount);

        Logger.LogDebug("DEBUG: Task configuration - Name: {TaskName}, LogCount: {LogCount}, ShouldFail: {ShouldFail}",
            task.TaskName, task.LogCount, task.ShouldFail);

        Logger.LogInformation("INFORMATION: Task '{TaskName}' is processing...", task.TaskName);

        // Simulate work with multiple log levels
        for (int i = 1; i <= task.LogCount; i++)
        {
            await Task.Delay(100, cancellationToken); // Simulate some work

            var logLevel = i % 6; // Cycle through log levels
            switch (logLevel)
            {
                case 0:
                    Logger.LogTrace("TRACE: Processing step {Step}/{Total} - Detailed diagnostic information", i, task.LogCount);
                    break;
                case 1:
                    Logger.LogDebug("DEBUG: Processing step {Step}/{Total} - Debug information for developers", i, task.LogCount);
                    break;
                case 2:
                    Logger.LogInformation("INFORMATION: Processing step {Step}/{Total} - Normal operation", i, task.LogCount);
                    break;
                case 3:
                    Logger.LogWarning("WARNING: Processing step {Step}/{Total} - Potential issue detected", i, task.LogCount);
                    break;
                case 4:
                    Logger.LogError("ERROR: Processing step {Step}/{Total} - Recoverable error encountered", i, task.LogCount);
                    break;
                case 5:
                    Logger.LogCritical("CRITICAL: Processing step {Step}/{Total} - Critical system state", i, task.LogCount);
                    break;
            }

            // Add some contextual logs every 5 steps
            if (i % 5 == 0)
            {
                Logger.LogInformation("CHECKPOINT: Completed {Completed}% of task '{TaskName}'",
                    (i * 100 / task.LogCount), task.TaskName);
            }
        }

        if (task.ShouldFail)
        {
            Logger.LogError("ERROR: Task '{TaskName}' is configured to fail - throwing exception", task.TaskName);
            throw new InvalidOperationException($"Simulated failure for task '{task.TaskName}' as requested");
        }

        Logger.LogInformation("SUCCESS: Task '{TaskName}' completed successfully with {LogCount} log messages",
            task.TaskName, task.LogCount);
    }

    public override ValueTask OnStarted(Guid persistenceId)
    {
        Logger.LogInformation("=== STARTED: DemoLoggingTask {PersistenceId} ===", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid persistenceId)
    {
        Logger.LogInformation("=== COMPLETED: DemoLoggingTask {PersistenceId} ===", persistenceId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        Logger.LogError(exception, "=== FAILED: DemoLoggingTask {PersistenceId} - {Message} ===",
            persistenceId, message);
        return ValueTask.CompletedTask;
    }
}
