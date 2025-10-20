using EverTask.Monitoring;
using System.Collections.Concurrent;

namespace EverTask.Tests.Monitoring.TestHelpers;

/// <summary>
/// Helper class for testing SignalR connections
/// </summary>
public class SignalRTestClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly ConcurrentBag<EverTaskEventData> _receivedEvents = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);

    public IReadOnlyCollection<EverTaskEventData> ReceivedEvents => _receivedEvents;
    public HubConnectionState State => _connection.State;
    public string? ConnectionId => _connection.ConnectionId;

    public SignalRTestClient(string url, Action<IHubConnectionBuilder>? configure = null)
    {
        var builder = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect();

        configure?.Invoke(builder);

        _connection = builder.Build();

        // Subscribe to events
        _connection.On<EverTaskEventData>("EverTaskEvent", eventData =>
        {
            _receivedEvents.Add(eventData);
        });

        // Handle reconnection events
        _connection.Reconnecting += error =>
        {
            Console.WriteLine($"SignalR reconnecting: {error?.Message}");
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            Console.WriteLine($"SignalR reconnected: {connectionId}");
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            Console.WriteLine($"SignalR connection closed: {error?.Message}");
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Start the connection to the hub
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(ct);
                // Wait longer to ensure connection is fully established and ready to receive messages
                // SignalR connection negotiation is async and involves multiple round trips
                // The connection must complete transport upgrade and be ready to receive server-sent messages
                await Task.Delay(500, ct);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Stop the connection
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        await _connectionLock.WaitAsync(ct);
        try
        {
            if (_connection.State == HubConnectionState.Connected)
            {
                await _connection.StopAsync(ct);
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <summary>
    /// Clear all received events
    /// </summary>
    public void ClearEvents()
    {
        _receivedEvents.Clear();
    }

    /// <summary>
    /// Wait for a specific number of events to be received
    /// </summary>
    public async Task<bool> WaitForEventsAsync(int expectedCount, int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;
        while (_receivedEvents.Count < expectedCount)
        {
            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                return false;

            await Task.Delay(50);
        }
        return true;
    }

    /// <summary>
    /// Wait for a specific event matching a condition
    /// </summary>
    public async Task<EverTaskEventData?> WaitForEventAsync(
        Func<EverTaskEventData, bool> predicate,
        int timeoutMs = 5000)
    {
        var startTime = DateTime.UtcNow;
        while (true)
        {
            var matchingEvent = _receivedEvents.FirstOrDefault(predicate);
            if (matchingEvent != null)
                return matchingEvent;

            if ((DateTime.UtcNow - startTime).TotalMilliseconds > timeoutMs)
                return null;

            await Task.Delay(50);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _connection.DisposeAsync();
        _connectionLock.Dispose();
    }
}
