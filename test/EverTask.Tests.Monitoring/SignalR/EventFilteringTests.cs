using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

public class EventFilteringTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact]
    public async Task Should_receive_events_for_all_severity_levels()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act - Dispatch successful and failing tasks
        var successTask = new SampleTask("Success test");
        var failTask = new SampleFailingTask("Fail test");

        await dispatcher.Dispatch(successTask);
        await dispatcher.Dispatch(failTask);

        // Wait for events
        await client.WaitForEventsAsync(4, timeoutMs: 5000); // Expect at least 4 events (2 started + 1 completed + 1 error)

        // Assert
        var informationEvents = client.ReceivedEvents.Where(e => e.Severity == "Information").ToList();
        var errorEvents = client.ReceivedEvents.Where(e => e.Severity == "Error").ToList();

        informationEvents.Count.ShouldBeGreaterThan(0);
        errorEvents.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Should_receive_complete_event_data()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleTask("Complete data test");
        await dispatcher.Dispatch(task);

        // Wait for event
        var receivedEvent = await client.WaitForEventAsync(
            e => e.TaskParameters.Contains("Complete data test"),
            timeoutMs: 5000);

        // Assert
        receivedEvent.ShouldNotBeNull();
        receivedEvent.TaskId.ShouldNotBe(Guid.Empty);
        receivedEvent.EventDateUtc.ShouldNotBe(default);
        receivedEvent.Severity.ShouldNotBeNullOrEmpty();
        receivedEvent.TaskType.ShouldNotBeNullOrEmpty();
        receivedEvent.TaskHandlerType.ShouldNotBeNullOrEmpty();
        receivedEvent.TaskParameters.ShouldNotBeNullOrEmpty();
        receivedEvent.Message.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Should_include_exception_details_for_failed_tasks()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleFailingTask("Exception test");
        await dispatcher.Dispatch(task);

        // Wait for error event
        var errorEvent = await client.WaitForEventAsync(
            e => e.Severity == "Error" && e.Exception != null,
            timeoutMs: 5000);

        // Assert
        errorEvent.ShouldNotBeNull();
        errorEvent.Exception.ShouldNotBeNullOrEmpty();
        errorEvent.Exception.ShouldContain("InvalidOperationException");
        errorEvent.Exception.ShouldContain("This task always fails");
    }
}
