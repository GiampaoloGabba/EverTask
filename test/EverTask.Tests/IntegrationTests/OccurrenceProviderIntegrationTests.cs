using System.Collections.Concurrent;
using System.Globalization;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Scheduler;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Occurrence providers end to end, on a real host over a real storage: a schedule whose grid the fluent API
/// cannot express takes its slots from a registered <see cref="INextOccurrenceProvider"/>, and everything
/// above that seam — the dispatch, the advance after a run, the recovery, a catch-up, the schedule manager —
/// works without knowing one exists.
/// </summary>
/// <remarks>
/// The provider is the REAL one, resolved from the container in its own scope on every question; what a test
/// steers is the calendar behind it (<see cref="OccurrenceProviderProbe"/>), which is what an application
/// would read from its own database. The storage instance is shared across the hosts a test builds, so
/// "restart" means the same rows in a new process.
/// </remarks>
[Collection("TimingSensitiveTests")]
public class OccurrenceProviderIntegrationTests : IsolatedIntegrationTestBase
{
    private const string Key              = "probe";
    private const string DeterministicKey = "probe-deterministic";
    private const string FragileKey       = "probe-fragile";
    private const string AsyncScopeKey    = "probe-async-scope";

    private readonly MemoryTaskStorage _shared = new(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>());
    private readonly DurableOccurrenceRecorder _recorder = new();
    private readonly OccurrenceProviderProbe _probe = new();

    private Task<IHost> StartHostAsync(bool startHost = true,
                                       Action<EverTaskServiceConfiguration>? configure = null,
                                       Action<EverTaskServiceBuilder>? extra = null) =>
        CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(_probe);

                // Scoped and async-disposable, like the DbContext or repository the docs describe: it is
                // built and released with the scope each provider call gets.
                b.Services.AddSingleton<ProviderScopeLedger>();
                b.Services.AddScoped<AsyncOnlyCalendar>();

                b.AddOccurrenceProvider<ProbeOccurrenceProvider>(Key)
                 .AddOccurrenceProvider<DeterministicProbeProvider>(DeterministicKey)
                 .AddOccurrenceProvider<FragileProbeProvider>(FragileKey)
                 .AddOccurrenceProvider<AsyncCalendarProvider>(AsyncScopeKey);

