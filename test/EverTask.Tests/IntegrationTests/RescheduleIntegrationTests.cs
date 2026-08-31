using System.Collections.Concurrent;
using System.Globalization;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Scheduler;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Runtime schedule management end to end, on a real host with a real storage: a running series is given a new
/// definition, a new cursor and a new version, and everything that was already pointing at the old one either
/// stops or recomputes.
/// </summary>
/// <remarks>
/// The storage instance is shared across the hosts a test builds, so "restart" means what it says: the same
/// rows, a new process. Backlogs are SEEDED as rows, because there is no honest way to produce a downtime by
/// sleeping.
/// </remarks>
[Collection("TimingSensitiveTests")]
public class RescheduleIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly MemoryTaskStorage _shared = new(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>());
    private readonly RescheduleRecorder _recorder = new();
    private readonly ScheduleFaultInjector _faults = new();

    private readonly RegistrationWatch _registrations = new();

    /// <summary>
    /// Builds the test's host and, when it is started, does not come back until its STARTUP RECOVERY has
    /// finished.
    /// </summary>
    /// <remarks>
    /// Recovery captures its cutoff when it begins, not when the host starts, and it begins on a thread pool
    /// thread: a row the test dispatches in the meantime is created BEFORE that cutoff and is recovered like
    /// any leftover of a previous process — a re-dispatch that legitimately re-registers the very schedule the
    /// test is working on. Every registration these tests arm a fault on, count, or read back would then be
    /// racing that one. Waiting for the recovery's own terminal event is what makes "the rows of this test
    /// come after recovery" a fact instead of an assumption.
    /// </remarks>
    private async Task StartHostAsync(bool startHost = true, bool faultyScheduler = false,
                                             bool watchRegistrations = false, ITaskStorage? storage = null,
                                             TimeProvider? clock = null,
                                             Action<EverTaskServiceConfiguration>? configureEverTask = null,
                                             RecordingLogger<WorkerExecutor>? workerLog = null)
    {
        var recovery = new StartupRecoveryWatch();

        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.Services.AddSingleton<IEverTaskLogger<WorkerService>>(recovery);
            b.Services.AddSingleton(storage ?? _shared);
            b.Services.AddSingleton(_recorder);

            if (workerLog != null)
                b.Services.AddSingleton<IEverTaskLogger<WorkerExecutor>>(workerLog);

            if (faultyScheduler)
            {
                // The REAL scheduler with one Schedule() made to throw: the shape of a re-park that fails
                // after the new definition is already committed. It carries the registration watch too when a
                // test needs both — a reschedule landing INSIDE a delivery whose own re-park then fails.
                b.Services.AddSingleton(_faults);
                b.Services.AddSingleton(_registrations);
                b.Services.AddSingleton<IScheduler>(sp => new ScheduleFaultingScheduler(
                    BuildRealScheduler(sp), sp.GetRequiredService<ScheduleFaultInjector>(),
                    watchRegistrations ? sp.GetRequiredService<RegistrationWatch>() : null));

                return;
            }

            if (!watchRegistrations)
                return;

            // The REAL scheduler, watched: every registration is recorded with the answer it got, and one
            // armed hook runs just before a chosen one reaches it. That is the only place a test can put a
            // reschedule INSIDE the window these findings are about — between a delivery deciding what comes
            // next and the moment it hands it over.
            b.Services.AddSingleton(_registrations);
            b.Services.AddSingleton<IScheduler>(sp => new RegistrationWatchingScheduler(
                BuildRealScheduler(sp), sp.GetRequiredService<RegistrationWatch>()));
        }, startHost, configureEverTask, clock);

        if (startHost)
            await recovery.Finished.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static PeriodicTimerScheduler BuildRealScheduler(IServiceProvider sp) =>
        new(sp.GetRequiredService<IWorkerQueueManager>(),
            sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(), null, sp.GetService<ITaskStorage>(),
            sp.GetRequiredService<TimeProvider>());

    private ITaskScheduleManager Manager => Host!.Services.GetRequiredService<ITaskScheduleManager>();

    private IScheduler Scheduler => Host!.Services.GetRequiredService<IScheduler>();

    private ScheduleVersionRegistry Versions => Host!.Services.GetRequiredService<ScheduleVersionRegistry>();

    private async Task<QueuedTask> RowAsync(Guid id) => (await _shared.Get(t => t.Id == id))[0];

    /// <summary>
    /// Runs <paramref name="act"/> with every monitoring event recorded, and hands the whole set back once one
    /// event carrying each of <paramref name="expected"/> has arrived.
    /// </summary>
    /// <remarks>
    /// Publishing is fire-and-forget by contract — one <c>Task.Run</c> per subscriber — so "the event was
    /// said" is a wait and never a read taken the instant the call returns, and two events published in a row
    /// arrive in no guaranteed order. The subscription is a static event, so it is removed in a finally.
    /// </remarks>
    private async Task<EverTaskEventData[]> EventsOfAsync(Func<Task> act, params string[] expected)
    {
        var events = new ConcurrentQueue<EverTaskEventData>();

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await act();

            await TaskWaitHelper.WaitForConditionAsync(
                () => expected.All(phrase =>
                    events.Any(e => e.Message.Contains(phrase, StringComparison.Ordinal))), 20000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        return [.. events];

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// The executor a persisted row would be delivered as — the same rebuild recovery and the materializer do,
    /// so a test can hand the REAL worker a delivery instead of imitating one.
    /// </summary>
    private async Task<TaskHandlerExecutor> BuildExecutorAsync(QueuedTask row)
    {
        var recovered = RecoveredTaskFactory.FromRowWithoutRegistries(row);

        using var scope = Host!.Services.CreateScope();

        return await EverTask.Dispatcher.Dispatcher.CreateCachedWrapper(recovered.Task!.GetType())
                               .Handle(recovered.Task, recovered.ExecutionTime, recovered.Recurring,
                                   scope.ServiceProvider, recovered.AuditLevel, row.Id, row.TaskKey,
                                   useLazyExecutor: true, recovered.RowMetadata);
    }

    // cursor: where the schedule stands, or null for a series that has already ended.
    private async Task<Guid> SeedDurableScheduleAsync(RecurringTask definition, DateTimeOffset? cursor,
                                                      string? taskKey,
                                                      QueuedTaskStatus status = QueuedTaskStatus.Queued)
    {
        var row = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            CreatedAtUtc    = Clock.GetUtcNow().AddHours(-1),
            Type            = typeof(RescheduleProbeTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new RescheduleProbeTask("seeded")),
            Handler         = typeof(RescheduleProbeTaskHandler).AssemblyQualifiedName!,
            Status          = status,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(definition),
            RecurringInfo   = definition.ToString(),
            NextRunUtc      = cursor,
            CurrentRunCount = 0,
            MaxRuns         = definition.MaxRuns,
            RunUntil        = definition.RunUntil,
            QueueName       = QueueNames.Recurring,
            TaskKey         = taskKey,
            AuditLevel      = (int)AuditLevel.Full
        };

        await _shared.Persist(row);
        return row.Id;
    }

    /// <summary>
    /// One occurrence row of <paramref name="parentId"/>, with a slot already in the past. Two occurrences of
    /// the same schedule need two different <paramref name="slotMinutesAgo"/>: the unique index on
    /// (parent, slot) refuses the second otherwise.
    /// </summary>
    private QueuedTask NewOccurrence(Guid parentId, string marker, QueuedTaskStatus status,
                                     int slotMinutesAgo = 9) => new()
    {
        Id                    = Guid.NewGuid(),
        CreatedAtUtc          = Clock.GetUtcNow().AddMinutes(-10),
        Type                  = typeof(RescheduleProbeTask).AssemblyQualifiedName!,
        Request               = EverTaskJson.Serialize(new RescheduleProbeTask(marker)),
        Handler               = typeof(RescheduleProbeTaskHandler).AssemblyQualifiedName!,
        Status                = status,
        ParentTaskId          = parentId,
        ScheduledExecutionUtc = Clock.GetUtcNow().AddMinutes(-slotMinutesAgo),
        QueueName             = QueueNames.Recurring,
        AuditLevel            = (int)AuditLevel.Full
    };

    private static RecurringTask MinuteCatchUp(int maxOccurrences, int maxPending = 5) => new()
    {
        MinuteInterval = new MinuteInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromDays(1),
            MaxOccurrences        = maxOccurrences,
            OverflowPolicy        = CatchUpOverflowPolicy.Halt,
            MaxPendingOccurrences = maxPending
        }
    };

    // ---- The definition really changes ------------------------------------------------------------

    [Fact]
    public async Task A_rescheduled_series_runs_on_its_new_definition()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("live"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-runs");

        var result = await Manager.Reschedule("reschedule-runs", r => r.Schedule().Every(1).Seconds());

        result.TaskId.ShouldBe(id);
        result.ScheduleVersion.ShouldBe(1, "every reschedule bumps the version the row is compared against");
        result.PreviousScheduleVersion.ShouldBe(0);

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);

        var row = await RowAsync(id);
        row.ScheduleVersion.ShouldBe(1);
        row.RecurringTask!.ShouldContain("SecondInterval", Case.Insensitive,
            "the row carries the new definition, not the one it was dispatched with");
    }

    [Fact]
    public async Task The_slot_the_old_definition_had_parked_never_fires()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("parked"),
            r => r.Schedule().Every(4).Seconds(), taskKey: "reschedule-parked");

        var result = await Manager.Reschedule("reschedule-parked", r => r.Schedule().Every(1).Hours());

        result.NextRunUtc!.Value.ShouldBeGreaterThan(Clock.GetUtcNow().AddMinutes(50));

        // Well past the slot the first definition had parked: the scheduler's registration is replaced
        // latest-wins, so nothing is left to fire it.
        await Task.Delay(6000);

        _recorder.Count.ShouldBe(0, "the old slot was replaced, not merely superseded on the row");
        (await RowAsync(id)).NextRunUtc.ShouldBe(result.NextRunUtc);
    }

    [Fact]
    public async Task A_run_that_finishes_after_a_reschedule_applies_the_new_definition()
    {
        _recorder.Hold = true;

        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("in-progress"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-inprogress");

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.InProgress,
            "a reschedule is never refused because a run is in flight (S3)");

        var result = await Manager.Reschedule("reschedule-inprogress", r => r.Schedule().Every(1).Hours());

        _recorder.Release();

        var row = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].Status == QueuedTaskStatus.Completed, 20000);

        row[0].NextRunUtc.ShouldBe(result.NextRunUtc,
            "the completion lost the compare-and-swap and kept the cursor the reschedule wrote");
        row[0].CurrentRunCount.ShouldBe(1, "the run still happened, so it is still counted");

        var afterCompletion = _recorder.Count;
        await Task.Delay(2500);
        _recorder.Count.ShouldBe(afterCompletion, "the per-second grid is gone");
    }

    // ---- Rebase -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_rebase_keeps_the_logical_day_of_a_zoned_schedule()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("zoned"),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(23, 0)).InTimeZone("Europe/Rome"),
            taskKey: "reschedule-rebase");

        var before = (await RowAsync(id)).NextRunUtc!.Value;
        var rome   = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

        var result = await Manager.Reschedule("reschedule-rebase",
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(23, 30)).InTimeZone("Europe/Rome"),
            RescheduleMode.RebaseFromCursor);

        var after = TimeZoneInfo.ConvertTime(result.NextRunUtc!.Value, rome);

        after.Date.ShouldBe(TimeZoneInfo.ConvertTime(before, rome).Date,
            "moving the hour keeps the schedule on the day it was already on");
        after.TimeOfDay.ShouldBe(new TimeSpan(23, 30, 0));
    }

    [Fact]
    public async Task A_rebase_onto_a_different_shape_is_refused()
    {
        await StartHostAsync();

        await Dispatcher.Dispatch(new RescheduleProbeTask("shape"),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)), taskKey: "reschedule-shape");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-shape", r => r.Schedule().Every(1).Hours(),
                RescheduleMode.RebaseFromCursor));

        error.Message.ShouldContain("RecalculateFromNow");
    }

    [Fact]
    public async Task A_cron_schedule_cannot_be_rebased()
    {
        await StartHostAsync();

        await Dispatcher.Dispatch(new RescheduleProbeTask("cron"),
            r => r.Schedule().UseCron("0 9 * * *"), taskKey: "reschedule-cron");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-cron", r => r.Schedule().UseCron("0 10 * * *"),
                RescheduleMode.RebaseFromCursor));

        error.Message.ShouldContain("no nominal period");
    }

    [Fact]
    public async Task Should_recalculate_onto_the_first_non_excluded_grid_slot()
    {
        var saturday = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        await StartHostAsync(clock: new FakeTimeProvider(saturday));

        await Dispatcher.Dispatch(new RescheduleProbeTask("filtered-recalculate"),
            recurring => recurring.Schedule().Every(1).Hours(), taskKey: "filtered-recalculate");

        var result = await Manager.Reschedule("filtered-recalculate",
            recurring => recurring.Schedule()
                                  .EveryDay()
                                  .AtTime(new TimeOnly(9, 0))
                                  .ExceptWeekends());

        result.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 8, 31, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Should_surface_exclusion_budget_exhaustion_before_a_schedule_update_is_written()
    {
        var saturday = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        await StartHostAsync(clock: new FakeTimeProvider(saturday));

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("budget-refusal"),
            recurring => recurring.Schedule().Every(1).Hours(), taskKey: "budget-refusal");
        var before           = await RowAsync(id);
        var previousVersion  = before.ScheduleVersion;
        var previousCursor   = before.NextRunUtc;
        var previousSchedule = before.RecurringTask;

        await Should.ThrowAsync<ExclusionSearchBudgetExceededException>(() =>
            Manager.Reschedule("budget-refusal",
                recurring => recurring.Schedule()
                                      .Every(7)
                                      .Days()
                                      .Except(exclusion => exclusion.OnDays(DayOfWeek.Saturday))));

        var after = await RowAsync(id);
        after.ScheduleVersion.ShouldBe(previousVersion);
        after.NextRunUtc.ShouldBe(previousCursor);
        after.RecurringTask.ShouldBe(previousSchedule);
    }

    // ---- What it refuses --------------------------------------------------------------------------

    [Fact]
    public async Task An_unknown_task_key_is_refused()
    {
        await StartHostAsync();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("nothing-was-dispatched-with-this", r => r.Schedule().Every(1).Hours()));
    }

    [Fact]
    public async Task A_one_shot_is_refused()
    {
        await StartHostAsync();

        await Dispatcher.Dispatch(new RescheduleProbeTask("one-shot"), TimeSpan.FromHours(1),
            taskKey: "reschedule-oneshot");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-oneshot", r => r.Schedule().Every(1).Hours()));

        error.Message.ShouldContain("one-shot");
    }

    [Fact]
    public async Task A_schedule_with_no_occurrence_left_is_refused_instead_of_being_left_unfinishable()
    {
        await StartHostAsync();

        await Dispatcher.Dispatch(new RescheduleProbeTask("exhausted"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-exhausted");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-exhausted",
                r => r.Schedule().Every(1).Hours().RunUntil(Clock.GetUtcNow().AddMinutes(10))));

        error.Message.ShouldContain("CancelSchedule");
    }

    [Fact]
    public async Task An_unknown_task_key_is_refused_by_a_cancel_too()
    {
        await StartHostAsync();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.CancelSchedule("nothing-was-dispatched-with-this"));

        error.Message.ShouldContain("There is no task with key");
    }

    [Fact]
    public async Task A_one_shot_is_refused_by_a_cancel_too()
    {
        // A cancel reaches the row by a different road from the three that rewrite it — no capability to ask
        // for, no definition to rebuild — so what it refuses has to be pinned on that road and not on theirs.
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancel-one-shot"), TimeSpan.FromHours(1),
            taskKey: "reschedule-cancel-oneshot");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.CancelSchedule("reschedule-cancel-oneshot"));

        error.Message.ShouldContain("one-shot");

        (await RowAsync(id)).Status.ShouldNotBe(QueuedTaskStatus.Cancelled,
            "the refusal comes BEFORE the cancel pipeline: a one-shot addressed as a schedule is not " +
            "quietly cancelled instead");
    }

    [Fact]
    public async Task A_cancelled_schedule_really_restarts_when_it_is_dispatched_again_under_its_key()
    {
        // The documented way back from a cancel — "a cancelled schedule cannot be rescheduled, it has to be
        // dispatched again" — and it did not work inside the process that had cancelled it. A recurring
        // re-registration REUSES the row, so both halves of the cancellation followed the id: the blacklist
        // entry lives about an hour and made WorkerQueue drop every delivery the new registration produced,
        // while the row stayed Cancelled, which no recovery predicate selects.
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("restart"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-restart");

        await Manager.CancelSchedule("reschedule-restart");

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled, "the premise: the series really ended");

        var before = _recorder.Count;

        var restarted = await Dispatcher.Dispatch(new RescheduleProbeTask("restart"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-restart");

        restarted.ShouldBe(id, "a recurring re-registration keeps the row, which is why the cancel follows it");

        var row = await RowAsync(id);
        row.Status.ShouldNotBe(QueuedTaskStatus.Cancelled,
            "a status no recovery predicate selects would lose the series at the first restart before its " +
            "first slot");
        row.Status.ShouldBe(QueuedTaskStatus.WaitingQueue, "which is where a brand new dispatch leaves a row");

        WorkerBlacklist.IsBlacklisted(id).ShouldBeFalse(
            "and the cancellation entry is dropped: the dispatch IS the decision to run it again");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count > before, 20000);

        _recorder.Count.ShouldBeGreaterThan(before, "the series really runs again");
    }

    [Fact]
    public async Task An_occurrence_the_cancel_ended_does_not_run_when_its_schedule_is_dispatched_again()
    {
        // The revival drops the schedule's blacklist entry, and that entry is the ONLY in-process cover the
        // occurrences the cancel already terminalized have: past the enqueue boundary nothing re-reads the
        // row, and SetInProgress is unconditional. One occurrence is held inside its handler so the single
        // consumer is busy and the other stays in the channel — the exposure the documented restart (cancel,
        // then dispatch again under the key) used to free.
        _recorder.Hold = true;

        await StartHostAsync(startHost: false, configureEverTask: cfg => cfg.SetMaxDegreeOfParallelism(1));

        var scheduleId = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddHours(1),
            "reschedule-inflight-cancel");

        var first  = NewOccurrence(scheduleId, "in-flight-1", QueuedTaskStatus.Queued);
        var second = NewOccurrence(scheduleId, "in-flight-2", QueuedTaskStatus.Queued, slotMinutesAgo: 8);

        await _shared.Persist(first);
        await _shared.Persist(second);

        await Host!.StartAsync();

        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));
        await TaskWaitHelper.WaitForConditionAsync(() => deliveries.OccurrencesOf(scheduleId).Length == 2, 20000);

        // Which of the two the single consumer took first is not the point, so the test reads it back.
        var running = (await RowAsync(first.Id)).Status == QueuedTaskStatus.InProgress ? first.Id : second.Id;
        var waiting = running == first.Id ? second.Id : first.Id;

        (await RowAsync(running)).Status.ShouldBe(QueuedTaskStatus.InProgress);
        (await RowAsync(waiting)).Status.ShouldBe(QueuedTaskStatus.Queued,
            "the premise: it is past the enqueue boundary, waiting in the channel behind the busy consumer");

        await Manager.CancelSchedule("reschedule-inflight-cancel");

        (await RowAsync(waiting)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the cancel terminalizes the pending occurrences with the schedule (M15)");

        await Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
            r => r.Schedule().Every(1).Minutes().WithDurableOccurrences(),
            taskKey: "reschedule-inflight-cancel");

        _recorder.Release();

        await TaskWaitHelper.WaitForConditionAsync(() => deliveries.OccurrencesOf(scheduleId).Length == 0, 20000);

        _recorder.Count.ShouldBe(1, "only the occurrence the cancel had let finish ever reached a handler");
        (await RowAsync(waiting)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "an occurrence the cancel confirmed terminal must not be written back to InProgress and completed");
        (await RowAsync(running)).Status.ShouldBe(QueuedTaskStatus.Completed,
            "and the one it left running still finishes: covering that one too would suppress work already done");
    }

    [Fact]
    public async Task A_halted_schedule_dispatched_again_after_a_cancel_comes_back_without_its_halt()
    {
        // The revival promises the row goes back where a brand new dispatch would have left it, and a brand
        // new row carries no runtime state. A halt that survived it left the series parked on a marker the
        // materializer reports without re-planning and without re-parking: one delivery, no occurrence, and
        // the same thing after every restart — while the caller had just been handed a dispatch id.
        await StartHostAsync(startHost: false);

        var cursor = Clock.GetUtcNow().AddMinutes(-40);
        var scheduleId = await SeedDurableScheduleAsync(MinuteCatchUp(5, maxPending: 5), cursor,
            "reschedule-halted-restart");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
            rows => rows[0].RuntimeInfo != null, 20000);

        (await RowAsync(scheduleId)).RuntimeInfo!.ShouldContain("Halted", Case.Insensitive,
            "the premise: a backlog of 40 slots against a cap of 5 halts the catch-up");
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();

        await Manager.CancelSchedule("reschedule-halted-restart");

        (await RowAsync(scheduleId)).RuntimeInfo.ShouldNotBeNull(
            "a cancel writes the status and leaves the marker where it is");

        await Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
            r => r.Schedule()
                  .Every(1).Minutes()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(1), 1000)
                  {
                      MaxPendingOccurrences = 5
                  })),
            taskKey: "reschedule-halted-restart");

        (await RowAsync(scheduleId)).RuntimeInfo.ShouldBeNull(
            "the halt belonged to the series the cancel ended, not to the one this dispatch registers");

        var occurrences = await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId),
            rows => rows.Length > 0, 20000);

        occurrences.ShouldNotBeEmpty("the restarted series really materializes again");
    }

    [Fact]
    public async Task A_standing_halt_survives_an_ordinary_re_registration_of_its_schedule()
    {
        // The complement, and the reason the revival is the only exception: re-declaring your schedules at
        // startup is boilerplate, not an operator asking for the replay a halt exists to stop (M10). Only
        // ResumeSchedule, Reschedule — or the cancel-then-dispatch above — release one.
        await StartHostAsync(startHost: false);

        var cursor = Clock.GetUtcNow().AddMinutes(-40);
        var scheduleId = await SeedDurableScheduleAsync(MinuteCatchUp(5, maxPending: 5), cursor,
            "reschedule-halted-kept");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
            rows => rows[0].RuntimeInfo != null, 20000);

        await Dispatcher.Dispatch(new RescheduleProbeTask("re-registered"),
            r => r.Schedule()
                  .Every(1).Minutes()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(1), 1000)
                  {
                      MaxPendingOccurrences = 5
                  })),
            taskKey: "reschedule-halted-kept");

        (await RowAsync(scheduleId)).RuntimeInfo!.ShouldContain("Halted", Case.Insensitive,
            "a re-registration writes the definition and leaves the runtime state alone");

        await Task.Delay(1500);

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty(
            "and the schedule stays where the halt left it: nothing is replayed");
    }

    [Fact]
    public async Task A_run_of_the_series_a_cancel_ended_cannot_take_the_row_back_from_the_revival()
    {
        // The revival drops the schedule's blacklist entry, and for an INLINE schedule that entry was the only
        // thing standing between its OWN in-flight delivery and every write it makes: the cover the revival
        // moves is built from the deliveries registered as occurrences OF a schedule, and a schedule's own
        // delivery is not one of them. A re-registration REUSES the row, so the two deliveries answer to the
        // same id and no blacklist can separate them — the version can, which is why the revival is a new
        // generation of the row and not the same one.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancelled-series"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-revival-race");

        var  revived = Guid.Empty;
        var  landed  = 0;

        // The documented restart, landing INSIDE the window the old run's advance is about to close: its
        // blacklist check and its reading of the row are both behind it already, so nothing else can speak
        // for the row it is about to write.
        faulty.RunBefore(nameof(ITaskStorage.CompleteRecurringRun), () =>
        {
            if (Interlocked.Exchange(ref landed, 1) == 1)
                return;

            Dispatcher.Cancel(id).GetAwaiter().GetResult();

            revived = Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
                                    r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-revival-race")
                                .GetAwaiter().GetResult();
        });

        await TaskWaitHelper.WaitForConditionAsync(() => Volatile.Read(ref landed) == 1 && revived != Guid.Empty,
            20000);

        revived.ShouldBe(id, "a recurring re-registration keeps the row, which is why the cancel follows it");

        var row = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].CurrentRunCount >= 1, 20000);

        row[0].NextRunUtc!.Value.ShouldBeGreaterThan(Clock.GetUtcNow().AddMinutes(50),
            "the old run's advance lost its compare-and-swap and left the revival's cursor alone");
        row[0].Status.ShouldNotBe(QueuedTaskStatus.Cancelled, "the series really restarted");
        row[0].RecurringTask!.ShouldContain("HourInterval", Case.Insensitive,
            "and the row still carries the definition the restart wrote");

        var runsWhenRevived = _recorder.Count;
        await Task.Delay(2500);

        _recorder.Count.ShouldBe(runsWhenRevived,
            "and the per-second grid the cancel ended is not re-parked over the hourly one that replaced it");

        // What all of the above rests on: the two deliveries answer to the same id, so only the version can
        // tell them apart.
        row[0].ScheduleVersion.ShouldBe(1, "the revival replaces the definition, so it moves the version too");

        Versions.TryGetLatest(id, out var published).ShouldBeTrue(
            "and it publishes the lower bound every delivery of the old definition is measured against");
        published.ShouldBe(1);
    }

    [Fact]
    public async Task The_ending_of_a_run_a_new_definition_outlived_is_not_written_over_the_series_that_owns_the_row()
    {
        // The other half of the same id being reused: a run that was already executing when its definition
        // was replaced — by a reschedule, or by the dispatch that revives a cancelled series — is past the
        // pre-gate drop, and both terminal writes of its ENDING are unconditional. Cancelled is the one no
        // recovery predicate selects and no advance moves past, so an ending that lands late killed the
        // series that had just taken the row over. The advance beside it was already compare-and-swapped;
        // this is the half that had nothing to lose to.
        _recorder.Hold            = true;
        _recorder.CancelAfterHold = true;

        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("outlived"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-outlived-run");

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        var result = await Manager.Reschedule("reschedule-outlived-run", r => r.Schedule().Every(1).Hours());

        result.ScheduleVersion.ShouldBe(1, "the premise: the row belongs to a new generation now");

        // The run ends here, on a definition nobody is waiting for any more.
        _recorder.Release();

        var row = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].CurrentRunCount >= 1 || rows[0].Status == QueuedTaskStatus.Cancelled, 20000);

        row[0].Status.ShouldNotBe(QueuedTaskStatus.Cancelled,
            "the run that ended belonged to the definition that was replaced, not to the one that owns the row");
        row[0].NextRunUtc.ShouldBe(result.NextRunUtc,
            "and the advance that recorded it kept the cursor the new definition wrote");
    }

    [Fact]
    public async Task An_ending_that_lands_after_the_revival_is_not_written_over_the_restarted_series()
    {
        // The same half, in the window the registry cannot see at all: Cancel REMOVES the entry and the
        // revival publishes the new generation only after the re-park, so the superseded guard answers "no"
        // for the whole cancel-to-publish span. An ending whose guard read falls in there wrote its Cancelled
        // over the row TryReviveCancelledSchedule had just taken to the next version — the restarted series
        // parked nowhere, selected by no recovery predicate, behind a dispatch that answered with an id.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        _recorder.Hold            = true;
        _recorder.CancelAfterHold = true;

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancelled-run"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-ending-vs-revival");

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        // This run is past the point where the token would have turned it back: dropping its source is how the
        // test says so. On a real storage the ending is asynchronous anyway — in memory every await completes
        // inline, so a cancel would otherwise run the whole unwind on the cancelling thread and no ending
        // could ever land after the cancel it belongs to.
        CancellationSourceProvider.Delete(id);

        await Dispatcher.Cancel(id);

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the premise: the operator ended the series and the run is still unwinding behind it");

        var atTheDoor      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restartWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held           = 0;

        // The ending is stopped on the threshold of its own write, which is where the race lives: its guard
        // has already been read against a registry the cancel emptied, and the restart is what happens next.
        faulty.RunBefore(nameof(ITaskStorage.TrySetTerminalOutcome), () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 1)
                return;

            atTheDoor.SetResult();
            restartWritten.Task.GetAwaiter().GetResult();
        });

        // Everything from here belongs to the series the restart registers, so nothing else is held.
        _recorder.Hold            = false;
        _recorder.CancelAfterHold = false;
        var before = _recorder.Count;

        _recorder.Release();

        await atTheDoor.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var restarted = await Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-ending-vs-revival");

        restarted.ShouldBe(id, "a recurring re-registration keeps the row, which is why the cancel follows it");
        (await RowAsync(id)).ScheduleVersion.ShouldBe(1,
            "the premise: the revival replaces the definition, so it moves the version too");

        // The ending commits only now, on a row that already belongs to the definition just registered.
        restartWritten.SetResult();

        var deliveries = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();
        await TaskWaitHelper.WaitForConditionAsync(() => !deliveries.IsDelivering(id), 20000);

        (await RowAsync(id)).Status.ShouldNotBe(QueuedTaskStatus.Cancelled,
            "the run that ended belonged to the series the cancel closed, not to the one that owns the row now");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count > before, 20000);

        _recorder.Count.ShouldBeGreaterThan(before, "and the restarted series really runs");
    }

    [Fact]
    public async Task A_failure_that_lands_after_a_cancel_does_not_erase_it_and_resurrect_the_series()
    {
        // The half no version can answer for: a cancel writes the status and leaves the version and the
        // cursor exactly where they were, so an ending that is not a cancellation matched every guard and
        // wrote its Failed straight over the Cancelled. A recurring row that reads Failed with a live cursor
        // is recoverable, so the next restart re-dispatched the series an operator had ended — and the
        // documented restart was broken with it, since the revival keys on Cancelled alone.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        _recorder.Hold           = true;
        _recorder.FaultAfterHold = true;

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("faulting-run"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-failure-vs-cancel");

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        var atTheDoor       = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelPersisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held            = 0;

        faulty.RunBefore(nameof(ITaskStorage.TrySetTerminalOutcome), () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 1)
                return;

            atTheDoor.SetResult();
            cancelPersisted.Task.GetAwaiter().GetResult();
        });

        // The run faults on its own — a TimeoutException, which no retry policy retries — while nothing has
        // cancelled anything yet, so its ending takes the Failed branch.
        _recorder.Hold           = false;
        _recorder.FaultAfterHold = false;
        _recorder.Release();

        await atTheDoor.Task.WaitAsync(TimeSpan.FromSeconds(20));

        await Dispatcher.Cancel(id);

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled, "the premise: the operator ended it");

        cancelPersisted.SetResult();

        var deliveries = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();
        await TaskWaitHelper.WaitForConditionAsync(() => !deliveries.IsDelivering(id), 20000);

        var row = await RowAsync(id);
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "an ending that lands after the cancel must not erase the decision that ended the series");
        row.IsRecoverableForExecution(Clock.GetUtcNow()).ShouldBeFalse(
            "a recurring row left Failed with a live cursor is exactly what a restart puts back in a queue");

        var runs = _recorder.Count;

        // The same rows, a new process: the only thing that decides now is what the row says.
        await StartHostAsync();
        await Task.Delay(1500);

        _recorder.Count.ShouldBe(runs, "so the cancelled series does not come back at the next restart");
    }

    [Fact]
    public async Task A_revival_whose_un_cancel_is_lost_fails_the_dispatch_instead_of_reporting_a_restart()
    {
        // SetStatus is best effort on every relational provider — it logs its own failed write and hands the
        // caller a completed task — so the one write the whole restart depends on could be lost while the
        // dispatch returned an id and logged a restart. Nothing polls behind it: the registration is refused
        // at its first fire because the row is still Cancelled, and no recovery predicate selects one either.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("lost-uncancel"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-lost-uncancel");

        await Manager.CancelSchedule("reschedule-lost-uncancel");

        // The shape of a database blip that rolled the un-cancel back: the call returns without reaching the
        // store, exactly as the swallowing writes do.
        faulty.SwallowNext(nameof(ITaskStorage.TryReviveCancelledSchedule), 1);

        var events = await EventsOfAsync(async () =>
            {
                var failure = await Should.ThrowAsync<InvalidOperationException>(() =>
                    Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
                        r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-lost-uncancel"));

                failure.Message.ShouldContain("could not be taken out of Cancelled", Case.Sensitive,
                    "the caller has to learn that the dispatch it was told about did nothing");
            },
            "could not be taken out of Cancelled");

        events.ShouldNotBeEmpty("a log line alone is not enough where nothing polls behind the write (V4)");

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the row is exactly where the cancel left it");

        WorkerBlacklist.IsBlacklisted(id).ShouldBeTrue(
            "and its cover survives: the entry is dropped only once the un-cancel is committed");

        Scheduler.IsScheduled(id).ShouldBeFalse("nothing is parked over a row that is still cancelled");

        Versions.TryGetLatest(id, out _).ShouldBeFalse(
            "and no version is published for a generation the row does not carry");

        await Task.Delay(1500);

        _recorder.Count.ShouldBe(0, "the series does not run");
    }

    [Fact]
    public async Task A_revival_whose_un_cancel_is_lost_parks_nothing_even_when_the_dispatch_may_not_throw()
    {
        // ThrowIfUnableToPersist(false) buys a dispatch that does not fail when storage does. It does not buy
        // a registration over a terminal row: WorkerQueue refuses one at its first fire while the scheduler
        // consumes it, so parking here would only make the loss quieter.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty, configureEverTask: cfg => cfg.SetThrowIfUnableToPersist(false));

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("lost-uncancel-quiet"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-lost-uncancel-quiet");

        await Manager.CancelSchedule("reschedule-lost-uncancel-quiet");

        faulty.SwallowNext(nameof(ITaskStorage.TryReviveCancelledSchedule), 1);

        var answered = await Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-lost-uncancel-quiet");

        answered.ShouldBe(id);

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);
        WorkerBlacklist.IsBlacklisted(id).ShouldBeTrue();
        Scheduler.IsScheduled(id).ShouldBeFalse();

        await Task.Delay(1500);

        _recorder.Count.ShouldBe(0);
    }

    [Fact]
    public async Task An_exhausted_definition_dispatched_under_a_cancelled_key_leaves_the_cancellation_alone()
    {
        // The taskKey branch hands the row's own status to the finalization as its compare-and-swap
        // expectation, and Cancelled MATCHES: the guard confirmed the very state it exists to lose to, so an
        // exhausted re-registration wrote Completed over a series an operator had ended — erasing the record
        // of the cancellation and, with it, the one status a later re-dispatch needs to see to undo it (X3).
        await StartHostAsync(startHost: false);

        // The same seeder with an INLINE definition, and a cursor a downtime left in the past: that is what
        // makes the definition below exhausted instead of merely future.
        var cursor = Clock.GetUtcNow().AddHours(-3).AddMinutes(-30);
        var id = await SeedDurableScheduleAsync(new RecurringTask { HourInterval = new HourInterval(1) }, cursor,
            "reschedule-exhausted-cancel");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => Scheduler.IsScheduled(id), 20000);

        await Manager.CancelSchedule("reschedule-exhausted-cancel");

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled, "the premise: an operator ended it");

        // Every slot this definition has left falls past its own bound, so the dispatch decides the series is
        // exhausted — the branch that used to finalize the row it was handed.
        var answered = await Dispatcher.Dispatch(new RescheduleProbeTask("exhausted"),
            r => r.Schedule().Every(1).Hours().RunUntil(Clock.GetUtcNow().AddMinutes(1)),
            taskKey: "reschedule-exhausted-cancel");

        answered.ShouldBe(id);

        var row = await RowAsync(id);

        row.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "a cancelled series is already terminal and is never rewritten to Completed");
        row.NextRunUtc.ShouldBe(cursor, "and its cursor is left where the cancel found it");

        (await _shared.GetStatusAuditsPage(id, 0, int.MaxValue)).Audits
            .ShouldNotContain(a => a.NewStatus == QueuedTaskStatus.Completed,
            "the trail must not say a series ran to completion at the moment of a deploy that ran nothing");

        // The second-order half: because the row is still Cancelled, the key still leads back to the revival.
        await Dispatcher.Dispatch(new RescheduleProbeTask("restarted"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-exhausted-cancel");

        (await RowAsync(id)).Status.ShouldNotBe(QueuedTaskStatus.Cancelled);

        WorkerBlacklist.IsBlacklisted(id).ShouldBeFalse(
            "the entry a row flipped to Completed would have stranded, with nothing left to drop it");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count > 0, 20000);

        _recorder.Count.ShouldBeGreaterThan(0, "and the series really runs again");
    }

    /// <summary>The occurrence rows of a schedule, whatever state they are in.</summary>
    private async Task<QueuedTask[]> OccurrencesOfAsync(Guid scheduleId) =>
        await _shared.Get(t => t.ParentTaskId == scheduleId);

    [Fact]
    public async Task A_series_that_has_already_ended_is_an_ordinary_reschedule_target()
    {
        await StartHostAsync();

        // No cursor at all: the shape a finished series leaves behind. The compare-and-swap has to read that
        // null expectation as IS NULL rather than as an equality, or restarting a series an operator wants
        // back would lose every time.
        var id = await SeedDurableScheduleAsync(new RecurringTask { HourInterval = new HourInterval(1) },
            cursor: null, "reschedule-ended", QueuedTaskStatus.Completed);

        var result = await Manager.Reschedule("reschedule-ended", r => r.Schedule().Every(1).Seconds());

        result.PreviousNextRunUtc.ShouldBeNull("the series it replaces had ended");
        result.NextRunUtc.ShouldNotBeNull();
        result.ScheduleVersion.ShouldBe(1);

        var row = await RowAsync(id);
        row.NextRunUtc.ShouldBe(result.NextRunUtc);
        row.RecurringTask!.ShouldContain("SecondInterval");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);

        _recorder.Count.ShouldBeGreaterThanOrEqualTo(2, "and the series really runs again");
    }

    [Fact]
    public async Task A_series_that_has_already_ended_has_no_cursor_to_resume_or_rebase()
    {
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(new RecurringTask { HourInterval = new HourInterval(1) },
            cursor: null, "reschedule-ended-nocursor", QueuedTaskStatus.Completed);

        // The two modes that do not compute a cursor of their own read it off the row, and there is none:
        // a resume replans the backlog the cursor points at, a rebase maps that cursor onto the new grid.
        (await Should.ThrowAsync<InvalidOperationException>(() => Manager.ResumeSchedule("reschedule-ended-nocursor")))
            .Message.ShouldContain("no cursor to resume");

        (await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-ended-nocursor", r => r.Schedule().Every(1).Hours(),
                RescheduleMode.RebaseFromCursor))).Message.ShouldContain("no cursor to rebase");

        (await RowAsync(id)).ScheduleVersion.ShouldBe(0, "neither refusal writes anything");
    }

    // ---- The version registry (S4) ----------------------------------------------------------------

    [Fact]
    public async Task A_delivery_a_reschedule_superseded_is_discarded_before_it_runs()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("superseded"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-superseded");

        // The delivery the scheduler was holding when the reschedule landed: already past the scheduler's
        // reach, which is the only case the registry exists for.
        var stale = await BuildExecutorAsync(await RowAsync(id));

        await Manager.Reschedule("reschedule-superseded", r => r.Schedule().Every(2).Hours());

        Versions.TryGetLatest(id, out var published).ShouldBeTrue();
        published.ShouldBe(1);

        await WorkerExecutor.DoWork(stale, CancellationToken.None);

        _recorder.Count.ShouldBe(0, "the delivery carried the definition that was just replaced");
    }

    [Fact]
    public async Task Without_a_published_version_a_recovered_delivery_is_never_discarded()
    {
        await StartHostAsync();

        // A row several reschedules old, as a restart would find it: nothing has been published in THIS
        // process, so there is no lower bound and the delivery must go through.
        var id = await SeedDurableScheduleAsync(
            new RecurringTask { HourInterval = new HourInterval(1) }, Clock.GetUtcNow().AddHours(1),
            "reschedule-recovered");

        var row = await RowAsync(id);
        row.RecurringTask = EverTaskJson.Serialize(new RecurringTask { HourInterval = new HourInterval(1) });
        row.ScheduleVersion = 3;
        await _shared.UpdateTask(row);

        Versions.TryGetLatest(id, out _).ShouldBeFalse();

        var recovered = await BuildExecutorAsync(await RowAsync(id));

        await WorkerExecutor.DoWork(recovered with { ScheduleVersion = 0 }, CancellationToken.None);

        _recorder.Count.ShouldBe(1, "the absence of an entry is not a lower bound of zero");
    }

    [Fact]
    public async Task A_failed_repark_publishes_nothing_and_the_old_delivery_advances_onto_the_new_definition()
    {
        await StartHostAsync(faultyScheduler: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("repark"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-repark");

        var old = await BuildExecutorAsync(await RowAsync(id));

        _faults.FailNextFor(id);

        var result = await Manager.Reschedule("reschedule-repark", r => r.Schedule().Every(2).Hours());

        result.ScheduleVersion.ShouldBe(1, "the definition was written: only the re-park failed");
        Versions.TryGetLatest(id, out _).ShouldBeFalse(
            "publishing a version whose executor never reached the scheduler would drop the old delivery " +
            "with nothing to take its place");

        await WorkerExecutor.DoWork(old, CancellationToken.None);

        _recorder.Count.ShouldBe(1, "the old delivery runs once more, exactly as the contract says");

        var row = await RowAsync(id);
        row.NextRunUtc.ShouldBe(result.NextRunUtc,
            "and its advance lost the compare-and-swap, so the new definition's cursor stands");
    }

    [Fact]
    public async Task A_failed_repark_leaves_the_series_parked_on_its_new_definition_instead_of_nowhere()
    {
        await StartHostAsync(faultyScheduler: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("repark-parked"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-repark-parked");

        var old = await BuildExecutorAsync(await RowAsync(id));

        _faults.FailNextFor(id);

        // Committed, then not handed over: from here the ONLY thing holding the series is the registration of
        // the definition that was just replaced, and the contract is that its advance applies the new one.
        var result = await Manager.Reschedule("reschedule-repark-parked", r => r.Schedule().Every(2).Seconds());

        Versions.TryGetLatest(id, out _).ShouldBeFalse();

        await WorkerExecutor.DoWork(old, CancellationToken.None);

        (await RowAsync(id)).NextRunUtc.ShouldBe(result.NextRunUtc);

        // Applying the new definition is only half of it: the advance that applies it is also the only thing
        // left that can park it. Returning empty-handed here left the row in no scheduler, no queue and no
        // delivery until a restart — which on a two-second grid means it simply never runs again.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 20000);

        _recorder.Count.ShouldBeGreaterThanOrEqualTo(3,
            "the series went on running on the definition the failed re-park had written");
    }

    [Fact]
    public async Task A_first_reschedule_landing_inside_an_advance_is_not_overwritten_by_it()
    {
        // The window S1 exists for: the advance read the row BEFORE the reschedule existed, so nothing it
        // carries — not the row it read, not the delivery's version, not the registry, which is published
        // last — says the schedule is managed. Only a compare-and-swap can answer, and it has to be the one
        // this advance makes.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("first-reschedule"),
            r => r.Schedule().Every(1).Seconds(), taskKey: "reschedule-first");

        ScheduleUpdateResult? rescheduled = null;
        var                   landed      = 0;

        faulty.RunBefore(nameof(ITaskStorage.CompleteRecurringRun), () =>
        {
            if (Interlocked.Exchange(ref landed, 1) == 1)
                return;

            rescheduled = Manager.Reschedule("reschedule-first", r => r.Schedule().Every(1).Hours())
                                 .GetAwaiter().GetResult();
        });

        await TaskWaitHelper.WaitForConditionAsync(() => Volatile.Read(ref landed) == 1 && rescheduled != null,
            20000);

        var row = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].CurrentRunCount >= 1, 20000);

        row[0].ScheduleVersion.ShouldBe(1);
        row[0].NextRunUtc.ShouldBe(rescheduled!.NextRunUtc,
            "the run that finished under the old definition lost the compare-and-swap and left the new cursor");

        var afterReschedule = _recorder.Count;
        await Task.Delay(2500);

        _recorder.Count.ShouldBe(afterReschedule, "and the per-second grid it belonged to is gone");
    }

    [Fact]
    public async Task Should_repark_from_the_winning_row_when_an_exclusion_budget_advance_loses_ownership()
    {
        _recorder.Hold = true;

        var saturday = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var faulty   = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(startHost: false, storage: faulty, watchRegistrations: true,
            clock: new FakeTimeProvider(saturday));

        var definition = new RecurringTask
        {
            RunNow      = true,
            DayInterval = new DayInterval(7),
            MaxRuns     = 2,
            Exclusions  = new ScheduleExclusions { Days = [DayOfWeek.Saturday] }
        };
        var id = await SeedDurableScheduleAsync(definition, saturday, "budget-race");
        var work = WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None)
                                 .AsTask();

        await _recorder.Entered.WaitAsync(TimeSpan.FromSeconds(20));

        ScheduleUpdateResult? rescheduled = null;
        var landed = 0;

        faulty.RunBefore(nameof(ITaskStorage.RecordRecurringRunForExclusionRetry), () =>
        {
            if (Interlocked.Exchange(ref landed, 1) == 1)
                return;

            rescheduled = Manager.Reschedule("budget-race", recurring => recurring.Schedule().Every(1).Hours())
                                 .GetAwaiter().GetResult();
        });

        _recorder.Release();

        await work;

        await TaskWaitHelper.WaitForConditionAsync(() => Volatile.Read(ref landed) == 1 && rescheduled != null,
            30000);
        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => (rows[0].CurrentRunCount ?? 0) == 1, 30000);

        _registrations.Registrations.Count(registration =>
                registration.Id == id && registration.Version == rescheduled!.ScheduleVersion &&
                registration.Accepted)
            .ShouldBeGreaterThanOrEqualTo(2,
                "the manager parks its definition, then the losing advance reparks from that same row");
        _registrations.AcceptedAStaleRegistration(id).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_refuse_repark_when_winning_row_names_an_unknown_calendar()
    {
        var faulty = new FaultInjectingTaskStorage(_shared);
        var log    = new RecordingLogger<WorkerExecutor>();

        await StartHostAsync(startHost: false, storage: faulty, watchRegistrations: true, workerLog: log);

        var id = await SeedDurableScheduleAsync(
            new RecurringTask { HourInterval = new HourInterval(1), MaxRuns = 2 },
            Clock.GetUtcNow(), $"unknown-calendar-repark-{Guid.NewGuid():N}");
        var delivery = await BuildExecutorAsync(await RowAsync(id));

        faulty.RunBefore(nameof(ITaskStorage.CompleteRecurringRun), () =>
        {
            var row = _shared.Get(task => task.Id == id).GetAwaiter().GetResult().ShouldHaveSingleItem();
            var replacement = new RecurringTask
            {
                HourInterval = new HourInterval(1),
                MaxRuns      = 2,
                Exclusions   = new ScheduleExclusions { Calendars = ["removed-holidays"] }
            };

            row.RecurringTask = EverTaskJson.Serialize(replacement);
            row.RecurringInfo = replacement.ToString();
            row.ScheduleVersion++;
            _shared.UpdateTask(row).GetAwaiter().GetResult();
        });

        await WorkerExecutor.DoWork(delivery, CancellationToken.None);

        _registrations.Registrations.ShouldNotContain(registration => registration.Id == id,
            "a row naming an unknown calendar must not be handed back to the scheduler");
        log.Count(1234).ShouldBe(1, "the failed row rebuild is reported by the repark path");
    }

    [Fact]
    public async Task Should_abandon_schedule_retry_when_row_names_an_unknown_calendar()
    {
        var log = new RecordingLogger<WorkerExecutor>();
        await StartHostAsync(startHost: false, watchRegistrations: true, workerLog: log);

        var definition = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            MaxRuns      = 2,
            Exclusions   = new ScheduleExclusions { Calendars = ["removed-holidays"] }
        };
        var id = await SeedDurableScheduleAsync(definition, Clock.GetUtcNow(),
            $"unknown-calendar-retry-{Guid.NewGuid():N}");
        var retry = (await BuildExecutorAsync(await RowAsync(id))) with
        {
            ExecutionTime        = Clock.GetUtcNow(),
            IsScheduleRetry      = true,
            ScheduleRetryFromUtc = Clock.GetUtcNow()
        };

        await WorkerExecutor.DoWork(retry, CancellationToken.None);

        _registrations.Registrations.ShouldNotContain(registration => registration.Id == id,
            "an invalid retry row must not reach the scheduler");
        log.Count(1238).ShouldBe(1, "the retry is abandoned at the row-rebuild guard");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Should_resume_a_recorded_exclusion_budget_advance_without_reexecuting_its_slot(
        bool versioned)
    {
        await StartHostAsync(configureEverTask: configuration => configuration.SetOccurrenceProviderRetry(retry =>
        {
            retry.InitialBackoff = TimeSpan.FromDays(1);
            retry.MaxBackoff     = TimeSpan.FromDays(1);
        }));

        var events = new ConcurrentQueue<EverTaskEventData>();
        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            var slot = Clock.GetUtcNow();
            var blocked = new RecurringTask
            {
                RunNow      = true,
                DayInterval = new DayInterval(7),
                MaxRuns     = 2,
                Exclusions  = new ScheduleExclusions { Days = [slot.DayOfWeek] }
            };
            var id = await SeedDurableScheduleAsync(blocked, slot,
                versioned ? $"budget-resume-{Guid.NewGuid():N}" : null);

            await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);

            var recorded = await RowAsync(id);
            recorded.CurrentRunCount.ShouldBe(1);
            recorded.NextRunUtc.ShouldBe(slot);
            _recorder.Count.ShouldBe(1);

            var repaired = new RecurringTask
            {
                DayInterval = new DayInterval(1),
                MaxRuns     = 2
            };
            recorded.RecurringTask = EverTaskJson.Serialize(repaired);
            recorded.RecurringInfo = repaired.ToString();
            await _shared.UpdateTask(recorded);

            var retry = await BuildExecutorAsync(recorded);
            retry = retry with
            {
                ExecutionTime              = Clock.GetUtcNow(),
                IsScheduleRetry            = true,
                ScheduleRetryFromUtc       = slot,
                ScheduleRunAlreadyRecorded = true
            };

            await WorkerExecutor.DoWork(retry, CancellationToken.None);
            await Task.Delay(500);

            var resumed = await RowAsync(id);
            resumed.CurrentRunCount.ShouldBe(1,
                "the retry resumes the advance whose run is already on the row");
            resumed.NextRunUtc.ShouldBe(slot,
                "the retained cursor is not rewritten until the next real run advances it");
            _recorder.Count.ShouldBe(1, "the executed slot is never delivered a second time");

            await TaskWaitHelper.WaitForConditionAsync(() => events.Any(e =>
                e.Message.Contains("Exclusion search for schedule", StringComparison.Ordinal)), 20000);

            events.Single(e => e.Message.Contains("Exclusion search for schedule", StringComparison.Ordinal))
                  .Severity.ShouldBe(nameof(SeverityLevel.Warning));
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Should_publish_an_error_event_when_an_exclusion_retry_cannot_be_parked()
    {
        await StartHostAsync(startHost: false, faultyScheduler: true);

        var slot = Clock.GetUtcNow();
        var blocked = new RecurringTask
        {
            RunNow      = true,
            DayInterval = new DayInterval(7),
            MaxRuns     = 2,
            Exclusions  = new ScheduleExclusions { Days = [slot.DayOfWeek] }
        };
        var id = await SeedDurableScheduleAsync(blocked, slot, $"budget-park-failure-{Guid.NewGuid():N}");

        _faults.FailNextFor(id);

        var events = await EventsOfAsync(
            async () => await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)),
                CancellationToken.None),
            "could not be parked after its exclusion search");

        events.Single(e => e.Message.Contains("could not be parked after its exclusion search",
                  StringComparison.Ordinal))
              .Severity.ShouldBe(nameof(SeverityLevel.Error));
    }

    [Fact]
    public async Task Should_resume_a_recorded_exclusion_budget_advance_after_restart()
    {
        static void ConfigureRetry(EverTaskServiceConfiguration configuration) =>
            configuration.SetOccurrenceProviderRetry(retry =>
            {
                retry.InitialBackoff = TimeSpan.FromDays(1);
                retry.MaxBackoff     = TimeSpan.FromDays(1);
            });

        await StartHostAsync(configureEverTask: ConfigureRetry);

        var slot = Clock.GetUtcNow();
        var blocked = new RecurringTask
        {
            RunNow      = true,
            DayInterval = new DayInterval(7),
            MaxRuns     = 2,
            Exclusions  = new ScheduleExclusions { Days = [slot.DayOfWeek] }
        };
        var id = await SeedDurableScheduleAsync(blocked, slot, $"budget-restart-{Guid.NewGuid():N}");

        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);

        var recorded = await RowAsync(id);
        recorded.CurrentRunCount.ShouldBe(1);
        recorded.NextRunUtc.ShouldBe(slot);
        ScheduleRuntimeInfo.TryParse(recorded.RuntimeInfo)?.ExclusionAdvanceRetry.ShouldNotBeNull(
            "the memory-only delivery flag needs a persisted counterpart for startup recovery");

        var repaired = new RecurringTask
        {
            DayInterval = new DayInterval(1),
            MaxRuns     = 2
        };
        recorded.RecurringTask = EverTaskJson.Serialize(repaired);
        recorded.RecurringInfo = repaired.ToString();
        await _shared.UpdateTask(recorded);

        await StartHostAsync(configureEverTask: ConfigureRetry);
        await Task.Delay(500);

        var resumed = await RowAsync(id);
        resumed.CurrentRunCount.ShouldBe(1,
            "startup recovery resumes the recorded advance instead of granting its retained cursor grace");
        resumed.NextRunUtc.ShouldBe(slot);
        _recorder.Count.ShouldBe(1, "the recorded slot is never recovered as a pending delivery");
    }

    [Fact]
    public async Task Should_resume_a_recorded_exclusion_advance_against_the_restarted_calendar_union()
    {
        var slot = Clock.GetUtcNow();

        await StartHostAsync(configureEverTask: configuration =>
        {
            configuration.SetOccurrenceProviderRetry(retry =>
            {
                retry.InitialBackoff = TimeSpan.FromDays(1);
                retry.MaxBackoff = TimeSpan.FromDays(1);
            });
            configuration.AddScheduleCalendar(
                "closed", calendar => calendar.OnDays(slot.DayOfWeek));
        });
        var definition = new RecurringTask
        {
            RunNow = true,
            DayInterval = new DayInterval(7),
            MaxRuns = 2,
            Exclusions = new ScheduleExclusions { Calendars = ["closed"] }
        };
        var id = await SeedDurableScheduleAsync(definition, slot, $"calendar-budget-{Guid.NewGuid():N}");
        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);

        var recorded = await RowAsync(id);
        recorded.CurrentRunCount.ShouldBe(1);
        ScheduleRuntimeInfo.TryParse(recorded.RuntimeInfo)?.ExclusionAdvanceRetry.ShouldNotBeNull();

        await StartHostAsync(watchRegistrations: true, configureEverTask: configuration =>
        {
            configuration.SetOccurrenceProviderRetry(retry =>
            {
                retry.InitialBackoff = TimeSpan.FromDays(1);
                retry.MaxBackoff = TimeSpan.FromDays(1);
            });
            configuration.AddScheduleCalendar("closed", calendar => calendar.OnDates(
                DateOnly.FromDateTime(slot.AddDays(7).UtcDateTime)));
        });

        await TaskWaitHelper.WaitForConditionAsync(() => _registrations.Registrations.Any(registration =>
            registration.Id == id), 20000);
        _registrations.Registrations.Last(registration => registration.Id == id).At.ShouldBe(
            new DateTimeOffset(slot.AddDays(14).UtcDateTime.Date, TimeSpan.Zero));

        var resumed = await RowAsync(id);
        resumed.CurrentRunCount.ShouldBe(1);
        resumed.NextRunUtc.ShouldBe(slot);
        _recorder.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_reschedule_that_an_advance_beats_to_the_row_writes_nothing_and_publishes_nothing()
    {
        // The other half of the same compare-and-swap. When the reschedule wins, the advance re-aims and the
        // new definition stands; when it LOSES, nothing at all may happen — no definition written, no version
        // published, no registration replaced — and the caller has to be told which of the three things took
        // the row rather than being handed a bare false to guess at.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty, watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("advance-race"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-advance-race");

        var before = await RowAsync(id);
        var moved  = Clock.GetUtcNow().AddHours(4);

        // An ordinary advance landing inside the window the manager's own write is about to open. It moves the
        // cursor and the run counter and leaves the version exactly where it was, which is the whole reason
        // the guard keys on the cursor too.
        faulty.RunBefore(nameof(ITaskStorage.UpdateSchedule),
            () => _shared.UpdateCurrentRun(id, 12, moved, AuditLevel.Full, before.ScheduleVersion)
                         .GetAwaiter().GetResult());

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-advance-race", r => r.Schedule().Every(1).Seconds()));

        error.Message.ShouldContain("changed under this call");
        error.Message.ShouldContain("nothing was written");

        var row = await RowAsync(id);
        row.ScheduleVersion.ShouldBe(0, "a lost compare-and-swap writes nothing at all");
        row.NextRunUtc.ShouldBe(moved, "the advance's cursor is the one that stands");
        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.SecondInterval
                    .ShouldBeNull("the per-second definition the reschedule carried never reached the row");

        Versions.TryGetLatest(id, out _).ShouldBeFalse("a write that never landed publishes no lower bound");

        _registrations.Registrations.Any(r => r.Id == id && r.Version > 0).ShouldBeFalse(
            "and nothing was parked for a version the row does not carry");
    }

    [Fact]
    public async Task A_run_the_row_is_rewritten_under_at_every_attempt_is_still_recorded()
    {
        // The advance re-aims a bounded number of times, so a third party rewriting the row in a loop cannot
        // spin it for ever. What that bound must never cost is the RUN: the handler executed, and dropping its
        // write leaves the row in the InProgress the delivery set, the execution unaudited, the run counter —
        // and with it the MaxRuns budget — one short for ever, and the series parked nowhere until a restart.
        // Past the last re-aim the GUARD is given up, not the write.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("rewritten-at-every-attempt"),
            r => r.Schedule().Every(2).Seconds(), taskKey: "reschedule-rewritten");

        var basis   = Clock.GetUtcNow();
        var cursors = new[] { basis.AddHours(1), basis.AddHours(2), basis.AddHours(3) };
        var rewrites = 0;

        // One rewrite in front of each of the three attempts and none in front of the fourth call, which is
        // the unconditional one: the run therefore lands on the definition the third rewrite left behind.
        faulty.RunBefore(nameof(ITaskStorage.CompleteRecurringRun), () =>
        {
            var round = Interlocked.Increment(ref rewrites);

            if (round > cursors.Length)
                return;

            var row = _shared.Get(t => t.Id == id).GetAwaiter().GetResult()[0];

            _shared.UpdateSchedule(id, row.ScheduleVersion, row.NextRunUtc, row.RecurringTask!, row.RecurringInfo,
                       cursors[round - 1], row.MaxRuns, row.RunUntil, row.RuntimeInfo)
                   .GetAwaiter().GetResult();
        });

        var recorded = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].CurrentRunCount == 1, 20000);

        recorded[0].ScheduleVersion.ShouldBe(cursors.Length, "every attempt really was rewritten under");
        recorded[0].LastExecutionUtc.ShouldNotBeNull("the run happened, so it is on the row");
        recorded[0].NextRunUtc.ShouldBe(cursors[^1],
            "and the cursor written is the last owner's own, never this delivery's idea of what came next");

        // Parked from that row, not left in no scheduler: without it a schedule whose every attempt lost would
        // wait for a restart.
        await TaskWaitHelper.WaitForConditionAsync(() => Scheduler.IsScheduled(id), 10000);
        Scheduler.IsScheduled(id).ShouldBeTrue();

        var runs = _recorder.Count;
        await Task.Delay(2500);

        _recorder.Count.ShouldBe(runs, "the two-second grid the delivery came from is gone");
    }

    [Fact]
    public async Task A_rebase_is_refused_when_the_new_run_budget_is_already_spent()
    {
        await StartHostAsync(startHost: false);

        var definition = new RecurringTask
        {
            DayInterval = new DayInterval { Interval = 1, OnTimes = [new TimeOnly(9, 0)] },
            MaxRuns     = 10
        };

        var id = await SeedDurableScheduleAsync(definition, Clock.GetUtcNow().AddHours(1), "reschedule-budget");

        var row = await RowAsync(id);
        row.CurrentRunCount = 5;
        await _shared.UpdateTask(row);

        // The rebase reaches the grid through a question that applies RunUntil and nothing else, so without a
        // budget gate of its own it answers with a cursor for a definition that has no run left — and the
        // schedule runs a sixth time under a budget of three, where RecalculateFromNow refuses outright.
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-budget",
                r => r.Schedule().EveryDay().AtTime(new TimeOnly(10, 0)).MaxRuns(3),
                RescheduleMode.RebaseFromCursor));

        error.Message.ShouldContain("CancelSchedule");

        (await RowAsync(id)).ScheduleVersion.ShouldBe(0, "a refusal writes nothing");

        // The control: the same rebase with a budget the series has NOT spent goes through, so what was
        // refused is the budget and not the shape.
        var accepted = await Manager.Reschedule("reschedule-budget",
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(10, 0)).MaxRuns(20),
            RescheduleMode.RebaseFromCursor);

        accepted.ScheduleVersion.ShouldBe(1);
    }

    // ---- Durable schedules ------------------------------------------------------------------------

    [Fact]
    public async Task Recalculating_from_now_discards_a_durable_backlog_and_reports_it()
    {
        await StartHostAsync(startHost: false);

        var seededAt = Clock.GetUtcNow();
        var id       = await SeedDurableScheduleAsync(MinuteCatchUp(100), seededAt.AddMinutes(-5),
            "reschedule-backlog");

        await EventsOfAsync(async () =>
        {
            var result = await Manager.ReevaluateSchedule("reschedule-backlog");

            result.DiscardedBacklog.ShouldBe(6, "the cursor and the five minutes behind it were all owed");
            result.DiscardedBacklogIsExact.ShouldBeTrue("a plain cadence is counted by division, never walked");
            result.NextRunUtc!.Value.ShouldBeGreaterThan(seededAt);
        }, "6 due slot(s) were discarded");

        (await _shared.Get(t => t.ParentTaskId == id)).ShouldBeEmpty(
            "the backlog was dropped, not replayed");
    }

    [Fact]
    public async Task A_discarded_backlog_too_big_to_count_is_reported_as_a_lower_bound()
    {
        // The count that reaches the result and the event is bounded, because a three-month one-second backlog
        // must not be enumerated to produce a number nothing decides on. A bound that is REACHED may never be
        // dressed up as a total: eight days of a one-minute grid owe 11,520 slots, the count stops one past
        // the cap of ten thousand, and DiscardedBacklogIsExact is what says which of the two the caller holds.
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(100), Clock.GetUtcNow().AddDays(-8),
            "reschedule-lower-bound");

        await EventsOfAsync(async () =>
        {
            var result = await Manager.ReevaluateSchedule("reschedule-lower-bound");

            result.DiscardedBacklogIsExact.ShouldBeFalse("the count was truncated, and it says so");
            result.DiscardedBacklog.ShouldBe(10_001,
                "one past the cap, which is exactly what makes the number a lower bound and not a total");
        }, "at least 10001 due slot(s) were discarded");

        (await _shared.Get(t => t.ParentTaskId == id)).ShouldBeEmpty();
        (await RowAsync(id)).ScheduleVersion.ShouldBe(1, "the change itself is an ordinary one");
    }

    [Fact]
    public async Task The_event_a_reschedule_publishes_carries_the_whole_change()
    {
        // S5, in full: where the schedule stood and where it stands now, on both the version and the cursor,
        // the mode that decided it, and what it discarded. A subscriber given only the new version cannot tell
        // a first reschedule from a tenth, nor say what the change moved the series off.
        await StartHostAsync(startHost: false);

        var cursor = Clock.GetUtcNow().AddMinutes(-5);
        var id     = await SeedDurableScheduleAsync(MinuteCatchUp(100), cursor, "reschedule-event-contract");

        ScheduleUpdateResult? result = null;

        var events = await EventsOfAsync(async () => result = await Manager.ReevaluateSchedule(
                "reschedule-event-contract"),
            "rescheduled from version");

        var change = events.First(e => e.Message.Contains("rescheduled from version", StringComparison.Ordinal));

        change.Message.ShouldContain("rescheduled from version 0 to version 1");
        change.Message.ShouldContain($"({RescheduleMode.RecalculateFromNow})");
        change.Message.ShouldContain(cursor.ToString("O", CultureInfo.InvariantCulture), Case.Sensitive,
            "the cursor it was moved off is part of the change, not only the one it was moved to");
        change.Message.ShouldContain(result!.NextRunUtc!.Value.ToString("O", CultureInfo.InvariantCulture));
        change.Message.ShouldContain("6 due slot(s) were discarded");
        change.ScheduleVersion.ShouldBe(1);
        change.TaskId.ShouldBe(id);
    }

    [Fact]
    public async Task A_failed_repark_still_publishes_the_change_it_committed()
    {
        // The write commits before the parking is attempted, so a re-park that throws leaves the row at a new
        // version, a new cursor and a new definition all the same. Publishing only the failure left a
        // subscriber with no record of any of it — and the failure alone reads like a change that never
        // happened.
        await StartHostAsync(faultyScheduler: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("repark-event"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-repark-event");

        _faults.FailNextFor(id);

        ScheduleUpdateResult? result = null;

        var events = await EventsOfAsync(async () => result = await Manager.Reschedule(
                "reschedule-repark-event", r => r.Schedule().Every(2).Hours()),
            "rescheduled from version 0 to version 1", "could not be handed back to the scheduler");

        result!.ScheduleVersion.ShouldBe(1);
        (await RowAsync(id)).ScheduleVersion.ShouldBe(1, "the definition really is committed");

        events.First(e => e.Message.Contains("could not be handed back", StringComparison.Ordinal))
              .Severity.ShouldBe(nameof(SeverityLevel.Error));
    }

    [Fact]
    public async Task Resuming_a_halt_releases_it_and_the_backlog_it_was_holding_is_replayed()
    {
        // The other half of the contract phase 4 pinned. There a halt survives ageing, a restart and every
        // kick — nothing releases it on its own (M10). Here a PERSON releases it, and the point is not the
        // marker disappearing but the materialization really starting again over the backlog the marker was
        // holding back.
        //
        // A resume replans against the definition AS IT STANDS, so the only thing that can make the same
        // backlog fit under the same cap is the age window sliding over it — which is exactly the case phase 4
        // proved must NOT release the halt by itself. A series whose grid ends before now can only lose slots
        // to that window, never gain any, so the arithmetic is fixed: ten slots owed, seven of them out of the
        // window twelve minutes later, three left against a cap of five.
        var start = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        await StartHostAsync(startHost: false, clock: clock);

        var definition = MinuteCatchUp(5, maxPending: 10);
        definition.Misfire!.MaxAge = TimeSpan.FromMinutes(45);
        definition.RunUntil        = start.AddMinutes(-30);

        var cursor = start.AddMinutes(-40);
        var id     = await SeedDurableScheduleAsync(definition, cursor, "reschedule-resume-releases");

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(id, null);

        var halted = ScheduleRuntimeInfo.TryParse((await RowAsync(id)).RuntimeInfo)?.Halted;

        halted.ShouldNotBeNull("the premise: ten owed slots against a cap of five really did trip the breaker");
        halted.DetectedAtLeast.ShouldBe(10);
        (await _shared.Get(t => t.ParentTaskId == id)).ShouldBeEmpty("and a halt materializes nothing");

        clock.Advance(TimeSpan.FromMinutes(12));

        var resumed = await Manager.ResumeSchedule("reschedule-resume-releases");

        resumed.ReleasedHalt.ShouldBeTrue();
        resumed.NextRunUtc.ShouldBe(cursor, "a resume replans from where the schedule stands, it never skips");

        var slots = (await _shared.Get(t => t.ParentTaskId == id))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .ToArray();

        slots.ShouldBe([start.AddMinutes(-33), start.AddMinutes(-32), start.AddMinutes(-31)],
            "the release replays every slot the age window still allows, not merely the newest one");

        var row = await RowAsync(id);

        row.RuntimeInfo.ShouldBeNull("the marker is cleared, not rewritten: this backlog no longer overflows");
        row.NextRunUtc.ShouldBeNull("the grid had nothing left after the last replayed slot");
        row.Status.ShouldBe(QueuedTaskStatus.Completed,
            "so the series ends in the same commit that created that occurrence");

        // The materialization that ended the series ran INSIDE this call, and it drops the schedule's version
        // where a durable series really ends (S4). Publishing afterwards put the entry straight back, for a
        // schedule that will never run again — the one way the registry grows for the life of the process.
        Versions.TryGetLatest(id, out _).ShouldBeFalse(
            "a resume that ends the series leaves no version behind to publish a lower bound for");
    }

    [Fact]
    public async Task Resuming_a_halt_replans_the_same_backlog_and_halts_again_while_it_still_overflows()
    {
        await StartHostAsync(startHost: false);

        var cursor = Clock.GetUtcNow().AddMinutes(-40);
        var id     = await SeedDurableScheduleAsync(MinuteCatchUp(5), cursor, "reschedule-halt");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].RuntimeInfo != null, 20000);

        var firstHalt = ScheduleRuntimeInfo.TryParse((await RowAsync(id)).RuntimeInfo)!.Halted!;

        var resumed = await Manager.ResumeSchedule("reschedule-halt");

        resumed.ReleasedHalt.ShouldBeTrue();
        resumed.NextRunUtc.ShouldBe(cursor, "a resume keeps the cursor: the backlog is what has to be replanned");

        var secondHalt = ScheduleRuntimeInfo.TryParse(
            (await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
                rows => rows[0].RuntimeInfo != null, 20000))[0].RuntimeInfo)!.Halted!;

        secondHalt.ScheduleVersion.ShouldBeGreaterThan(firstHalt.ScheduleVersion,
            "the marker was really cleared and written again against the resumed version");
        (await _shared.Get(t => t.ParentTaskId == id)).ShouldBeEmpty(
            "the backlog still exceeds the cap, so nothing is replayed");
    }

    [Fact]
    public async Task A_rebase_of_a_halted_catch_up_keeps_its_backlog_and_a_wider_cap_replays_it()
    {
        await StartHostAsync(startHost: false);

        var cursor = Clock.GetUtcNow().AddMinutes(-6);
        var id     = await SeedDurableScheduleAsync(MinuteCatchUp(3), cursor, "reschedule-widen");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == id),
            rows => rows[0].RuntimeInfo != null, 20000);

        // The same cadence with a bigger cap: the shape is identical, so the cursor is carried over verbatim
        // and the backlog the halt was protecting is still there to replay.
        var result = await Manager.Reschedule("reschedule-widen",
            r => r.Schedule().Every(1).Minutes()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(1), 100)
                  {
                      MaxPendingOccurrences = 10
                  })),
            RescheduleMode.RebaseFromCursor);

        result.NextRunUtc.ShouldBe(cursor, "a plain cadence rebases onto the instant it was already at");
        result.ReleasedHalt.ShouldBeTrue();

        var occurrences = await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.ParentTaskId == id),
            rows => rows.Length >= 5, 20000);

        occurrences.Length.ShouldBeGreaterThanOrEqualTo(5, "the backlog the halt had held is replayed");
        (await RowAsync(id)).RuntimeInfo.ShouldBeNull("and the marker is gone");
    }

    // ---- Occurrences ------------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_occurrence_is_requeued_with_its_own_id_and_history()
    {
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddHours(1),
            "reschedule-requeue");

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = Clock.GetUtcNow().AddMinutes(-10),
            Type                  = typeof(RescheduleProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new RescheduleProbeTask("failed-occurrence")),
            Handler               = typeof(RescheduleProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Failed,
            Exception             = "it failed once",
            ParentTaskId          = id,
            ScheduledExecutionUtc = Clock.GetUtcNow().AddMinutes(-9),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };

        await _shared.Persist(occurrence);

        await Host!.StartAsync();

        (await Manager.RequeueFailedOccurrence(occurrence.Id)).ShouldBeTrue();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        var requeued = await RowAsync(occurrence.Id);
        requeued.Id.ShouldBe(occurrence.Id, "the identity is the whole point of requeuing instead of re-dispatching");
        requeued.Exception.ShouldBeNull();
        requeued.StatusAudits.Count.ShouldBeGreaterThan(0, "the audit trail is kept");

        (await Manager.RequeueFailedOccurrence(occurrence.Id)).ShouldBeFalse(
            "a row that is no longer terminal is not requeued twice");
    }

    [Fact]
    public async Task Requeuing_an_occurrence_does_not_spend_another_run_of_the_series()
    {
        await StartHostAsync(startHost: false);

        // Two runs, both already spent on materializations (M14): the series is over as a SCHEDULE, and the
        // occurrence that failed is a row of its own, so putting it back must not create a third.
        var definition = MinuteCatchUp(10);
        definition.MaxRuns = 2;

        var id = await SeedDurableScheduleAsync(definition, Clock.GetUtcNow().AddHours(1), "reschedule-maxruns");

        var row = await RowAsync(id);
        row.CurrentRunCount = 2;
        await _shared.UpdateTask(row);

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = Clock.GetUtcNow().AddMinutes(-10),
            Type                  = typeof(RescheduleProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new RescheduleProbeTask("spent")),
            Handler               = typeof(RescheduleProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Failed,
            ParentTaskId          = id,
            ScheduledExecutionUtc = Clock.GetUtcNow().AddMinutes(-9),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };

        await _shared.Persist(occurrence);
        await Host!.StartAsync();

        (await Manager.RequeueFailedOccurrence(occurrence.Id)).ShouldBeTrue();
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        await Host.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await RowAsync(id)).CurrentRunCount.ShouldBe(2,
            "MaxRuns counts materializations, and a requeue materializes nothing");
        (await _shared.Get(t => t.ParentTaskId == id)).Length.ShouldBe(1,
            "the spent run budget still ends the series");
    }

    [Fact]
    public async Task A_reschedule_and_a_dispatch_of_the_same_key_do_not_interleave()
    {
        // The two are the same read-decide-write over the same row, and UpdateTask carries no version — only
        // the shared per-taskKey section keeps the dispatch from overwriting the definition the reschedule
        // has just committed with the one it read before it.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        await Dispatcher.Dispatch(new RescheduleProbeTask("interleave"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-interleave");

        Task<Guid>? dispatch = null;

        faulty.RunBefore(nameof(ITaskStorage.UpdateSchedule), () =>
        {
            dispatch = Dispatcher.Dispatch(new RescheduleProbeTask("interleave"),
                r => r.Schedule().Every(3).Hours(), taskKey: "reschedule-interleave");

            // Long enough for an unserialized dispatch to have read the row and written it back.
            Thread.Sleep(500);

            dispatch.IsCompleted.ShouldBeFalse(
                "the dispatch is waiting on the section this reschedule is holding");
        });

        await Manager.Reschedule("reschedule-interleave", r => r.Schedule().Every(2).Hours());

        await dispatch!;

        var row        = await _shared.GetByTaskKey("reschedule-interleave");
        var definition = EverTaskJson.Deserialize<RecurringTask>(row!.RecurringTask!);

        definition!.HourInterval!.Interval.ShouldBe(3,
            "the dispatch ran after the reschedule, on the row the reschedule had written");
        row.ScheduleVersion.ShouldBe(1, "and it left the version the reschedule set, because it never writes one");
    }

    [Fact]
    public async Task An_occurrence_of_a_cancelled_schedule_is_not_requeued()
    {
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddMinutes(5),
            "reschedule-cancelled-parent");

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = Clock.GetUtcNow().AddMinutes(-10),
            Type                  = typeof(RescheduleProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new RescheduleProbeTask("cancelled-parent")),
            Handler               = typeof(RescheduleProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = id,
            ScheduledExecutionUtc = Clock.GetUtcNow().AddMinutes(-9),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };

        await _shared.Persist(occurrence);

        // The host stays stopped on purpose: this is the shape the finding describes, an operator calling the
        // admin API long after the cancel — or from a process that never issued it — and a started host would
        // only race its own recovery against the seeded row.
        await Manager.CancelSchedule("reschedule-cancelled-parent");

        (await RowAsync(occurrence.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.RequeueFailedOccurrence(occurrence.Id));

        error.Message.ShouldContain("cancelled");

        // The blacklist is no defence: its entries lapse after about an hour and never existed in a process
        // that did not issue the cancel, so what must not happen is the row going back to Queued at all.
        (await RowAsync(occurrence.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "a cancellation is terminal for every row under the schedule");
    }

    [Fact]
    public async Task An_id_no_row_carries_is_refused_by_a_requeue()
    {
        await StartHostAsync();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.RequeueFailedOccurrence(Guid.NewGuid()));

        error.Message.ShouldContain("There is no task with id");
    }

    [Fact]
    public async Task An_occurrence_whose_payload_cannot_be_rebuilt_is_refused_instead_of_queued()
    {
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddHours(1),
            "reschedule-unrebuildable");

        // A type this build can no longer load: the row is intact, and nothing that reads it can produce a
        // task to run. Requeuing it would put a row nobody can deliver back in the queue, where it would be
        // picked up, fail to resolve and end up exactly where it started.
        var occurrence = NewOccurrence(id, "unrebuildable", QueuedTaskStatus.Failed);
        occurrence.Type = "EverTask.Tests.ATypeThatIsGone, EverTask.Tests.AnAssemblyThatIsGone";

        await _shared.Persist(occurrence);

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.RequeueFailedOccurrence(occurrence.Id));

        error.Message.ShouldContain("cannot be rebuilt");

        (await RowAsync(occurrence.Id)).Status.ShouldBe(QueuedTaskStatus.Failed,
            "the executor is built BEFORE the requeue, so the row keeps the state an operator is looking at " +
            "instead of gaining a Queued transition and an audit row for a delivery that never happens");
    }

    [Fact]
    public async Task A_durable_series_that_ends_is_forgotten_by_the_version_registry()
    {
        // The entry is dropped when the schedule ends (S4), and a DURABLE series ends inside the
        // materialization that nulls its cursor — never through QueueNextOccourrence, which is where an
        // inline one drops it. Without that the entry of every durable schedule an operator had rescheduled
        // outlived its series for the life of the process.
        await StartHostAsync(startHost: false);

        var definition = MinuteCatchUp(10);
        definition.MaxRuns = 1;

        var id = await SeedDurableScheduleAsync(definition, Clock.GetUtcNow().AddHours(1), "reschedule-forgotten");

        // Publishes version 1 while the series is still alive: the recomputed cursor is a minute away, so the
        // materializer run this reschedule makes has nothing due to materialize.
        await Manager.ReevaluateSchedule("reschedule-forgotten");

        Versions.TryGetLatest(id, out _).ShouldBeTrue();

        // The row as the one occurrence its budget allows leaves it: the run is spent, so the next
        // materializer run ends the series in the same write instead of creating anything.
        var row = await RowAsync(id);
        row.CurrentRunCount = 1;
        await _shared.UpdateTask(row);

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Completed);
        (await RowAsync(id)).NextRunUtc.ShouldBeNull();

        Versions.TryGetLatest(id, out _).ShouldBeFalse(
            "a series that will not run again is no longer a version this process publishes a lower bound for");
    }

    [Fact]
    public async Task A_schedule_row_cannot_be_requeued_as_if_it_were_an_occurrence()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("not-an-occurrence"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-not-occurrence");

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.RequeueFailedOccurrence(id));

        error.Message.ShouldContain("not an occurrence");
    }

    [Fact]
    public async Task Cancelling_through_the_manager_stops_the_series_and_forgets_its_version()
    {
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancel"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-cancel");

        await Manager.Reschedule("reschedule-cancel", r => r.Schedule().Every(2).Hours());
        Versions.TryGetLatest(id, out _).ShouldBeTrue();

        await Manager.CancelSchedule("reschedule-cancel");

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);
        Versions.TryGetLatest(id, out _).ShouldBeFalse();

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-cancel", r => r.Schedule().Every(1).Hours()));

        error.Message.ShouldContain("terminal");
    }

    [Fact]
    public async Task A_removed_row_takes_its_published_version_with_it()
    {
        // S4 names three ends for a published version: a series that finishes, one that is cancelled, and a
        // row that is REMOVED. The third is the taskKey re-dispatch of a terminal one-shot, and it is the only
        // place the library deletes a row at all.
        //
        // Nothing in production can reach that branch holding a published version — a recurring row is refused
        // a one-shot re-dispatch before it ever gets there, so the row deleted here has never been a schedule
        // — which is why the entry is placed by hand. What is under test is the clause, not the premise: a
        // lower bound whose row no longer exists is one nothing can ever clear again.
        await StartHostAsync();

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("removed"), TimeSpan.FromHours(1),
            taskKey: "reschedule-removed");

        var row = await RowAsync(id);
        row.Status = QueuedTaskStatus.Completed;
        await _shared.UpdateTask(row);

        Versions.Publish(id, 4);

        var replacement = await Dispatcher.Dispatch(new RescheduleProbeTask("removed-again"),
            TimeSpan.FromHours(2), taskKey: "reschedule-removed");

        replacement.ShouldNotBe(id, "the terminal row was removed and a new one took the key");
        (await _shared.Get(t => t.Id == id)).ShouldBeEmpty();

        Versions.TryGetLatest(id, out _).ShouldBeFalse("and the version it had published went with it");
    }

    // ---- A superseded delivery never takes the series' registration away --------------------------

    [Fact]
    public async Task An_advance_that_won_its_compare_and_swap_still_leaves_a_newer_registration_alone()
    {
        await StartHostAsync(watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("advance-vs-repark"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-advance-repark");

        var old = await BuildExecutorAsync(await RowAsync(id));

        // The window the compare-and-swap does NOT cover: it owns the storage write, and the reschedule
        // commits, parks and publishes between that write and the registration that follows it.
        _registrations.ArmBeforeNextRegistrationOf(id, () =>
            Manager.Reschedule("reschedule-advance-repark", r => r.Schedule().Every(1).Seconds())
                   .GetAwaiter().GetResult());

        await WorkerExecutor.DoWork(old, CancellationToken.None);

        _registrations.Refused(id, 0).ShouldBeTrue(
            "the next occurrence of the definition that was just replaced must not be parked over the one " +
            "the reschedule published");
        _registrations.AcceptedAStaleRegistration(id).ShouldBeFalse();

        (await RowAsync(id)).ScheduleVersion.ShouldBe(1);
        Scheduler.IsScheduled(id).ShouldBeTrue("the series is parked, on the definition the operator asked for");

        // And it is the NEW grid that is parked: the old one was hourly, so a run per second is only possible
        // if the registration the reschedule made is the one that survived.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 20000);
    }

    [Fact]
    public async Task A_rate_limit_skip_landing_after_a_reschedule_leaves_the_new_registration_alone()
    {
        // The advance path that writes NOTHING to storage, so no compare-and-swap can speak for it: a
        // terminally rejected occurrence skips forward and re-parks the series from the definition it was
        // delivered with. Left unconditional, that registration replaced the one the reschedule had just
        // published — and the published version then dropped it the moment it fired, leaving the series in no
        // scheduler, no queue and no delivery until a restart.
        await StartHostAsync(startHost: false, watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new ThrottledRescheduleProbeTask("throttled-key"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-throttled");

        // Burns the one permit the key has for the hour: every later delivery is rejected past the horizon.
        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);
        _recorder.Count.ShouldBe(1, "the first delivery has budget and runs");

        var superseded = await BuildExecutorAsync(await RowAsync(id));
        superseded.ScheduleVersion.ShouldBe(0);

        _registrations.ArmBeforeNextRegistrationOf(id, () =>
            Manager.Reschedule("reschedule-throttled", r => r.Schedule().Every(2).Seconds())
                   .GetAwaiter().GetResult());

        await WorkerExecutor.DoWork(superseded, CancellationToken.None);

        _recorder.Count.ShouldBe(1, "the second delivery had no budget and never reached the handler");

        _registrations.Refused(id, 0).ShouldBeTrue(
            "the skipped occurrence belongs to the grid the reschedule replaced");
        _registrations.AcceptedAStaleRegistration(id).ShouldBeFalse();

        Scheduler.IsScheduled(id).ShouldBeTrue("the series is still parked, on the definition just written");

        var row = await RowAsync(id);
        row.ScheduleVersion.ShouldBe(1);
        row.RecurringTask!.ShouldContain("SecondInterval");
    }

    [Fact]
    public async Task A_rate_limit_deferral_landing_after_a_reschedule_does_not_unschedule_the_series()
    {
        // The gate's set-then-check drops ITS OWN registration when the invalidation epoch moved while it was
        // holding the task — and a reschedule moves that epoch without ever unscheduling anything. With the
        // deferral's re-park replacing the reschedule's registration, "our registration is still parked" was
        // true of a registration that was not ours at all, and the conditional unschedule deleted the only
        // one the series had.
        await StartHostAsync(startHost: false, watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new DeferrableRescheduleProbeTask("deferrable-key"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-deferred");

        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);
        _recorder.Count.ShouldBe(1);

        var held = await BuildExecutorAsync(await RowAsync(id));

        _registrations.ArmBeforeNextRegistrationOf(id, () =>
            Manager.Reschedule("reschedule-deferred", r => r.Schedule().Every(2).Seconds())
                   .GetAwaiter().GetResult());

        await WorkerExecutor.DoWork(held, CancellationToken.None);

        _registrations.Refused(id, 0).ShouldBeTrue("the deferral carries the definition that was replaced");
        Scheduler.IsScheduled(id).ShouldBeTrue(
            "the reschedule's registration is the series' only one, and the deferral must not take it away");

        Versions.TryGetLatest(id, out var version).ShouldBeTrue();
        version.ShouldBe(1);
    }

    [Fact]
    public async Task A_delivery_dropped_as_superseded_leaves_the_series_parked_on_its_new_definition()
    {
        // Dropping is only safe while something else holds the series. Nothing asserted that before, and the
        // three paths above are exactly the ones that could take the registration away first.
        await StartHostAsync(watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("superseded-drop"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-superseded-drop");

        var old = await BuildExecutorAsync(await RowAsync(id));

        await Manager.Reschedule("reschedule-superseded-drop", r => r.Schedule().Every(1).Seconds());

        await WorkerExecutor.DoWork(old, CancellationToken.None);

        _recorder.RunNumbers.ShouldBeEmpty("the superseded delivery ran no handler");
        Scheduler.IsScheduled(id).ShouldBeTrue("and it left the series parked where the reschedule put it");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);
    }

    [Fact]
    public async Task A_schedule_turned_inline_by_a_failed_repark_is_parked_by_its_old_durable_delivery()
    {
        // Durable to inline, with the hand-over lost in between. The old durable delivery is the only thing
        // left holding the series — the scheduler consumed the registration that produced it — and its
        // materializer used to read "the row is inline now" as "somebody else has parked it", which is the
        // one assumption a failed re-park invalidates.
        await StartHostAsync(faultyScheduler: true);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(100), Clock.GetUtcNow().AddHours(1),
            "reschedule-durable-to-inline");

        var durable = await BuildExecutorAsync(await RowAsync(id));
        durable.IsScheduleOnly.ShouldBeTrue();

        _faults.FailNextFor(id);

        await Manager.Reschedule("reschedule-durable-to-inline", r => r.Schedule().Every(1).Seconds());

        Versions.TryGetLatest(id, out _).ShouldBeFalse("a re-park that threw publishes nothing");
        Scheduler.IsScheduled(id).ShouldBeFalse("and leaves the row parked nowhere");

        await WorkerExecutor.DoWork(durable, CancellationToken.None);

        Scheduler.IsScheduled(id).ShouldBeTrue(
            "the old durable delivery found an inline row nothing was holding and parked it");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);
    }

    [Fact]
    public async Task A_rate_limit_skip_that_ends_the_series_leaves_a_reschedule_that_extended_it_alone()
    {
        // The one advance in the tree left without a compare-and-swap: a skip writes nothing, so when it is
        // the LAST one — the limiter's next slot falls past the series' own RunUntil — the terminal
        // Completed and the null cursor were written unconditionally. An operator extending the bound while
        // that delivery waited at the gate therefore had their live schedule declared over, on a row that
        // then satisfies neither recovery predicate: not even a restart brings the series back, and the
        // audit trail says it ended normally.
        await StartHostAsync(startHost: false, faultyScheduler: true);

        var id = await Dispatcher.Dispatch(new ThrottledRescheduleProbeTask("throttled-end-key"),
            r => r.Schedule().Every(1).Seconds().RunUntil(Clock.GetUtcNow().AddMinutes(10)),
            taskKey: "reschedule-throttled-end");

        // Burns the one permit the key has for the hour: the next delivery is rejected past the horizon, and
        // the slot the limiter offers instead is an hour away — past the bound this definition carries.
        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);
        _recorder.Count.ShouldBe(1);

        var superseded = await BuildExecutorAsync(await RowAsync(id));
        superseded.ScheduleVersion.ShouldBe(0);

        // The re-park is made to fail, which is what publishes no version — the only thing that lets a
        // delivery of the replaced definition reach the row at all (S4).
        _faults.FailNextFor(id);

        var extended = await Manager.Reschedule("reschedule-throttled-end",
            r => r.Schedule().Every(1).Seconds().RunUntil(Clock.GetUtcNow().AddDays(30)));

        Versions.TryGetLatest(id, out _).ShouldBeFalse("a re-park that threw publishes nothing");

        await WorkerExecutor.DoWork(superseded, CancellationToken.None);

        var row = await RowAsync(id);

        row.NextRunUtc.ShouldBe(extended.NextRunUtc,
            "the series the operator just extended is not this delivery's to end: finalizing clears the cursor");
        row.ScheduleVersion.ShouldBe(1);

        // The shape a finalization would have left: Completed with no cursor answers NEITHER recovery
        // predicate, so the extended series would be dead for good — no restart, no recovery, and a status
        // audit reading like an ordinary end of series.
        row.IsRecoverableForExecution(Clock.GetUtcNow().AddDays(1)).ShouldBeTrue(
            "the row a restart has to find is still there");

        Scheduler.IsScheduled(id).ShouldBeTrue(
            "and the skip parked the row from itself instead of leaving it in no scheduler at all");
    }

    [Fact]
    public async Task A_failed_repark_leaves_the_gate_epoch_alone_so_the_deferral_it_stranded_parks_the_series()
    {
        // The gate's set-then-check deletes ITS OWN registration when the invalidation epoch moved while it
        // held the task. Bumping that epoch before the new registration exists made the two steps disagree: a
        // re-park that threw created nothing, so the deferral's own registration — the last thing holding the
        // series — was the one the check deleted, and the schedule sat in no scheduler, no queue and no
        // delivery until a restart.
        await StartHostAsync(startHost: false, faultyScheduler: true, watchRegistrations: true);

        var id = await Dispatcher.Dispatch(new DeferrableRescheduleProbeTask("deferrable-stranded-key"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-deferred-stranded");

        await WorkerExecutor.DoWork(await BuildExecutorAsync(await RowAsync(id)), CancellationToken.None);
        _recorder.Count.ShouldBe(1);

        var held = await BuildExecutorAsync(await RowAsync(id));

        // Armed on the registration the DEFERRAL is about to make, so the reschedule lands after the gate has
        // already captured its epoch — the window the epoch exists for.
        _registrations.ArmBeforeNextRegistrationOf(id, () =>
        {
            _faults.FailNextFor(id);
            Manager.Reschedule("reschedule-deferred-stranded", r => r.Schedule().Every(2).Seconds())
                   .GetAwaiter().GetResult();
        });

        await WorkerExecutor.DoWork(held, CancellationToken.None);

        Versions.TryGetLatest(id, out _).ShouldBeFalse("the re-park threw, so nothing was published");

        Scheduler.IsScheduled(id).ShouldBeTrue(
            "the deferral's own registration is all the series has left, and nothing may delete it");

        (await RowAsync(id)).ScheduleVersion.ShouldBe(1, "the new definition is committed all the same");
    }

    [Fact]
    public async Task A_reschedule_that_a_cancel_beats_to_the_row_does_not_overwrite_the_cancellation()
    {
        // The compare-and-swap keys on the version and the cursor, and a cancel touches NEITHER: it writes
        // the status and nothing else. A reschedule that read the row first therefore matched on both and
        // committed a live definition, a fresh cursor and a new version over a series an operator had just
        // ended — then reported success, while the blacklist quietly dropped every delivery it produced.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(storage: faulty);

        var id = await Dispatcher.Dispatch(new RescheduleProbeTask("cancel-race"),
            r => r.Schedule().Every(1).Hours(), taskKey: "reschedule-cancel-race");

        faulty.RunBefore(nameof(ITaskStorage.UpdateSchedule),
            () => Dispatcher.Cancel(id).GetAwaiter().GetResult());

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-cancel-race", r => r.Schedule().Every(1).Seconds()));

        error.Message.ShouldContain("cancelled under this call");

        var row = await RowAsync(id);
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled, "the cancellation stands");
        row.ScheduleVersion.ShouldBe(0, "and the reschedule wrote nothing at all");
        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.SecondInterval
                    .ShouldBeNull("the per-second definition the reschedule carried was never written");

        Versions.TryGetLatest(id, out _).ShouldBeFalse("a write that never landed publishes no version");

        var runs = _recorder.Count;
        await Task.Delay(2000);

        _recorder.Count.ShouldBe(runs, "and the series really is over");
    }

    [Fact]
    public async Task An_occurrence_cancelled_by_its_own_id_is_really_delivered_when_it_is_requeued()
    {
        // Cancel(occurrenceId) leaves a blacklist entry that lives about an hour and that only a delivery
        // reaching WorkerExecutor ever consumes. Requeuing put the row back to Queued and handed it to the
        // scheduler, whose enqueue WorkerQueue then dropped on that very entry — the registration consumed,
        // nothing queued, nothing delivering, and true returned to the caller.
        await StartHostAsync(startHost: false);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddHours(1),
            "reschedule-cancelled-occurrence");

        var occurrence = NewOccurrence(id, "cancelled-occurrence", QueuedTaskStatus.Queued);
        await _shared.Persist(occurrence);

        await Host!.StartAsync();

        // ONE occurrence cancelled, not the series: the schedule stays live, so nothing refuses the requeue.
        await Dispatcher.Cancel(occurrence.Id);
        (await RowAsync(occurrence.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);

        (await Manager.RequeueFailedOccurrence(occurrence.Id)).ShouldBeTrue();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        _recorder.Count.ShouldBeGreaterThanOrEqualTo(1,
            "answering true is not the fact under test: the occurrence has to actually run");

        Host.Services.GetRequiredService<IWorkerBlacklist>().IsBlacklisted(occurrence.Id)
            .ShouldBeFalse("requeuing IS the decision to run it again, so its own cancellation is undone");
    }

    [Fact]
    public async Task A_requeue_a_cancel_of_the_schedule_overtakes_is_undone_instead_of_left_live()
    {
        // The parent check and the requeue are two round trips, and a Failed occurrence is not in the pending
        // set a cancel cascades to — so a cancel landing between them was caught by nobody: the series ended
        // terminal with one Queued child under it, and the API answered true.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await StartHostAsync(startHost: false, storage: faulty);

        var id = await SeedDurableScheduleAsync(MinuteCatchUp(10), Clock.GetUtcNow().AddHours(1),
            "reschedule-requeue-vs-cancel");

        var occurrence = NewOccurrence(id, "requeue-vs-cancel", QueuedTaskStatus.Failed);
        await _shared.Persist(occurrence);

        faulty.RunBefore(nameof(ITaskStorage.RequeueTerminal),
            () => Manager.CancelSchedule("reschedule-requeue-vs-cancel").GetAwaiter().GetResult());

        (await Manager.RequeueFailedOccurrence(occurrence.Id)).ShouldBeFalse(
            "the schedule was ended while this call was in flight, so the occurrence is not delivered");

        (await RowAsync(id)).Status.ShouldBe(QueuedTaskStatus.Cancelled);
        (await RowAsync(occurrence.Id)).Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "and no row under a cancelled schedule is left non-terminal");

        await Host!.StartAsync();
        await Task.Delay(1500);

        _recorder.Count.ShouldBe(0, "not even a restart's recovery finds anything to run");
    }

    // ---- Bounds the two modes have to agree on ----------------------------------------------------

    [Fact]
    public async Task Winding_a_series_down_with_RunUntil_is_refused_by_both_modes()
    {
        await StartHostAsync(startHost: false);

        var definition = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var id         = await SeedDurableScheduleAsync(definition, Clock.GetUtcNow().AddSeconds(30),
            "reschedule-wind-down");

        // A bound the builder accepts — it refuses one in the past outright — that the cursor has already
        // gone past. A plain cadence keeps its cursor verbatim, so nothing on that path ever asks the grid,
        // and the grid is the only thing that applies RunUntil: the rebase used to accept a cursor beyond the
        // end the operator had just set and run the handler once more, while RecalculateFromNow refused the
        // very same definition.
        var bound = Clock.GetUtcNow().AddSeconds(5);

        var rebased = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-wind-down",
                r => r.Schedule().Every(30).Seconds().RunUntil(bound), RescheduleMode.RebaseFromCursor));

        rebased.Message.ShouldContain("RunUntil is exclusive");

        var recalculated = await Should.ThrowAsync<InvalidOperationException>(() =>
            Manager.Reschedule("reschedule-wind-down", r => r.Schedule().Every(30).Seconds().RunUntil(bound)));

        recalculated.Message.ShouldContain("no occurrence left to run");

        (await RowAsync(id)).ScheduleVersion.ShouldBe(0, "neither refusal writes anything");
    }

    // ---- Test doubles -----------------------------------------------------------------------------

    /// <summary>
    /// The worker's logger, kept for one thing: it says when the startup recovery is over.
    /// </summary>
    /// <remarks>
    /// The recovery has no other completion signal — it is a task nothing hands back — and its terminal log
    /// line is the real one, written by the recovery itself at the end of the pass. Every way
    /// <c>ProcessPendingAsync</c> can end counts: completed, completed with failures, cancelled by a shutdown,
    /// no persistence at all, or thrown (<c>WorkerServiceLog</c> 1104, 1105, 1115, 1117, 1118). A test that
    /// waited only for the happy one would hang on the very hosts whose storage it made fail.
    /// </remarks>
    private sealed class StartupRecoveryWatch : IEverTaskLogger<WorkerService>
    {
        private static readonly int[] RecoveryEnded = [1104, 1105, 1115, 1117, 1118];

        private readonly TaskCompletionSource _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Finished => _finished.Task;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (Array.IndexOf(RecoveryEnded, eventId.Id) >= 0)
                _finished.TrySetResult();
        }

        // Unconditionally enabled: the generated log methods check this first, and a false here would drop
        // the very line this watch exists to see.
        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    /// <summary>
    /// Which schedule the next <c>Schedule()</c> must fail for. Armed per id, so only the re-park under test
    /// throws and everything else the host does keeps working.
    /// </summary>
    private sealed class ScheduleFaultInjector
    {
        private readonly HashSet<Guid> _armed = [];

        public void FailNextFor(Guid taskId)
        {
            lock (_armed) _armed.Add(taskId);
        }

        public bool ShouldFail(Guid taskId)
        {
            lock (_armed) return _armed.Remove(taskId);
        }
    }

    /// <summary>The REAL scheduler with one armed <c>Schedule()</c> throwing; everything else is forwarded.</summary>
    /// <remarks>
    /// It fires the registration watch's armed hook too when a test supplies one, which is what lets a
    /// reschedule land inside a delivery AND have its own re-park fail — the two halves of the same window.
    /// </remarks>
    private sealed class ScheduleFaultingScheduler(PeriodicTimerScheduler inner, ScheduleFaultInjector faults,
                                                   RegistrationWatch? watch = null)
        : IScheduler, IDisposable
    {
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            watch?.FireIfArmed(item.PersistenceId);

            if (faults.ShouldFail(item.PersistenceId))
                throw new InvalidOperationException("the scheduler refused this registration");

            inner.Schedule(item, nextRecurringRun);
            watch?.Record(item.PersistenceId, item.ScheduleVersion, true, nextRecurringRun);
        }

        public bool TrySchedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            watch?.FireIfArmed(item.PersistenceId);

            if (faults.ShouldFail(item.PersistenceId))
                throw new InvalidOperationException("the scheduler refused this registration");

            // Forwarded, never degraded to the interface default: a wrapper that answered "scheduled" for a
            // registration the real scheduler would refuse would hide exactly what these tests are about.
            var accepted = inner.TrySchedule(item, nextRecurringRun);
            watch?.Record(item.PersistenceId, item.ScheduleVersion, accepted, nextRecurringRun);

            return accepted;
        }

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public bool IsScheduled(Guid persistenceId) => inner.IsScheduled(persistenceId);

        public bool SupportsScheduleInspection => inner.SupportsScheduleInspection;

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// What the scheduler was asked to register, what it answered, and one armed hook that runs just before a
    /// chosen registration reaches it.
    /// </summary>
    private sealed class RegistrationWatch
    {
        private readonly object  _gate = new();
        private          Guid    _armedFor;
        private          Action? _hook;

        public ConcurrentQueue<(Guid Id, int Version, bool Accepted, DateTimeOffset? At)> Registrations { get; } = new();

        /// <summary>Runs <paramref name="hook"/> ONCE, just before the next registration of that task.</summary>
        public void ArmBeforeNextRegistrationOf(Guid taskId, Action hook)
        {
            lock (_gate)
            {
                _armedFor = taskId;
                _hook     = hook;
            }
        }

        public void FireIfArmed(Guid taskId)
        {
            Action? hook;

            // Taken out under the lock BEFORE it runs: the hook reschedules, which registers again through
            // this same wrapper, and a hook that could re-enter itself would never return.
            lock (_gate)
            {
                if (taskId != _armedFor)
                    return;

                hook  = _hook;
                _hook = null;
            }

            hook?.Invoke();
        }

        public void Record(Guid taskId, int version, bool accepted, DateTimeOffset? at) =>
            Registrations.Enqueue((taskId, version, accepted, at));

        public bool Refused(Guid taskId, int version) =>
            Registrations.Any(r => r.Id == taskId && r.Version == version && !r.Accepted);

        /// <summary>
        /// True when a registration older than one already accepted was let through after it — the shape every
        /// one of these findings ends in, whatever produced the stale executor.
        /// </summary>
        public bool AcceptedAStaleRegistration(Guid taskId)
        {
            var highest = int.MinValue;

            foreach (var version in Registrations.Where(r => r.Id == taskId && r.Accepted).Select(r => r.Version))
            {
                if (version < highest)
                    return true;

                highest = version;
            }

            return false;
        }
    }

    /// <summary>The REAL scheduler, with every registration recorded and the armed hook fired before it.</summary>
    private sealed class RegistrationWatchingScheduler(PeriodicTimerScheduler inner, RegistrationWatch watch)
        : IScheduler, IDisposable
    {
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            watch.FireIfArmed(item.PersistenceId);
            inner.Schedule(item, nextRecurringRun);
            watch.Record(item.PersistenceId, item.ScheduleVersion, true, nextRecurringRun);
        }

        public bool TrySchedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            watch.FireIfArmed(item.PersistenceId);

            var accepted = inner.TrySchedule(item, nextRecurringRun);
            watch.Record(item.PersistenceId, item.ScheduleVersion, accepted, nextRecurringRun);

            return accepted;
        }

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public bool IsScheduled(Guid persistenceId) => inner.IsScheduled(persistenceId);

        public bool SupportsScheduleInspection => inner.SupportsScheduleInspection;

        public void Dispose() => inner.Dispose();
    }
}
