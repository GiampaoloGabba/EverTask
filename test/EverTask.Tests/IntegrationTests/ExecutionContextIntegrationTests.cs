using System.Collections.Concurrent;
using EverTask.Configuration;
using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// What a handler reads about the delivery it is running, produced by a real host: a real dispatch or a real
/// startup recovery, the real worker, the real retry policy, the real rate-limit gate.
/// </summary>
/// <remarks>
/// The context is only worth what it says on the paths a handler cannot reconstruct by itself — the slot a
/// deferred task was actually scheduled for, the run number after a restart, the attempt inside a retry — so
/// those are driven end to end rather than asserted on a hand-built executor.
/// </remarks>
public class ExecutionContextIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly ExecutionContextRecorder _recorder = new();

    private Task<IHost> CreateContextHostAsync(Action<EverTaskServiceConfiguration>? configureEverTask = null) =>
        CreateIsolatedHostAsync(
            configureEverTask: configureEverTask,
            configureServices: services =>
            {
                services.AddSingleton(_recorder);
                services.AddScoped<AmbientContextReader>();
            });

    /// <summary>
    /// A host that does NOT start, so a row can be seeded before startup recovery reads it. The scheduler is
    /// the fast one: a recovered occurrence lands in the past and must be delivered without a second of wait.
    /// </summary>
    private Task<IHost> CreateSeedableHostAsync(Action<EverTaskServiceConfiguration>? configureEverTask = null) =>
        CreateIsolatedHostWithBuilderAsync(builder =>
            {
                builder.AddMemoryStorage();
                builder.Services.AddSingleton(_recorder);
                builder.Services.AddScoped<AmbientContextReader>();
                builder.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new PeriodicTimerScheduler(
                    sp.GetRequiredService<IWorkerQueueManager>(),
                    sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                    TimeSpan.FromMilliseconds(50))));
            },
            startHost: false,
            configureEverTask: configureEverTask);

    private Task WaitForPhaseAsync(string phase, int count = 1) =>
        TaskWaitHelper.WaitForConditionAsync(
            () => _recorder.For(phase).Length >= count,
            TestEnvironment.GetTimeout(8000, 30000));

    [Fact]
    public async Task An_immediate_task_reports_its_identity_in_Handle_and_in_every_callback()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new ContextProbeTask("immediate"), taskKey: "ctx-immediate");

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("OnCompleted");

        var phases = new[] { "OnStarted", "Handle", "OnCompleted" };

        foreach (var phase in phases)
        {
            var snapshot = _recorder.Single(phase).ShouldNotBeNull($"the context must be readable from {phase}");

            snapshot.TaskId.ShouldBe(taskId);
            snapshot.TaskKey.ShouldBe("ctx-immediate");
            snapshot.ScheduleId.ShouldBeNull();
            snapshot.ScheduledAtUtc.ShouldBeNull("a task dispatched to run now stands for no slot");
            snapshot.ScheduledAtLocal.ShouldBeNull();
            snapshot.TimeZoneId.ShouldBeNull();
            snapshot.Attempt.ShouldBe(1);
            snapshot.RunNumber.ShouldBe(1);
            snapshot.ScheduleVersion.ShouldBe(0);
            snapshot.IsRecurring.ShouldBeFalse();
            snapshot.IsOccurrence.ShouldBeFalse();
            snapshot.Misfire.ShouldBeNull("a task with no slot can never be late");
            snapshot.StartedAtUtc.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        // The same instance across the whole delivery: the callbacks are not given a fresh context each.
        _recorder.Snapshots.Select(s => s.StartedAtUtc).Distinct().Count()
                 .ShouldBe(1, "one delivery has one start time");
    }

    [Fact]
    public async Task A_delayed_task_reports_the_slot_it_was_scheduled_for_in_Handle_and_in_every_callback()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new ContextProbeTask("delayed"), TimeSpan.FromMilliseconds(400));

        var row = await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("OnCompleted");

        foreach (var phase in new[] { "OnStarted", "Handle", "OnCompleted" })
        {
            var snapshot = _recorder.Single(phase).ShouldNotBeNull($"the context must be readable from {phase}");

            snapshot.TaskId.ShouldBe(taskId);
            snapshot.ScheduledAtUtc.ShouldBe(row.ScheduledExecutionUtc,
                "the slot the row was persisted with is the slot the handler must see");
            snapshot.StartedAtUtc.ShouldBeGreaterThanOrEqualTo(row.ScheduledExecutionUtc!.Value);
            snapshot.ScheduleId.ShouldBeNull();
            snapshot.IsRecurring.ShouldBeFalse();
            snapshot.IsOccurrence.ShouldBeFalse();
            snapshot.RunNumber.ShouldBe(1, "a one-shot task is always its own first run");
            snapshot.Attempt.ShouldBe(1);
            snapshot.Misfire.ShouldBeNull("400 ms of scheduling is well inside the 5 s default threshold");
        }
    }

    [Fact]
    public async Task A_recovered_task_that_slept_through_its_slot_is_reported_as_late()
    {
        await CreateSeedableHostAsync();

        var slot   = DateTimeOffset.UtcNow.AddMinutes(-10);
        var taskId = await SeedDelayedRowAsync(slot);

        await Host!.StartAsync();

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("Handle");

        var snapshot = _recorder.Single("Handle").ShouldNotBeNull();

        snapshot.ScheduledAtUtc.ShouldBe(slot);
        snapshot.Misfire.ShouldNotBeNull("ten minutes past the slot is far beyond the 5 s default threshold");
        snapshot.Misfire.Kind.ShouldBe(MisfireKind.Late);
        snapshot.Misfire.Lateness.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(10));
        snapshot.Misfire.MissedCount.ShouldBe(0, "the delivery is late for its own slot, it skipped none");
    }

    [Fact]
    public async Task The_same_late_delivery_reports_no_misfire_under_a_wider_threshold()
    {
        await CreateSeedableHostAsync(cfg => cfg.SetMisfireThreshold(TimeSpan.FromHours(1)));

        var slot   = DateTimeOffset.UtcNow.AddMinutes(-10);
        var taskId = await SeedDelayedRowAsync(slot);

        await Host!.StartAsync();

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("Handle");

        var snapshot = _recorder.Single("Handle").ShouldNotBeNull();

        snapshot.ScheduledAtUtc.ShouldBe(slot, "the slot is a fact, only the verdict on it is configurable");
        snapshot.Misfire.ShouldBeNull("ten minutes is within the configured hour of tolerance");
    }

    [Fact]
    public async Task A_recurring_series_numbers_its_runs_from_one_in_Handle_and_in_every_callback()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new ContextRecurringTask(),
            recurring => recurring.Schedule().Every(1).Seconds().MaxRuns(3));

        await WaitForPhaseAsync("OnCompleted", 3);

        var runs = _recorder.For("Handle").OrderBy(s => s.RunNumber).ToArray();

        runs.Select(s => s.RunNumber).Take(3).ShouldBe([1, 2, 3]);

        foreach (var run in runs)
        {
            run.TaskId.ShouldBe(taskId);
            run.IsRecurring.ShouldBeTrue();
            run.IsOccurrence.ShouldBeFalse("an inline recurring run IS the schedule row, not an occurrence of it");
            run.ScheduleId.ShouldBeNull();
            run.ScheduledAtUtc.ShouldNotBeNull("every occurrence stands for a slot");
            run.Attempt.ShouldBe(1);

            // The callbacks of a run belong to the SAME delivery as its Handle, and say so: same context
            // instance, therefore the same start time, the same slot and the same run number.
            var delivery = _recorder.Snapshots.Where(s => s.RunNumber == run.RunNumber).ToArray();

            // OnStarted and OnCompleted read the context of the occurrence they bracket.
            delivery.Select(s => s.Phase).OrderBy(p => p, StringComparer.Ordinal).ToArray()
                    .ShouldBe(["Handle", "OnCompleted", "OnStarted"]);
            delivery.Select(s => s.StartedAtUtc).Distinct().Count().ShouldBe(1, "one delivery has one start time");
            delivery.Select(s => s.ScheduledAtUtc).Distinct().Count().ShouldBe(1, "and stands for one slot");
            delivery.ShouldAllBe(s => s.IsRecurring && !s.IsOccurrence && s.ScheduleId == null);
        }

        runs[1].ScheduledAtUtc!.Value.ShouldBeGreaterThan(runs[0].ScheduledAtUtc!.Value,
            "consecutive runs stand for consecutive slots");
    }

    [Fact]
    public async Task A_recovered_series_keeps_counting_its_runs_from_the_stored_counter()
    {
        await CreateSeedableHostAsync();

        var slot     = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scheduleId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = scheduleId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            CreatedAtUtc    = slot.AddHours(-1),
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(new RecurringTask { MinuteInterval = new MinuteInterval(30) }),
            RecurringInfo   = "every 30 minutes",
            NextRunUtc      = slot,
            CurrentRunCount = 4
        });

        await Host!.StartAsync();

        await WaitForPhaseAsync("Handle");

        var snapshot = _recorder.For("Handle")[0];

        snapshot.TaskId.ShouldBe(scheduleId);
        snapshot.RunNumber.ShouldBe(5,
            "the run number is durable: four runs happened before the restart, this one is the fifth");
        snapshot.IsRecurring.ShouldBeTrue();
        snapshot.ScheduledAtUtc.ShouldBe(slot, "the slot that slipped through the downtime is still its own");
        snapshot.Misfire!.Kind.ShouldBe(MisfireKind.Late);

        // The counter only moves once the run is over, which is exactly why the context cannot read it: it
        // has to land on 5 AFTER the delivery that reported itself as run 5.
        var stored = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == scheduleId)).SingleOrDefault(),
            row => row?.CurrentRunCount >= 5,
            TestEnvironment.GetTimeout(8000, 30000));

        stored!.CurrentRunCount.ShouldBe(5, "the context reported the run the counter was about to reach");
    }

    [Fact]
    public async Task A_durable_occurrence_reports_its_schedule_its_slot_and_the_run_of_the_series_it_is()
    {
        await CreateSeedableHostAsync();

        const int scheduleVersion = 7;
        const int runNumber       = 12;

        var slot       = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = scheduleId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            CreatedAtUtc    = slot.AddHours(-1),
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(new RecurringTask { MinuteInterval = new MinuteInterval(5) }),
            RecurringInfo   = "every 5 minutes",
            NextRunUtc      = slot,
            CurrentRunCount = runNumber - 1,
            ScheduleVersion = scheduleVersion
        });

        var child = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            Type                  = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler               = "seeded-by-test",
            Status                = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = slot,
            ScheduledExecutionUtc = slot,
            ParentTaskId          = scheduleId,
            // What the ROW says about itself: an occurrence is a one-shot, so its own run counter is zero
            // and only this can answer which run of the series the handler is executing.
            RuntimeInfo = EverTaskJson.Serialize(
                new OccurrenceRuntimeInfo { SlotUtc = slot, RunNumber = runNumber })
        };

        // The last slot of the series: the same call that creates the occurrence ends the schedule, so the
        // only row left for the recovery to pick up — and the only handler that runs — is the occurrence.
        (await Storage.MaterializeOccurrence(scheduleId, scheduleVersion, slot, child, newCursorUtc: null,
             AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);

        await Host!.StartAsync();

        await WaitForTaskStatusAsync(child.Id, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("OnCompleted");

        foreach (var phase in new[] { "OnStarted", "Handle", "OnCompleted" })
        {
            var snapshot = _recorder.Single(phase).ShouldNotBeNull($"the context must be readable from {phase}");

            snapshot.TaskId.ShouldBe(child.Id, "the row in execution is the occurrence, not the schedule");
            snapshot.ScheduleId.ShouldBe(scheduleId, "and the schedule it belongs to is the parent");
            snapshot.IsOccurrence.ShouldBeTrue();
            snapshot.IsRecurring.ShouldBeTrue(
                "an occurrence carries no definition of its own — it belongs to the series through its parent");
            snapshot.ScheduleVersion.ShouldBe(scheduleVersion);
            snapshot.ScheduledAtUtc.ShouldBe(slot, "the occurrence stands for the slot its row was created for");
            snapshot.RunNumber.ShouldBe(runNumber,
                "the run of the series comes from the occurrence's own metadata: its row is a one-shot whose " +
                "counter is zero, so deriving it there would report every occurrence as run 1");
            snapshot.TaskKey.ShouldBeNull("a schedule's key never travels to its occurrences");
            snapshot.Misfire.ShouldNotBeNull().Kind.ShouldBe(MisfireKind.Late);
        }
    }

    [Fact]
    public async Task A_live_occurrence_reads_its_slot_and_its_run_from_its_row_when_the_executor_carries_neither()
    {
        // Started FIRST: startup recovery is over before either row exists, so the only thing that can deliver
        // the occurrence is the live hand-off below — and what the handler reads is what the live path
        // produced, never what RecoveredTaskFactory would have stamped on a recovered executor.
        await CreateSeedableHostAsync();
        await Host!.StartAsync();

        const int scheduleVersion = 5;
        const int runNumber       = 8;

        var now        = Clock.GetUtcNow();
        var slot       = now.AddMinutes(-4);
        var scheduleId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id              = scheduleId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            CreatedAtUtc    = now,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(new RecurringTask { MinuteInterval = new MinuteInterval(5) }),
            RecurringInfo   = "every 5 minutes",
            NextRunUtc      = slot,
            CurrentRunCount = runNumber - 1,
            ScheduleVersion = scheduleVersion
        });

        var child = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            Type                  = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler               = "seeded-by-test",
            Status                = QueuedTaskStatus.WaitingQueue,
            CreatedAtUtc          = now,
            ScheduledExecutionUtc = slot,
            ParentTaskId          = scheduleId,
            RuntimeInfo           = EverTaskJson.Serialize(
                new OccurrenceRuntimeInfo { SlotUtc = slot, RunNumber = runNumber })
        };

        // The last slot of the series: the same call that creates the occurrence ends the schedule, so the
        // occurrence is the only row with a handler left to run.
        (await Storage.MaterializeOccurrence(scheduleId, scheduleVersion, slot, child, newCursorUtc: null,
             AuditLevel.Full))
            .ShouldBe(OccurrenceMaterializationOutcome.Created);

        // The delivery of an occurrence that was just materialized: built through the same wrapper the
        // dispatcher uses, handed to the real scheduler, and carrying the row's occurrence metadata WITHOUT
        // the slot and run number pre-stamped on it. The overdue slot is fired now, so the moment of the
        // delivery and the slot it stands for are four minutes apart — which is the whole point: the row is
        // the only thing that still knows the difference.
        var executor = await global::EverTask.Dispatcher.Dispatcher
                             .CreateCachedWrapper(typeof(ContextRecurringTask))
                             .Handle(new ContextRecurringTask(), now, recurring: null, Host.Services,
                                 AuditLevel.Full, existingTaskId: child.Id, taskKey: null, useLazyExecutor: true,
                                 new DispatchRowMetadata(scheduleId, child.RuntimeInfo, scheduleVersion,
                                     QueueNames.Recurring));

        executor.NominalSlotUtc.ShouldBeNull("the executor under test is the one that was never stamped");
        executor.RunNumber.ShouldBeNull();

        Host.Services.GetRequiredService<IScheduler>().Schedule(executor);

        await WaitForTaskStatusAsync(child.Id, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("Handle");

        var snapshot = _recorder.Single("Handle").ShouldNotBeNull();

        snapshot.TaskId.ShouldBe(child.Id);
        snapshot.ScheduleId.ShouldBe(scheduleId);
        snapshot.IsOccurrence.ShouldBeTrue();
        snapshot.ScheduleVersion.ShouldBe(scheduleVersion);
        snapshot.ScheduledAtUtc.ShouldBe(slot,
            "an occurrence stands for the slot ITS ROW states, not for the moment the delivery was fired at");
        snapshot.RunNumber.ShouldBe(runNumber,
            "the run of the series is the other fact only the row can state: a child is a one-shot, so " +
            "deriving it from its own counter would report every occurrence of every series as run 1");
        snapshot.Misfire.ShouldNotBeNull().Kind.ShouldBe(MisfireKind.Late);
        snapshot.Misfire.Lateness.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMinutes(4),
            "a slot read from ExecutionTime instead would have made a four-minute-late catch-up look punctual");
    }

    [Fact]
    public async Task The_attempt_number_follows_the_retries_and_the_OnRetry_callback_sees_the_one_about_to_start()
    {
        await CreateContextHostAsync();
        _recorder.FailuresToInject = 2;

        var taskId = await Dispatcher.Dispatch(new ContextRetryTask());

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("Handle", 3);

        _recorder.For("Handle").Select(s => s.Attempt).ShouldBe([1, 2, 3]);
        _recorder.For("OnRetry").Select(s => s.Attempt)
                 .ShouldBe([2, 3], "OnRetry runs immediately before the attempt it announces");
    }

    [Fact]
    public async Task The_attempt_number_in_OnError_is_the_last_attempt_that_ran()
    {
        await CreateContextHostAsync();
        _recorder.FailuresToInject = int.MaxValue;

        var taskId = await Dispatcher.Dispatch(new ContextRetryTask());

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);
        await WaitForPhaseAsync("OnError");

        // LinearRetryPolicy(3): the initial attempt plus three retries.
        _recorder.For("Handle").Select(s => s.Attempt).ShouldBe([1, 2, 3, 4]);
        _recorder.Single("OnError").ShouldNotBeNull().Attempt
                 .ShouldBe(4, "OnError reports the attempt that failed, not a fifth that never ran");
    }

    [Fact]
    public async Task The_attempt_in_OnError_ignores_a_retry_that_was_cancelled_before_it_started()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new ContextCancelledRetryTask());

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Cancelled);
        await WaitForPhaseAsync("OnError");

        _recorder.For("Handle").Select(s => s.Attempt)
                 .ShouldBe([1], "the second attempt was cancelled before the handler was entered again");
        _recorder.Single("OnRetry").ShouldNotBeNull().Attempt
                 .ShouldBe(2, "OnRetry announces the attempt that is about to start");
        _recorder.Single("OnError").ShouldNotBeNull().Attempt
                 .ShouldBe(1, "OnError reports the last attempt that RAN, not the one only announced");
    }

    [Fact]
    public async Task The_attempt_in_OnError_ignores_a_retry_the_rate_limit_gate_turned_back()
    {
        await CreateContextHostAsync();

        // Attempt 1 takes the single permit of the 30 s window and throws; the throttled retry finds no
        // budget and the Discard policy rejects it outright, so the handler is never entered a second time.
        // The rejection is terminal for a one-shot: Failed plus OnError, the exact pair that would report a
        // second attempt if the number moved before the gate instead of after it.
        var taskId = await Dispatcher.Dispatch(new ContextThrottledRetryTask());

        var row = await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed);
        await WaitForPhaseAsync("OnError");

        row.Exception.ShouldNotBeNull();
        row.Exception.ShouldContain("RateLimitRejectedException");

        _recorder.For("Handle").Select(s => s.Attempt)
                 .ShouldBe([1], "the gate turned the retry back before the handler could run again");
        _recorder.Single("OnRetry").ShouldNotBeNull().Attempt
                 .ShouldBe(2, "OnRetry still announces the attempt that was about to start");
        _recorder.Single("OnError").ShouldNotBeNull().Attempt
                 .ShouldBe(1, "OnError reports the last attempt that RAN, not one the gate never admitted");
    }

    [Fact]
    public async Task A_terminal_rate_limit_rejection_still_gives_OnError_its_context_and_logger()
    {
        await CreateContextHostAsync();

        var callbackFailures = new List<string>();
        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            if (data.Message.Contains("callback override", StringComparison.Ordinal))
                lock (callbackFailures) callbackFailures.Add(data.Message);

            return Task.CompletedTask;
        };

        // The warm-up takes the single 5 s permit, so the next free slot is 5 s out — past the 1 s
        // reservation horizon. The second dispatch is rejected terminally: the only lifecycle path that
        // reaches OnError without ever entering the execution core.
        var warmupId = await Dispatcher.Dispatch(new RateLimitRejectedContextTask(0));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed);

        var rejectedId = await Dispatcher.Dispatch(new RateLimitRejectedContextTask(1));
        await WaitForTaskStatusAsync(rejectedId, QueuedTaskStatus.Failed);
        await WaitForPhaseAsync("OnError");

        // Nothing is recorded unless BOTH injections happened: the handler reads Logger and Context before
        // it records, and the worker swallows whatever either of them throws.
        var snapshot = _recorder.Single("OnError").ShouldNotBeNull();

        snapshot.TaskId.ShouldBe(rejectedId);
        snapshot.ScheduledAtUtc.ShouldBeNull("an immediate dispatch stands for no slot, rejected or not");
        snapshot.Attempt.ShouldBe(1);
        snapshot.RunNumber.ShouldBe(1);
        snapshot.IsRecurring.ShouldBeFalse();
        snapshot.Misfire.ShouldBeNull();

        _recorder.AmbientReads.ShouldContain(("OnError", rejectedId),
            "a service that is not the handler reads the same context through the ambient accessor");

        callbackFailures.ShouldBeEmpty("a callback that threw would only surface as a generic failure event");
        _recorder.For("Handle-1").ShouldBeEmpty("a rejected task never executes");
    }

    [Fact]
    public async Task Re_registering_a_finished_series_under_the_same_taskKey_keeps_counting_its_runs()
    {
        await CreateContextHostAsync();

        const string taskKey    = "ctx-terminal-series";
        var          scheduleId = Guid.NewGuid();

        // A series that ran five times and then ended: terminal status, cursor cleared, counter kept. The
        // counter is what storage keeps counting from the moment the same key is registered again.
        await Storage.Persist(new QueuedTask
        {
            Id              = scheduleId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddHours(-1),
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(new RecurringTask { SecondInterval = new SecondInterval(1) }),
            RecurringInfo   = "every 1 second(s)",
            TaskKey         = taskKey,
            NextRunUtc      = null,
            CurrentRunCount = 5
        });

        var taskId = await Dispatcher.Dispatch(new ContextRecurringTask(),
            recurring => recurring.Schedule().Every(1).Seconds(), taskKey: taskKey);

        taskId.ShouldBe(scheduleId, "a recurring row is updated under its taskKey, never recreated");

        await WaitForPhaseAsync("Handle", 2);

        _recorder.For("Handle").Select(s => s.RunNumber).Take(2)
                 .ShouldBe([6, 7], "five runs happened before the re-registration: the counter resumes, never restarts");

        var stored = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == scheduleId)).SingleOrDefault(),
            row => row?.CurrentRunCount >= 6,
            TestEnvironment.GetTimeout(8000, 30000));

        (stored!.CurrentRunCount ?? 0).ShouldBeGreaterThanOrEqualTo(6,
            "the run the handler reported as the sixth is the one storage recorded");
    }

    [Fact]
    public async Task Re_registering_a_versioned_schedule_under_the_same_taskKey_keeps_reporting_its_version()
    {
        await CreateContextHostAsync();

        const string taskKey    = "ctx-versioned-series";
        var          scheduleId = Guid.NewGuid();
        var          definition = EverTaskJson.Serialize(new RecurringTask { SecondInterval = new SecondInterval(1) });
        var          cursor     = DateTimeOffset.UtcNow.AddSeconds(1);

        var events = new ConcurrentQueue<EverTaskEventData>();
        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        };

        await Storage.Persist(new QueuedTask
        {
            Id              = scheduleId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Queued,
            CreatedAtUtc    = DateTimeOffset.UtcNow,
            IsRecurring     = true,
            RecurringTask   = definition,
            RecurringInfo   = "every 1 second(s)",
            TaskKey         = taskKey,
            NextRunUtc      = cursor,
            CurrentRunCount = 2
        });

        // Two real schedule updates leave the row at version 2, exactly where a pair of reschedules would.
        for (var expectedVersion = 0; expectedVersion < 2; expectedVersion++)
        {
            (await Storage.UpdateSchedule(scheduleId, expectedVersion, cursor, definition, "every 1 second(s)",
                 cursor, null, null, null))
                .ShouldBeTrue("the compare-and-swap of the schedule update must win on an untouched row");
        }

        // The idempotent registration a host does at every startup: same key, same definition, and the row is
        // updated in place — UpdateTask never rewrites the version column, so the delivery still belongs to
        // version 2 and has to say so.
        var taskId = await Dispatcher.Dispatch(new ContextRecurringTask(),
            recurring => recurring.Schedule().Every(1).Seconds(), taskKey: taskKey);

        taskId.ShouldBe(scheduleId, "a recurring row is updated under its taskKey, never recreated");

        await WaitForPhaseAsync("Handle");

        var snapshot = _recorder.For("Handle")[0];

        snapshot.TaskId.ShouldBe(scheduleId);
        snapshot.ScheduleVersion.ShouldBe(2,
            "the version is a fact of the row the re-registration updated, not a fresh 0 for the dispatch");
        snapshot.RunNumber.ShouldBe(3, "two runs happened before the re-registration");
        snapshot.IsRecurring.ShouldBeTrue();

        (await Storage.Get(t => t.Id == scheduleId))[0].ScheduleVersion
            .ShouldBe(2, "and the row itself never left version 2");

        // The same value reaches the dashboard: the monitoring events of a delivery are built from the same
        // executor, so a version lost at dispatch is lost for every subscriber too.
        await TaskWaitHelper.WaitForConditionAsync(
            () => events.Any(e => e.TaskId == scheduleId),
            TestEnvironment.GetTimeout(8000, 30000));

        events.Where(e => e.TaskId == scheduleId)
              .ShouldAllBe(e => e.ScheduleVersion == 2, "every event of the delivery reports its schedule version");
    }

    [Fact]
    public async Task A_scoped_service_of_an_eager_handler_reads_the_context_from_the_ambient_accessor()
    {
        // Eager resolution: the handler AND its scoped dependency are built in the dispatcher's own scope,
        // long before this delivery starts. That is the case a scoped accessor could not serve (C3).
        await CreateContextHostAsync(cfg => cfg.DisableLazyHandlerResolution());

        var taskId = await Dispatcher.Dispatch(new AmbientContextTask("eager"));

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("eager");

        _recorder.AmbientReads.ShouldContain(("eager", taskId));
    }

    [Fact]
    public async Task A_scoped_service_of_a_lazy_handler_reads_the_context_from_the_ambient_accessor()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new AmbientContextTask("lazy"));

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("lazy");

        _recorder.AmbientReads.ShouldContain(("lazy", taskId));

        Host!.Services.GetRequiredService<ITaskExecutionContextAccessor>().Current
             .ShouldBeNull("the ambient context belongs to the delivery's flow, not to the host");
    }

    [Fact]
    public async Task A_handler_implementing_the_interface_directly_runs_unchanged_through_the_no_op_injection()
    {
        await CreateContextHostAsync();

        // RawInterfaceTaskHandler declares no SetExecutionContext, so the injector compiled for its type
        // calls the interface's own empty body. That call happens before OnStarted, inside the execution
        // core: had it thrown — or bound to nothing — the delivery would end Failed with the exception on
        // the row, and none of the phases below would have run.
        var taskId = await Dispatcher.Dispatch(new RawInterfaceTask());

        var row = await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        row.Exception.ShouldBeNull();

        await TaskWaitHelper.WaitForConditionAsync(
            () => _recorder.AmbientReads.Any(read => read.Phase == "raw-OnCompleted"),
            TestEnvironment.GetTimeout(8000, 30000));

        _recorder.AmbientReads.Select(read => read.Phase)
                 .ShouldBe(["raw-OnStarted", "raw-Handle", "raw-OnCompleted"],
                     "the whole lifecycle of a handler written against the bare interface still runs");
        _recorder.AmbientReads.ShouldAllBe(read => read.TaskId == taskId,
            "the accessor is the route to the context for a handler that has no Context property");
    }

    [Fact]
    public async Task A_handler_implementing_the_interface_directly_can_take_the_context_by_implementing_the_member()
    {
        await CreateContextHostAsync();

        var taskId = await Dispatcher.Dispatch(new RawInterfaceContextTask(), taskKey: "raw-ctx");

        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);
        await WaitForPhaseAsync("raw-context-Handle");

        // The handler throws when it is asked to record without a context, so reaching these assertions is
        // itself the proof that the injector resolved to THIS class's member — and therefore that the same
        // call, on a handler that declares none, reaches the interface's default body.
        foreach (var phase in new[] { "raw-context-OnStarted", "raw-context-Handle" })
        {
            var snapshot = _recorder.Single(phase).ShouldNotBeNull($"the context must be readable from {phase}");

            snapshot.TaskId.ShouldBe(taskId);
            snapshot.TaskKey.ShouldBe("raw-ctx");
            snapshot.Attempt.ShouldBe(1);
            snapshot.RunNumber.ShouldBe(1);
            snapshot.IsRecurring.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task A_rate_limit_deferral_moves_the_delivery_but_not_the_slot_it_stands_for()
    {
        await CreateSeedableHostAsync();
        await Host!.StartAsync();

        // Both are scheduled for the same slot on the same key, and the policy has one permit: the second
        // delivery is deferred to the limiter's reserved slot ~900 ms later.
        var slotDelay = TimeSpan.FromMilliseconds(300);
        var first     = await Dispatcher.Dispatch(new RateLimitedContextTask(1), slotDelay);
        var second    = await Dispatcher.Dispatch(new RateLimitedContextTask(2), slotDelay);

        await WaitForPhaseAsync("Handle-1");
        await WaitForPhaseAsync("Handle-2");

        var rows      = await Storage.Get(t => t.Id == first || t.Id == second);
        var snapshots = new[] { _recorder.Single("Handle-1")!, _recorder.Single("Handle-2")! };

        foreach (var snapshot in snapshots)
        {
            var row = rows.Single(r => r.Id == snapshot.TaskId);
            snapshot.ScheduledAtUtc.ShouldBe(row.ScheduledExecutionUtc,
                "the reserved slot the gate parks a task at is not the slot it was scheduled for");
        }

        // The one that ran last is the one the gate deferred.
        var deferred       = snapshots.MaxBy(s => s.StartedAtUtc)!;
        var slotOfDeferred = rows.Single(r => r.Id == deferred.TaskId).ScheduledExecutionUtc!.Value;

        (deferred.StartedAtUtc - slotOfDeferred)
            .ShouldBeGreaterThan(TimeSpan.FromMilliseconds(500),
                "the deferred delivery really did run at a moved slot — and still reported the original one");
    }

    [Fact]
    public void A_handler_reading_its_context_before_the_worker_injects_it_gets_a_clear_error()
    {
        var handler = new UninjectedContextHandler();

        var error = Should.Throw<InvalidOperationException>(() => handler.ReadContext());
        error.Message.ShouldContain(nameof(UninjectedContextHandler));
    }

    [Fact]
    public void A_negative_misfire_threshold_is_refused_instead_of_being_applied()
    {
        // A negative tolerance would report every delivery as late by construction. The docs promise it
        // throws, so the guard is part of the option's public contract.
        var configuration = new EverTaskServiceConfiguration();

        Should.Throw<ArgumentOutOfRangeException>(() => configuration.SetMisfireThreshold(TimeSpan.FromTicks(-1)))
              .ParamName.ShouldBe("threshold");

        configuration.MisfireThreshold.ShouldBe(TimeSpan.FromSeconds(5), "a refused value must not be applied");

        // Zero is the boundary the guard must let through: it reports every delivery that starts after its
        // slot, which is a legitimate — if noisy — setting.
        configuration.SetMisfireThreshold(TimeSpan.Zero).MisfireThreshold.ShouldBe(TimeSpan.Zero);
    }

    /// <summary>Seeds a delayed one-shot row whose slot is already past, ready for startup recovery.</summary>
    private async Task<Guid> SeedDelayedRowAsync(DateTimeOffset slot)
    {
        var taskId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id                    = taskId,
            Type                  = typeof(ContextProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new ContextProbeTask("recovered")),
            Handler               = "seeded-by-test",
            Status                = QueuedTaskStatus.Queued,
            CreatedAtUtc          = slot.AddMinutes(-1),
            ScheduledExecutionUtc = slot
        });

        return taskId;
    }
}

/// <summary>A handler nobody dispatches: it exists to read <c>Context</c> outside a delivery.</summary>
public record UninjectedContextTask : IEverTask;

public class UninjectedContextHandler : EverTaskHandler<UninjectedContextTask>
{
    public ITaskExecutionContext ReadContext() => Context;

    public override Task Handle(UninjectedContextTask backgroundTask, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
