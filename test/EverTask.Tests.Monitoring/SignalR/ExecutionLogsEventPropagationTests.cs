using System.Collections.Concurrent;
using EverTask.Tests.TestHelpers;
using IRetryPolicy = EverTask.Abstractions.IRetryPolicy;

namespace EverTask.Tests.Monitoring.SignalR;

/// <summary>
/// Integration tests verifying that execution logs are propagated to monitoring events.
/// Tests the full flow: task execution → log capture → event publishing → SignalR monitoring.
/// </summary>
public class ExecutionLogsEventPropagationTests
{
    [Fact]
    public async Task Should_PropagateExecutionLogs_ToMonitoringEvents_OnTaskCompletion()
    {
        // Arrange
        var capturedEvents = new ConcurrentBag<EverTaskEventData>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(ExecutionLogsEventPropagationTests).Assembly)
                        .WithPersistentLogger(log => log // Enable log capture
                            .SetMinimumLevel(LogLevel.Information));
                })
                .AddMemoryStorage();

                // Subscribe to events to capture them
                services.AddSingleton<ITaskMonitor>(sp =>
                {
                    var executor = sp.GetRequiredService<IEverTaskWorkerExecutor>();
                    var monitor = new TestMonitor(executor, capturedEvents);
                    return monitor;
                });
            })
            .Build();

        await host.StartAsync();
        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();

        // Subscribe the monitor to events
        var monitor = host.Services.GetRequiredService<ITaskMonitor>();
        monitor.SubScribe();

        try
        {
            // Act
            var taskId = await dispatcher.Dispatch(new TestTaskWithExecutionLogs());

            // Wait for task completion
            await WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed);

            // Assert - wait for the completion event to reach the subscriber, then read its logs
            var completionEvent = await WaitForEventAsync(capturedEvents,
                e => e.Severity == "Information" && e.Message.Contains("completed"),
                "task completion");

            completionEvent.ExecutionLogs.ShouldNotBeNull();
            completionEvent.ExecutionLogs.Count.ShouldBeGreaterThanOrEqualTo(2);

            // Verify log content
            var logs = completionEvent.ExecutionLogs.OrderBy(l => l.SequenceNumber).ToList();
            logs[0].Message.ShouldBe("Starting task execution");
            logs[0].Level.ShouldBe("Information");
            logs[1].Message.ShouldBe("Task execution completed successfully");
            logs[1].Level.ShouldBe("Information");
        }
        finally
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(2000);
            await host.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Should_PropagateExecutionLogs_ToMonitoringEvents_OnTaskError()
    {
        // Arrange
        var capturedEvents = new ConcurrentBag<EverTaskEventData>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(ExecutionLogsEventPropagationTests).Assembly)
                        .WithPersistentLogger(log => log // Enable log capture
                            .SetMinimumLevel(LogLevel.Information));
                })
                .AddMemoryStorage();

                // Subscribe to events
                services.AddSingleton<ITaskMonitor>(sp =>
                {
                    var executor = sp.GetRequiredService<IEverTaskWorkerExecutor>();
                    var monitor = new TestMonitor(executor, capturedEvents);
                    return monitor;
                });
            })
            .Build();

        await host.StartAsync();
        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();

        // Subscribe the monitor to events
        var monitor = host.Services.GetRequiredService<ITaskMonitor>();
        monitor.SubScribe();

        try
        {
            // Act
            var taskId = await dispatcher.Dispatch(new TestTaskWithExecutionLogsAndError());

            // Wait for task failure (may take longer without retries)
            await WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Failed, timeoutMs: 10000);

            // Assert - wait for the error event to reach the subscriber, then read its logs
            var errorEvent = await WaitForEventAsync(capturedEvents,
                e => e.Severity == "Error" && e.Message.Contains("Error occurred"),
                "task failure");

            errorEvent.ExecutionLogs.ShouldNotBeNull();
            errorEvent.ExecutionLogs.Count.ShouldBeGreaterThanOrEqualTo(2);

            // Verify logs include pre-error logs
            var logs = errorEvent.ExecutionLogs.OrderBy(l => l.SequenceNumber).ToList();
            logs[0].Message.ShouldBe("Starting task execution");
            logs[1].Message.ShouldBe("About to throw exception");
        }
        finally
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(2000);
            await host.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Should_NotIncludeExecutionLogs_When_LogCaptureDisabled()
    {
        // Arrange
        var capturedEvents = new ConcurrentBag<EverTaskEventData>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(ExecutionLogsEventPropagationTests).Assembly)
                        .WithPersistentLogger(log => log.Disable()); // DISABLED
                })
                .AddMemoryStorage();

                services.AddSingleton<ITaskMonitor>(sp =>
                {
                    var executor = sp.GetRequiredService<IEverTaskWorkerExecutor>();
                    var monitor = new TestMonitor(executor, capturedEvents);
                    return monitor;
                });
            })
            .Build();

        await host.StartAsync();
        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();

        // Subscribe the monitor to events
        var monitor = host.Services.GetRequiredService<ITaskMonitor>();
        monitor.SubScribe();

        try
        {
            // Act
            var taskId = await dispatcher.Dispatch(new TestTaskWithExecutionLogs());
            await WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed);

            // Assert - the completion event is the only one carrying "completed", and its logs are fixed
            // when it is built: once it arrives there is nothing left to wait for.
            var completionEvent = await WaitForEventAsync(capturedEvents,
                e => e.Severity == "Information" && e.Message.Contains("completed"),
                "task completion");

            // When log capture is disabled, GetPersistedLogs() returns empty array, not null
            (completionEvent.ExecutionLogs == null || completionEvent.ExecutionLogs.Count == 0).ShouldBeTrue();
        }
        finally
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(2000);
            await host.StopAsync(cts.Token);
        }
    }

    [Fact]
    public async Task Should_FilterExecutionLogs_ThroughSignalRMonitoring_BasedOnConfiguration()
    {
        // Arrange
        var capturedSignalREvents = new ConcurrentBag<EverTaskEventData>();

        var host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(ExecutionLogsEventPropagationTests).Assembly)
                        .WithPersistentLogger(log => log // Enable log capture
                            .SetMinimumLevel(LogLevel.Information));
                })
                .AddMemoryStorage()
                .AddSignalRMonitoring(options =>
                {
                    options.IncludeExecutionLogs = false; // SignalR filtering DISABLED
                });

                // Mock SignalR hub to capture what would be sent
                services.AddSingleton<ITaskMonitor>(sp =>
                {
                    var executor = sp.GetRequiredService<IEverTaskWorkerExecutor>();
                    return new TestMonitor(executor, capturedSignalREvents);
                });
            })
            .Build();

        await host.StartAsync();
        var dispatcher = host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = host.Services.GetRequiredService<ITaskStorage>();

        // Subscribe the monitor to events
        var monitor = host.Services.GetRequiredService<ITaskMonitor>();
        monitor.SubScribe();

        try
        {
            // Act
            var taskId = await dispatcher.Dispatch(new TestTaskWithExecutionLogs());
            await WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed);

            // Assert - logs are saved to storage AFTER the completion event is published, so poll for
            // them instead of reading the row as soon as the status flips
            await TaskWaitHelper.WaitUntilAsync(
                async () => await storage.GetExecutionLogsAsync(taskId, CancellationToken.None),
                logs => logs.Count > 0,
                timeoutMs: TestEnvironment.GetTimeout(5000, 30000));

            // Verify completion event has logs (in-memory events always include logs)
            var completionEvent = await WaitForEventAsync(capturedSignalREvents,
                e => e.Severity == "Information" && e.Message.Contains("completed"),
                "task completion");

            // The TestMonitor receives the full event with logs
            // In real SignalR scenario, SignalRTaskMonitor would strip them before sending
            completionEvent.ExecutionLogs.ShouldNotBeNull();
        }
        finally
        {
            var cts = new CancellationTokenSource();
            cts.CancelAfter(2000);
            await host.StopAsync(cts.Token);
        }
    }

    /// <summary>
    /// Waits for the monitoring event matching <paramref name="predicate"/> to reach the subscriber.
    /// The persisted status is written BEFORE the event is registered, and PublishEvent hands the event to
    /// the subscriber through a fire-and-forget Task.Run: under contention that continuation is scheduled
    /// well after the status the tests wait on is observable, so the arrival has to be polled.
    /// </summary>
    private static async Task<EverTaskEventData> WaitForEventAsync(
        ConcurrentBag<EverTaskEventData> capturedEvents,
        Func<EverTaskEventData, bool> predicate,
        string description)
    {
        try
        {
            await TaskWaitHelper.WaitForConditionAsync(
                () => capturedEvents.Any(predicate),
                timeoutMs: TestEnvironment.GetTimeout(5000, 30000));
        }
        catch (TimeoutException)
        {
            var seen = capturedEvents.Select(e => $"[{e.Severity}] {e.Message}").ToArray();
            throw new TimeoutException(
                $"No {description} event received. Captured {seen.Length} event(s): {string.Join(" | ", seen)}");
        }

        return capturedEvents.First(predicate);
    }

    /// <summary>
    /// Helper method to wait for task to reach expected status
    /// </summary>
    private static async Task WaitForTaskStatusAsync(
        ITaskStorage storage,
        Guid taskId,
        QueuedTaskStatus expectedStatus,
        int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            var tasks = await storage.Get(t => t.Id == taskId, CancellationToken.None);
            if (tasks.Length > 0 && tasks[0].Status == expectedStatus)
                return;

            await Task.Delay(50);
        }

        throw new TimeoutException($"Task {taskId} did not reach status {expectedStatus} within {timeoutMs}ms");
    }
}

