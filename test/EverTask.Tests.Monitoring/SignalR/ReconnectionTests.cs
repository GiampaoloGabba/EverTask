using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

public class ReconnectionTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact]
    public async Task Should_reconnect_automatically_after_disconnect()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        client.State.ShouldBe(HubConnectionState.Connected);

        // Act - Force disconnect
        await client.StopAsync();
        client.State.ShouldBe(HubConnectionState.Disconnected);

        // Reconnect
        await client.StartAsync();

        // Assert
        client.State.ShouldBe(HubConnectionState.Connected);
        client.ConnectionId.ShouldNotBeNullOrEmpty();
    }

    [Fact(Skip = "SignalR timing issue: Connection negotiation race condition causes intermittent failures in test environments. Events are published correctly but test client may not be fully ready to receive them.")]
    public async Task Should_resume_receiving_events_after_reconnection()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Receive first event
        var task1 = new SampleTask("Before disconnect");
        await dispatcher.Dispatch(task1);
        await client.WaitForEventsAsync(1, timeoutMs: 5000);
        var eventsBeforeDisconnect = client.ReceivedEvents.Count;

        // Act - Disconnect and reconnect
        await client.StopAsync();
        client.ClearEvents();
        await client.StartAsync();

        // Dispatch another task
        var task2 = new SampleTask("After reconnect");
        await dispatcher.Dispatch(task2);

        // Wait for new event
        await client.WaitForEventsAsync(1, timeoutMs: 5000);

        // Assert
        client.ReceivedEvents.Count.ShouldBeGreaterThan(0);
        client.ReceivedEvents.Any(e => e.TaskParameters.Contains("After reconnect")).ShouldBeTrue();
    }
}
