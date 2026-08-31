using System.Collections.Concurrent;
using EverTask.Tests.Monitoring.TestData;
using EverTask.Tests.Monitoring.TestHelpers;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.Monitoring.SignalR;

/// <summary>
/// What the monitoring hub really puts on the wire, read as JSON text rather than through a deserializer.
/// </summary>
/// <remarks>
/// <c>EverTaskEventData</c> is the second wire the dashboard reads, and the only one that does not come from
/// a <c>Monitor.Api</c> DTO: EverTask publishes the record and <c>SignalRTaskMonitor</c> sends it verbatim,
/// so the guardian in <c>JsonContractTests</c> — which walks the <c>EverTask.Monitor.Api.DTOs</c> namespace —
/// cannot see it, and the schedule context this release added to the record was mirrored by nobody. The
/// payload is therefore taken as a <see cref="JsonElement"/> exactly as it arrived, and every key on it has
/// to be a key <c>UI/src/types/signalr.types.ts</c> declares.
/// </remarks>
public class EventWireContractTests : MonitoringTestBase
{
    protected override bool EnableWorker => true;

    private ITaskDispatcher Dispatcher => Factory.Services.GetRequiredService<ITaskDispatcher>();

    /// <summary>The keys that say which schedule a delivery belongs to.</summary>
    private static readonly string[] ScheduleKeys = ["parentTaskId", "scheduledAtUtc", "scheduleVersion"];

    [Fact]
    public async Task Should_declare_every_key_the_hub_sends_in_the_dashboard_types()
    {
        await using var client = CreateRawClient();
        await client.StartAsync();

        var taskId = await Dispatcher.Dispatch(new SampleTask("wire contract"));

        var payload = await client.WaitForAsync(e => e.GetProperty("taskId").GetString() == taskId.ToString());
        payload.ShouldNotBeNull("the hub delivers an event for a task the worker just ran");

        ShouldBeDeclared(nameof(EverTaskEventData), payload.Value);
    }

    /// <summary>
    /// A key holding null is not the same as no key at all, and on this wire it is the FIRST that happens:
    /// the hub protocol writes nulls. A consumer therefore tells a schedule delivery apart by the VALUE of
    /// these keys, not by their presence — the opposite of the REST wire, where the API omits them.
    /// </summary>
    [Fact]
    public async Task Should_write_the_schedule_keys_as_null_for_a_task_that_belongs_to_no_schedule()
    {
        await using var client = CreateRawClient();
        await client.StartAsync();

        var taskId = await Dispatcher.Dispatch(new SampleTask("no schedule"));

        var payload = await client.WaitForAsync(e => e.GetProperty("taskId").GetString() == taskId.ToString());
        payload.ShouldNotBeNull();

        foreach (var key in ScheduleKeys)
        {
            payload.Value.TryGetProperty(key, out var value).ShouldBeTrue(
                $"the hub writes '{key}' rather than omitting it");
            value.ValueKind.ShouldBe(JsonValueKind.Null, $"'{key}' is null on a task that belongs to no schedule");
        }
    }

    [Fact]
    public async Task Should_carry_the_schedule_context_of_an_occurrence()
    {
        await using var client = CreateRawClient();
        await client.StartAsync();

        var scheduleId = await Dispatcher.Dispatch(new SampleTask("durable on the wire"), r =>
        {
            var schedule = r.Schedule().Every(1).Seconds().WithDurableOccurrences();
            schedule.MaxRuns(1);
        });

        var payload = await client.WaitForAsync(e =>
            e.TryGetProperty("parentTaskId", out var parent)
            && parent.ValueKind == JsonValueKind.String
            && parent.GetString() == scheduleId.ToString());

        payload.ShouldNotBeNull("the occurrence the schedule materializes publishes its own events");

        payload.Value.GetProperty("scheduledAtUtc").ValueKind.ShouldBe(JsonValueKind.String,
            "an occurrence stands for a nominal slot");
        payload.Value.GetProperty("scheduleVersion").GetInt32().ShouldBe(0,
            "a schedule nobody has rescheduled is at version 0");

        ShouldBeDeclared(nameof(EverTaskEventData), payload.Value);
    }

    /// <summary>
    /// The captured logs the hub forwards, whose shape no host in this suite produces: persistent logging is
    /// off in the factory, so an event with logs is built here and written with the protocol's OWN options —
    /// the same <c>JsonHubProtocolOptions</c> defaults the server serializes with.
    /// </summary>
    [Fact]
    public void Should_declare_every_key_of_a_forwarded_execution_log()
    {
        var log = new TaskExecutionLog
        {
            Id             = Guid.NewGuid(),
            TaskId         = Guid.NewGuid(),
            TimestampUtc   = DateTimeOffset.UtcNow,
            Level          = nameof(LogLevel.Information),
            Message        = "captured",
            SequenceNumber = 0
        };

        var data = new EverTaskEventData(Guid.NewGuid(), DateTimeOffset.UtcNow, nameof(SeverityLevel.Information),
            "type", "handler", "{}", "message", null, [log]);

        var payload = JsonSerializer.SerializeToElement(data, new JsonHubProtocolOptions().PayloadSerializerOptions);

        ShouldBeDeclared(nameof(EverTaskEventData), payload);

        var entries = payload.GetProperty("executionLogs");
        entries.GetArrayLength().ShouldBe(1);
        ShouldBeDeclared("TaskExecutionLogData", entries[0]);
    }

    /// <summary>Every key of <paramref name="payload"/> is a key the named TypeScript interface declares.</summary>
    private static void ShouldBeDeclared(string interfaceName, JsonElement payload)
    {
        var types = TypeScriptTypes.Load();
        types.Declares(interfaceName).ShouldBeTrue($"the dashboard declares {interfaceName}");

        var undeclared = payload.EnumerateObject()
                                .Where(p => types.StateOf(interfaceName, p.Name) == TypeScriptTypes.KeyState.Missing)
                                .Select(p => p.Name)
                                .ToList();

        undeclared.ShouldBeEmpty(
            $"the hub sends {string.Join(", ", undeclared)} and {interfaceName} in " +
            "UI/src/types/signalr.types.ts declares no such key");
    }

    private RawSignalRClient CreateRawClient()
    {
        var baseUrl = Client.BaseAddress!.ToString().TrimEnd('/');
        return new RawSignalRClient($"{baseUrl}/evertask-monitoring/hub", Factory.Server.CreateHandler());
    }

    /// <summary>
    /// A hub client that keeps the payload as JSON. <c>HubConnection</c> binds the argument types of the
    /// FIRST handler registered for a method name, so this cannot share <see cref="SignalRTestClient"/>'s
    /// typed subscription: it is a connection of its own.
    /// </summary>
    private sealed class RawSignalRClient : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly ConcurrentQueue<JsonElement> _payloads = new();

        public RawSignalRClient(string url, HttpMessageHandler handler)
        {
            _connection = new HubConnectionBuilder()
                          .WithUrl(url, options => options.HttpMessageHandlerFactory = _ => handler)
                          .Build();

            _connection.On<JsonElement>("EverTaskEvent", payload => _payloads.Enqueue(payload.Clone()));
        }

        public Task StartAsync() => _connection.StartAsync();

        public async Task<JsonElement?> WaitForAsync(Func<JsonElement, bool> predicate, int timeoutMs = 30000)
        {
            var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTimeOffset.UtcNow < deadline)
            {
                foreach (var payload in _payloads)
                {
                    if (predicate(payload))
                        return payload;
                }

                await Task.Delay(50);
            }

            return null;
        }

        public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
    }
}
