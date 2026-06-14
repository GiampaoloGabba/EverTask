using EverTask.Configuration;
using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using EverTask.Worker;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// M3 — taskKey dedup atomicity and in-flight payload safety:
/// CU6/L31 (in-flight re-dispatch loses the new payload), G14/CU23 (concurrent same-taskKey insert),
/// G16 (recurring destroyed when re-dispatched as one-shot), G17 (terminal Remove races a recovery
/// delivery), CU15 (parking-lot leak when the re-dispatch enqueue fails).
/// </summary>
public class TaskKeyDedupIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly TaskKeyTestState _state = new();
    private readonly ResilienceTestState _resilienceState = new();

    [Fact]
    public async Task Should_not_lose_new_payload_on_inflight_taskkey_redispatch()
    {
        // CU6/L31: re-dispatching a taskKey whose previous delivery is in flight must NOT silently
        // accept a new payload that never runs (old executes, new lost). Either the new payload runs
        // or the re-dispatch is rejected — never "stored=new but executed=old".
        await CreateIsolatedHostAsync(
            channelCapacity: 5,
            maxDegreeOfParallelism: 1,
            configureServices: s => s.AddSingleton(_state));

        // Occupy the single consumer so the keyed task stays in the channel (Queued, delivering).
        await Dispatcher.Dispatch(new TaskKeyBlockerTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        var id1 = await Dispatcher.Dispatch(new TaskKeyPayloadTask(1), taskKey: "payload-key");
        var id2 = await Dispatcher.Dispatch(new TaskKeyPayloadTask(2), taskKey: "payload-key");
        id2.ShouldBe(id1);

        _state.BlockingGate.Release(10);
        await WaitForTaskStatusAsync(id1, QueuedTaskStatus.Completed, timeoutMs: 10000);
        await Task.Delay(300);

        // Exactly one keyed payload executed, and it matches the payload finally stored (no
        // accepted-but-lost new payload).
        var executed = _state.ExecutedPayloads.Where(p => p is 1 or 2).ToArray();
        executed.Length.ShouldBe(1);

        var stored = JsonConvert.DeserializeObject<TaskKeyPayloadTask>(
            (await Storage.GetAll()).Single(t => t.Id == id1).Request)!.Payload;
        executed.ShouldContain(stored);
    }

    [Fact]
    public async Task Should_return_winner_id_on_concurrent_taskkey_insert_conflict()
    {
        // G14/CU23 + G13: many concurrent dispatches of the SAME new taskKey must resolve to a single
        // winning row and id (the read-decide-write is serialized per taskKey), never multiple rows
        // each executing as a logical duplicate.
        await CreateIsolatedHostAsync(
            maxDegreeOfParallelism: 4,
            configureServices: s => s.AddSingleton(_state));

        const string key = "concurrent-key";
        const int concurrency = 20;

        var dispatches = Enumerable.Range(0, concurrency)
            .Select(i => Dispatcher.Dispatch(new TaskKeyPayloadTask(i), taskKey: key))
            .ToArray();

        var ids = await Task.WhenAll(dispatches);

        ids.Distinct().Count().ShouldBe(1, "all concurrent same-taskKey dispatches must resolve to one winner id");
        (await Storage.GetAll()).Count(t => t.TaskKey == key)
            .ShouldBe(1, "exactly one row must exist for the taskKey");
    }

    [Fact]
    public async Task Should_not_destroy_recurring_when_redispatched_as_oneshot()
    {
        // G16: a recurring task re-dispatched with the SAME taskKey but no recurring config must not be
        // converted to / replaced by a one-shot (schedule + history destroyed).
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var recurringId = await Dispatcher.Dispatch(new TaskKeyRecurringTask(),
            r => r.RunDelayed(TimeSpan.FromMinutes(30)).Then().UseCron("0 0 * * *"),
            taskKey: "recurring-key");

        // Parked in the scheduler (far-future first occurrence): stable recurring row.
        var recurring = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).FirstOrDefault(t => t.Id == recurringId),
            t => t is { IsRecurring: true, RecurringTask: not null },
            timeoutMs: 5000);
        recurring.ShouldNotBeNull();

        // Re-dispatch the same key as a one-shot (no recurring builder).
        var oneShotId = await Dispatcher.Dispatch(new TaskKeyRecurringTask(), taskKey: "recurring-key");
        oneShotId.ShouldBe(recurringId);

        var row = (await Storage.GetAll()).Single(t => t.Id == recurringId);
        row.IsRecurring.ShouldBeTrue("the recurring definition must be preserved");
        row.RecurringTask.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_not_double_execute_when_terminal_remove_races_recovery()
    {
        // G17: a row read as terminal (ServiceStopped) by the taskKey path must not be Removed while a
        // concurrent recovery delivery of it is in flight — that deletes the row under the delivery and
        // creates a second one (double execution).
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var registry = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();

        var seededId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id           = seededId,
            Type         = typeof(TaskKeyPayloadTask).AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new TaskKeyPayloadTask(7)),
            Handler      = typeof(TaskKeyPayloadTaskHandler).AssemblyQualifiedName!,
            Status       = QueuedTaskStatus.ServiceStopped,
            TaskKey      = "terminal-key",
            CreatedAtUtc = DateTimeOffset.UtcNow // after the recovery cutoff: recovery ignores it
        });

        // Simulate a concurrent recovery whose delivery of this id is in flight.
        registry.TryBegin(seededId).ShouldBeTrue();

        var redispatchId = await Dispatcher.Dispatch(new TaskKeyPayloadTask(8), taskKey: "terminal-key");

        // The in-flight row must not be removed/replaced: the re-dispatch returns the existing id.
        redispatchId.ShouldBe(seededId);
        (await Storage.GetAll()).Count(t => t.Id == seededId).ShouldBe(1);

        registry.End(seededId);
    }

    [Fact]
    public async Task Should_not_leak_parkinglot_on_failed_taskkey_enqueue()
    {
        // CU15: an immediate re-dispatch of a parked taskKey unschedules the old occurrence BEFORE the
        // new enqueue; if the enqueue fails (full ThrowException queue) the parked registration is lost
        // and nothing is enqueued. The unschedule must happen only AFTER a successful enqueue.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                        .SetMaxDegreeOfParallelism(1)
                                        .SetFullBehavior(QueueFullBehavior.ThrowException));
            b.Services.AddSingleton(_resilienceState);
        });

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();

        // A delayed task parked in the scheduler, targeting the "blocked" queue.
        var parkedId = await Dispatcher.Dispatch(new ResilienceBlockingTask(), TimeSpan.FromSeconds(30), taskKey: "park-key");
        await WaitForTaskStatusAsync(parkedId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);
        scheduler.IsScheduled(parkedId).ShouldBeTrue();

        // Saturate the "blocked" queue: consumer busy + channel full.
        await Dispatcher.Dispatch(new ResilienceBlockingTask());
        (await _resilienceState.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Dispatcher.Dispatch(new ResilienceBlockingTask());

        // Immediate re-dispatch with the same taskKey: the enqueue to the full ThrowException queue fails.
        var threw = false;
        try
        {
            await Dispatcher.Dispatch(new ResilienceBlockingTask(), taskKey: "park-key");
        }
        catch (QueueFullException)
        {
            threw = true;
        }

        threw.ShouldBeTrue("the immediate re-dispatch enqueue should fail on the full ThrowException queue");

        // The parked registration must survive the failed enqueue (no leak).
        scheduler.IsScheduled(parkedId).ShouldBeTrue(
            "the parked scheduler registration must be preserved when the re-dispatch enqueue fails");
    }
}
