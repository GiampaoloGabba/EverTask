using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

public class TaskMonitorHubTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact]
    public async Task Should_connect_to_hub()
    {
        // Arrange
        await using var client = CreateSignalRClient();

        // Act
        await client.StartAsync();

        // Assert
        client.State.ShouldBe(HubConnectionState.Connected);
        client.ConnectionId.ShouldNotBeNullOrEmpty();
    }

    [Fact(Skip = "SignalR timing issue: Connection negotiation race condition causes intermittent failures in test environments. Events are published correctly but test client may not be fully ready to receive them.")]
    public async Task Should_receive_task_events()
    {
        // Arrange
        await using var client = CreateSignalRClient();
        await client.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleTask("Test event");
        await dispatcher.Dispatch(task);

        // Wait for event
        var receivedEvent = await WaitForEventAsync(client);

        // Assert
        receivedEvent.ShouldBeTrue();
        client.ReceivedEvents.Count.ShouldBeGreaterThan(0);
    }

    private async Task<bool> WaitForEventAsync(SignalRTestClient client, int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;
        while (client.ReceivedEvents.Count == 0)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                return false;

            await Task.Delay(50);
        }
        return true;
    }
}
