using EverTask.Tests.Monitoring.TestHelpers;
using EverTask.Tests.Monitoring.TestData;

namespace EverTask.Tests.Monitoring.SignalR;

public class MultiClientTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    [Fact]
    public async Task Should_broadcast_to_all_connected_clients()
    {
        // Arrange - Connect 3 clients
        await using var client1 = CreateSignalRClient();
        await using var client2 = CreateSignalRClient();
        await using var client3 = CreateSignalRClient();

        await client1.StartAsync();
        await client2.StartAsync();
        await client3.StartAsync();

        var dispatcher = Factory.Services.GetRequiredService<ITaskDispatcher>();

        // Act
        var task = new SampleTask("Broadcast test");
        await dispatcher.Dispatch(task);

        // Wait for all clients to receive events
        await Task.WhenAll(
            client1.WaitForEventsAsync(1, timeoutMs: 5000),
            client2.WaitForEventsAsync(1, timeoutMs: 5000),
            client3.WaitForEventsAsync(1, timeoutMs: 5000)
        );

        // Assert
        client1.ReceivedEvents.Count.ShouldBeGreaterThan(0);
        client2.ReceivedEvents.Count.ShouldBeGreaterThan(0);
        client3.ReceivedEvents.Count.ShouldBeGreaterThan(0);

        // Verify all clients received the same event
        var event1 = client1.ReceivedEvents.First();
        var event2 = client2.ReceivedEvents.First();
        var event3 = client3.ReceivedEvents.First();

        event1.TaskId.ShouldBe(event2.TaskId);
        event2.TaskId.ShouldBe(event3.TaskId);
    }

    [Fact]
    public async Task Should_handle_concurrent_connections()
    {
        // Arrange - Create 5 clients concurrently
        var clients = new List<SignalRTestClient>();
        for (int i = 0; i < 5; i++)
        {
            clients.Add(CreateSignalRClient());
        }

        // Act - Connect all clients concurrently
        var connectTasks = clients.Select(c => c.StartAsync()).ToArray();
        await Task.WhenAll(connectTasks);

        // Assert
        foreach (var client in clients)
        {
            client.State.ShouldBe(HubConnectionState.Connected);
            client.ConnectionId.ShouldNotBeNullOrEmpty();
        }

        // Cleanup
        foreach (var client in clients)
        {
            await client.DisposeAsync();
        }
    }
}
