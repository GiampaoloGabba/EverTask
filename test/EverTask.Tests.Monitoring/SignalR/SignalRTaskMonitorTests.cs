using EverTask.Monitoring;
using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

/// <summary>
/// Integration tests for SignalR broadcasting.
/// Tests verify end-to-end event propagation through SignalR.
/// </summary>
public class SignalRTaskMonitorTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact(Skip = "SignalR timing issue: Connection negotiation race condition causes intermittent failures in test environments. Events are published correctly but test client may not be fully ready to receive them.")]
    public async Task Should_broadcast_task_started_event()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleTask("Test broadcast");
        await dispatcher.Dispatch(task);

        // Wait for event
        var receivedEvent = await client.WaitForEventAsync(
            e => e.Message.Contains("started", StringComparison.OrdinalIgnoreCase),
            timeoutMs: 5000);

        // Assert
        receivedEvent.ShouldNotBeNull();
        receivedEvent.Severity.ShouldBe("Information");
        receivedEvent.TaskType.ShouldContain("SampleTask");
    }

    [Fact]
    public async Task Should_broadcast_task_completed_event()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleTask("Test completion");
        await dispatcher.Dispatch(task);

        // Wait for completed event
        var receivedEvent = await client.WaitForEventAsync(
            e => e.Message.Contains("completed", StringComparison.OrdinalIgnoreCase),
            timeoutMs: 5000);

        // Assert
        receivedEvent.ShouldNotBeNull();
        receivedEvent.Severity.ShouldBe("Information");
        receivedEvent.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_broadcast_task_error_event()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act - Dispatch a failing task
        var task = new SampleFailingTask("Test error");
        await dispatcher.Dispatch(task);

        // Wait for error event
        var receivedEvent = await client.WaitForEventAsync(
            e => e.Severity == "Error",
            timeoutMs: 5000);

        // Assert
        receivedEvent.ShouldNotBeNull();
        receivedEvent.Severity.ShouldBe("Error");
        receivedEvent.Exception.ShouldNotBeNullOrEmpty();
        receivedEvent.TaskType.ShouldContain("SampleFailingTask");
    }
}