                extra?.Invoke(b);
            },
            startHost,
            configure ?? (cfg => cfg.SetOccurrenceProviderRetry(retry =>
            {
                // The wait is what a test would otherwise sit through: the behaviour under test is that the
                // schedule comes back on its own, not how long it waits.
                retry.InitialBackoff = TimeSpan.FromSeconds(1);
                retry.MaxBackoff     = TimeSpan.FromSeconds(2);
            })));

    private static MisfireSettings SkipOldest(int maxOccurrences) =>
        CatchUp(maxOccurrences, CatchUpOverflowPolicy.SkipOldest);

    /// <summary>The real scheduler with every re-park of a stalled schedule refused, one way or the other.</summary>
    private Action<EverTaskServiceBuilder> RefusingScheduler(bool byThrowing) =>
        b => b.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new ReParkRefusingScheduler(
            new PeriodicTimerScheduler(
                sp.GetRequiredService<IWorkerQueueManager>(),
                sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                TimeSpan.FromMilliseconds(50),
                null,
                sp.GetRequiredService<TimeProvider>()),
            _probe, byThrowing)));

    private ReParkRefusingScheduler Scheduler => (ReParkRefusingScheduler)Host!.Services.GetRequiredService<IScheduler>();

    private static bool SaysParkedToAskAgain(EverTaskEventData data) =>
        data.Message?.Contains("parked to ask again at", StringComparison.Ordinal) == true;

    /// <summary>
    /// A backoff long enough that the schedule under test cannot come back and record a second failure while
    /// the assertions read what the first one reported.
    /// </summary>
    private static readonly Action<EverTaskServiceConfiguration> SlowRetry = cfg =>
        cfg.SetOccurrenceProviderRetry(retry =>
        {
            retry.InitialBackoff = TimeSpan.FromMinutes(5);
            retry.MaxBackoff     = TimeSpan.FromMinutes(5);
        });

    /// <summary>
    /// What a subscriber is told when a schedule is waiting on its calendar (V4): a WARNING naming the
    /// provider, the schedule, how many consecutive failures it has and when it will ask again.
    /// </summary>
    /// <param name="wroteNothing">
    /// The half of the sentence that says which branch reported it — an advance and a recovery write nothing,
    /// a materialization materializes nothing.
    /// </param>
    private static void AssertWaitReported(EverTaskEventData reported, Guid scheduleId, string wroteNothing)
    {
        reported.Severity.ShouldBe(nameof(SeverityLevel.Warning),
            "a series that will come back on its own is a warning, never an error");
        reported.Message.ShouldContain($"'{Key}'");
        reported.Message.ShouldContain(scheduleId.ToString());
        // The counter is what tells a hiccup from an outage that has been going on for a while.
        reported.Message.ShouldContain("(1 consecutive failure(s))", Case.Sensitive);
        reported.Message.ShouldContain(wroteNothing, Case.Sensitive);

        // The instant is the backoff applied to now, so it is in the future and it is really five minutes out
        // rather than the moment the failure happened.
        var parkedAt = reported.Message!.Split("parked to ask again at ", StringSplitOptions.None)[1].TrimEnd();

        DateTimeOffset.Parse(parkedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                      .ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(4));
    }

    private Task<QueuedTask> RowOfAsync(Guid id) =>
        _shared.Get(t => t.Id == id).ContinueWith(t => t.Result[0], TaskScheduler.Default);

    private Task<QueuedTask[]> OccurrencesOfAsync(Guid scheduleId) =>
        _shared.Get(t => t.ParentTaskId == scheduleId);

    private async Task<Guid> SeedScheduleAsync(RecurringTask definition, DateTimeOffset cursor,
                                               string? taskKey = null,
                                               QueuedTaskStatus status = QueuedTaskStatus.Queued,
                                               int recoveryFailures = 0)
    {
        var row = new QueuedTask
        {
            TaskKey                      = taskKey,
            Id                           = Guid.NewGuid(),
            CreatedAtUtc                 = Clock.GetUtcNow().AddHours(-1),
            Type                         = typeof(ProviderScheduleTask).AssemblyQualifiedName!,
            Request                      = EverTaskJson.Serialize(new ProviderScheduleTask("seeded")),
            Handler                      = typeof(ProviderScheduleTaskHandler).AssemblyQualifiedName!,
            Status                       = status,
            IsRecurring                  = true,
            RecurringTask                = EverTaskJson.Serialize(definition),
            RecurringInfo                = definition.ToString(),
            NextRunUtc                   = cursor,
            CurrentRunCount              = 0,
            MaxRuns                      = definition.MaxRuns,
            RunUntil                     = definition.RunUntil,
            QueueName                    = QueueNames.Recurring,
            AuditLevel                   = (int)AuditLevel.Full,
            RecoveryDispatchFailureCount = recoveryFailures
        };

        await _shared.Persist(row);
        return row.Id;
    }

    private static RecurringTask ProviderDefinition(string key = Key, MisfireSettings? misfire = null,
                                                    OccurrenceMode mode = OccurrenceMode.Inline) => new()
    {
        Provider       = new ProviderSettings { Key = key },
        OccurrenceMode = mode,
        Misfire        = misfire
    };

    private static MisfireSettings CatchUp(int maxOccurrences,
                                           CatchUpOverflowPolicy overflow = CatchUpOverflowPolicy.Halt) => new()
    {
        Policy                = MisfirePolicy.CatchUp,
        MaxAge                = TimeSpan.FromHours(1),
        MaxOccurrences        = maxOccurrences,
        OverflowPolicy        = overflow,
        MaxPendingOccurrences = 1
    };

    // ---- The ordinary life of a provider-driven schedule ------------------------------------------

    [Fact]
    public async Task Every_run_of_a_provider_schedule_lands_on_the_slot_the_provider_answered()
    {
        await StartHostAsync();

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("live"),
            r => r.Schedule().UseOccurrenceProvider(Key).MaxRuns(3));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 20000);

        var executions = _recorder.Snapshot();
        executions.Length.ShouldBeGreaterThanOrEqualTo(3);

        foreach (var execution in executions.Take(3))
        {
            execution.SlotUtc.ShouldNotBeNull();
            (execution.SlotUtc!.Value.UtcTicks % TimeSpan.TicksPerSecond)
                .ShouldBe(0, "the grid the provider answers on is what the schedule fires on");
        }

        executions.Take(3).Select(e => e.RunNumber).ShouldBe([1, 2, 3]);

        // Every question carried the schedule's own identity, which is what a provider keys its calendar on.
        var requests = _probe.Snapshot();
        requests.ShouldAllBe(r => r.ProviderKey == Key);
        requests.ShouldContain(r => r.ScheduleId == scheduleId);
    }

    [Fact]
    public async Task A_provider_with_nothing_left_to_answer_ends_the_series()
    {
        await StartHostAsync();

        var only = Clock.GetUtcNow().AddSeconds(1);

        _probe.Answer = request => request.AfterUtc < only ? only : null;

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("last"),
            r => r.Schedule().UseOccurrenceProvider(Key));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);
        await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.Status == QueuedTaskStatus.Completed, 20000);

        var schedule = await RowOfAsync(scheduleId);
        schedule.NextRunUtc.ShouldBeNull("a null answer is how a provider says the series is over");
        schedule.CurrentRunCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_key_no_provider_answers_to_is_refused_while_the_caller_is_still_holding_the_dispatch()
    {
        await StartHostAsync();

        var refusal = await Should.ThrowAsync<ArgumentException>(async () =>
            await Dispatcher.Dispatch(new ProviderScheduleTask("unknown"),
                r => r.Schedule().UseOccurrenceProvider("nobody-registers-this")));

        refusal.Message.ShouldContain("nobody-registers-this");

        (await _shared.Get(t => true)).ShouldBeEmpty("nothing is persisted for a schedule that was refused");
    }

    [Fact]
    public async Task A_row_naming_a_provider_this_build_no_longer_registers_is_poisoned_by_recovery()
    {
        await StartHostAsync(startHost: false);

        var scheduleId = await SeedScheduleAsync(ProviderDefinition("gone-in-this-build"),
            Clock.GetUtcNow().AddSeconds(-30));

        await Host!.StartAsync();

        // Corrupt schedule metadata, exactly like an unparseable cron: terminal, and the cursor cleared with
        // it, so no restart brings the row back to be re-poisoned.
        var poisoned = await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.Status == QueuedTaskStatus.Failed, 20000);

        poisoned.NextRunUtc.ShouldBeNull();
        _recorder.Count.ShouldBe(0);
    }

    // ---- A provider that cannot answer ------------------------------------------------------------

    [Fact]
    public async Task A_provider_that_cannot_answer_at_dispatch_says_so_to_the_caller()
    {
        await StartHostAsync();

        _probe.Fault = () => new InvalidOperationException("the holiday table is unreachable");

        var failure = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await Dispatcher.Dispatch(new ProviderScheduleTask("down"),
                r => r.Schedule().UseOccurrenceProvider(Key)));

        failure.ProviderKey.ShouldBe(Key);
        failure.InnerException.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task A_provider_that_cannot_answer_during_recovery_leaves_the_row_alone_and_asks_again()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer        = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));
        _probe.FailNextCalls = 1;

        var cursor     = Clock.GetUtcNow().AddSeconds(-30);
        var scheduleId = await SeedScheduleAsync(ProviderDefinition(), cursor);

        await Host!.StartAsync();

        // The first question fails, so the recovery writes NOTHING and parks the schedule to ask again. What
        // it must not do is count that against the poison budget: a calendar that is down across five restarts
        // would mark the series Failed for ever.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 30000);

        var schedule = await RowOfAsync(scheduleId);
        schedule.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        (schedule.RecoveryDispatchFailureCount ?? 0)
            .ShouldBe(0, "a provider that could not answer is not a failed re-dispatch");

        _probe.Calls.ShouldBeGreaterThan(1, "the schedule really came back and asked again");
    }

    [Fact]
    public async Task A_provider_that_fails_an_advance_keeps_the_series_alive()
    {
        await StartHostAsync();

        var grid   = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));
        var failed = 0;

        // The failure lands on the ADVANCE, which is the one question asked after a run: nothing is written
        // there either, so the row keeps the state a crash between a side effect and its write leaves.
        _probe.Answer = request =>
        {
            if (request.RunNumber >= 2 && Interlocked.Increment(ref failed) == 1)
                throw new InvalidOperationException("the calendar went away mid-series");

            return grid(request);
        };

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("resilient"),
            r => r.Schedule().UseOccurrenceProvider(Key).RunUntil(Clock.GetUtcNow().AddMinutes(2)));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 30000);

        Volatile.Read(ref failed).ShouldBeGreaterThan(0, "the advance really did fail once");

        var schedule = await RowOfAsync(scheduleId);
        schedule.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        schedule.NextRunUtc.ShouldNotBeNull("the series is still parked on a slot of its own grid");
    }

    [Fact]
    public async Task A_series_stalled_on_its_provider_survives_a_restart_because_nothing_was_written()
    {
        await StartHostAsync();

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("stalled"),
            r => r.Schedule().UseOccurrenceProvider(Key).RunUntil(Clock.GetUtcNow().AddMinutes(5)));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        // From here the calendar is gone. The advance after that run cannot be computed, so the run's own
        // completion is not written either: the row stays exactly as a crash between a side effect and its
        // storage write would leave it.
        var ranBefore   = _recorder.Count;
        var askedBefore = _probe.Calls;

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        // Wait until the provider has really been asked again and refused, so the stall below is the one the
        // failure caused and not a slot that had not come due yet.
        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls > askedBefore, 20000);

        var stalled = await RowOfAsync(scheduleId);
        stalled.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        stalled.NextRunUtc.ShouldNotBeNull("the cursor is what makes the row recoverable across the restart");
        stalled.IsRecoverableForExecution(Clock.GetUtcNow())
               .ShouldBeTrue("startup recovery is what asks the provider again");

        // A new process over the same rows, with the calendar back.
        await StopHostAsync();
        _probe.Fault = null;

        await StartHostAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count > ranBefore, 30000);

        (await RowOfAsync(scheduleId)).Status.ShouldNotBe(QueuedTaskStatus.Failed);
    }

    [Fact]
    public async Task A_schedule_asking_its_calendar_again_writes_no_status_and_no_audit_row()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        // InProgress with a cursor is exactly the shape V4 leaves behind: a run happened, its calendar could
        // not say what came next, and neither the completion nor the next cursor was written.
        var cursor     = Clock.GetUtcNow().AddSeconds(-30);
        var scheduleId = await SeedScheduleAsync(ProviderDefinition(), cursor, status: QueuedTaskStatus.InProgress);

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        await Host!.StartAsync();

        // Each attempt asks the calendar exactly once — the grace window's question — so three questions are
        // the recovery's own plus two retry deliveries after it. Those deliveries cross the worker queue,
        // which is where a schedule that has decided NOTHING used to be marked Queued and audited for it,
        // once per retry, for as long as the outage lasted.
        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls >= 3, 30000);

        var row = await RowOfAsync(scheduleId);

        row.Status.ShouldBe(QueuedTaskStatus.InProgress,
            "V4 writes no state at all: the row stays exactly where the outage found it");
        row.NextRunUtc.ShouldBe(cursor, "and its cursor is what keeps it recoverable across a crash");
        row.CurrentRunCount.ShouldBe(0);
        row.StatusAudits.ShouldBeEmpty(
            "a schedule waiting on its calendar leaves no trail of transitions it never made");

        // The control, so none of the above is the absence of any retry at all: the calendar comes back and
        // the same row runs, which is the transition — and the audit row — this test says must not exist
        // before it.
        _probe.Fault = null;

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 30000);

        (await RowOfAsync(scheduleId)).StatusAudits.ShouldNotBeEmpty("a delivery that really ran writes one");
    }

    [Fact]
    public async Task A_recovery_deferred_by_a_provider_leaves_a_failure_counter_an_earlier_restart_earned()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        // Two failed re-dispatches are already on this row — a storage hiccup at each of the last two
        // restarts. The counter is what stops a permanently broken row from being retried for ever, so a
        // restart that proved nothing about it must not spend it: the calendar being down is somebody else's
        // outage, and reading a deferral as a successful dispatch cleared a budget this row had really used.
        var scheduleId = await SeedScheduleAsync(ProviderDefinition(), Clock.GetUtcNow().AddSeconds(-30),
            recoveryFailures: 2);

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls >= 2, 30000);

        var deferred = await RowOfAsync(scheduleId);

        deferred.RecoveryDispatchFailureCount.ShouldBe(2,
            "a dispatch deferred by a provider neither clears the counter nor adds to it");
        deferred.Status.ShouldNotBe(QueuedTaskStatus.Failed);

        // The control: the same row, the same counter, a calendar that answers. THAT is a re-dispatch which
        // really succeeded, and it is the one allowed to forget the failures before it.
        await StopHostAsync();
        _probe.Fault = null;

        await StartHostAsync(startHost: false);
        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => (row.RecoveryDispatchFailureCount ?? 0) == 0, 30000);
    }

    // ---- What a schedule waiting on its calendar reports (V4) -------------------------------------

    [Fact]
    public async Task A_recovery_that_cannot_answer_reports_the_wait_as_a_warning_with_its_failure_count()
    {
        var events = new ConcurrentQueue<EverTaskEventData>();

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        await StartHostAsync(startHost: false, configure: SlowRetry);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await SeedScheduleAsync(ProviderDefinition(), Clock.GetUtcNow().AddSeconds(-30));

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Host!.StartAsync();

            await TaskWaitHelper.WaitForConditionAsync(() => events.Any(SaysParkedToAskAgain), 30000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        // The five-minute backoff makes this the FIRST wait of this schedule and the only one for the rest of
        // the test, so the counter it reports is exactly one.
        AssertWaitReported(events.First(SaysParkedToAskAgain), scheduleId, "nothing was written");
    }

    [Fact]
    public async Task An_advance_that_cannot_answer_reports_the_wait_as_a_warning_with_its_failure_count()
    {
        var events = new ConcurrentQueue<EverTaskEventData>();

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        await StartHostAsync(configure: SlowRetry);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("advance-warns"),
                r => r.Schedule().UseOccurrenceProvider(Key));

            await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

            // The OTHER branch: the question asked after a run, inside the worker. Nothing was written there
            // either — the completion and the next cursor are one operation — and a subscriber has to be told
            // that this series is waiting rather than merely quiet.
            _probe.Fault = () => new InvalidOperationException("the calendar is gone");

            await TaskWaitHelper.WaitForConditionAsync(() => events.Any(SaysParkedToAskAgain), 30000);

            AssertWaitReported(events.First(SaysParkedToAskAgain), scheduleId, "nothing was written");
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }
    }

    [Fact]
    public async Task A_materialization_that_cannot_answer_reports_the_wait_as_a_warning_with_its_failure_count()
    {
        var events = new ConcurrentQueue<EverTaskEventData>();

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        // The third branch, and the only one that says "nothing was materialized": the materializer is driven
        // directly on an unstarted host, so the run below is the whole story and no delivery can produce the
        // same sentence behind it.
        await StartHostAsync(startHost: false, configure: SlowRetry);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(Key, CatchUp(50), OccurrenceMode.Durable), Clock.GetUtcNow().AddSeconds(-30));

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

            await TaskWaitHelper.WaitForConditionAsync(() => events.Any(SaysParkedToAskAgain), 20000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        AssertWaitReported(events.First(SaysParkedToAskAgain), scheduleId, "nothing was materialized");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
    }

    // ---- Durable occurrences over a provider ------------------------------------------------------

    [Fact]
    public async Task A_catch_up_over_a_provider_replays_the_slots_its_grid_owes()
    {
        await StartHostAsync(startHost: false);

        var now     = Clock.GetUtcNow();
        var backlog = FloorToMinute(now).AddMinutes(-4);
        var last    = FloorToMinute(now);

        // A grid that OWES exactly five slots and then ends. A bounded calendar is what makes the assertion
        // an exact one: an endless minute grid keeps adding a slot every time the test's clock crosses a
        // minute, so "at least four" was the only thing it could ever say — and that passes just as well when
        // an occurrence is lost or one is materialized twice.
        _probe.Answer = request =>
        {
            var next = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1))(request);
            return next is { } slot && slot <= last ? slot : null;
        };

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(misfire: CatchUp(10), mode: OccurrenceMode.Durable), backlog);

        await Host!.StartAsync();

        // Five slots are owed and the cap allows ten, so the series replays all of them and then ends: the
        // terminal status is what says the run is over and the count below is final.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 5, 30000);
        await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.Status == QueuedTaskStatus.Completed, 30000);

        var occurrences = await OccurrencesOfAsync(scheduleId);

        occurrences.Select(o => o.ScheduledExecutionUtc).ShouldBe(
        [
            backlog, backlog.AddMinutes(1), backlog.AddMinutes(2), backlog.AddMinutes(3), last
        ], ignoreOrder: true);

        var executions = _recorder.Snapshot();
        executions.Length.ShouldBe(5, "one execution per owed slot, no more and no fewer");
        executions.ShouldAllBe(e => e.IsOccurrence);
        executions.ShouldAllBe(e => e.ScheduleId == scheduleId);
    }

    [Fact]
    public async Task A_replay_asks_the_calendar_about_each_slot_once_and_not_once_per_occurrence()
    {
        const int slots = 40;

        await StartHostAsync(startHost: false);

        var last    = FloorToSecond(Clock.GetUtcNow());
        var backlog = last.AddSeconds(-(slots - 1));

        // The bounded calendar again, a slot a second: forty owed and then nothing, so the replay ends by
        // itself and the count below is final while the wall clock keeps running underneath it.
        _probe.Answer = request =>
        {
            var next = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1))(request);
            return next is { } slot && slot <= last ? slot : null;
        };

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(misfire: CatchUp(5_000), mode: OccurrenceMode.Durable), backlog);

        var asked = _probe.Calls;

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= slots, 60000);
        await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.Status == QueuedTaskStatus.Completed, 30000);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(slots, "one occurrence per owed slot");

        // The budget is one, so the schedule plans once per occurrence — and each of those plans used to count
        // the whole remaining backlog before writing anything: forty questions for the first occurrence,
        // thirty-nine for the second, against a calendar that is a real table in somebody's database. The
        // measurement is taken once and continued from where the last run left the cursor instead.
        (_probe.Calls - asked).ShouldBeLessThan(slots * 5,
            "a replay costs about what walking its backlog once costs, never that squared");
    }

    [Fact]
    public async Task A_backlog_bigger_than_the_cap_halts_a_provider_catch_up_like_any_other()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1));

        var backlog = FloorToMinute(Clock.GetUtcNow()).AddMinutes(-8);

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(misfire: CatchUp(2), mode: OccurrenceMode.Durable), backlog);

        await Host!.StartAsync();

        var halted = await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.RuntimeInfo?.Contains("Halted", StringComparison.Ordinal) == true, 30000);

        halted.NextRunUtc.ShouldBe(backlog, "a halt never moves the cursor: the backlog is still there");
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("a halted catch-up materializes nothing");
    }

    [Fact]
    public async Task Keeping_only_the_newest_slots_needs_a_provider_that_answers_the_same_way_twice()
    {
        await StartHostAsync();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Dispatcher.Dispatch(new ProviderScheduleTask("skip-oldest"),
                r => r.Schedule()
                      .UseOccurrenceProvider(Key)
                      .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 5)
                      {
                          OverflowPolicy = CatchUpOverflowPolicy.SkipOldest
                      }))));

        refusal.Message.ShouldContain("IsDeterministic");

        // The very same policy over the provider that DOES promise it is an ordinary schedule.
        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1));

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("skip-oldest-ok"),
            r => r.Schedule()
                  .UseOccurrenceProvider(DeterministicKey)
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 5)
                  {
                      OverflowPolicy = CatchUpOverflowPolicy.SkipOldest
                  })));

        (await RowOfAsync(scheduleId)).NextRunUtc.ShouldNotBeNull();
    }

    [Fact]
    public async Task Skip_oldest_over_a_provider_drops_the_oldest_slots_of_a_backlog()
    {
        await StartHostAsync(startHost: false);

        var last    = FloorToMinute(Clock.GetUtcNow());
        var backlog = last.AddMinutes(-6);

        // Bounded, for the same reason as the catch-up above: seven slots are owed, the cap keeps two, and
        // the grid then ends — so "exactly the two newest" is a statement the test can actually make.
        _probe.Answer = request =>
        {
            var next = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1))(request);
            return next is { } slot && slot <= last ? slot : null;
        };

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(DeterministicKey, CatchUp(2, CatchUpOverflowPolicy.SkipOldest),
                OccurrenceMode.Durable),
            backlog);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 30000);
        await TaskWaitHelper.WaitUntilAsync(() => RowOfAsync(scheduleId),
            row => row.Status == QueuedTaskStatus.Completed, 30000);

        var occurrences = await OccurrencesOfAsync(scheduleId);

        occurrences.Select(o => o.ScheduledExecutionUtc).ShouldBe([last.AddMinutes(-1), last], ignoreOrder: true);
        _recorder.Count.ShouldBe(2, "the five older slots were dropped, not deferred");
    }

    // ---- The schedule manager ---------------------------------------------------------------------

    [Fact]
    public async Task Re_evaluating_a_schedule_asks_the_provider_again()
    {
        await StartHostAsync();

        var first = FloorToMinute(Clock.GetUtcNow()).AddHours(1);

        _probe.Answer = request => request.AfterUtc < first ? first : first.AddHours(1);

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("reevaluate"),
            r => r.Schedule().UseOccurrenceProvider(Key), taskKey: "provider-reevaluate");

        (await RowOfAsync(scheduleId)).NextRunUtc.ShouldBe(first);

        // The application's calendar changed — a holiday was added — and this is how it says so.
        var moved = first.AddHours(5);
        _probe.Answer = request => request.AfterUtc < moved ? moved : moved.AddHours(1);

        var manager = Host!.Services.GetRequiredService<ITaskScheduleManager>();
        var result  = await manager.ReevaluateSchedule("provider-reevaluate");

        result.NextRunUtc.ShouldBe(moved);
        (await RowOfAsync(scheduleId)).NextRunUtc.ShouldBe(moved);
    }

    [Fact]
    public async Task A_provider_schedule_has_no_period_to_rebase_a_cursor_onto()
    {
        await StartHostAsync();

        var slot = FloorToMinute(Clock.GetUtcNow()).AddHours(1);

        _probe.Answer = request => request.AfterUtc < slot ? slot : slot.AddHours(1);

        await Dispatcher.Dispatch(new ProviderScheduleTask("rebase"),
            r => r.Schedule().UseOccurrenceProvider(Key), taskKey: "provider-rebase");

        var manager = Host!.Services.GetRequiredService<ITaskScheduleManager>();

        var refusal = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await manager.Reschedule("provider-rebase",
                r => r.Schedule().UseOccurrenceProvider(Key),
                RescheduleMode.RebaseFromCursor));

        refusal.Message.ShouldContain("provider");
        refusal.Message.ShouldContain("RecalculateFromNow");
    }


    // ---- What must NOT reach the recovery path (decisions 3.7) ------------------------------------

    [Fact]
    public async Task A_seeded_skip_oldest_row_is_not_poisoned_when_its_provider_stops_promising_determinism()
    {
        await StartHostAsync(startHost: false);

        var last    = FloorToMinute(Clock.GetUtcNow());
        var backlog = last.AddMinutes(-6);

        _probe.Answer = request =>
        {
            var next = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1))(request);
            return next is { } slot && slot <= last ? slot : null;
        };

        // The NON-deterministic key. A dispatch is refused this combination while its caller is holding it,
        // but the row exists: written by a build whose provider declared IsDeterministic, or by hand. Running
        // the same gate on the recovery path throws something the V4 catch does not recognise, so the poison
        // counter would take it and five restarts would end the series for good.
        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(Key, SkipOldest(2), OccurrenceMode.Durable), backlog);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 30000);

        var row = await RowOfAsync(scheduleId);

        row.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        (row.RecoveryDispatchFailureCount ?? 0).ShouldBe(0, "no provider call ever failed");

        (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc)
            .ShouldBe([last.AddMinutes(-1), last], ignoreOrder: true,
                "it bisects exactly as a deterministic grid would");
    }

    [Fact]
    public async Task A_provider_that_cannot_even_be_built_at_startup_costs_a_retry_and_not_the_series()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        // A provider that caches its calendar in its constructor — what the docs recommend — while the
        // database behind it is not up yet. Building it is not answering a question, so a failure there is
        // still transient: the schedule waits and asks again.
        _probe.FailNextConstructions = 1;

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(FragileKey, SkipOldest(5), OccurrenceMode.Durable),
            Clock.GetUtcNow().AddSeconds(-30));

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 30000);

        var row = await RowOfAsync(scheduleId);

        row.Status.ShouldNotBe(QueuedTaskStatus.Failed);
        (row.RecoveryDispatchFailureCount ?? 0)
            .ShouldBe(0, "a container that could not build the provider yet is not a failed re-dispatch");
    }

    [Fact]
    public async Task A_provider_whose_dependencies_are_async_disposable_passes_the_skip_oldest_gate()
    {
        await StartHostAsync();

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1));

        // The gate resolves the provider to ask whether it is deterministic, which builds a scope around a
        // calendar that can only be disposed asynchronously. Disposing that scope synchronously throws, and
        // the throw is not an OccurrenceProviderException: it would reach the caller of a perfectly valid
        // dispatch.
        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("async-scope"),
            r => r.Schedule()
                  .UseOccurrenceProvider(AsyncScopeKey)
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 5)
                  {
                      OverflowPolicy = CatchUpOverflowPolicy.SkipOldest
                  })));

        (await RowOfAsync(scheduleId)).NextRunUtc.ShouldNotBeNull();
    }

    // ---- The delivery that closes a run ----------------------------------------------------------

    [Fact]
    public async Task A_provider_that_never_answers_is_cancelled_when_the_host_stops()
    {
        await StartHostAsync();

        using var hang = new SemaphoreSlim(0);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        await Dispatcher.Dispatch(new ProviderScheduleTask("hangs"),
            r => r.Schedule().UseOccurrenceProvider(Key));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        // From here the calendar stops coming back at all — no exception, just silence. The advance after a
        // run awaits it, so without the delivery's own token on that call the worker consumer, the delivery
        // registry entry and the host's shutdown are held for ever.
        var askedBefore = _probe.Calls;
        _probe.Hang = hang;

        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls > askedBefore, 20000);

        await StopHostAsync(15000);

        _probe.CancelledWaits.ShouldBeGreaterThan(0,
            "stopping the host really reached the provider, which means the advance handed it a token");
    }

    [Fact]
    public async Task The_kick_a_durable_occurrence_gives_its_schedule_is_cancelled_when_the_host_stops()
    {
        // The other call into the grid a delivery makes, and the one a DURABLE provider schedule really uses:
        // an occurrence that has ended kicks its schedule from DoWork's finally, before the single End. The
        // backlog retry is ten minutes out, so once the first occurrence fills a budget of one the schedule
        // row is parked far away and the kick is the ONLY thing left asking the calendar anything.
        await StartHostAsync(startHost: false, configure: cfg =>
        {
            cfg.SetBacklogRetryInterval(TimeSpan.FromMinutes(10));
            cfg.SetOccurrenceProviderRetry(retry =>
            {
                retry.InitialBackoff = TimeSpan.FromMinutes(5);
                retry.MaxBackoff     = TimeSpan.FromMinutes(5);
            });
        });

        using var hang = new SemaphoreSlim(0);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(Key, CatchUp(500), OccurrenceMode.Durable),
            Clock.GetUtcNow().AddSeconds(-30));

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        // From here the calendar stops coming back at all — no exception, just silence. Without this
        // delivery's own token on the kick, the occurrence never reaches its End, so it stays "in delivery"
        // for ever and the schedule's budget of one is spent for ever with it; the worker consumer and the
        // host's shutdown are held too.
        var askedBefore = _probe.Calls;
        _probe.Hang = hang;

        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls > askedBefore, 20000);

        await StopHostAsync(15000);

        _probe.CancelledWaits.ShouldBeGreaterThan(0,
            "stopping the host really reached the provider, which means the kick handed it a token");

        (await OccurrencesOfAsync(scheduleId)).ShouldNotBeEmpty("the premise: an occurrence ran and kicked");
    }

    [Fact]
    public async Task A_series_with_no_storage_at_all_comes_back_after_its_provider_fails()
    {
        // A host without ITaskStorage still runs recurring series, on an in-memory run counter (F18). The
        // provider retry has no row to re-read there, and giving up because of that stopped such a series for
        // good on the first hiccup of its calendar.
        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddSingleton(_recorder);
                services.AddSingleton(_probe);

                services.AddEverTask(cfg => cfg
                                            .RegisterTasksFromAssembly(typeof(ProviderScheduleTask).Assembly)
                                            .SetOccurrenceProviderRetry(retry =>
                                            {
                                                retry.InitialBackoff = TimeSpan.FromSeconds(1);
                                                retry.MaxBackoff     = TimeSpan.FromSeconds(1);
                                            }))
                        .AddOccurrenceProvider<ProbeOccurrenceProvider>(Key);
            })
            .Build();

        try
        {
            await host.StartAsync();

            _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

            await host.Services.GetRequiredService<ITaskDispatcher>()
                      .Dispatch(new ProviderScheduleTask("no-storage"),
                          r => r.Schedule().UseOccurrenceProvider(Key));

            await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

            // Stall it for real before measuring: the calendar goes down, the advance after the current run
            // fails, and from that moment nothing is parked — so the run count cannot move again unless the
            // retry delivery brings the series back.
            var askedBefore = _probe.Calls;
            _probe.Fault = () => new InvalidOperationException("the calendar is gone");

            await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls > askedBefore, 20000);

            var ranBefore = _recorder.Count;
            var stalledAt = _recorder.Snapshot()[^1].SlotUtc;
            _probe.Fault = null;

            stalledAt.ShouldNotBeNull("the premise: the advance that failed belongs to the run just recorded");

            await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count > ranBefore, 30000);

            // And it comes back to the QUESTION the outage interrupted, not to the instant the retry fires
            // at: the slot travels on the retry delivery (ScheduleRetryFromUtc) because with no row there is
            // nowhere else to read it, and re-anchoring the advance on retryAt would skip every slot in
            // between. Two questions from the same instant is what that looks like from the calendar — the
            // advance that failed, and the one that resumed it.
            _probe.Snapshot()
                  .Count(request => request.AfterUtc == stalledAt)
                  .ShouldBeGreaterThanOrEqualTo(2,
                      "the retry re-asked the advance from the slot it was interrupted at");
        }
        finally
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
    }

    // ---- A re-park that fails (V4's second mandatory condition) -----------------------------------

    [Fact]
    public async Task A_re_park_that_fails_is_reported_as_an_error_and_never_as_a_parked_schedule()
    {
        var events = new ConcurrentQueue<EverTaskEventData>();

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        await StartHostAsync(extra: RefusingScheduler(byThrowing: true));

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Dispatcher.Dispatch(new ProviderScheduleTask("repark-fails"),
                r => r.Schedule().UseOccurrenceProvider(Key));

            await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

            // Arming the calendar arms the scheduler with it: from here every re-park of this schedule throws,
            // so the sentence "parked to ask again" can never be true and must never be published.
            _probe.Fault = () => new InvalidOperationException("the calendar is gone");

            await TaskWaitHelper.WaitForConditionAsync(
                () => events.Any(e => e.Message?.Contains("could not be parked to ask the occurrence provider",
                    StringComparison.Ordinal) == true), 30000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        var reported = events.First(e => e.Message?.Contains("could not be parked to ask the occurrence provider",
            StringComparison.Ordinal) == true);

        reported.Severity.ShouldBe(nameof(SeverityLevel.Error));

        events.Any(SaysParkedToAskAgain)
              .ShouldBeFalse(
                  "a re-park that threw leaves the series waiting for a restart: saying it is parked is the " +
                  "one thing that is not true about it");
    }

    [Fact]
    public async Task A_re_park_the_scheduler_refuses_is_not_reported_as_a_schedule_parked_to_ask_again()
    {
        // The OTHER answer TrySchedule gives, and the one that is not an exception: "a newer definition owns
        // this row's parking, or I am stopping". The materializer read it as a schedule it had parked, and
        // said so — while the series sat in no scheduler, no queue and no delivery.
        var log = new RecordingLogger<OccurrenceMaterializer>();

        // No consumers: the materializer is driven directly, so the two runs below are the whole story of this
        // test and nothing else can park the row or say anything about it.
        await StartHostAsync(startHost: false, extra: b =>
        {
            b.Services.AddSingleton<IEverTaskLogger<OccurrenceMaterializer>>(log);
            RefusingScheduler(byThrowing: false)(b);
        });

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(Key, CatchUp(50), OccurrenceMode.Durable),
            Clock.GetUtcNow().AddSeconds(-10));

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        var scheduler    = Scheduler;
        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        scheduler.Refusals.ShouldBe(1, "the premise: the re-park was refused, and refusing is not throwing");
        scheduler.IsScheduled(scheduleId).ShouldBeFalse("so nothing at all is parked for this schedule");

        log.Count(1822).ShouldBe(1, "the refusal is what the run has to report");
        log.Count(1821).ShouldBe(0,
            "and a schedule parked nowhere is never a schedule parked to ask again — the monitoring event " +
            "rides the same branch as this line");

        // The control, on the same schedule and the same failure: with the refusal lifted the next attempt
        // parks it for real, and THAT is the run allowed to say so.
        scheduler.Refusing = false;

        await materializer.RunAsync(scheduleId, null);

        scheduler.IsScheduled(scheduleId).ShouldBeTrue();
        log.Count(1821).ShouldBe(1, "the re-park that really registered the schedule is the one that says so");

        (await RowOfAsync(scheduleId)).NextRunUtc.ShouldNotBeNull("and neither run wrote anything (V4)");
    }

    [Fact]
    public async Task A_recovery_re_park_the_scheduler_refuses_is_not_reported_as_parked_either()
    {
        // The same defect on the recovery path, where the re-park is a SCHEDULE RETRY delivery. The backoff is
        // long enough that no retry delivery can fire and park the row behind the test's back.
        var refused = new RecordingLogger<Dispatcher.Dispatcher>();

        await StartHostAsync(startHost: false, configure: SlowRetry, extra: b =>
        {
            b.Services.AddSingleton<IEverTaskLogger<Dispatcher.Dispatcher>>(refused);
            RefusingScheduler(byThrowing: false)(b);
        });

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await SeedScheduleAsync(ProviderDefinition(Key), Clock.GetUtcNow().AddMinutes(-5));

        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        var refusing = Scheduler;

        await Host!.StartAsync();

        // The refusal is recorded INSIDE TrySchedule, so by the time it is visible the recovery has already
        // said whatever it was going to say about this schedule.
        await TaskWaitHelper.WaitForConditionAsync(() => refusing.Refusals >= 1, 20000);

        refusing.IsScheduled(scheduleId).ShouldBeFalse("this recovery parked nothing for it");
        refused.Count(1019).ShouldBe(0, "so it must not have announced a schedule parked to ask again");
        refused.Count(1021).ShouldBe(1, "what it has to report is the refusal");

        // The control, so the absence above is not vacuous: the same rows in a new process, the calendar still
        // down, and a scheduler that takes the registration. THAT recovery says the schedule is parked.
        var parked = new RecordingLogger<Dispatcher.Dispatcher>();

        await StartHostAsync(startHost: false, configure: SlowRetry, extra: b =>
        {
            b.Services.AddSingleton<IEverTaskLogger<Dispatcher.Dispatcher>>(parked);
            RefusingScheduler(byThrowing: false)(b);
        });

        Scheduler.Refusing = false;

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => parked.Count(1019) >= 1, 20000);

        parked.Count(1021).ShouldBe(0, "nothing was refused this time");
    }

    // ---- Runtime schedule management ------------------------------------------------------------

    [Fact]
    public async Task Rescheduling_a_provider_schedule_counts_its_discarded_backlog_within_the_provider_bound()
    {
        await StartHostAsync(startHost: false);

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1));

        // A cursor a week behind on a per-minute calendar: about ten thousand slots are owed. The count of
        // what a reschedule throws away goes to an event and to the result, never to a decision, so it takes
        // the bound a grid made of round trips can afford instead of the one a walked calendar can.
        var backlog = FloorToMinute(Clock.GetUtcNow()).AddDays(-7);

        var scheduleId = await SeedScheduleAsync(
            ProviderDefinition(Key, CatchUp(50), OccurrenceMode.Durable), backlog, "provider-backlog");

        var asked  = _probe.Calls;
        var result = await Host!.Services.GetRequiredService<ITaskScheduleManager>()
                                .ReevaluateSchedule("provider-backlog");

        (_probe.Calls - asked).ShouldBeLessThan(ProviderScheduleGrid.MaxDiagnosticWalk * 2,
            "a diagnostic count must not walk a week of a per-minute calendar one query at a time");

        result.TaskId.ShouldBe(scheduleId);
        result.DiscardedBacklog.ShouldBe(ProviderScheduleGrid.MaxDiagnosticWalk + 1);
        result.DiscardedBacklogIsExact.ShouldBeFalse("a count that stopped at its bound is a lower bound");
    }

    [Fact]
    public async Task A_cancelled_schedule_stops_being_tracked_by_the_provider_backoff()
    {
        await StartHostAsync(configure: cfg => cfg.SetOccurrenceProviderRetry(retry =>
        {
            // Long enough that the schedule cannot come back and record another failure while this test
            // asserts what the cancel left behind.
            retry.InitialBackoff = TimeSpan.FromMinutes(5);
            retry.MaxBackoff     = TimeSpan.FromMinutes(5);
        }));

        _probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1));

        var scheduleId = await Dispatcher.Dispatch(new ProviderScheduleTask("cancelled"),
            r => r.Schedule().UseOccurrenceProvider(Key));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        var askedBefore = _probe.Calls;
        _probe.Fault = () => new InvalidOperationException("the calendar is gone");

        // One failed advance is one recorded failure for this schedule, and the five-minute backoff means it
        // is the only one for the rest of the test.
        await TaskWaitHelper.WaitForConditionAsync(() => _probe.Calls > askedBefore, 20000);

        var retries = Host!.Services.GetRequiredService<OccurrenceProviderRetryRegistry>();

        await Dispatcher.Cancel(scheduleId);

        retries.RecordFailure(scheduleId).Failures.ShouldBe(1,
            "a schedule cancelled while its calendar was down never gets the answer that would clear it, so " +
            "the cancel is what forgets it");
    }

    /// <summary>
    /// The REAL scheduler, refusing to take a schedule back once the calendar behind it has failed: the two
    /// shapes of a re-park that parks nothing, which is the one thing V4 says must not pass unreported.
    /// </summary>
    /// <param name="byThrowing">
    /// True for the registration that THROWS. False for the other answer <see cref="IScheduler.TrySchedule"/>
    /// gives — "a newer definition owns this row's parking, or I am stopping" — which is not an exception and
    /// was therefore read as a schedule that had been parked.
    /// </param>
    private sealed class ReParkRefusingScheduler(PeriodicTimerScheduler inner, OccurrenceProviderProbe probe,
                                                 bool byThrowing) : IScheduler, IDisposable
    {
        private int _refusals;

        /// <summary>How many re-parks this scheduler answered with a refusal instead of a registration.</summary>
        public int Refusals => Volatile.Read(ref _refusals);

        /// <summary>Lifted to let the next re-park through: the control every refusal test needs.</summary>
        public bool Refusing { get; set; } = true;

        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
            inner.Schedule(item, nextRecurringRun);

        public bool TrySchedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            if (Refusing && probe.Fault != null && (item.IsScheduleRetry || item.RecurringTask?.IsDurable == true))
            {
                if (byThrowing)
                    throw new InvalidOperationException("the scheduler cannot take this registration");

                Interlocked.Increment(ref _refusals);
                return false;
            }

            return inner.TrySchedule(item, nextRecurringRun);
        }

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public bool IsScheduled(Guid persistenceId) => inner.IsScheduled(persistenceId);

        public bool SupportsScheduleInspection => inner.SupportsScheduleInspection;

        public void Dispose() => inner.Dispose();
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);

    private static DateTimeOffset FloorToSecond(DateTimeOffset instant) =>
        new(instant.UtcTicks - instant.UtcTicks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
}
