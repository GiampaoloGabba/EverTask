using System.Diagnostics;
using EverTask.Configuration;
using EverTask.Dispatcher;
using EverTask.Handler;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Regression tests for the queue-saturation failure modes:
/// 1. startup deadlock when the cold-start backlog exceeds a queue's capacity;
/// 2. tasks persisted as WaitingQueue invisible to startup recovery (silent loss);
/// 3. scheduler head-of-line blocking across queues when one queue is full;
/// 4. dispatch on a full Wait queue not honoring the caller's CancellationToken;
/// 5. recurring tasks dying after a restart without taskKey re-registration.
/// </summary>
public class QueueResilienceIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    private static QueuedTask CreateSeededTask(IEverTask task, QueuedTaskStatus status, DateTimeOffset createdAt)
        => new()
        {
            Id           = Guid.NewGuid(),
            Type         = task.GetType().AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(task),
            Handler      = "seeded-by-test",
            Status       = status,
            CreatedAtUtc = createdAt
        };

    [Fact]
    public async Task Should_not_deadlock_at_startup_when_pending_backlog_exceeds_queue_capacity()
    {
        const int backlogSize = 12;

        // Host with a tiny default queue (capacity 3): before the fix, recovery filled the
        // channel before consumers were started and the startup wedged forever.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false,
            configureEverTask: cfg => cfg.SetChannelOptions(3).SetMaxDegreeOfParallelism(2));

        var seededAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < backlogSize; i++)
        {
            await Storage.Persist(CreateSeededTask(
                new ResilienceCounterTask(i), QueuedTaskStatus.Queued, seededAt.AddMilliseconds(i)));
        }

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ExecutedIndexes.Count >= backlogSize, timeoutMs: 20000);

        _state.ExecutedIndexes.OrderBy(x => x)
              .ShouldBe(Enumerable.Range(0, backlogSize));

        await TaskWaitHelper.WaitUntilAsync(
            async () => await Storage.GetAll(),
            tasks => tasks.All(t => t.Status == QueuedTaskStatus.Completed),
            timeoutMs: 10000);
    }

    [Fact]
    public async Task Should_recover_delayed_WaitingQueue_task_after_restart()
    {
        // Host 1: dispatch a delayed one-shot task; while parked in the in-memory scheduler
        // its storage status is WaitingQueue.
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var taskId = await Dispatcher.Dispatch(new ResilienceCounterTask(7), TimeSpan.FromSeconds(8));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);

        var sharedStorage = Storage;

        // Host 2 shares the same storage: simulates an application restart while the task was parked.
        // Before the fix, RetrievePending excluded WaitingQueue and the task was silently lost.
        await CreateIsolatedHostAsync(configureServices: s =>
        {
            s.AddSingleton(_state);
            s.AddSingleton<ITaskStorage>(sharedStorage);
        });

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 20000);
        _state.ExecutedIndexes.ShouldContain(7);
    }

    [Fact]
    public async Task Should_recover_task_rejected_by_ThrowException_queue_at_next_restart()
    {
        // Host 1: "blocked" queue with capacity 1, single consumer, fail-fast on full.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                        .SetMaxDegreeOfParallelism(1)
                                        .SetFullBehavior(QueueFullBehavior.ThrowException));
            b.Services.AddSingleton(_state);
        });

        // Occupy the single consumer, then fill the channel.
        await Dispatcher.Dispatch(new ResilienceBlockingTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Dispatcher.Dispatch(new ResilienceBlockingTask());

        // Saturated: the dispatcher must fail fast...
        var ex = await Should.ThrowAsync<QueueFullException>(
            () => Dispatcher.Dispatch(new ResilienceBlockingTask()));

        // ...and the rejected task must stay persisted as WaitingQueue (recoverable), not lost.
        var rejected = await TaskWaitHelper.WaitForTaskStatusAsync(
            Storage, ex.TaskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);
        rejected.ShouldNotBeNull();

        var sharedStorage = Storage;

        // Host 2 (restart) with a roomier queue: ALL three tasks must complete,
        // including the one rejected by the full queue.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(10).SetMaxDegreeOfParallelism(1));
            b.Services.AddSingleton(_state);
            b.Services.AddSingleton<ITaskStorage>(sharedStorage);
        });

        _state.BlockingGate.Release(10);

        await TaskWaitHelper.WaitForConditionAsync(() => _state.BlockingCompleted >= 3, timeoutMs: 20000);
        await WaitForTaskStatusAsync(ex.TaskId, QueuedTaskStatus.Completed, timeoutMs: 10000);
    }

    [Fact]
    public async Task Should_cancel_dispatch_wait_when_caller_token_is_cancelled_on_full_queue()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                        .SetMaxDegreeOfParallelism(1)
                                        .SetFullBehavior(QueueFullBehavior.Wait));
            b.Services.AddSingleton(_state);
        });

        // Occupy the single consumer, then fill the channel.
        var firstId = await Dispatcher.Dispatch(new ResilienceBlockingTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        var secondId = await Dispatcher.Dispatch(new ResilienceBlockingTask());

        // The third dispatch waits on the full queue: the caller's token (e.g. an aborted
        // HTTP request) must unblock it. Before the fix this await hung forever.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var stopwatch = Stopwatch.StartNew();
        var cancelled = false;
        try
        {
            await Dispatcher.Dispatch(new ResilienceBlockingTask(), cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        stopwatch.Stop();
        cancelled.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));

        // The cancelled task is persisted with status EXACTLY Queued (not Failed, not lost):
        // SetQueued ran before the cancelled write and nothing may downgrade it.
        var thirdTask = (await Storage.GetAll()).Single(t => t.Id != firstId && t.Id != secondId);
        thirdTask.Status.ShouldBe(QueuedTaskStatus.Queued);

        // The system stays healthy: release the gate and drain the queue.
        var sharedStorage = Storage;
        _state.BlockingGate.Release(20);
        await TaskWaitHelper.WaitForConditionAsync(() => _state.BlockingCompleted >= 2, timeoutMs: 10000);

        // Invariant: the cancelled-while-waiting task is recovered and executed at the next restart.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(10).SetMaxDegreeOfParallelism(1));
            b.Services.AddSingleton(_state);
            b.Services.AddSingleton<ITaskStorage>(sharedStorage);
        });

        await WaitForTaskStatusAsync(thirdTask.Id, QueuedTaskStatus.Completed, timeoutMs: 15000);
        _state.BlockingCompleted.ShouldBe(3);
    }

    [Fact]
    public async Task Should_not_block_other_queues_scheduled_tasks_when_one_queue_is_full()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                        .SetMaxDegreeOfParallelism(1)
                                        .SetFullBehavior(QueueFullBehavior.Wait));
            b.Services.AddSingleton(_state);
        });

        // Shorten the scheduler retry backoff to keep the test fast.
        var scheduler = Host!.Services.GetRequiredService<IScheduler>().ShouldBeOfType<PeriodicTimerScheduler>();
        scheduler.FullQueueRetryDelay = TimeSpan.FromMilliseconds(250);

        // Saturate the "blocked" queue: consumer busy + channel full.
        await Dispatcher.Dispatch(new ResilienceBlockingTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();
        await Dispatcher.Dispatch(new ResilienceBlockingTask());

        // Two delayed tasks due at the same time: one targets the saturated queue,
        // the other the default queue.
        var blockedDelayedId = await Dispatcher.Dispatch(new ResilienceBlockingTask(), TimeSpan.FromMilliseconds(700));
        var defaultDelayedId = await Dispatcher.Dispatch(new ResilienceDefaultQueueTask(), TimeSpan.FromMilliseconds(700));

        // Before the fix the single scheduler loop wedged on the full queue and the default
        // queue's task was never dispatched (head-of-line blocking).
        await WaitForTaskStatusAsync(defaultDelayedId, QueuedTaskStatus.Completed, timeoutMs: 10000);
        _state.DefaultQueueCompleted.ShouldBe(1);

        // Once the queue frees up, the parked task must be dispatched by the retry backoff.
        _state.BlockingGate.Release(10);
        await WaitForTaskStatusAsync(blockedDelayedId, QueuedTaskStatus.Completed, timeoutMs: 15000);
        await TaskWaitHelper.WaitForConditionAsync(() => _state.BlockingCompleted >= 3, timeoutMs: 10000);
    }

    [Fact]
    public async Task Should_execute_each_task_exactly_once_when_recovery_races_live_dispatch()
    {
        const int seededCount = 120; // > pageSize (100): exercises keyset pagination too
        const int liveCount   = 20;

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false,
            configureEverTask: cfg => cfg.SetChannelOptions(50).SetMaxDegreeOfParallelism(4));

        var seededAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < seededCount; i++)
        {
            await Storage.Persist(CreateSeededTask(
                new ResilienceCounterTask(i), QueuedTaskStatus.Queued, seededAt.AddMilliseconds(i)));
        }

        await Host!.StartAsync();

        // Live dispatches while recovery is still paginating: none of these (nor the seeded
        // ones) may be executed more than once (in-channel registry + in-flight guard + cutoff).
        for (var i = 0; i < liveCount; i++)
        {
            await Dispatcher.Dispatch(new ResilienceCounterTask(1000 + i));
        }

        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ExecutedIndexes.Count >= seededCount + liveCount, timeoutMs: 30000);
        await Task.Delay(500); // margin: a duplicate execution would land right behind

        var executions = _state.ExecutedIndexes.GroupBy(i => i).ToList();
        executions.Count.ShouldBe(seededCount + liveCount);
        executions.ShouldAllBe(g => g.Count() == 1);
    }

    [Fact]
    public async Task Should_preserve_stored_NextRunUtc_when_reviving_recurring_task()
    {
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var taskId = await Dispatcher.Dispatch(new ResilienceRecurringTask(),
            r => r.RunDelayed(TimeSpan.FromMilliseconds(300)).Then().UseCron("*/10 * * * * *"));

        // Wait for a completed run whose NextRunUtc is far enough away to survive the restart window.
        var anchorTask = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).FirstOrDefault(t => t.Id == taskId),
            t => t is { NextRunUtc: not null, CurrentRunCount: > 0 }
                 && t.NextRunUtc.Value - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(6),
            timeoutMs: 35000);

        var anchor       = anchorTask!.NextRunUtc!.Value;
        var runsAtAnchor = anchorTask.CurrentRunCount!.Value;

        var sharedStorage = Storage;
        await CreateIsolatedHostAsync(configureServices: s =>
        {
            s.AddSingleton(_state);
            s.AddSingleton<ITaskStorage>(sharedStorage);
        });

        // Lost-update guard: revival must NOT rewrite the definition/NextRunUtc in storage.
        await Task.Delay(1500); // recovery has certainly processed the row by now
        var afterRevival = (await Storage.GetAll()).First(t => t.Id == taskId);
        afterRevival.NextRunUtc.ShouldBe(anchor);

        // Occurrence-skip guard (P0): the parked occurrence at 'anchor' must execute AT anchor,
        // not at the following cron slot (anchor + 10s with the old recalculation).
        var afterRun = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).First(t => t.Id == taskId),
            t => (t.CurrentRunCount ?? 0) >= runsAtAnchor + 1,
            timeoutMs: 25000);

        afterRun.LastExecutionUtc.ShouldNotBeNull();
        afterRun.LastExecutionUtc!.Value.ShouldBeLessThan(anchor.AddSeconds(8));
    }

    [Fact]
    public async Task Should_not_double_execute_delayed_task_when_redispatched_immediately_with_same_task_key()
    {
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        // Delayed task parked in the scheduler...
        var delayedId = await Dispatcher.Dispatch(new ResilienceCounterTask(500),
            TimeSpan.FromSeconds(4), taskKey: "resilience-rekey");
        await WaitForTaskStatusAsync(delayedId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);

        // ...re-dispatched immediately with the same taskKey: the parked registration must be
        // invalidated (TryUnschedule), or the stale occurrence would fire again at +4s.
        var immediateId = await Dispatcher.Dispatch(new ResilienceCounterTask(500), taskKey: "resilience-rekey");
        immediateId.ShouldBe(delayedId);

        await WaitForTaskStatusAsync(delayedId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // Wait past the original delayed execution time before asserting single execution.
        await Task.Delay(5500);
        _state.ExecutedIndexes.Count(i => i == 500).ShouldBe(1);
    }

    [Fact]
    public async Task Should_reexecute_task_that_was_in_progress_at_crash()
    {
        // At-least-once contract: a task interrupted mid-execution by a crash (status InProgress)
        // is re-dispatched at startup and executed again from scratch.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        await Storage.Persist(CreateSeededTask(
            new ResilienceCounterTask(4242), QueuedTaskStatus.InProgress, DateTimeOffset.UtcNow.AddMinutes(-5)));

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).Single(),
            t => t.Status == QueuedTaskStatus.Completed,
            timeoutMs: 15000);

        _state.ExecutedIndexes.Count(i => i == 4242).ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_one_shot_exactly_once_across_repeated_restarts()
    {
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var taskId = await Dispatcher.Dispatch(new ResilienceCounterTask(777), TimeSpan.FromSeconds(15));
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 5000);

        var sharedStorage = Storage;

        // Three restarts while the task is still parked: at each boot the recovery must
        // re-schedule it WITHOUT multiplying it.
        for (var i = 0; i < 3; i++)
        {
            await CreateIsolatedHostAsync(configureServices: s =>
            {
                s.AddSingleton(_state);
                s.AddSingleton<ITaskStorage>(sharedStorage);
            });
            await Task.Delay(300); // let the recovery pass run
        }

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 30000);
        await Task.Delay(500);
        _state.ExecutedIndexes.Count(i => i == 777).ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_last_recurring_occurrence_before_RunUntil_on_revival()
    {
        // A recurring task between runs, whose NEXT (and last) occurrence falls just before RunUntil.
        // Before the P0 fix the revival recalculated past NextRunUtc, landing beyond RunUntil and
        // marking the task Failed (the last occurrence was lost). It must execute instead.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var lastOccurrence = DateTimeOffset.UtcNow.AddSeconds(2);
        var recurring = new RecurringTask
        {
            SecondInterval = new SecondInterval(2),
            RunUntil       = lastOccurrence.AddSeconds(1) // next-after-last would exceed this
        };

        var seeded = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed, // between runs
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = lastOccurrence,
            RunUntil        = recurring.RunUntil,
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await Storage.Persist(seeded);

        await Host!.StartAsync();

        // The last occurrence must execute...
        await TaskWaitHelper.WaitForConditionAsync(() => _state.ExecutedIndexes.Contains(-1), timeoutMs: 12000);

        // ...and the task must NOT be marked Failed (the next run legitimately exceeds RunUntil).
        await Task.Delay(1000);
        var task = (await Storage.GetAll()).Single();
        task.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        _state.ExecutedIndexes.Count(i => i == -1).ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_pending_occurrence_in_recent_past_on_recovery()
    {
        // L16: a recurring occurrence whose scheduled time slipped just into the past (a short downtime
        // across it) must be EXECUTED on recovery, not skipped. Here the next occurrence is 28 s away,
        // so before the grace-window fix nothing runs in the test window.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var pendingOccurrence = DateTimeOffset.UtcNow.AddSeconds(-2); // just missed (within the 30 s grace)
        var recurring         = new RecurringTask { SecondInterval = new SecondInterval(30) };

        await Storage.Persist(new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed, // between runs
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = pendingOccurrence,
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        // With the grace window the missed occurrence runs (almost) immediately; without it, the next
        // run is ~28 s out and nothing executes in this window.
        await TaskWaitHelper.WaitForConditionAsync(() => _state.ExecutedIndexes.Contains(-1), timeoutMs: 6000);
        _state.ExecutedIndexes.Count(i => i == -1).ShouldBeGreaterThanOrEqualTo(1,
            "the pending occurrence in the recent past must be executed on recovery, not skipped");
    }

    [Fact]
    public async Task Should_not_reapply_initial_delay_on_recovery()
    {
        // L25-firstrun: a recurring task whose first occurrence never ran (CurrentRunCount 0) with a
        // stale NextRunUtc must NOT re-apply InitialDelay on recovery (which would shift the whole grid
        // forward by the delay at every restart). With a 30 s InitialDelay, the buggy recompute schedules
        // ~27 s out; the fix keeps it on the 1 s interval grid (runs almost immediately).
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var staleFirstOccurrence = DateTimeOffset.UtcNow.AddSeconds(-3); // never ran, slipped past the 1 s grace
        var recurring            = new RecurringTask
        {
            InitialDelay   = TimeSpan.FromSeconds(30),
            SecondInterval = new SecondInterval(1)
        };

        await Storage.Persist(new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.WaitingQueue, // never executed
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = staleFirstOccurrence,
            CurrentRunCount = 0,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        // The fix keeps the next run on the 1 s grid (runs within a couple of seconds); the bug shifts it
        // ~27 s out (InitialDelay re-applied), so nothing executes in this 5 s window.
        await TaskWaitHelper.WaitForConditionAsync(() => _state.ExecutedIndexes.Contains(-1), timeoutMs: 5000);
        _state.ExecutedIndexes.Count(i => i == -1).ShouldBeGreaterThanOrEqualTo(1,
            "recovery must not re-apply InitialDelay and push the next run a whole delay into the future");
    }

    [Fact]
    public async Task Should_resume_recurring_task_after_restart_without_reregistration()
    {
        // Host 1: dynamically created recurring task (no taskKey re-registration at startup).
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var taskId = await Dispatcher.Dispatch(new ResilienceRecurringTask(),
            r => r.RunDelayed(TimeSpan.FromMilliseconds(300)).Then().UseCron("*/5 * * * * *"));

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1, timeoutMs: 10000);

        var sharedStorage = Storage;

        // Host 2 (restart) with the same storage and NO re-dispatch: between runs the task sits
        // in storage as Completed with a future NextRunUtc. Before the fix it was never revived.
        await CreateIsolatedHostAsync(configureServices: s =>
        {
            s.AddSingleton(_state);
            s.AddSingleton<ITaskStorage>(sharedStorage);
        });

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 2, timeoutMs: 20000);
    }



    [Fact]
    public async Task Should_register_delivery_until_execution_ends()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var registry = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();
        var taskId   = await Dispatcher.Dispatch(new ResilienceCounterTask(1));

        // Written to the channel, not yet executed: the delivery is in flight
        registry.IsDelivering(taskId).ShouldBeTrue();

        await Host.StartAsync();
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // The delivery End is the LAST act of DoWork: poll briefly for the release
        await TaskWaitHelper.WaitForConditionAsync(() => !registry.IsDelivering(taskId), timeoutMs: 5000);
    }

    [Fact]
    public async Task Should_reject_recovery_redelivery_while_live_copy_is_executing()
    {
        // The write-boundary defense: a recovery-style re-dispatch of a task whose live copy is
        // currently executing must be rejected at the channel write, deterministically, with no
        // dependence on cutoff timestamps (the original recovery-race produced a duplicate
        // delivery exactly in this state).
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.Services.AddSingleton(_state);
        });

        var taskId = await Dispatcher.Dispatch(new ResilienceBlockingTask());
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Simulate the startup recovery re-delivering the same persisted row (stale page read)
        var dispatcherInternal = Host!.Services.GetRequiredService<ITaskDispatcherInternal>();
        await dispatcherInternal.ExecuteDispatch(new ResilienceBlockingTask(),
            executionTime: null, recurring: null, currentRun: null,
            ct: CancellationToken.None, existingTaskId: taskId, isRecovery: true);

        // The redelivery was rejected at the write: no second handler entered
        (await _state.BlockingEntered.WaitAsync(TimeSpan.FromMilliseconds(500))).ShouldBeFalse();

        _state.BlockingGate.Release(10);
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 10000);
        _state.BlockingCompleted.ShouldBe(1);
    }

    [Fact]
    public async Task Should_not_register_delivery_when_live_enqueue_fails_on_full_queue()
    {
        // A dispatch refused by a full ThrowException queue must leave NO delivery registration:
        // the persisted row (WaitingQueue) stays free for the startup recovery to rescue.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.AddQueue("blocked", q => q.SetChannelCapacity(1)
                                            .SetMaxDegreeOfParallelism(1)
                                            .SetFullBehavior(QueueFullBehavior.ThrowException));
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var registry = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();

        // No consumers (host not started): the first dispatch fills the channel
        var firstId = await Dispatcher.Dispatch(new ResilienceBlockingTask());

        var ex = await Should.ThrowAsync<QueueFullException>(
            () => Dispatcher.Dispatch(new ResilienceBlockingTask()));

        registry.IsDelivering(firstId).ShouldBeTrue();    // in channel: delivery in flight
        registry.IsDelivering(ex.TaskId).ShouldBeFalse(); // refused write: no registration left
    }

    [Fact]
    public async Task Should_release_registration_when_cancelled_task_is_dropped_at_dequeue()
    {
        // Cancel() of a task still sitting in the channel: the consumer drops it at dequeue
        // (blacklist) and DoWork's outer finally must release the delivery registration —
        // cancellations never leak registrations.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var registry = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();
        var taskId   = await Dispatcher.Dispatch(new ResilienceCounterTask(42));
        registry.IsDelivering(taskId).ShouldBeTrue();

        await Dispatcher.Cancel(taskId);
        await Host.StartAsync(); // the consumer dequeues, sees the blacklist, drops the task

        await TaskWaitHelper.WaitForConditionAsync(() => !registry.IsDelivering(taskId), timeoutMs: 5000);
        _state.ExecutedIndexes.ShouldNotContain(42);
        (await Storage.GetAll()).Single(t => t.Id == taskId).Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    // ---- L11 — scheduler boundary must respect the recoverable predicate -----------------------

    /// <summary>
    /// Builds the lazy recurring executor a stale scheduler slot would carry for an already-persisted
    /// row (id), exactly as the recovery/scheduler path does.
    /// </summary>
    private static TaskHandlerExecutor StaleRecurringExecutor(Guid id, RecurringTask recurring) =>
        new(new ResilienceRecurringTask(),
            Handler: null,
            HandlerTypeName: typeof(ResilienceRecurringTaskHandler).AssemblyQualifiedName,
            ExecutionTime: DateTimeOffset.UtcNow.AddMilliseconds(-50),
            RecurringTask: recurring,
            HandlerCallback: null,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: id,
            QueueName: null,
            TaskKey: null,
            AuditLevel.Full);

    [Fact]
    public async Task Should_not_resurrect_terminally_finished_row_when_scheduler_slot_fires()
    {
        // L11: a SCHEDULED/RECURRING task re-enters via TryEnqueueImmediate -> TryQueue, which today
        // does an UNCONDITIONAL SetQueued. If the row terminally finished after the recovery page-read,
        // a stale scheduler slot resurrects it (SetQueued over Completed => a second execution).
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.Services.AddSingleton(_state);
        });

        // Seed a recurring row that has terminally finished: MaxRuns exhausted, no NextRunUtc.
        // QueuedTask.IsRecoverable is false for it, so it must never be re-queued.
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(2), MaxRuns = 1 };
        var terminalId = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = terminalId,
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = typeof(ResilienceRecurringTaskHandler).AssemblyQualifiedName!,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = null, // series ended
            MaxRuns         = 1,
            CurrentRunCount = 2,    // > MaxRuns => exhausted
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        // Force the scheduler to dispatch a stale, already-due slot for that id.
        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        scheduler.Schedule(StaleRecurringExecutor(terminalId, recurring), DateTimeOffset.UtcNow.AddMilliseconds(-50));

        // Margin: a resurrection (the pre-fix bug) would dispatch + execute within a scheduler tick.
        await Task.Delay(1500);

        _state.ExecutedIndexes.ShouldNotContain(-1);
        var row = (await Storage.GetAll()).Single(t => t.Id == terminalId);
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.CurrentRunCount.ShouldBe(2);
    }

    [Fact]
    public async Task Should_not_double_execute_recurring_at_maxruns_when_recovery_schedules_concurrently()
    {
        // L11 end-to-end at the MaxRuns boundary: a recurring task runs its last allowed occurrence
        // (real recovery + scheduler), the series terminates, and a concurrently-registered recovery
        // slot for the same id fires afterwards. The scheduler boundary must not resurrect it.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.AddMemoryStorage();
            b.Services.AddSingleton(_state);
        },
        startHost: false);

        var recurring = new RecurringTask { SecondInterval = new SecondInterval(2), MaxRuns = 1 };
        var id = Guid.NewGuid();
        await Storage.Persist(new QueuedTask
        {
            Id              = id,
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = typeof(ResilienceRecurringTaskHandler).AssemblyQualifiedName!,
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = DateTimeOffset.UtcNow.AddMilliseconds(700), // last occurrence, recoverable
            MaxRuns         = 1,
            CurrentRunCount = 0,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        await Host!.StartAsync();

        // Recovery runs the final occurrence exactly once; the series then exhausts (NextRunUtc cleared).
        await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.GetAll()).Single(t => t.Id == id),
            t => (t.CurrentRunCount ?? 0) >= 1 && t.NextRunUtc == null,
            timeoutMs: 15000);
        _state.ExecutedIndexes.Count(i => i == -1).ShouldBe(1);

        // A concurrent recovery registration (read while the last run was still in flight) now fires
        // its stale slot for the same — now exhausted — id.
        var scheduler = Host.Services.GetRequiredService<IScheduler>();
        scheduler.Schedule(StaleRecurringExecutor(id, recurring), DateTimeOffset.UtcNow.AddMilliseconds(-50));

        await Task.Delay(1500); // margin for the (buggy) resurrection to land

        _state.ExecutedIndexes.Count(i => i == -1).ShouldBe(1);
        (await Storage.GetAll()).Single(t => t.Id == id).CurrentRunCount.ShouldBe(1);
    }
}