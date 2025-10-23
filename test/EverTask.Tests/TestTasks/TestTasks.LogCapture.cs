namespace EverTask.Tests;

// Task that logs during execution (for log capture testing)
public record TestTaskWithLogs(string Data) : IEverTask;

public class TestTaskWithLogsHandler : EverTaskHandler<TestTaskWithLogs>
{
    public override async Task Handle(TestTaskWithLogs task, CancellationToken ct)
    {
        Logger.LogInformation($"Processing task with data: {task.Data}");
        await Task.Delay(10, ct);
        Logger.LogInformation("Task processing completed");
    }
}

// Task that fails after logging
public record TestTaskThatFailsWithLogs(string Data) : IEverTask;

public class TestTaskThatFailsWithLogsHandler : EverTaskHandler<TestTaskThatFailsWithLogs>
{
    public override async Task Handle(TestTaskThatFailsWithLogs task, CancellationToken ct)
    {
        Logger.LogInformation($"Starting task with data: {task.Data}");
        Logger.LogWarning("About to throw exception");
        await Task.Delay(10, ct);
        Logger.LogError("Throwing test exception");
        throw new InvalidOperationException("Test exception");
    }
}

// Task that logs at different levels
public record TestTaskMultiLevelLogs() : IEverTask;

public class TestTaskMultiLevelLogsHandler : EverTaskHandler<TestTaskMultiLevelLogs>
{
    public override async Task Handle(TestTaskMultiLevelLogs task, CancellationToken ct)
    {
        Logger.LogTrace("This is a trace message");
        Logger.LogDebug("This is a debug message");
        Logger.LogInformation("This is an information message");
        Logger.LogWarning("This is a warning message");
        Logger.LogError("This is an error message");
        Logger.LogCritical("This is a critical message");
        await Task.CompletedTask;
    }
}

// Task that logs many times (for maxLogs testing)
public record TestTaskManyLogs(int count) : IEverTask;

public class TestTaskManyLogsHandler : EverTaskHandler<TestTaskManyLogs>
{
    public override async Task Handle(TestTaskManyLogs task, CancellationToken ct)
    {
        for (int i = 0; i < task.count; i++)
        {
            Logger.LogInformation($"Log message {i + 1} of {task.count}");
        }
        await Task.CompletedTask;
    }
}

// Task that logs with exception
public record TestTaskLogWithException() : IEverTask;

public class TestTaskLogWithExceptionHandler : EverTaskHandler<TestTaskLogWithException>
{
    public override async Task Handle(TestTaskLogWithException task, CancellationToken ct)
    {
        Logger.LogInformation("Starting task");

        try
        {
            throw new InvalidOperationException("Inner exception");
        }
        catch (Exception ex)
        {
            Logger.LogError("Caught exception", ex);
        }

        Logger.LogInformation("Task completed");
        await Task.CompletedTask;
    }
}