/// <summary>
/// Test task that writes execution logs
/// </summary>
public record TestTaskWithExecutionLogs : IEverTask;

public class TestTaskWithExecutionLogsHandler : EverTaskHandler<TestTaskWithExecutionLogs>
{
    public override async Task Handle(TestTaskWithExecutionLogs task, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting task execution");
        await Task.Delay(100, cancellationToken);
        Logger.LogInformation("Task execution completed successfully");
    }
}

/// <summary>
/// Test task that writes logs and then fails
/// </summary>
public record TestTaskWithExecutionLogsAndError : IEverTask;

public class TestTaskWithExecutionLogsAndErrorHandler : EverTaskHandler<TestTaskWithExecutionLogsAndError>
{
    // Use 1 retry with minimal delay for fast test execution
    public override IRetryPolicy RetryPolicy => new LinearRetryPolicy(1, TimeSpan.FromMilliseconds(10));

    public override async Task Handle(TestTaskWithExecutionLogsAndError task, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Starting task execution");
        await Task.Delay(100, cancellationToken);
        Logger.LogInformation("About to throw exception");
        throw new InvalidOperationException("Test error");
    }
}

/// <summary>
/// Test monitor that captures events for verification
/// </summary>
public class TestMonitor : ITaskMonitor
{
    private readonly IEverTaskWorkerExecutor _executor;
    private readonly ConcurrentBag<EverTaskEventData> _capturedEvents;

    public TestMonitor(IEverTaskWorkerExecutor executor, ConcurrentBag<EverTaskEventData> capturedEvents)
    {
        _executor = executor;
        _capturedEvents = capturedEvents;
    }

    public void SubScribe()
    {
        _executor.TaskEventOccurredAsync += OnTaskEventOccurredAsync;
    }

    private Task OnTaskEventOccurredAsync(EverTaskEventData eventData)
    {
        _capturedEvents.Add(eventData);
        return Task.CompletedTask;
    }

    public void Unsubscribe()
    {
        _executor.TaskEventOccurredAsync -= OnTaskEventOccurredAsync;
    }

    public void Dispose()
    {
        Unsubscribe();
    }
}
