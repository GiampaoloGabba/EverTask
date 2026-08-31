using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;

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
        var task   = new SampleTask("Broadcast test");
        var taskId = await dispatcher.Dispatch(task);

        // Anchor on THIS task's id: startup recovery of other fixtures' rows broadcasts its own events,
        // so the first event a client happens to receive is not necessarily this dispatch.
        var events = await Task.WhenAll(
            client1.WaitForEventAsync(e => e.TaskId == taskId, timeoutMs: 10000),
            client2.WaitForEventAsync(e => e.TaskId == taskId, timeoutMs: 10000),
            client3.WaitForEventAsync(e => e.TaskId == taskId, timeoutMs: 10000)
        );

        // Assert - every client received this task's event
        events[0].ShouldNotBeNull();
        events[1].ShouldNotBeNull();
        events[2].ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_handle_concurrent_connections()
    {
        // Arrange - Create 5 clients concurrently
        var clients = new List<SignalRTestClient>();
        for (var i = 0; i < 5; i++)
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
