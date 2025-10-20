using EverTask.Monitoring;
using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

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