/// <summary>
/// Unit tests for SignalRTaskMonitor filtering behavior.
/// Tests verify that execution logs are correctly filtered based on configuration.
/// </summary>
public class SignalRTaskMonitorUnitTests
{
    [Fact]
    public async Task Should_IncludeExecutionLogs_When_IncludeExecutionLogsIsTrue()
    {
        // Arrange
        var executorMock = new Mock<IEverTaskWorkerExecutor>();
        var hubContextMock = new Mock<IHubContext<TaskMonitorHub>>();
        var loggerMock = new Mock<IEverTaskLogger<SignalRTaskMonitor>>();

        var options = Options.Create(new SignalRMonitoringOptions
        {
            IncludeExecutionLogs = true // ENABLED
        });

        var monitor = new SignalRTaskMonitor(
            executorMock.Object,
            hubContextMock.Object,
            loggerMock.Object,
            options);

        // Create event data with execution logs
        var executionLogs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "Test log message",
                SequenceNumber = 0
            }
        };

        var eventData = new EverTaskEventData(
            TaskId: Guid.NewGuid(),
            EventDateUtc: DateTimeOffset.UtcNow,
            Severity: "Information",
            TaskType: "TestTask",
            TaskHandlerType: "TestHandler",
            TaskParameters: "{}",
            Message: "Task completed",
            Exception: null,
            ExecutionLogs: executionLogs);

        EverTaskEventData? capturedEventData = null;
        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(c => c.SendCoreAsync("EverTaskEvent", It.IsAny<object[]>(), default))
            .Callback<string, object[], CancellationToken>((method, args, ct) =>
            {
                capturedEventData = (EverTaskEventData)args[0];
            })
            .Returns(Task.CompletedTask);

        hubContextMock
            .Setup(h => h.Clients.All)
            .Returns(clientProxyMock.Object);

        // Act
        monitor.SubScribe();
        executorMock.Raise(e => e.TaskEventOccurredAsync += null, eventData);
        await Task.Delay(100); // Allow async event handler to complete

        // Assert
        capturedEventData.ShouldNotBeNull();
        capturedEventData.ExecutionLogs.ShouldNotBeNull();
        capturedEventData.ExecutionLogs.Count.ShouldBe(1);
        capturedEventData.ExecutionLogs[0].Message.ShouldBe("Test log message");
    }

    [Fact]
    public async Task Should_ExcludeExecutionLogs_When_IncludeExecutionLogsIsFalse()
    {
        // Arrange
        var executorMock = new Mock<IEverTaskWorkerExecutor>();
        var hubContextMock = new Mock<IHubContext<TaskMonitorHub>>();
        var loggerMock = new Mock<IEverTaskLogger<SignalRTaskMonitor>>();

        var options = Options.Create(new SignalRMonitoringOptions
        {
            IncludeExecutionLogs = false // DISABLED (default)
        });

        var monitor = new SignalRTaskMonitor(
            executorMock.Object,
            hubContextMock.Object,
            loggerMock.Object,
            options);

        // Create event data with execution logs
        var executionLogs = new List<TaskExecutionLog>
        {
            new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = Guid.NewGuid(),
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = "Information",
                Message = "Test log message",
                SequenceNumber = 0
            }
        };

        var eventData = new EverTaskEventData(
            TaskId: Guid.NewGuid(),
            EventDateUtc: DateTimeOffset.UtcNow,
            Severity: "Information",
            TaskType: "TestTask",
            TaskHandlerType: "TestHandler",
            TaskParameters: "{}",
            Message: "Task completed",
            Exception: null,
            ExecutionLogs: executionLogs); // Logs present in event

        EverTaskEventData? capturedEventData = null;
        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(c => c.SendCoreAsync("EverTaskEvent", It.IsAny<object[]>(), default))
            .Callback<string, object[], CancellationToken>((method, args, ct) =>
            {
                capturedEventData = (EverTaskEventData)args[0];
            })
            .Returns(Task.CompletedTask);

        hubContextMock
            .Setup(h => h.Clients.All)
            .Returns(clientProxyMock.Object);

        // Act
        monitor.SubScribe();
        executorMock.Raise(e => e.TaskEventOccurredAsync += null, eventData);
        await Task.Delay(100); // Allow async event handler to complete

        // Assert
        capturedEventData.ShouldNotBeNull();
        capturedEventData.ExecutionLogs.ShouldBeNull(); // Logs stripped by monitor
    }

    [Fact]
    public async Task Should_HandleNullExecutionLogs_When_IncludeExecutionLogsIsTrue()
    {
        // Arrange
        var executorMock = new Mock<IEverTaskWorkerExecutor>();
        var hubContextMock = new Mock<IHubContext<TaskMonitorHub>>();
        var loggerMock = new Mock<IEverTaskLogger<SignalRTaskMonitor>>();

        var options = Options.Create(new SignalRMonitoringOptions
        {
            IncludeExecutionLogs = true // ENABLED
        });

        var monitor = new SignalRTaskMonitor(
            executorMock.Object,
            hubContextMock.Object,
            loggerMock.Object,
            options);

        // Create event data WITHOUT execution logs
        var eventData = new EverTaskEventData(
            TaskId: Guid.NewGuid(),
            EventDateUtc: DateTimeOffset.UtcNow,
            Severity: "Information",
            TaskType: "TestTask",
            TaskHandlerType: "TestHandler",
            TaskParameters: "{}",
            Message: "Task started",
            Exception: null,
            ExecutionLogs: null); // No logs

        EverTaskEventData? capturedEventData = null;
        var clientProxyMock = new Mock<IClientProxy>();
        clientProxyMock
            .Setup(c => c.SendCoreAsync("EverTaskEvent", It.IsAny<object[]>(), default))
            .Callback<string, object[], CancellationToken>((method, args, ct) =>
            {
                capturedEventData = (EverTaskEventData)args[0];
            })
            .Returns(Task.CompletedTask);

        hubContextMock
            .Setup(h => h.Clients.All)
            .Returns(clientProxyMock.Object);

        // Act
        monitor.SubScribe();
        executorMock.Raise(e => e.TaskEventOccurredAsync += null, eventData);
        await Task.Delay(100); // Allow async event handler to complete

        // Assert
        capturedEventData.ShouldNotBeNull();
        capturedEventData.ExecutionLogs.ShouldBeNull(); // Still null
    }

    [Fact]
    public void Should_UseDefaultConfiguration_When_OptionsNotProvided()
    {
        // Arrange
        var executorMock = new Mock<IEverTaskWorkerExecutor>();
        var hubContextMock = new Mock<IHubContext<TaskMonitorHub>>();
        var loggerMock = new Mock<IEverTaskLogger<SignalRTaskMonitor>>();

        // Default options (IncludeExecutionLogs = false)
        var options = Options.Create(new SignalRMonitoringOptions());

        // Act
        var monitor = new SignalRTaskMonitor(
            executorMock.Object,
            hubContextMock.Object,
            loggerMock.Object,
            options);

        // Assert - no exception thrown, monitor created successfully
        monitor.ShouldNotBeNull();
    }
}
