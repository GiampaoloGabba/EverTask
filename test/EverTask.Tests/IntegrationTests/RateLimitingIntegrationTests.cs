using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// End-to-end keyed rate limiting tests (real IHost, memory storage, fast scheduler).
/// Determinism rules: deferral monitoring events as sync points (a deferral is
/// storage-invisible), lower-bound-only timing assertions with ≥100 ms tolerance,
/// budget windows ≥ 10× the 50 ms scheduler check interval, generous condition-wait ceilings.
/// </summary>
public class RateLimitingIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly RateLimitTestState _state = new();

    /// <summary>
    /// Creates a host tuned for rate limiting tests: fast scheduler (50 ms check interval) and
    /// per-deferral events (aggregation window zeroed) so every deferral is observable.
    /// </summary>
    private async Task<IHost> CreateRateLimitHostAsync(
        int channelCapacity = 10,
        int maxDegreeOfParallelism = 3,
        Action<EverTaskServiceBuilder>? configureBuilder = null,
        ITaskStorage? sharedStorage = null)
    {
        var host = await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);

                if (sharedStorage != null)
                    b.Services.AddSingleton(sharedStorage);

                configureBuilder?.Invoke(b);

                // Fast scheduler: parked tasks must redeliver with 50 ms granularity
                b.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new PeriodicTimerScheduler(
                    sp.GetRequiredService<IWorkerQueueManager>(),
                    sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                    TimeSpan.FromMilliseconds(50))));
            },
            configureEverTask: cfg => cfg.SetChannelOptions(channelCapacity)
                                         .SetMaxDegreeOfParallelism(maxDegreeOfParallelism));

        // Per-deferral events: the aggregation window would otherwise swallow the second
        // deferral of the same key, which tests use as a sync point
        ((RateLimitGate)host.Services.GetRequiredService<IRateLimitGate>()).DeferralEventWindow = TimeSpan.Zero;

        return host;
    }

    [Fact]
    public async Task Should_not_block_other_keys_when_one_key_exhausts_budget()
    {
        await CreateRateLimitHostAsync();

        // Key A: 5 single-permit tasks → backlog stretching ~3.6 s (slots every 900 ms)
        var idsA = new List<Guid>();
        for (var i = 0; i < 5; i++)
            idsA.Add(await Dispatcher.Dispatch(new RateLimitedSinglePermitTask("key-A", i)));

        // Key B dispatched while A is backlogged: must flow immediately (no head-of-line blocking)
        await Dispatcher.Dispatch(new RateLimitedSinglePermitTask("key-B", 100));

        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ExecutionCountByIndex.ContainsKey(100), timeoutMs: 15000);

        // All of A eventually completes exactly once
        await TaskWaitHelper.WaitForConditionAsync(
            () => Enumerable.Range(0, 5).All(i => _state.ExecutionCountByIndex.ContainsKey(i)), timeoutMs: 30000);

        Enumerable.Range(0, 5).ShouldAllBe(i => _state.ExecutionCountByIndex[i] == 1);
        _state.ExecutionCountByIndex[100].ShouldBe(1);

        // Ordering: B (instant) finished before A's last backlogged occurrence (slot ≥ +3.6 s)
        var bExecutedAt = _state.Executions.Single(e => e.Index == 100).ExecutedAtUtc;
        var lastA       = _state.TimestampsForKey("key-A").Last();
        bExecutedAt.ShouldBeLessThan(lastA, "a key without budget must never stall other keys");

        // Lower-bound spacing on the throttled key (tolerance 100 ms)
        var aTimestamps = _state.TimestampsForKey("key-A");
        for (var i = 1; i < aTimestamps.Length; i++)
        {
            (aTimestamps[i] - aTimestamps[i - 1]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(800),
                "key-A executions must respect the 900 ms emission interval (lower bound)");
        }
    }

    [Fact]
    public async Task Should_recover_parked_task_after_restart()
    {
        await CreateRateLimitHostAsync();

        // Warm-up consumes the single 8 s permit; the second task gets parked far in the future
        var warmupId = await Dispatcher.Dispatch(new RateLimitedLongWindowTask("restart-key", 0));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        using var deferrals = TaskWaitHelper.CreateDeferralCollector(WorkerExecutor);
        var parkedId = await Dispatcher.Dispatch(new RateLimitedLongWindowTask("restart-key", 1));
        await deferrals.WaitForTaskAsync(parkedId, timeoutMs: 10000);

        // A deferral is storage-invisible: the parked task still reads Queued (recoverable)
        (await Storage.GetAll()).Single(t => t.Id == parkedId).Status.ShouldBe(QueuedTaskStatus.Queued);
        _state.ExecutionCountByIndex.ContainsKey(1).ShouldBeFalse();

        var sharedStorage = Storage;

        // Restart: the parked registration (in-memory only) is lost with host 1; startup
        // recovery of host 2 must re-dispatch the Queued task. The fresh limiter admits it
        // immediately (documented restart semantics: buckets restart full).
        await CreateRateLimitHostAsync(sharedStorage: sharedStorage);

        await WaitForTaskStatusAsync(parkedId, QueuedTaskStatus.Completed, timeoutMs: 20000);
        _state.ExecutionCountByIndex[1].ShouldBe(1);
    }

    [Fact]
    public async Task Should_not_execute_task_when_cancelled_while_parked()
    {
        await CreateRateLimitHostAsync();

        // Long 8 s window: the Cancel below must land well before the reserved slot even under
        // heavy parallel test load
        var warmupId = await Dispatcher.Dispatch(new RateLimitedLongWindowTask("cancel-key", 0));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        using var deferrals = TaskWaitHelper.CreateDeferralCollector(WorkerExecutor);
        var parkedId = await Dispatcher.Dispatch(new RateLimitedLongWindowTask("cancel-key", 1));
        await deferrals.WaitForTaskAsync(parkedId, timeoutMs: 10000);

        await Dispatcher.Cancel(parkedId);

        var cancelled = await WaitForTaskStatusAsync(parkedId, QueuedTaskStatus.Cancelled, timeoutMs: 5000);
        cancelled.ShouldNotBeNull();

        // Wait until past the reserved slot (parsed from the deferral event would be nicer, but
        // the slot is warmup execution + 8 s; +1 s margin): the cancelled task must never fire
        var slotUtc = _state.Executions.Single(e => e.Index == 0).ExecutedAtUtc.AddSeconds(8 + 1);
        var remaining = slotUtc - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining);

        _state.ExecutionCountByIndex.ContainsKey(1).ShouldBeFalse(
            "a task cancelled while parked must not execute when its slot fires");

        (await Storage.GetAll()).Single(t => t.Id == parkedId).Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_execute_latest_payload_once_when_redispatched_with_same_task_key_while_parked()
    {
        await CreateRateLimitHostAsync();

        // Warm-up exhausts the 2 s budget of the key
        var warmupId = await Dispatcher.Dispatch(new RateLimitedPayloadTask("payload-key", "warmup"));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // v1 is parked at the reserved slot...
        using var deferrals = TaskWaitHelper.CreateDeferralCollector(WorkerExecutor);
        var v1Id = await Dispatcher.Dispatch(new RateLimitedPayloadTask("payload-key", "v1"), taskKey: "pk-latest");
        await deferrals.WaitForTaskAsync(v1Id, timeoutMs: 10000);

        // ...and v2 re-dispatches the SAME taskKey while v1 is parked: same persistence id,
        // latest payload must win and execute exactly once
        var v2Id = await Dispatcher.Dispatch(new RateLimitedPayloadTask("payload-key", "v2"), taskKey: "pk-latest");
        v2Id.ShouldBe(v1Id, "the taskKey re-dispatch updates the same persisted task");

        await WaitForTaskStatusAsync(v1Id, QueuedTaskStatus.Completed, timeoutMs: 20000);

        // Give any stale parked occurrence a chance to misfire before asserting exactly-once
        await Task.Delay(2500);

        var executed = _state.ExecutedPayloads.Where(p => p != "warmup").ToArray();
        executed.ShouldBe(new[] { "v2" },
            "the latest payload must execute exactly once; the stale parked payload must not fire");
    }

    [Fact]
    public async Task Should_execute_all_tasks_exactly_once_when_single_key_floods_beyond_channel_capacity()
    {
        // Channel capacity 3 << 8 flooded tasks on one key (budget 2 per 700 ms):
        // deferrals re-park and re-enter the bounded channel in waves
        await CreateRateLimitHostAsync(channelCapacity: 3);

        const int floodSize = 8;
        var ids = new List<Guid>();
        for (var i = 0; i < floodSize; i++)
            ids.Add(await Dispatcher.Dispatch(new RateLimitedFloodTask("flood-key", i)));

        await TaskWaitHelper.WaitForConditionAsync(
            () => Enumerable.Range(0, floodSize).All(i => _state.ExecutionCountByIndex.ContainsKey(i)),
            timeoutMs: 30000);

        // Exactly once each, no loss and no double execution
        Enumerable.Range(0, floodSize).ShouldAllBe(i => _state.ExecutionCountByIndex[i] == 1);

        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.All(t => t.Status == QueuedTaskStatus.Completed),
            timeoutMs: 10000);
    }

    [Fact]
    public async Task Should_gate_task_when_rerouted_to_default_queue_by_FallbackToDefault()
    {
        await CreateRateLimitHostAsync(configureBuilder: b =>
            b.AddQueue("tiny", q => q.SetChannelCapacity(1)
                                     .SetMaxDegreeOfParallelism(1)
                                     .SetFullBehavior(QueueFullBehavior.FallbackToDefault)));

        // Task 0 occupies the single "tiny" consumer (and consumes the key's only permit)...
        await Dispatcher.Dispatch(new RateLimitedQueueRoutedTask("route-key", 0));
        (await _state.SlowEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // ...task 1 fills the tiny channel...
        await Dispatcher.Dispatch(new RateLimitedQueueRoutedTask("route-key", 1));

        // ...and task 2 overflows it, falling back to the DEFAULT queue. The gate is per task
        // type, so the reroute must still be rate-limited: with no budget left it gets deferred.
        using var deferrals = TaskWaitHelper.CreateDeferralCollector(WorkerExecutor);
        var fallbackId = await Dispatcher.Dispatch(new RateLimitedQueueRoutedTask("route-key", 2));

        var deferral = await deferrals.WaitForTaskAsync(fallbackId, timeoutMs: 10000);
        deferral.ShouldNotBeNull("the task rerouted by FallbackToDefault must still hit the rate-limit gate");

        // Release the blocker: everything must drain to completion, each exactly once
        _state.SlowGate.Release(10);

        await TaskWaitHelper.WaitForConditionAsync(
            () => Enumerable.Range(0, 3).All(i => _state.ExecutionCountByIndex.ContainsKey(i)), timeoutMs: 30000);

        Enumerable.Range(0, 3).ShouldAllBe(i => _state.ExecutionCountByIndex[i] == 1);
    }

    // ---------------------------------------------------------------- WS4: retry integration

    [Fact]
    public async Task Should_throttle_retries_when_ThrottleRetries_enabled()
    {
        await CreateRateLimitHostAsync();

        // Fails on attempts 1 and 2, succeeds on attempt 3; every retry must re-acquire budget
        // (1 permit per 700 ms), waiting in-slot between attempts
        var taskId = await Dispatcher.Dispatch(new RateLimitedRetryTask("retry-key", 0));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 20000);

        _state.ExecutionCountByIndex[0].ShouldBe(3, "two failures + the successful third attempt");

        var attempts = _state.TimestampsForKey("retry-key");
        attempts.Length.ShouldBe(3);
        for (var i = 1; i < attempts.Length; i++)
        {
            (attempts[i] - attempts[i - 1]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(600),
                "throttled retries must respect the 700 ms emission interval (lower bound)");
        }
    }

    [Fact]
    public async Task Should_not_throttle_retries_when_ThrottleRetries_disabled()
    {
        await CreateRateLimitHostAsync();

        // 60 s window with ThrottleRetries=false: if retries were throttled, attempt 2 would
        // park for ~60 s and the task could not complete within the ceiling below
        var taskId = await Dispatcher.Dispatch(new RateLimitedUnthrottledRetryTask("unthrottled-key", 0));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 15000);

        _state.ExecutionCountByIndex[0].ShouldBe(3,
            "all three attempts run back-to-back when retries bypass the limiter");
    }

    [Fact]
    public async Task Should_not_erode_attempt_timeout_when_waiting_for_token()
    {
        await CreateRateLimitHostAsync();

        // Attempt 2 waits ~650 ms for budget and then runs for 300 ms against a 500 ms
        // per-attempt timeout: it can only succeed if the budget wait did NOT consume the timeout
        var taskId = await Dispatcher.Dispatch(new RateLimitedTimeoutRetryTask("timeout-key", 0));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 20000);

        _state.ExecutionCountByIndex[0].ShouldBe(2);

        var attempts = _state.TimestampsForKey("timeout-key");
        (attempts[1] - attempts[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(600),
            "the budget wait happens BEFORE the attempt (and before its timeout starts)");
    }

    [Fact]
    public async Task Should_repark_retry_when_budget_wait_exceeds_MaxInSlotWait()
    {
        await CreateRateLimitHostAsync();

        // MaxInSlotWait=0 forbids in-slot waits: the throttled retry must re-park the task at
        // its +2 s slot and the attempt sequence restarts on redelivery (no Failed status)
        var taskId = await Dispatcher.Dispatch(new RateLimitedReparkRetryTask("repark-key", 0));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 25000);

        _state.ExecutionCountByIndex[0].ShouldBe(2,
            "first delivery fails attempt 1 and re-parks; the redelivery succeeds at attempt 1");

        var attempts = _state.TimestampsForKey("repark-key");
        (attempts[1] - attempts[0]).ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1700),
            "the second execution must wait for the re-parked +2 s slot (lower bound)");
    }

    [Fact]
    public async Task Should_eventually_execute_parked_task_when_slot_lands_during_in_flight_duplicate()
    {
        await CreateRateLimitHostAsync();

        // Duplicate delivery of the SAME persistence id (e.g. startup recovery racing a live
        // dispatch). Crafted directly against the worker executor for deterministic interleaving.
        var policy = new RateLimitPolicy(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 };
        var executor = new TaskHandlerExecutor(
            new RateLimitedSlowTask("dup-key"),
            Handler: null,
            typeof(RateLimitedSlowTaskHandler).AssemblyQualifiedName,
            ExecutionTime: null,
            RecurringTask: null,
            null, null, null, null,
            GuidGenerator.NewDatabaseFriendly(),
            "default",
            TaskKey: null,
            AuditLevel.Full,
            policy,
            "dup-key");

        // First delivery acquires the only permit and blocks in the handler (in-flight)
        var first = WorkerExecutor.DoWork(executor, CancellationToken.None).AsTask();
        (await _state.SlowEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Second delivery: no budget → slot at +700 ms ≤ MaxInSlotWait (1 s) → the gate waits
        // IN-SLOT and redeems while the duplicate is still executing. The in-flight guard then
        // skips it: no double execution, no scheduler round-trip, no hang.
        var second = WorkerExecutor.DoWork(executor with { }, CancellationToken.None).AsTask();
        (await Task.WhenAny(second, Task.Delay(10000))).ShouldBe(second, "the duplicate delivery must not hang");

        // Let the first (real) execution finish
        _state.SlowGate.Release(10);
        await first;

        await Task.Delay(500); // any late stray execution would surface here

        Volatile.Read(ref _state.SlowExecutions).ShouldBe(1,
            "the task executes exactly once: the in-flight execution IS the task, the duplicate is skipped");
    }
}
