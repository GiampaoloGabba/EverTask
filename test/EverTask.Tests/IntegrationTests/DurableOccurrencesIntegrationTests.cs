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
using EverTask.Worker;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Durable occurrences end to end, on a real host with a real storage: the schedule row stops running the
/// handler and becomes a definition plus a cursor, and every due slot becomes its own row with its own status,
/// retries and audit trail.
/// </summary>
/// <remarks>
/// The storage instance is shared across the hosts a test builds, so "restart" means what it says: the same
/// rows, a new process. Backlogs are SEEDED as rows rather than waited for — a catch-up is about what a
/// schedule owes after a downtime, and there is no honest way to produce one by sleeping.
/// </remarks>
[Collection("TimingSensitiveTests")]
public class DurableOccurrencesIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly MemoryTaskStorage _shared = new(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>());
    private readonly DurableOccurrenceRecorder _recorder = new();

    private Task<IHost> StartHostAsync(bool startHost = true,
                                       Action<EverTaskServiceConfiguration>? configure = null,
                                       TimeProvider? clock = null,
                                       RecordingLogger<OccurrenceMaterializer>? materializerLog = null) =>
        CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);

                if (materializerLog != null)
                    b.Services.AddSingleton<IEverTaskLogger<OccurrenceMaterializer>>(materializerLog);
            },
            startHost,
            configure,
            clock);

    private async Task<Guid> SeedScheduleAsync(RecurringTask definition, DateTimeOffset cursor,
                                               int currentRunCount = 0, string queueName = QueueNames.Recurring,
                                               string? taskKey = null)
    {
        var row = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            CreatedAtUtc    = Clock.GetUtcNow().AddHours(-1),
            Type            = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new DurableProbeTask("seeded")),
            Handler         = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status          = QueuedTaskStatus.Queued,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(definition),
            RecurringInfo   = definition.ToString(),
            NextRunUtc      = cursor,
            CurrentRunCount = currentRunCount,
            MaxRuns         = definition.MaxRuns,
            RunUntil        = definition.RunUntil,
            QueueName       = queueName,
            TaskKey         = taskKey,
            AuditLevel      = (int)AuditLevel.Full
        };

        await _shared.Persist(row);
        return row.Id;
    }

    private static RecurringTask MinuteCatchUp(TimeSpan maxAge, int maxOccurrences,
                                               CatchUpOverflowPolicy overflow = CatchUpOverflowPolicy.Halt,
                                               int maxPending = 1) => new()
    {
        MinuteInterval = new MinuteInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = maxAge,
            MaxOccurrences        = maxOccurrences,
            OverflowPolicy        = overflow,
            MaxPendingOccurrences = maxPending
        }
    };

    private Task<QueuedTask[]> OccurrencesOfAsync(Guid scheduleId) =>
        _shared.Get(t => t.ParentTaskId == scheduleId);

    private async Task<QueuedTask> SeedOccurrenceAsync(Guid scheduleId, DateTimeOffset slot,
                                                       QueuedTaskStatus status)
    {
        var row = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = Clock.GetUtcNow().AddHours(-1),
            Type                  = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new DurableProbeTask("seeded-occurrence")),
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = status,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = slot,
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };

        await _shared.Persist(row);
        return row;
    }

    /// <summary>
    /// The executor a persisted row would be delivered as — the same rebuild the recovery and the materializer
    /// do, so a test can hand the REAL worker a delivery instead of imitating one.
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

    private Task WaitForOccurrencesAsync(Guid scheduleId, int count, int timeoutMs = 20000) =>
        TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId), rows => rows.Length >= count,
            timeoutMs);

    [Fact]
    public async Task Should_replay_only_non_holiday_slots_from_a_named_calendar()
    {
        var now = new DateTimeOffset(2026, 12, 27, 12, 0, 0, TimeSpan.Zero);
        await StartHostAsync(startHost: false,
            configure: options => options.AddScheduleCalendar(
                "holidays", calendar => calendar.OnDates(new DateOnly(2026, 12, 25))),
            clock: new FakeTimeProvider(now));
        var definition = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(8, 0)] },
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire = new MisfireSettings
            {
                Policy = MisfirePolicy.CatchUp,
                MaxAge = TimeSpan.FromDays(7),
                MaxOccurrences = 10,
                MaxPendingOccurrences = 10
            },
            Exclusions = new ScheduleExclusions { Calendars = ["holidays"] }
        };
        var id = await SeedScheduleAsync(definition,
            new DateTimeOffset(2026, 12, 24, 8, 0, 0, TimeSpan.Zero));

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await OccurrencesOfAsync(id)).Select(row => row.ScheduledExecutionUtc!.Value).OrderBy(slot => slot)
            .ShouldBe([
                new DateTimeOffset(2026, 12, 24, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 26, 8, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 12, 27, 8, 0, 0, TimeSpan.Zero)
            ]);
    }

    [Fact]
    public async Task Should_materialize_nothing_when_schedule_row_names_an_unknown_calendar()
    {
        var log = new RecordingLogger<OccurrenceMaterializer>();
        await StartHostAsync(startHost: false, materializerLog: log);
        var definition = new RecurringTask
        {
            HourInterval = new HourInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Exclusions = new ScheduleExclusions { Calendars = ["removed-holidays"] }
        };
        var id = await SeedScheduleAsync(definition, Clock.GetUtcNow());

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await OccurrencesOfAsync(id)).ShouldBeEmpty();
        log.Count(1816).ShouldBe(1, "the materializer refuses the row at its rebuild boundary");
    }

    [Fact]
    public async Task Should_not_revoke_an_occurrence_materialized_before_a_calendar_widens()
    {
        var now = new DateTimeOffset(2026, 12, 26, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        await StartHostAsync(startHost: false,
            configure: options => options.AddScheduleCalendar(
                "holidays", calendar => calendar.OnDates(new DateOnly(2026, 12, 25))), clock: clock);
        var definition = new RecurringTask
        {
            DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(8, 0)] },
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire = new MisfireSettings
            {
                Policy = MisfirePolicy.CatchUp,
                MaxAge = TimeSpan.FromDays(7),
                MaxOccurrences = 10,
                MaxPendingOccurrences = 10
            },
            Exclusions = new ScheduleExclusions { Calendars = ["holidays"] }
        };
        var slot = new DateTimeOffset(2026, 12, 26, 8, 0, 0, TimeSpan.Zero);
        var id = await SeedScheduleAsync(definition, slot);
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);
        (await OccurrencesOfAsync(id)).ShouldHaveSingleItem().ScheduledExecutionUtc.ShouldBe(slot);

        await StartHostAsync(startHost: false,
            configure: options => options.AddScheduleCalendar("holidays", calendar => calendar.OnDates(
                new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26))), clock: clock);
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await OccurrencesOfAsync(id)).ShouldContain(row => row.ScheduledExecutionUtc == slot);
    }

    [Fact]
    public async Task Should_preserve_a_standing_halt_across_a_calendar_edit()
    {
        var now = new DateTimeOffset(2026, 12, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        await StartHostAsync(startHost: false,
            configure: options => options.AddScheduleCalendar(
                "holidays", calendar => calendar.OnDates(new DateOnly(2026, 12, 25))), clock: clock);
        var definition = MinuteCatchUp(TimeSpan.FromDays(1), 3);
        definition.Exclusions = new ScheduleExclusions { Calendars = ["holidays"] };
        var id = await SeedScheduleAsync(definition, now.AddMinutes(-10));
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);
        var halt = (await _shared.Get(t => t.Id == id))[0].RuntimeInfo.ShouldNotBeNull();

        await StartHostAsync(startHost: false,
            configure: options => options.AddScheduleCalendar("holidays", calendar => calendar.OnDates(
                new DateOnly(2026, 12, 21), new DateOnly(2026, 12, 25))), clock: clock);
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(id, null);

        (await _shared.Get(t => t.Id == id))[0].RuntimeInfo.ShouldBe(halt);
        (await OccurrencesOfAsync(id)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Should_persist_a_normalized_future_cursor_without_materializing_or_reporting_a_slot()
    {
        var now    = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var cursor = now.AddHours(1);
        var clock  = new FakeTimeProvider(now);
        var events = new ConcurrentQueue<EverTaskEventData>();

        await StartHostAsync(startHost: false, clock: clock);

        var scheduleId = await SeedScheduleAsync(new RecurringTask
        {
            HourInterval = new HourInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Exclusions = new ScheduleExclusions
            {
                Ranges = [new ExclusionRange { FromUtc = cursor, ToUtc = cursor.AddMinutes(30) }]
            }
        }, cursor);

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Host!.Services.GetRequiredService<OccurrenceMaterializer>()
                      .RunAsync(scheduleId, null);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        var row = (await _shared.Get(t => t.Id == scheduleId))[0];
        row.NextRunUtc.ShouldBe(cursor.AddHours(1));
        row.CurrentRunCount.ShouldBe(0);
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
        events.ShouldBeEmpty("normalizing a cursor is not a skipped or materialized occurrence");

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Should_replay_only_weekday_slots_when_catch_up_crosses_a_weekend()
    {
        var now    = new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var clock  = new FakeTimeProvider(now);

        await StartHostAsync(startHost: false, clock: clock);

        var scheduleId = await SeedScheduleAsync(new RecurringTask
        {
            DayInterval   = new DayInterval(1) { OnTimes = [new TimeOnly(12, 0)] },
            OccurrenceMode = OccurrenceMode.Durable,
            Exclusions     = new ScheduleExclusions
            {
                Days = [DayOfWeek.Saturday, DayOfWeek.Sunday]
            },
            Misfire = new MisfireSettings
            {
                Policy                = MisfirePolicy.CatchUp,
                MaxAge                = TimeSpan.FromDays(7),
                MaxOccurrences        = 10,
                OverflowPolicy        = CatchUpOverflowPolicy.Halt,
                MaxPendingOccurrences = 5
            }
        }, friday);

        await Host!.StartAsync();
        await WaitForOccurrencesAsync(scheduleId, 2);

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(row => row.ScheduledExecutionUtc!.Value)
                    .OrderBy(slot => slot)
                    .ToArray();

        slots.ShouldBe([friday, now]);
        slots.ShouldAllBe(slot => slot.DayOfWeek != DayOfWeek.Saturday && slot.DayOfWeek != DayOfWeek.Sunday);
    }

    [Fact]
    public async Task Should_start_a_backfill_after_the_excluded_region_containing_its_requested_instant()
    {
        var now      = new DateTimeOffset(2026, 8, 31, 13, 0, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 8, 29, 9, 0, 0, TimeSpan.Zero);

        await StartHostAsync(startHost: false, clock: new FakeTimeProvider(now));

        var scheduleId = await Dispatcher.Dispatch(new DurableProbeTask("excluded-backfill"),
            recurring => recurring.Schedule()
                                  .EveryDay()
                                  .AtTime(new TimeOnly(12, 0))
                                  .ExceptWeekends()
                                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(7), 10)))
                                  .BackfillFrom(saturday));

        var row = (await _shared.Get(t => t.Id == scheduleId))[0];
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 8, 31, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Should_surface_exclusion_budget_exhaustion_at_dispatch_without_persisting_a_row()
    {
        var saturday = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

        await StartHostAsync(clock: new FakeTimeProvider(saturday));

        await Should.ThrowAsync<ExclusionSearchBudgetExceededException>(() =>
            Dispatcher.Dispatch(new DurableProbeTask("empty-grid"),
                recurring => recurring.Schedule()
                                      .Every(7)
                                      .Days()
                                      .Except(exclusion => exclusion.OnDays(DayOfWeek.Saturday))));

        (await _shared.Get(_ => true)).ShouldBeEmpty();
    }

    // ---- The shape of a durable series ------------------------------------------------------------

    [Fact]
    public async Task Every_due_slot_becomes_its_own_row_and_the_schedule_row_never_runs_the_handler()
    {
        await StartHostAsync();

        var scheduleId = await Dispatcher.Dispatch(new DurableProbeTask("live"),
            r => r.Schedule().Every(1).Seconds().WithDurableOccurrences().MaxRuns(3));

        await WaitForOccurrencesAsync(scheduleId, 3);
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 20000);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(3, "one row per slot, and the run budget stops the series at three");

        foreach (var occurrence in occurrences)
        {
            occurrence.ParentTaskId.ShouldBe(scheduleId);
            occurrence.IsRecurring.ShouldBeFalse("an occurrence is a one-shot: the definition stays on the schedule");
            occurrence.RecurringTask.ShouldBeNull();
            occurrence.NextRunUtc.ShouldBeNull();
            occurrence.TaskKey.ShouldBeNull();
            occurrence.ScheduledExecutionUtc.ShouldNotBeNull("the slot IS the occurrence's identity");
            occurrence.QueueName.ShouldBe(QueueNames.Recurring,
                "the occurrence inherits the queue the schedule was routed to, not the default one");
        }

        occurrences.Select(o => o.ScheduledExecutionUtc).Distinct().Count()
                   .ShouldBe(3, "the unique index on (schedule, slot) is what makes a slot exist once");

        var schedule = (await _shared.Get(t => t.Id == scheduleId))[0];
        schedule.CurrentRunCount.ShouldBe(3, "a materialization is what a durable series counts as a run");
        schedule.NextRunUtc.ShouldBeNull("the run budget ended the series in the same commit as its last slot");
        schedule.Status.ShouldBe(QueuedTaskStatus.Completed);
        schedule.RunsAudits.ShouldBeEmpty("the schedule row never executed anything itself");

        var executions = _recorder.Snapshot();
        executions.Length.ShouldBe(3);
        executions.ShouldAllBe(e => e.IsOccurrence);
        executions.ShouldAllBe(e => e.ScheduleId == scheduleId);
        executions.Select(e => e.TaskId).ShouldBe(occurrences.Select(o => o.Id), ignoreOrder: true);
        executions.Select(e => e.RunNumber).OrderBy(n => n).ToArray().ShouldBe([1, 2, 3],
            "each occurrence knows which run of the series it is, which its own one-shot counter cannot say");
    }

    [Fact]
    public async Task An_occurrence_reports_its_own_nominal_slot_and_not_the_moment_it_was_fired()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 10), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        var first = _recorder.Snapshot()[0];
        first.SlotUtc.ShouldBe(cursor, "an overdue occurrence stands for its slot, never for the moment it ran");
        first.Misfire.ShouldNotBeNull();
        first.Misfire!.Kind.ShouldBe(MisfireKind.CatchUp);
        first.Misfire.MissedFromUtc.ShouldBe(cursor);
        first.Misfire.Lateness.ShouldBeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task The_schedule_row_never_spends_the_handlers_rate_limit_budget()
    {
        // Two permits an hour, two slots: if the schedule row took the gate as an ordinary delivery would,
        // it would burn both and neither occurrence could run.
        await StartHostAsync();

        var scheduleId = await Dispatcher.Dispatch(new RateLimitedDurableTask("gated"),
            r => r.Schedule().Every(1).Seconds().WithDurableOccurrences().MaxRuns(2));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(2);
        _recorder.Count.ShouldBe(2, "both occurrences had budget, because the schedule row consumed none");
    }

    // ---- Catch-up ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_backlog_is_replayed_oldest_first_one_row_per_slot()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-4);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 5), cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 5);
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 5, 20000);

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .ToArray();

        slots.ShouldBe([
            cursor, cursor.AddMinutes(1), cursor.AddMinutes(2), cursor.AddMinutes(3), cursor.AddMinutes(4)
        ], "the whole backlog is replayed, and no slot is invented or skipped");
    }

    [Fact]
    public async Task Slots_older_than_the_age_window_are_dropped_and_the_rest_is_replayed()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-30);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromMinutes(3), 20, maxPending: 5), cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 3);
        await Task.Delay(500);

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .ToArray();

        slots.Length.ShouldBeLessThanOrEqualTo(4, "the age window is the only ordinary way a slot is lost");
        slots.ShouldAllBe(s => s >= cursor.AddMinutes(20),
            "nothing older than the window may be materialized");
    }

    [Fact]
    public async Task Occurrences_stay_strictly_serial_while_only_one_may_be_pending()
    {
        _recorder.Hold = TimeSpan.FromMilliseconds(250);

        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 4, 30000);

        _recorder.MaxConcurrent.ShouldBe(1,
            "the default budget of one occurrence is what keeps a replay from overlapping itself");
    }

    [Fact]
    public async Task Occurrences_overlap_up_to_the_pending_budget()
    {
        _recorder.Hold = TimeSpan.FromMilliseconds(400);

        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-5);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 3), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 30000);

        _recorder.MaxConcurrent.ShouldBeGreaterThan(1, "three may be alive at once, so a backlog overlaps");
        _recorder.MaxConcurrent.ShouldBeLessThanOrEqualTo(3, "and never more than the budget allows");
    }

    [Fact]
    public async Task A_fire_once_policy_collapses_the_backlog_into_one_occurrence_carrying_its_range()
    {
        await StartHostAsync(startHost: false);

        var cursor = DateTimeOffset.UtcNow.AddMinutes(-6);
        var scheduleId = await SeedScheduleAsync(new RecurringTask
        {
            MinuteInterval = new MinuteInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = new MisfireSettings { Policy = MisfirePolicy.FireOnce }
        }, cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);
        await Task.Delay(500);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(1, "the whole run of missed slots collapses into one occurrence");

        var execution = _recorder.Snapshot()[0];
        execution.Misfire.ShouldNotBeNull();
        execution.Misfire!.Kind.ShouldBe(MisfireKind.FireOnce);
        execution.Misfire.MissedFromUtc.ShouldBe(cursor);
        execution.Misfire.MissedCount.ShouldBeGreaterThan(1,
            "the handler is told how many slots this one delivery stands for");
        execution.SlotUtc.ShouldBe(execution.Misfire.MissedThroughUtc,
            "and the slot it runs as is the most recent of them");
    }

    [Fact]
    public async Task A_halted_catch_up_materializes_nothing_and_stays_halted_across_a_restart()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromDays(1), 5), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
            rows => rows[0].RuntimeInfo != null, 20000);

        var halted = (await _shared.Get(t => t.Id == scheduleId))[0];
        halted.RuntimeInfo!.ShouldContain("Halted");
        halted.NextRunUtc.ShouldBe(cursor, "a halt writes no cursor: the backlog stays exactly where it is");
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
        _recorder.Count.ShouldBe(0);

        await StopHostAsync();
        await StartHostAsync();
        await Task.Delay(1000);

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty(
            "a halt is durable: only an explicit resume or reschedule starts the schedule again");
        (await _shared.Get(t => t.Id == scheduleId))[0].RuntimeInfo!.ShouldContain("Halted");
    }

    [Fact]
    public async Task Skip_oldest_replays_only_the_most_recent_slots_of_an_overflowing_backlog()
    {
        await StartHostAsync(startHost: false);

        var cursor = DateTimeOffset.UtcNow.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(
            MinuteCatchUp(TimeSpan.FromDays(1), 3, CatchUpOverflowPolicy.SkipOldest, maxPending: 5), cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 3);
        await Task.Delay(500);

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .ToArray();

        slots.Length.ShouldBe(3, "the cap is the number of slots one episode replays");
        slots[0].ShouldBeGreaterThan(cursor.AddMinutes(30), "and they are the MOST RECENT ones");
    }

    // ---- Failure, cancellation, reconciliation ----------------------------------------------------

    [Fact]
    public async Task A_failed_occurrence_is_dead_lettered_and_the_series_keeps_going()
    {
        _recorder.FailRunNumbers.Add(1);

        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 3);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        var failed      = occurrences.OrderBy(o => o.ScheduledExecutionUtc).First();

        await TaskWaitHelper.WaitForTaskStatusAsync(_shared, failed.Id, QueuedTaskStatus.Failed);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBeGreaterThanOrEqualTo(3,
            "a failed occurrence is a dead letter, not a stop sign: the series advances past it");
    }

    [Fact]
    public async Task Cancelling_a_durable_schedule_cancels_the_occurrences_still_waiting()
    {
        // One consumer and a handler that holds: the occurrences the materializer creates pile up behind the
        // one running, so the cancel below really does land on occurrences that have not started.
        _recorder.Hold = TimeSpan.FromSeconds(2);

        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetMaxDegreeOfParallelism(1).SetChannelOptions(20));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-5);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 4), cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 3);

        await Dispatcher.Cancel(scheduleId);

        var schedule = (await _shared.Get(t => t.Id == scheduleId))[0];
        schedule.Status.ShouldBe(QueuedTaskStatus.Cancelled);

        var cancelled = (await OccurrencesOfAsync(scheduleId))
                        .Where(o => o.Status == QueuedTaskStatus.Cancelled)
                        .Select(o => o.Id)
                        .ToHashSet();

        cancelled.ShouldNotBeEmpty("the premise: an occurrence was still waiting when the cancel landed");

        // Long enough for a waiting occurrence to have been picked up and run. It must not have been: an
        // occurrence carries no blacklist entry of its own, so what stops it is the SCHEDULE's — checked at
        // the enqueue and again at the delivery, or it would be executed and its Cancelled overwritten.
        await Task.Delay(4000);

        var after = await OccurrencesOfAsync(scheduleId);
        after.Where(o => cancelled.Contains(o.Id))
             .ShouldAllBe(o => o.Status == QueuedTaskStatus.Cancelled,
                 "a cancelled occurrence must stay cancelled, never be requeued and run");

        _recorder.Snapshot().Select(e => e.TaskId).ShouldNotBeOneOf(cancelled.ToArray());

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc
            .ShouldBe(schedule.NextRunUtc, "and a cancelled schedule grows no further occurrence");
    }

    [Fact]
    public async Task An_occurrence_stranded_with_no_delivery_behind_it_is_requeued_without_a_restart()
    {
        // The host never starts, so nothing consumes: the occurrence below is a row that IS non-terminal and
        // is parked nowhere — exactly the state a swallowed status write or a lost scheduling leaves behind.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(2);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-5),
            Type                  = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new DurableProbeTask("stranded")),
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.InProgress,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(stranded);

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        scheduler.IsScheduled(stranded.Id).ShouldBeFalse("the premise: nothing is parked for it");

        await Host.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var reconciled = (await _shared.Get(t => t.Id == stranded.Id))[0];
        reconciled.Status.ShouldNotBe(QueuedTaskStatus.InProgress,
            "the compare-and-swap on the status it was found in is what claims a stranded occurrence");

        // Parked, or already on its way: the slot is in the past, so the scheduler may have consumed the
        // registration into a delivery before the assertion runs. It consumes it only AFTER the enqueue
        // registers the delivery, so exactly one of the two always holds.
        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();
        (scheduler.IsScheduled(stranded.Id) || deliveries.IsDelivering(stranded.Id))
            .ShouldBeTrue("a reconciled occurrence is handed back to the scheduler, not merely noticed");
    }

    [Fact]
    public async Task A_stale_occurrence_keeps_consuming_the_budget_until_it_terminates()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-5);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-5),
            Type                  = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new DurableProbeTask("stranded")),
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.WaitingQueue,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = cursor,
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(stranded);

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(1,
            "noticing a stranded occurrence does not free its slot in the budget — only terminating it does");

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc
            .ShouldBe(cursor, "and the cursor stays where it is while the window is full");
    }

    [Fact]
    public async Task An_occurrence_whose_row_cannot_be_rebuilt_is_failed_instead_of_holding_the_budget_for_ever()
    {
        // A task type the last deploy renamed away, or a payload that no longer deserializes: the row is
        // non-terminal, nothing is delivering it and nothing has it parked, so reconciliation is what meets
        // it — and the verdict cannot change while the process lives. Requeued, it was rewritten (a Queued
        // transition plus its status-audit row) on every run for ever while it went on holding the only slot
        // of a budget of one: a series that never materializes anything again.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var unusable = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = "EverTask.Tests.ATypeThisDeploymentNoLongerHas, EverTask.Tests.Gone",
            Request               = "{}",
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(unusable);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        var afterFirstRun = (await _shared.Get(t => t.Id == unusable.Id))[0];

        afterFirstRun.Status.ShouldBe(QueuedTaskStatus.Failed,
            "an occurrence nothing in this build can deliver is ended, not put back into a queue it can never leave");
        afterFirstRun.Exception.ShouldNotBeNull(
            "and it keeps the reason, which is what makes RequeueFailedOccurrence a way back after a deploy");

        Host!.Services.GetRequiredService<IScheduler>().IsScheduled(unusable.Id)
             .ShouldBeFalse("nothing was handed to the scheduler: there was no executor to hand it");

        // The slot of the budget it was holding is free again in the SAME run, so the series materializes what
        // it owes instead of standing still behind a dead row.
        (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc).ShouldContain(cursor);

        var auditsAfterFirstRun = afterFirstRun.StatusAudits.Count;

        await materializer.RunAsync(scheduleId, null);

        (await _shared.Get(t => t.Id == unusable.Id))[0].StatusAudits.Count.ShouldBe(auditsAfterFirstRun,
            "and the row is terminal now, so no later run touches it: the write is paid once, not once a minute");
    }

    [Fact]
    public async Task An_occurrence_whose_handler_is_gone_is_failed_instead_of_stalling_every_reconciliation()
    {
        // The other half of "this row cannot be rebuilt", and the one that used to escape as an exception: the
        // payload deserializes perfectly and there is no IEverTaskHandler<T> left to run it — a schedule
        // re-registered onto a different task, with the old handler deleted from the application. Resolution
        // happens inside the executor build, so it threw out of the whole reconciliation: the run failed, the
        // schedule re-parked, and the occurrence stayed Queued — holding the only slot of a budget of one
        // behind a row nothing in this process can deliver, on every reconciliation pass for ever.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var handlerless = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = typeof(HandlerlessOccurrenceTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new HandlerlessOccurrenceTask("orphan")),
            Handler               = "EverTask.Tests.AHandlerThisDeploymentNoLongerHas, EverTask.Tests.Gone",
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(handlerless);

        RecoveredTaskFactory.FromRowWithoutRegistries(handlerless).Task.ShouldNotBeNull(
            "the premise: the row rebuilds its payload — what is missing is a handler to run it");

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var after = (await _shared.Get(t => t.Id == handlerless.Id))[0];

        after.Status.ShouldBe(QueuedTaskStatus.Failed,
            "an occurrence no build of this process can deliver is ended, whichever half of the rebuild failed");
        after.Exception.ShouldNotBeNull(
            "and it keeps the reason, which is what makes a requeue a way back after a deploy");

        (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc).ShouldContain(cursor,
            "the same run then spends the slot of the budget it just freed, instead of ending in an exception");
    }

    [Fact]
    public async Task An_occurrence_whose_handler_only_failed_to_activate_keeps_its_place_in_the_series()
    {
        // The third shape of "the rebuild threw", and the one that is NOT a verdict on the row: the handler is
        // registered and building it failed — a scoped dependency whose factory threw, a connection that was
        // not there. Ended on that, the occurrence is Failed without one of the retries its policy promises,
        // and the work it carries comes back only through an administrative requeue.
        var gate = new ActivationFaultGate();

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(gate);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = typeof(FlakyResolutionTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new FlakyResolutionTask("stranded")),
            Handler               = typeof(FlakyResolutionTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(stranded);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        gate.FailNext(1);

        await materializer.RunAsync(scheduleId, null);

        gate.Activations.ShouldBeGreaterThan(1,
            "the premise: the rebuild failed and the container was then asked whether anything is registered " +
            "for this task at all — the question that tells a missing handler from one that would not build");

        var afterFirstRun = (await _shared.Get(t => t.Id == stranded.Id))[0];

        afterFirstRun.Status.ShouldBe(QueuedTaskStatus.Queued,
            "a handler that exists and failed to build is a transient failure, not a row nothing can deliver");
        afterFirstRun.Exception.ShouldBeNull();

        (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem().Id.ShouldBe(stranded.Id,
            "and it keeps its slot of a budget of one: nothing is created behind an occurrence still owed");

        // The dependency is there again, so the same row is reconciled and handed on — the way back that a
        // terminal Failed would have taken away.
        await materializer.RunAsync(scheduleId, null);

        var scheduler  = Host.Services.GetRequiredService<IScheduler>();
        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        (scheduler.IsScheduled(stranded.Id) || deliveries.IsDelivering(stranded.Id)).ShouldBeTrue(
            "the occurrence is parked again, or already on its way: what matters is that it was not ended");

        (await _shared.Get(t => t.Id == stranded.Id))[0].Status.ShouldNotBe(QueuedTaskStatus.Failed);
    }

    [Fact]
    public async Task An_occurrence_that_never_rebuilds_is_failed_once_it_has_burned_its_rebuild_attempts()
    {
        // What the branch above costs when the failure is NOT transient (F6/#41): a constructor that throws
        // every time is a misconfiguration, not an outage, and "look again on the next run" then holds the
        // occurrence non-terminal for the whole life of the process — under the default budget of one, a
        // series that never materializes anything again while every run leaves the same warning no write ever
        // closes. The bound is the row's own recovery-failure counter, the one that already bounds a
        // re-dispatch the recovery cannot make — and it is spent at the SAME CADENCE, one attempt per process
        // start. Counting per run instead burned all five inside five minutes of a schedule re-planning at
        // BacklogRetryInterval, so a database failover and a misconfiguration ended the same way.
        var gate = new ActivationFaultGate();

        Task<IHost> RestartAsync() => CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(gate);
            },
            startHost: false);

        await RestartAsync();

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = typeof(FlakyResolutionTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new FlakyResolutionTask("never-builds")),
            Handler               = typeof(FlakyResolutionTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(stranded);

        gate.FailUntilReleased();

        const int ceiling = 3;

        for (var start = 1; start < ceiling; start++)
        {
            var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();
            materializer.MaxOccurrenceRebuildAttempts = ceiling;

            // Three runs, because a run is what USED to spend an attempt: an outage lasting three operational
            // retries has to cost the row exactly one.
            await materializer.RunAsync(scheduleId, null);
            await materializer.RunAsync(scheduleId, null);
            await materializer.RunAsync(scheduleId, null);

            var stillOwed = (await _shared.Get(t => t.Id == stranded.Id))[0];

            stillOwed.Status.ShouldBe(QueuedTaskStatus.Queued,
                $"process start {start} is still inside the ceiling: a failure that may not last is not a verdict on the row");
            stillOwed.RecoveryDispatchFailureCount.ShouldBe(start,
                "and three runs of one process are one outage, not three: the ceiling is spent per restart, " +
                "like the recovery counter it borrows");

            (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem().Id.ShouldBe(stranded.Id,
                "and while it is not a verdict the occurrence keeps the only slot of a budget of one");

            await RestartAsync();
        }

        var lastStart = Host!.Services.GetRequiredService<OccurrenceMaterializer>();
        lastStart.MaxOccurrenceRebuildAttempts = ceiling;

        await lastStart.RunAsync(scheduleId, null);

        var ended = (await _shared.Get(t => t.Id == stranded.Id))[0];

        ended.Status.ShouldBe(QueuedTaskStatus.Failed,
            "a handler that never builds is a misconfiguration, and the row that reports it is ended like any " +
            "other one nothing can deliver instead of stalling the series for ever");
        ended.Exception.ShouldNotBeNull(
            "and it keeps the reason, which is what makes a requeue the way back once the wiring is fixed");

        (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc).ShouldContain(cursor,
            "the same run then spends the slot of the budget it just freed: the series moves on");
    }

    [Fact]
    public async Task A_rebuild_that_finally_succeeds_clears_the_attempts_it_had_burned()
    {
        // The ceiling counts CONSECUTIVE failures, and that is the whole reason it is safe: a dependency that
        // is away for a minute now and then would otherwise walk its occurrence to the ceiling one outage at
        // a time and end work no handler ever refused.
        var gate = new ActivationFaultGate();

        Task<IHost> RestartAsync() => CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(gate);
            },
            startHost: false);

        await RestartAsync();

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = typeof(FlakyResolutionTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new FlakyResolutionTask("heals")),
            Handler               = typeof(FlakyResolutionTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(stranded);

        gate.FailUntilReleased();

        // Two process starts, so two attempts: the count lives on the row precisely because the process that
        // burned it may not be the one that finds the row healed.
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);
        await RestartAsync();
        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        (await _shared.Get(t => t.Id == stranded.Id))[0].RecoveryDispatchFailureCount.ShouldBe(2,
            "the attempts are counted on the row, so they survive the restart the process may not");

        gate.Release();

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var healed = (await _shared.Get(t => t.Id == stranded.Id))[0];

        healed.Status.ShouldNotBe(QueuedTaskStatus.Failed,
            "the dependency is back, so the occurrence is handed on — the ceiling was never reached");
        (healed.RecoveryDispatchFailureCount ?? 0).ShouldBe(0,
            "and the failures it had burned are cleared: two outages a week apart must not add up to a verdict");

        var scheduler  = Host.Services.GetRequiredService<IScheduler>();
        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        (scheduler.IsScheduled(stranded.Id) || deliveries.IsDelivering(stranded.Id)).ShouldBeTrue(
            "the occurrence is parked again, or already on its way");
    }

    [Fact]
    public async Task A_status_write_that_never_landed_does_not_free_the_slot_it_was_meant_to_free()
    {
        // SetStatus is best-effort on every relational provider: it logs its own failed transition and returns
        // normally. Read as success, the run frees the slot the dead row was holding and creates a successor —
        // two live occurrences under a budget that says one, and after a deploy that makes the old row
        // readable again both are recovered and both run.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var unusable = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-20),
            Type                  = "EverTask.Tests.ATypeThisDeploymentNoLongerHas, EverTask.Tests.Gone",
            Request               = "{}",
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-20),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(unusable);

        faulty.SwallowNext(nameof(ITaskStorage.SetStatus), 1);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        // The event is the only thing an operator sees, and it is the half that used to lie: the run reads the
        // row back to decide whether the slot is free, then announced "was marked Failed" whatever it read.
        var events = new ConcurrentQueue<EverTaskEventData>();
        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await materializer.RunAsync(scheduleId, null);

            (await _shared.Get(t => t.Id == unusable.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued,
                "the premise: the write was swallowed exactly the way a relational provider swallows its own");

            (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem().Id.ShouldBe(unusable.Id,
                "so the slot it holds is still taken: only a terminal state that really landed frees capacity");

            // Publishing is fire-and-forget, so the event is waited for rather than read when the call returns.
            await TaskWaitHelper.WaitForConditionAsync(
                () => events.Any(e => e.Message?.Contains("cannot be rebuilt from its row",
                                                          StringComparison.Ordinal) == true),
                20000);

            var lost = events.Single(e => e.Message?.Contains("cannot be rebuilt from its row",
                                                              StringComparison.Ordinal) == true);

            lost.Severity.ShouldBe(nameof(SeverityLevel.Error));
            lost.Message.ShouldContain("could not be marked Failed",
                customMessage: "the row is still Queued, and the event is what an operator reads that from");
            lost.Message.ShouldContain(nameof(QueuedTaskStatus.Queued),
                customMessage: "and it names the state the occurrence is actually in");
            lost.Message.Contains("and was marked Failed", StringComparison.Ordinal).ShouldBeFalse(
                "announcing a terminal state that never landed says the slot is free while the series is still " +
                "held behind the occurrence");

            events.Clear();

            await materializer.RunAsync(scheduleId, null);

            (await _shared.Get(t => t.Id == unusable.Id))[0].Status.ShouldBe(QueuedTaskStatus.Failed);

            await TaskWaitHelper.WaitForConditionAsync(
                () => events.Any(e => e.Message?.Contains("and was marked Failed", StringComparison.Ordinal) == true),
                20000);

            events.Single(e => e.Message?.Contains("cannot be rebuilt from its row", StringComparison.Ordinal) == true)
                  .Message.ShouldContain("and was marked Failed",
                      customMessage: "and the run whose write DID land says so, so the two are told apart");
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(2,
            "and the series moves on once the row is really terminal");
    }

    [Fact]
    public async Task A_kick_that_throws_still_lets_the_occurrence_end_its_delivery()
    {
        // The kick sits in the delivery's finally, one line before the single End. If it could escape, the
        // occurrence's registration would never be released and no successor delivery of that id could ever
        // be accepted — a materialization failure would cost the row, not just the kick.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        // The schedule's own slot is far away, so the ONLY materializer call in this test is the kick the
        // occurrence below gives when it ends.
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(10));

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-5),
            Type                  = typeof(DurableProbeTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new DurableProbeTask("kicker")),
            Handler               = typeof(DurableProbeTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(occurrence);

        faulty.FailAlways(nameof(ITaskStorage.GetOccurrences));

        await Host!.StartAsync();

        await TaskWaitHelper.WaitForTaskStatusAsync(_shared, occurrence.Id, QueuedTaskStatus.Completed, 20000);

        // The kick lives in the delivery's finally, AFTER the terminal write: on a fast machine the status is
        // already Completed while the kick has not been reached yet.
        await TaskWaitHelper.WaitForConditionAsync(
            () => faulty.Calls.ContainsKey(nameof(ITaskStorage.GetOccurrences)), 20000);

        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        await TaskWaitHelper.WaitForConditionAsync(() => !deliveries.IsDelivering(occurrence.Id), 20000);

        deliveries.IsDelivering(occurrence.Id)
                  .ShouldBeFalse("the single End runs after the kick, whatever the kick did");
    }

    // ---- Liveness: a run that fails must still leave the schedule somewhere ------------------------

    [Fact]
    public async Task A_materialization_that_fails_still_leaves_the_schedule_parked_for_its_retry()
    {
        // The kick is the entry point whose failure used to be logged and dropped. It is also the one that can
        // be HOLDING the per-schedule gate while the schedule's own delivery arrives, finds the gate taken and
        // returns without parking the row — trusting the holder to do it. A failure that ends the run quietly
        // therefore leaves the schedule parked nowhere at all: not in the scheduler, not in a delivery, and
        // with startup recovery the only thing left that could ever bring it back.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        scheduler.IsScheduled(scheduleId).ShouldBeFalse("the premise: the row is parked nowhere yet");

        faulty.FailAlways(nameof(ITaskStorage.GetOccurrences));

        await Host.Services.GetRequiredService<OccurrenceMaterializer>().KickAsync(scheduleId);

        faulty.Calls.ContainsKey(nameof(ITaskStorage.GetOccurrences))
              .ShouldBeTrue("the premise: the run really reached the storage and really failed");
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("nothing was materialized");

        scheduler.IsScheduled(scheduleId).ShouldBeTrue(
            "a failed run arms the operational retry itself: the schedule that was counting on it would " +
            "otherwise wait for the next restart");
    }

    [Fact]
    public async Task A_schedule_delivery_absorbed_by_a_failing_kick_is_still_parked_by_that_kick()
    {
        // The other half of the same contract, and the one a kick failing on its own never reaches: the kick
        // HOLDS the per-schedule gate while the schedule's own slot delivery arrives. That delivery returns
        // without parking the row — the holder has taken the re-park over — and the scheduler had already
        // consumed the row's registration to make the delivery, so at that instant the schedule is parked
        // nowhere at all. If the holder's failure ends its run quietly, nothing brings the series back: its
        // only occurrence has ended, so no kick can arrive, and recovery runs at startup.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var scheduler        = Host!.Services.GetRequiredService<IScheduler>();
        var worker           = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        var scheduleExecutor = await BuildExecutorAsync((await _shared.Get(t => t.Id == scheduleId))[0]);

        var insideRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim(false);

        var held = 0;
        faulty.RunBefore(nameof(ITaskStorage.GetOccurrences), () =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0)
                return;

            insideRun.TrySetResult();
            proceed.Wait(TimeSpan.FromSeconds(30));
        });

        var holder = Task.Run(() => Host.Services.GetRequiredService<OccurrenceMaterializer>()
                                        .KickAsync(scheduleId).AsTask());

        await insideRun.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The schedule's own slot firing, through the REAL worker: it finds the gate taken and returns.
        await worker.DoWork(scheduleExecutor, CancellationToken.None);

        scheduler.IsScheduled(scheduleId).ShouldBeFalse(
            "the premise: a delivery that finds the gate taken hands its re-park to the holder");

        faulty.FailAlways(nameof(ITaskStorage.GetOccurrences));
        proceed.Set();

        await holder.WaitAsync(TimeSpan.FromSeconds(30));

        await TaskWaitHelper.WaitForConditionAsync(() => scheduler.IsScheduled(scheduleId), 20000);

        scheduler.IsScheduled(scheduleId).ShouldBeTrue(
            "the gate holder owns the re-park of every run it absorbed, so its own failure has to arm the " +
            "operational retry — a swallowed one leaves the schedule parked nowhere until a restart");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("and nothing was materialized");
    }

    [Fact]
    public async Task A_schedule_whose_row_cannot_be_rebuilt_is_still_parked_for_its_retry()
    {
        // The zone the definition names stops resolving between the dispatch and the slot — a replica on an
        // image without that entry, an id the tz database withdrew — so the row no longer validates and
        // nothing can be rebuilt from it. T10 gives such a row a terminal poison, but at the NEXT startup
        // recovery: until then the slot firing IS the schedule's own delivery, and the registration that made
        // it is already consumed. Ending the run on a Debug line there left the row in no scheduler, no queue
        // and no delivery, and only a restart brought the series back.
        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetBacklogRetryInterval(TimeSpan.FromMinutes(5)));

        var scheduleId = await SeedScheduleAsync(ZonedDurableSchedule("Europe/Rome"),
            DateTimeOffset.UtcNow.AddMinutes(-1));

        // Built while the row still reads, exactly as the delivery the scheduler makes was built at dispatch.
        var row      = (await _shared.Get(t => t.Id == scheduleId))[0];
        var delivery = await BuildExecutorAsync(row);

        delivery.IsScheduleOnly.ShouldBeTrue("the premise: this delivery IS the schedule row, and runs no handler");

        // ...and then the zone stops resolving. The store hands back live entities, so this is the row.
        row.RecurringTask = EverTaskJson.Serialize(ZonedDurableSchedule("Mars/Olympus"));

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        scheduler.IsScheduled(scheduleId).ShouldBeFalse(
            "the premise: the delivery consumed the row's registration to exist");

        await Host.Services.GetRequiredService<IEverTaskWorkerExecutor>()
                  .DoWork(delivery, CancellationToken.None);

        scheduler.IsScheduled(scheduleId).ShouldBeTrue(
            "a run that cannot read the row still owes it somewhere to be: the operational retry, where every " +
            "other run that could not advance parks it");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("and nothing is materialized from a row nobody can read");

        var after = (await _shared.Get(t => t.Id == scheduleId))[0];
        after.Status.ShouldNotBe(QueuedTaskStatus.Failed,
            "the bounded retry and the terminal verdict on such a row stay startup recovery's, which owns the " +
            "counter that ends them");
        after.NextRunUtc.ShouldNotBeNull("so the row is still recoverable, cursor and all");
    }

    /// <summary>A durable calendar schedule anchored to a named zone — the one thing that can stop resolving.</summary>
    private static RecurringTask ZonedDurableSchedule(string timeZoneId) => new()
    {
        DayInterval    = new DayInterval(1) { OnTimes = [new TimeOnly(3, 0)] },
        TimeZoneId     = timeZoneId,
        OccurrenceMode = OccurrenceMode.Durable
    };

    // ---- A cursor pointing at a slot that already has a row ----------------------------------------

    [Fact]
    public async Task A_slot_that_already_has_an_occurrence_carries_the_cursor_past_it()
    {
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        // A row for the very slot the cursor still points at, already terminal so it consumes no budget: the
        // shape a cursor rewound over work already done leaves behind (a task key re-registered with a
        // backfill after its series finished), and the shape a row written from outside the materializer
        // leaves too. Re-reading answers the same thing forever — it is the CURSOR that is stale.
        await SeedOccurrenceAsync(scheduleId, cursor, QueuedTaskStatus.Completed);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        (await OccurrencesOfAsync(scheduleId)).Count(o => o.ScheduledExecutionUtc == cursor)
                                              .ShouldBe(1, "the taken slot is not materialized twice");

        // The proof that the run makes progress: walking past the served slot cost no capacity, so the SAME
        // run goes on to the slot after it instead of answering the same AlreadyExists once per operational
        // retry — and there is no occurrence to kick it back into life in between.
        (await OccurrencesOfAsync(scheduleId)).Select(o => o.ScheduledExecutionUtc)
                                              .ShouldContain(cursor.AddMinutes(1));

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc
            .ShouldBe(cursor.AddMinutes(2),
                "the cursor is past the slot that was already served AND past the one this run created");
    }

    [Fact]
    public async Task A_backlog_of_slots_that_already_have_occurrences_is_walked_in_a_single_run()
    {
        // The shape the one-slot-per-run advance could not handle: a cursor rewound over a whole episode that
        // has already been served — a task key re-registered with a backfill after its series finished. Every
        // slot answers AlreadyExists, so nothing is created, so no occurrence ends and no kick arrives; a run
        // that stopped at the first of them left the schedule to walk the backlog at one slot per
        // BacklogRetryInterval, which for this history is seven minutes of Warning events to reach work it had
        // already done.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 50), cursor);

        // Seven consecutive slots that already have a terminal row: they consume no budget, and the default
        // budget of one is what makes each pass hand back a plan of exactly one slot.
        for (var i = 0; i < 7; i++)
            await SeedOccurrenceAsync(scheduleId, cursor.AddMinutes(i), QueuedTaskStatus.Completed);

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var occurrences = await OccurrencesOfAsync(scheduleId);

        occurrences.Length.ShouldBe(8,
            "one run walks past every slot that already had a row and materializes the first one that did not");

        occurrences.Select(o => o.ScheduledExecutionUtc).ShouldContain(cursor.AddMinutes(7));

        occurrences.Select(o => o.ScheduledExecutionUtc)
                   .Distinct()
                   .Count()
                   .ShouldBe(8, "and no slot was served twice on the way");

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc
            .ShouldBe(cursor.AddMinutes(8), "the cursor ends past the whole stretch, not one minute along it");

        (await _shared.Get(t => t.Id == scheduleId))[0].CurrentRunCount
            .ShouldBe(1, "walking past a slot that is already served spends no run of the series");
    }

    [Fact]
    public async Task An_occurrence_created_after_a_walk_carries_the_backlog_that_is_still_owed()
    {
        // A misfire is one statement in two halves: the range bounds the backlog the decision saw, and the
        // count is how many grid slots that range holds. A run that walks past slots the backlog no longer owes
        // has to restate both, or the row it finally creates reports a range that starts at a slot somebody
        // else already served and a count that includes every one of them.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-10);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 50), cursor);

        for (var i = 0; i < 4; i++)
            await SeedOccurrenceAsync(scheduleId, cursor.AddMinutes(i), QueuedTaskStatus.Completed);

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var created = (await OccurrencesOfAsync(scheduleId))
            .Single(o => o.ScheduledExecutionUtc == cursor.AddMinutes(4));

        var misfire = OccurrenceRuntimeInfo.TryParse(created.RuntimeInfo).ShouldNotBeNull();

        misfire.MissedFromUtc.ShouldBe(created.ScheduledExecutionUtc,
            "the backlog a row reports begins at its own slot, never at one that was already served");

        ((misfire.MissedThroughUtc!.Value - misfire.MissedFromUtc!.Value).TotalMinutes + 1)
            .ShouldBe(misfire.MissedCount!.Value,
                "on a one-minute grid the range holds exactly as many slots as the count claims");
    }

    [Fact]
    public async Task A_backlog_worn_down_to_one_late_slot_still_says_it_stands_for_missed_work()
    {
        // The other half of that restatement (M1). What a walk leaves is a SMALLER backlog, and whether the
        // smaller one is still missed work is the threshold's answer, not "is it more than one slot": the
        // single slot left here came due half an hour ago, which is precisely what the policy was turned on
        // for. An hourly grid, so the newest slot a backlog owes can be late without another one behind it.
        await StartHostAsync(startHost: false);

        var newest = DateTimeOffset.UtcNow.AddMinutes(-30);
        var cursor = newest.AddHours(-3);

        var scheduleId = await SeedScheduleAsync(new RecurringTask
        {
            HourInterval   = new HourInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire = new MisfireSettings
            {
                Policy         = MisfirePolicy.CatchUp,
                MaxAge         = TimeSpan.FromDays(1),
                MaxOccurrences = 50
            }
        }, cursor);

        for (var i = 0; i < 3; i++)
            await SeedOccurrenceAsync(scheduleId, cursor.AddHours(i), QueuedTaskStatus.Completed);

        await Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var created = (await OccurrencesOfAsync(scheduleId)).Single(o => o.ScheduledExecutionUtc == newest);
        var misfire = OccurrenceRuntimeInfo.TryParse(created.RuntimeInfo).ShouldNotBeNull();

        misfire.MisfireKind.ShouldBe(MisfireKind.CatchUp, "one slot half an hour old is still missed work");
        misfire.MissedCount.ShouldBe(1, "three of the four the plan saw were already served");
        misfire.MissedFromUtc.ShouldBe(newest);
        misfire.MissedThroughUtc.ShouldBe(newest, "and one slot is its own range");
    }

    [Fact]
    public async Task Walking_a_longer_served_stretch_does_not_cost_a_longer_run()
    {
        // What the walk must NOT do: re-decide the whole misfire policy at every slot it walks past. Each of
        // those decisions counts the backlog and bisects it to find its newest slot — work proportional to
        // what is LEFT — so a run over a served stretch cost the square of its length, all of it while holding
        // the per-schedule gate and one permit of the global materialization budget, and, when the entry point
        // is an occurrence's kick, on a worker consumer thread.
        var evaluator = new CountingScheduleEvaluator(ScheduleEvaluator.Default);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.Replace(ServiceDescriptor.Singleton<IScheduleEvaluator>(evaluator));
            },
            startHost: false);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        var shortStretch = await WalkServedSlotsAsync(materializer, evaluator, servedSlots: 5);
        var longStretch  = await WalkServedSlotsAsync(materializer, evaluator, servedSlots: 45);

        shortStretch.Plans.ShouldBe(1);
        longStretch.Plans.ShouldBe(1,
            "one plan per run: what the walk changes is where the slots it granted are written, not which " +
            "policy decided them");

        (longStretch.Counts - shortStretch.Counts).ShouldBeLessThan(shortStretch.Counts,
            "forty more served slots must not cost forty more counts of the backlog — a walked slot costs one " +
            "step of the grid, not another decision over everything still due");
    }

    /// <summary>
    /// Runs one materialization over a stretch of slots that all already have a row, and reports what that run
    /// asked the grid.
    /// </summary>
    private async Task<(int Plans, int Counts)> WalkServedSlotsAsync(OccurrenceMaterializer materializer,
                                                                     CountingScheduleEvaluator evaluator,
                                                                     int servedSlots)
    {
        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-(servedSlots + 2));
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(2), 500), cursor);

        for (var i = 0; i < servedSlots; i++)
            await SeedOccurrenceAsync(scheduleId, cursor.AddMinutes(i), QueuedTaskStatus.Completed);

        var plansBefore  = evaluator.Plans;
        var countsBefore = evaluator.Counts;

        await materializer.RunAsync(scheduleId, null);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(servedSlots + 1,
            "the premise: one run walks the whole served stretch and materializes the slot behind it");

        return (evaluator.Plans - plansBefore, evaluator.Counts - countsBefore);
    }

    [Fact]
    public async Task A_slot_already_served_is_not_mistaken_for_the_last_run_the_budget_allows()
    {
        // A series re-registered under its task key with a backfill over slots it had already served, with one
        // run left in its budget. The plan grants exactly that one slot and nulls the cursor, because creating
        // it would end the series — but the slot already HAS a row, so it creates nothing and spends no run.
        // Read as a series that just ended, the run closed the schedule with its last run never materialized;
        // read as a series that simply moved on, the last run was created and the schedule was left Queued
        // with a cursor for a LATER run to close, which is not what M6/M14 say either.
        await StartHostAsync(startHost: false);

        var definition = MinuteCatchUp(TimeSpan.FromHours(1), 20);
        definition.MaxRuns = 2;

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(definition, cursor, currentRunCount: 1);

        await SeedOccurrenceAsync(scheduleId, cursor, QueuedTaskStatus.Completed);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        var occurrences = await OccurrencesOfAsync(scheduleId);

        occurrences.Length.ShouldBe(2,
            "the run the budget still owed is materialized at the slot behind the one that was already served");
        occurrences.Select(o => o.ScheduledExecutionUtc).ShouldContain(cursor.AddMinutes(1));

        var schedule = (await _shared.Get(t => t.Id == scheduleId))[0];
        schedule.CurrentRunCount.ShouldBe(2, "and the slot that was already served spent none of the budget");

        // M6/M14: the last occurrence MaxRuns allows ends the series in the commit that creates it, and it
        // does so wherever the walk put it — the budget counts materializations, not slots, so a slot that
        // turned out to be served moved the write along the grid without changing which write is the last.
        schedule.Status.ShouldBe(QueuedTaskStatus.Completed,
            "the write that spends the last run of the budget is the one that closes the series");
        schedule.NextRunUtc.ShouldBeNull("and it leaves no cursor for a later run to find");

        var auditsAtClose = schedule.StatusAudits.Count;

        // Nothing is left over for a later run: a series closed in that commit has no second act, and the row
        // never sat Queued with a spent budget waiting for one.
        await materializer.RunAsync(scheduleId, null);

        var closed = (await _shared.Get(t => t.Id == scheduleId))[0];
        closed.Status.ShouldBe(QueuedTaskStatus.Completed);
        closed.CurrentRunCount.ShouldBe(2);
        closed.StatusAudits.Count.ShouldBe(auditsAtClose, "the finalization was paid once, in that commit");
        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(2, "and no occurrence is created after it");
    }

    // ---- A halt costs nothing while it stands -----------------------------------------------------

    [Fact]
    public async Task A_halted_schedule_is_not_handed_back_to_the_worker_queue_every_retry()
    {
        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetBacklogRetryInterval(TimeSpan.FromSeconds(1)));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromDays(1), 5), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
            rows => rows[0].RuntimeInfo != null, 20000);

        var auditsAtHalt = (await _shared.Get(t => t.Id == scheduleId))[0].StatusAudits.Count;

        // Several retry intervals: enough for a re-park to have fired, been dispatched and written its
        // Queued transition and its audit row three or four times over.
        await Task.Delay(4000);

        Host.Services.GetRequiredService<IScheduler>().IsScheduled(scheduleId).ShouldBeFalse(
            "a halt never releases itself — not by aging, not by restarting — so re-parking it only puts the " +
            "row back through the worker queue every interval, for ever, to produce nothing");

        (await _shared.Get(t => t.Id == scheduleId))[0].StatusAudits.Count.ShouldBe(auditsAtHalt,
            "and each of those deliveries is a status transition plus an audit row on the shared tables");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
    }

    // ---- Cancelling a schedule with an occurrence in flight ---------------------------------------

    [Fact]
    public async Task An_occurrence_still_running_when_its_schedule_is_cancelled_is_not_left_recoverable()
    {
        // Long enough that only the shutdown can end it: the cancel deliberately lets an InProgress
        // occurrence finish on its own (M15), so what this pins is how that ending is CLASSIFIED.
        _recorder.Hold = TimeSpan.FromSeconds(30);

        await StartHostAsync(startHost: false, configure: cfg => cfg.SetMaxDegreeOfParallelism(1));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId),
            rows => rows.Any(o => o.Status == QueuedTaskStatus.InProgress), 20000);

        var running = (await OccurrencesOfAsync(scheduleId)).First(o => o.Status == QueuedTaskStatus.InProgress);

        await Dispatcher.Cancel(scheduleId);

        await StopHostAsync();

        var after = (await _shared.Get(t => t.Id == running.Id))[0];

        after.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "an occurrence carries no blacklist entry of its own, so classifying its shutdown by that entry " +
            "alone writes ServiceStopped — a status startup recovery puts straight back in a queue");
        after.IsRecoverableForExecution(DateTimeOffset.UtcNow).ShouldBeFalse();
    }

    [Fact]
    public async Task An_occurrence_a_crash_left_in_progress_is_cancelled_instead_of_resurrected()
    {
        // The rows a HARD crash leaves behind: the schedule already cancelled by the user, and one occurrence
        // frozen InProgress because no delivery ever wrote its outcome. InProgress is a status the recovery
        // filter accepts, and the in-memory blacklist that covers this case while the host lives does not
        // survive a restart.
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(-3));

        await _shared.SetCancelledByUser(scheduleId, AuditLevel.Full);

        var frozen = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-3),
            QueuedTaskStatus.InProgress);

        frozen.IsRecoverableForExecution(DateTimeOffset.UtcNow)
              .ShouldBeTrue("the premise: nothing about the row itself keeps recovery away from it");

        await StartHostAsync();

        await TaskWaitHelper.WaitForTaskStatusAsync(_shared, frozen.Id, QueuedTaskStatus.Cancelled, 20000);

        _recorder.Count.ShouldBe(0, "an occurrence of a schedule the user cancelled must never run");
    }

    [Fact]
    public async Task A_cancelled_schedules_own_dropped_delivery_keeps_covering_its_occurrences()
    {
        await StartHostAsync(startHost: false);

        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(10));

        var occurrence  = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-1),
            QueuedTaskStatus.Queued);
        var scheduleRow = (await _shared.Get(t => t.Id == scheduleId))[0];

        var blacklist = Host!.Services.GetRequiredService<IWorkerBlacklist>();
        var worker    = Host.Services.GetRequiredService<IEverTaskWorkerExecutor>();

        blacklist.Add(scheduleId);

        // A mid-catch-up schedule has its OWN row in the queue too, and a consumer can reach it first.
        await worker.DoWork(await BuildExecutorAsync(scheduleRow), CancellationToken.None);

        blacklist.IsBlacklisted(scheduleId).ShouldBeTrue(
            "the schedule's entry is the only thing covering the occurrences still behind it in the queue: " +
            "they carry none of their own");

        await worker.DoWork(await BuildExecutorAsync(occurrence), CancellationToken.None);

        _recorder.Count.ShouldBe(0, "an occurrence of a cancelled schedule must not be executed");
        (await _shared.Get(t => t.Id == occurrence.Id))[0].Status
            .ShouldBe(QueuedTaskStatus.Queued, "and its row is left exactly as the cancel left it");
    }

    [Fact]
    public async Task An_occurrence_cancelled_while_its_handler_was_resolving_is_not_put_back_in_progress()
    {
        // The one stretch of a delivery no cancellation check covers: the entry blacklist check and the
        // gate's both run BEFORE the handler is resolved, and resolving one costs whatever its dependencies
        // cost. A cancel landing in there has already written Cancelled on this row — CancelSchedule cancels
        // the pending occurrences together with their schedule, in one transaction (M15) — and the
        // InProgress transition that follows is unconditional, so it would write straight over it, run the
        // handler and complete an occurrence of a series the user had cancelled.
        var gate = new ResolutionGate();

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(gate);
            },
            startHost: false);

        // Far in the future: the only delivery in this test is the one it drives by hand.
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(30));

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-5),
            Type                  = typeof(SlowResolutionTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new SlowResolutionTask("slow")),
            Handler               = typeof(SlowResolutionTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(occurrence);

        var worker   = Host!.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        var executor = await BuildExecutorAsync(occurrence);

        // Armed only now: building the executor resolves the handler once for its metadata, and that
        // resolution is not the one this test needs to hold.
        gate.Arm();

        var delivery = Task.Run(() => worker.DoWork(executor, CancellationToken.None).AsTask());

        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            await Dispatcher.Cancel(scheduleId);

            (await _shared.Get(t => t.Id == occurrence.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
                "the premise: the cancel reached the row while the delivery was still resolving");
        }
        finally
        {
            // A failure above must report itself, not hang the test host behind a delivery nobody released.
            gate.Release();
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(30));

        var after = (await _shared.Get(t => t.Id == occurrence.Id))[0];

        after.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the schedule's cancellation is asked again right before the transition, so nothing writes over it");
        gate.Handled.ShouldBe(0, "and the handler of a cancelled occurrence never runs");
    }

    [Fact]
    public async Task An_occurrence_the_cancel_ended_is_not_freed_by_the_dispatch_that_revives_its_schedule()
    {
        // The test above, one step further: the operator does not stop at the cancel but restarts the series
        // the documented way — dispatch it again under its own task key. That revival drops the SCHEDULE's
        // blacklist entry, which is the only thing covering a delivery already past the pre-gate check, so the
        // last question before SetInProgress had nothing left to find: the occurrence the cancel had confirmed
        // terminal ran the old series' payload and completed it, over the row's Cancelled.
        var gate = new ResolutionGate();

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton(gate);
            },
            startHost: false);

        // Far in the future: the only delivery in this test is the one it drives by hand, and the revived
        // series has nothing due either.
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(30), taskKey: "durable-revived-in-resolution");

        var occurrence = new QueuedTask
        {
            Id                    = Guid.NewGuid(),
            CreatedAtUtc          = DateTimeOffset.UtcNow.AddMinutes(-5),
            Type                  = typeof(SlowResolutionTask).AssemblyQualifiedName!,
            Request               = EverTaskJson.Serialize(new SlowResolutionTask("revived")),
            Handler               = typeof(SlowResolutionTaskHandler).AssemblyQualifiedName!,
            Status                = QueuedTaskStatus.Queued,
            ParentTaskId          = scheduleId,
            ScheduledExecutionUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            QueueName             = QueueNames.Recurring,
            AuditLevel            = (int)AuditLevel.Full
        };
        await _shared.Persist(occurrence);

        var worker   = Host!.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        var executor = await BuildExecutorAsync(occurrence);

        // WorkerQueue registers every delivery it lets through, naming the schedule it belongs to, and that
        // registration is how a revival finds the occurrences it still has to cover. This delivery is driven
        // by hand, so the test makes the same registration the enqueue would have made; DoWork's finally ends
        // it exactly as it does for a queued one.
        Host.Services.GetRequiredService<TaskDeliveryRegistry>()
            .TryBegin(occurrence.Id, scheduleId)
            .ShouldBeTrue();

        // Armed only now: building the executor resolves the handler once for its metadata.
        gate.Arm();

        var delivery = Task.Run(() => worker.DoWork(executor, CancellationToken.None).AsTask());

        try
        {
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

            await Dispatcher.Cancel(scheduleId);

            (await _shared.Get(t => t.Id == occurrence.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
                "the premise: the cancel reached the row while the delivery was still resolving");

            await Dispatcher.Dispatch(new DurableProbeTask("restarted"),
                r => r.Schedule().Every(1).Minutes().WithDurableOccurrences(),
                taskKey: "durable-revived-in-resolution");

            WorkerBlacklist.IsBlacklisted(scheduleId).ShouldBeFalse(
                "the other premise: reviving the schedule really does drop the entry that covered it");
        }
        finally
        {
            // A failure above must report itself, not hang the test host behind a delivery nobody released.
            gate.Release();
        }

        await delivery.WaitAsync(TimeSpan.FromSeconds(30));

        var after = (await _shared.Get(t => t.Id == occurrence.Id))[0];

        after.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the cover moved onto the occurrence before the schedule's entry went, so nothing writes over it");
        gate.Handled.ShouldBe(0, "and the handler of an occurrence its own cancel ended never runs");
    }

    // ---- What MaxRuns counts (M14) ----------------------------------------------------------------

    [Fact]
    public async Task MaxRuns_counts_materializations_even_when_an_occurrence_fails()
    {
        // Six slots owed, three runs allowed, and the second occurrence always throws. A failed occurrence is
        // a dead letter: it does NOT buy back the run it spent, so the series still ends after three slots and
        // the fourth is never materialized.
        _recorder.FailRunNumbers.Add(2);

        await StartHostAsync(startHost: false);

        var definition = MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 5);
        definition.MaxRuns = 3;

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-6);
        var scheduleId = await SeedScheduleAsync(definition, cursor);

        await Host!.StartAsync();

        await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
            rows => rows[0].NextRunUtc == null, 30000);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(3, "three runs are three materializations, whatever becomes of them");

        await TaskWaitHelper.WaitUntilAsync(() => OccurrencesOfAsync(scheduleId),
            rows => rows.Any(o => o.Status == QueuedTaskStatus.Failed), 30000);

        var schedule = (await _shared.Get(t => t.Id == scheduleId))[0];
        schedule.CurrentRunCount.ShouldBe(3);
        schedule.Status.ShouldBe(QueuedTaskStatus.Completed,
            "the run budget ended the series in the same commit as its third materialization");

        await Task.Delay(1000);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(3,
            "a failed occurrence must not hand its run back to the backlog behind it");
    }

    [Fact]
    public async Task MaxRuns_counts_materializations_even_when_an_occurrence_is_cancelled()
    {
        // One consumer and a handler that holds, so the occurrences pile up behind the one running and the
        // cancel below really lands on some that never started.
        _recorder.Hold = TimeSpan.FromSeconds(3);

        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetMaxDegreeOfParallelism(1).SetChannelOptions(20));

        var definition = MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 5);
        definition.MaxRuns = 3;

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-6);
        var scheduleId = await SeedScheduleAsync(definition, cursor);

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 3);

        var beforeCancel = (await _shared.Get(t => t.Id == scheduleId))[0];
        beforeCancel.CurrentRunCount.ShouldBe(3);
        beforeCancel.NextRunUtc.ShouldBeNull("the third materialization spent the last run and closed the series");

        await Dispatcher.Cancel(scheduleId);

        (await OccurrencesOfAsync(scheduleId)).ShouldContain(o => o.Status == QueuedTaskStatus.Cancelled,
            "the premise: at least one occurrence was still waiting when the cancel landed");

        await Task.Delay(1000);

        var after = (await _shared.Get(t => t.Id == scheduleId))[0];
        after.CurrentRunCount.ShouldBe(3, "a cancelled occurrence does not buy back the run it was created with");
        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(3);
    }

    // ---- What a halt reports, and what does NOT release it ----------------------------------------

    [Fact]
    public async Task The_halt_event_says_how_big_the_backlog_was_and_whether_that_number_is_a_total()
    {
        await StartHostAsync(startHost: false);

        var events = new ConcurrentQueue<EverTaskEventData>();
        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        // A plain minute cadence counts by division, so the backlog is a real total and not a lower bound: 40
        // slots against a cap of 5.
        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromDays(1), 5), cursor);

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Host!.StartAsync();

            await TaskWaitHelper.WaitUntilAsync(() => _shared.Get(t => t.Id == scheduleId),
                rows => rows[0].RuntimeInfo != null, 20000);

            await TaskWaitHelper.WaitForConditionAsync(
                () => events.Any(e => e.Message?.Contains("halted at cursor", StringComparison.Ordinal) == true),
                20000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        var halt = ScheduleRuntimeInfo.TryParse((await _shared.Get(t => t.Id == scheduleId))[0].RuntimeInfo)?.Halted;

        halt.ShouldNotBeNull();
        halt!.IsExact.ShouldBeTrue("a grid that counts by division reports the real total, not a bound");
        halt.DetectedAtLeast.ShouldBeGreaterThan(5, "the number is what an operator reconciles the outage with");
        halt.CursorUtc.ShouldBe(cursor);

        var reported = events.First(e => e.Message?.Contains("halted at cursor", StringComparison.Ordinal) == true);
        reported.Severity.ShouldBe(nameof(SeverityLevel.Error));
        reported.Message.ShouldContain(halt.DetectedAtLeast.ToString(CultureInfo.InvariantCulture));
        reported.Message.Contains("at least", StringComparison.Ordinal).ShouldBeFalse(
            "an exact count must not be published as a lower bound: the two mean different things to an operator");
    }

    [Fact]
    public async Task A_halt_survives_the_backlog_ageing_out_of_its_own_window()
    {
        // The reason the halt is PERSISTED rather than recomputed: MaxAge keeps moving forward, so a backlog
        // that is over the cap today is under it tomorrow. A halt that recomputed itself would quietly resume
        // a schedule an operator was supposed to look at.
        var start = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        await StartHostAsync(startHost: false, clock: clock);

        // Ten slots owed against a cap of five, and a series that ends before "now": no new slot can ever be
        // added, so the backlog can only shrink as the age window slides past it.
        var cursor     = start.AddMinutes(-40);
        var definition = MinuteCatchUp(TimeSpan.FromMinutes(45), 5);
        definition.RunUntil = start.AddMinutes(-30);

        var scheduleId = await SeedScheduleAsync(definition, cursor);
        var row        = (await _shared.Get(t => t.Id == scheduleId))[0];
        row.RunUntil = definition.RunUntil;
        await _shared.UpdateTask(row);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        ScheduleRuntimeInfo.TryParse((await _shared.Get(t => t.Id == scheduleId))[0].RuntimeInfo)?.Halted
                           .ShouldNotBeNull("the premise: the backlog really did exceed the cap");

        // An hour later the whole backlog is older than the 45-minute window, so a recomputed decision would
        // find nothing left to replay and let the schedule finish.
        clock.Advance(TimeSpan.FromHours(1));

        await materializer.RunAsync(scheduleId, null);

        var after = (await _shared.Get(t => t.Id == scheduleId))[0];

        ScheduleRuntimeInfo.TryParse(after.RuntimeInfo)?.Halted.ShouldNotBeNull(
            "the passage of time is exactly what must NOT release a halt");
        after.NextRunUtc.ShouldBe(cursor, "and the cursor stays where the halt found it");
        after.Status.ShouldNotBe(QueuedTaskStatus.Completed);
        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_standing_halt_is_reported_again_only_after_its_rate_limit_has_elapsed()
    {
        // A halted schedule is not re-parked, so what brings it back is a kick or a restart — and each of
        // those has to say it is still halted, or a schedule nobody has resumed disappears from the dashboard.
        // The rate limit is what keeps "say it again" from becoming a flood.
        var start = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        await StartHostAsync(startHost: false, clock: clock);

        var events = new ConcurrentQueue<EverTaskEventData>();
        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        var cursor     = start.AddMinutes(-40);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromDays(1), 5), cursor);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            // Publishing is fire-and-forget, so every "it was said" is a wait and every "it was not said" is
            // a pause long enough for it to have been.
            await materializer.RunAsync(scheduleId, null);
            await TaskWaitHelper.WaitForConditionAsync(() => HaltEvents(events) == 1, 20000);

            // A second run a minute later: still halted, and deliberately silent.
            clock.Advance(TimeSpan.FromMinutes(1));
            await materializer.RunAsync(scheduleId, null);
            await Task.Delay(500);

            HaltEvents(events).ShouldBe(1,
                "the operational traffic of a halted schedule must not fill the dashboard with the same fact");

            // Past the window: a schedule still waiting for a person says so again.
            clock.Advance(TimeSpan.FromMinutes(10));
            await materializer.RunAsync(scheduleId, null);
            await TaskWaitHelper.WaitForConditionAsync(() => HaltEvents(events) == 2, 20000);

            HaltEvents(events).ShouldBe(2, "a halt nobody has resumed stays visible");
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty("and none of those runs materialized anything");

        static int HaltEvents(ConcurrentQueue<EverTaskEventData> events) =>
            events.Count(e => e.Message?.Contains("halted at cursor", StringComparison.Ordinal) == true);
    }

    // ---- The scheduler underneath is not part of the contract -------------------------------------

    [Fact]
    public async Task A_durable_series_runs_the_same_way_under_the_sharded_scheduler()
    {
        await StartHostAsync(configure: cfg => cfg.UseShardedScheduler(shardCount: 4));

        Host!.Services.GetRequiredService<IScheduler>().ShouldBeOfType<ShardedScheduler>();

        var scheduleId = await Dispatcher.Dispatch(new DurableProbeTask("sharded"),
            r => r.Schedule().Every(1).Seconds().WithDurableOccurrences().MaxRuns(3));

        await WaitForOccurrencesAsync(scheduleId, 3);
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 3, 20000);

        var occurrences = await OccurrencesOfAsync(scheduleId);
        occurrences.Length.ShouldBe(3);
        occurrences.ShouldAllBe(o => o.ParentTaskId == scheduleId);

        _recorder.Snapshot().ShouldAllBe(e => e.IsOccurrence);
        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc
            .ShouldBeNull("the shards park the schedule and its occurrences exactly as the single timer does");
    }

    // ---- The global materialization budget --------------------------------------------------------

    /// <summary>
    /// How many schedules may be inside a materialization run at once. Observed on the storage, because that
    /// is where a run's work actually lands: <c>GetOccurrences</c> is the first call every run makes, inside
    /// the budget's semaphore.
    /// </summary>
    private async Task<int> MeasureMaterializationConcurrencyAsync(int budget)
    {
        var probe  = new FaultInjectingTaskStorage(_shared);
        var live   = 0;
        var peak   = 0;

        probe.RunBefore(nameof(ITaskStorage.GetOccurrences), () =>
        {
            var now = Interlocked.Increment(ref live);

            int observed;
            while ((observed = Volatile.Read(ref peak)) < now &&
                   Interlocked.CompareExchange(ref peak, now, observed) != observed)
            {
            }

            // Long enough that a second run would overlap this one if the budget let it.
            Thread.Sleep(150);
            Interlocked.Decrement(ref live);
        });

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(probe);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false,
            configureEverTask: cfg => cfg.SetMaterializationConcurrency(budget));

        // Every schedule owes a slot and every schedule has an occurrence to reconcile, so each run really
        // does reach GetOccurrences. Their slots are in the future, so nothing is materialized and no delivery
        // ends: the only concurrency in play is the materializer's own.
        var schedules = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var id = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
                DateTimeOffset.UtcNow.AddMinutes(10));

            await SeedOccurrenceAsync(id, DateTimeOffset.UtcNow.AddMinutes(10), QueuedTaskStatus.WaitingQueue);
            schedules.Add(id);
        }

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await Task.WhenAll(schedules.Select(id => Task.Run(() => materializer.RunAsync(id, null))));

        probe.Calls[nameof(ITaskStorage.GetOccurrences)]
             .ShouldBeGreaterThanOrEqualTo(4, "the premise: every schedule really did run");

        return Volatile.Read(ref peak);
    }

    [Fact]
    public async Task The_materialization_budget_of_one_lets_a_single_schedule_materialize_at_a_time()
    {
        (await MeasureMaterializationConcurrencyAsync(budget: 1)).ShouldBe(1,
            "the global budget is what keeps a restart backlog of many schedules from hitting the store all " +
            "at once");
    }

    [Fact]
    public async Task A_wider_materialization_budget_really_does_let_schedules_overlap()
    {
        // The control for the test above: without it, a budget of one would pass on an implementation that
        // never runs two schedules concurrently for some other reason.
        (await MeasureMaterializationConcurrencyAsync(budget: 4)).ShouldBeGreaterThan(1);
    }

    // ---- Reconciliation: every non-terminal state a lost delivery can leave behind ------------------

    [Fact]
    public async Task An_occurrence_stranded_in_the_exact_queued_state_is_reconciled_too()
    {
        // Queued is the state a swallowed status write leaves after the channel accepted the delivery: the row
        // says it is on its way and nothing is carrying it.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(5);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var stranded = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-1),
            QueuedTaskStatus.Queued);

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        scheduler.IsScheduled(stranded.Id).ShouldBeFalse("the premise: nothing is parked for it");

        await Host.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();
        (scheduler.IsScheduled(stranded.Id) || deliveries.IsDelivering(stranded.Id))
            .ShouldBeTrue("a Queued row with nothing behind it is as stranded as an InProgress one");
    }

    [Fact]
    public async Task Two_occurrences_whose_status_writes_were_both_swallowed_are_both_reconciled()
    {
        // The case a per-kick exclusion could not cover: two occurrences of the same schedule lose their
        // status write in parallel, so neither delivery is there to notice the other. Both are stranded, both
        // must be rescued, and the schedule must not stay blocked behind them.
        await StartHostAsync(startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20, maxPending: 3), cursor);

        // Two different non-terminal states, on slots the schedule has already passed: each is claimed on the
        // status IT was found in, which is why the compare-and-swap takes an expected status at all.
        var first  = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-20),
            QueuedTaskStatus.InProgress);
        var second = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-19),
            QueuedTaskStatus.Queued);

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();

        await Host.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null);

        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();

        foreach (var id in new[] { first.Id, second.Id })
        {
            (scheduler.IsScheduled(id) || deliveries.IsDelivering(id))
                .ShouldBeTrue("each stranded occurrence is handed back, not merely noticed");
        }

        (await _shared.Get(t => t.Id == first.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued,
            "a requeued occurrence is back in the state a delivery starts from");

        // And BOTH still count against the budget: three allowed, two stale, so exactly one new slot fits.
        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(3,
            "noticing a stranded occurrence does not free its place — only terminating it does");
    }

    [Fact]
    public async Task A_scheduler_that_cannot_report_parking_reconciles_nothing_and_warns_once()
    {
        // The branch a custom IScheduler lands on. Without SupportsScheduleInspection there is no evidence
        // that separates "stranded" from "parked" — the interface's own IsScheduled answers a constant true —
        // so nothing may be requeued and every non-terminal occurrence goes on counting against the budget.
        // Conservative in the only safe direction: the schedule waits, instead of an occurrence running twice.
        var log = new RecordingLogger<OccurrenceMaterializer>();

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton<IEverTaskLogger<OccurrenceMaterializer>>(log);
                b.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new InspectionBlindScheduler(
                    new PeriodicTimerScheduler(
                        sp.GetRequiredService<IWorkerQueueManager>(),
                        sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                        TimeSpan.FromMilliseconds(50)))));
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        // Stranded for real — non-terminal, no delivery behind it, parked nowhere — which is exactly what an
        // inspectable scheduler requeues (the two tests above). Here it must be left alone.
        var stranded = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-2),
            QueuedTaskStatus.ServiceStopped);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);
        await materializer.RunAsync(scheduleId, null);

        (await _shared.Get(t => t.Id == stranded.Id))[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped,
            "nothing was reconciled: a scheduler that cannot be asked offers no evidence to act on");

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(1,
            "and the occurrence still counts as active, so a budget of one has no room to create another");

        log.Count(1805).ShouldBe(1, "the warning is once per process, not once per run");
        log.Count(1804).ShouldBe(0, "and no occurrence was reported as requeued");
    }

    [Fact]
    public async Task An_occurrence_whose_scheduling_was_lost_after_the_commit_is_reconciled_without_a_restart()
    {
        // The window the plan calls out: the materialization committed, so the row exists and the cursor has
        // moved, and then handing the occurrence to the scheduler threw. Nothing is parked for a row that IS
        // non-terminal, and under the default budget of one that schedule never moves again — unless the next
        // run notices and rescues it.
        var dropped = new ScheduleDropOnce();
        var log     = new RecordingLogger<OccurrenceMaterializer>();

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.Services.AddSingleton<IEverTaskLogger<OccurrenceMaterializer>>(log);
                b.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new SchedulingFaultInjector(
                    new PeriodicTimerScheduler(
                        sp.GetRequiredService<IWorkerQueueManager>(),
                        sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                        TimeSpan.FromMilliseconds(50)),
                    dropped)));
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        // The next occurrence handed to the scheduler is dropped — the schedule row's own re-park is not, or
        // the test would be measuring a lost re-park instead of a lost occurrence.
        dropped.ArmFor(scheduleId);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        await materializer.RunAsync(scheduleId, null);

        var occurrence = (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem();
        dropped.Dropped.ShouldContain(occurrence.Id, "the premise: the commit landed and the scheduling did not");

        var scheduler = Host.Services.GetRequiredService<IScheduler>();
        scheduler.IsScheduled(occurrence.Id).ShouldBeFalse("so nothing is carrying it");

        // No restart, no kick: the next run of the same materializer is what must find it.
        await materializer.RunAsync(scheduleId, null);

        log.Count(1804).ShouldBe(1,
            "an occurrence that exists but is parked nowhere is stale, and reconciliation is what rescues it");

        // The slot is already past, so the scheduler this run handed it to may have dispatched it within its
        // own check interval: what has to hold afterwards is that SOMETHING carries it again, parked or in
        // flight — asserting the registration alone reads a state the scheduler is entitled to consume.
        var deliveries = Host.Services.GetRequiredService<TaskDeliveryRegistry>();
        (scheduler.IsScheduled(occurrence.Id) || deliveries.IsDelivering(occurrence.Id)).ShouldBeTrue(
            "the rescue hands the occurrence back to the scheduler, it does not merely note it down");
    }

    [Fact]
    public async Task A_window_that_frees_up_with_no_kick_behind_it_still_drains_on_the_operational_retry()
    {
        // The kick each occurrence gives when it ends is the fast path, not the guarantee. Here the occupied
        // slot is freed by a write nobody delivered — the shape a crash leaves — so no kick exists at all, and
        // the only thing that can notice is the operational retry the full window armed.
        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetBacklogRetryInterval(TimeSpan.FromSeconds(1)));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        // Held by a delivery that does not exist: reconciliation leaves a delivering occurrence alone, so the
        // window is full and no kick will ever come for it.
        var occupied   = await SeedOccurrenceAsync(scheduleId, cursor, QueuedTaskStatus.InProgress);
        var deliveries = Host!.Services.GetRequiredService<TaskDeliveryRegistry>();
        deliveries.TryBegin(occupied.Id).ShouldBeTrue();

        var materializer = Host.Services.GetRequiredService<OccurrenceMaterializer>();
        await materializer.RunAsync(scheduleId, null);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(1, "the window was full: nothing new was created");
        Host.Services.GetRequiredService<IScheduler>().IsScheduled(scheduleId).ShouldBeTrue(
            "a full window parks the schedule at its retry interval, so progress does not depend on the kick");

        await Host.StartAsync();

        // Terminalized in storage and released from the registry, exactly as a crash-and-recover would leave
        // it: no delivery ended, so no kick was given.
        await _shared.SetCompleted(occupied.Id, 0, AuditLevel.Full);
        deliveries.End(occupied.Id);

        await WaitForOccurrencesAsync(scheduleId, 2, 20000);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBeGreaterThanOrEqualTo(2,
            "the retry is what drained the backlog, with no kick anywhere in the story");
    }

    [Fact]
    public async Task A_schedule_recovered_with_its_own_occurrence_does_not_overshoot_a_budget_of_one()
    {
        // What the two-wave barrier exists FOR, rather than the order it uses to get there. A schedule row
        // asks how many of its occurrences are alive the moment it is back; recovered while an occurrence of
        // its own has not been put back yet, it reads zero and creates a second one against a budget of one.
        _recorder.Hold = TimeSpan.FromSeconds(3);

        // A short operational retry: the run that ends the hold finds the slot it planned already taken by
        // the seeded row, so it carries the cursor past it and materializes nothing — and what brings the
        // schedule back after that is the retry, not another occurrence ending.
        await StartHostAsync(startHost: false,
            configure: cfg => cfg.SetMaxDegreeOfParallelism(2)
                                 .SetBacklogRetryInterval(TimeSpan.FromSeconds(1)));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-5);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        // The rows a restart mid-replay leaves: the schedule with its cursor, and one occurrence that was
        // never delivered. Both are recovered by the same pass.
        await SeedOccurrenceAsync(scheduleId, cursor, QueuedTaskStatus.WaitingQueue);

        await Host!.StartAsync();

        // The recovered occurrence is now inside its handler and holding, so it IS active.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        (await OccurrencesOfAsync(scheduleId)).Length.ShouldBe(1,
            "the schedule came back together with an occurrence of its own: a budget of one means it may not " +
            "create a second while that one is alive");

        // And it is a budget, not a stall: once the held occurrence ends, the replay goes on.
        await WaitForOccurrencesAsync(scheduleId, 2, 30000);
    }

    // ---- The queue an occurrence lands on -----------------------------------------------------------

    [Fact]
    public async Task A_durable_series_keeps_the_custom_queue_its_schedule_was_routed_to()
    {
        // An occurrence is dispatched with no recurring definition, and the fallback queue for one of those is
        // the default: a durable series on its own lane would leave it one occurrence at a time (M4).
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(_shared);
                b.Services.AddSingleton(_recorder);
                b.AddQueue(CustomQueueDurableTaskHandler.Queue);
            });

        var scheduleId = await Dispatcher.Dispatch(new CustomQueueDurableTask("lane"),
            r => r.Schedule().Every(1).Seconds().WithDurableOccurrences().MaxRuns(2));

        await WaitForOccurrencesAsync(scheduleId, 2);
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 2, 20000);

        (await _shared.Get(t => t.Id == scheduleId))[0].QueueName.ShouldBe(CustomQueueDurableTaskHandler.Queue);

        (await OccurrencesOfAsync(scheduleId)).ShouldAllBe(o =>
                o.QueueName == CustomQueueDurableTaskHandler.Queue,
            "the queue is copied from the schedule row, not re-derived from a definition the child does not have");
    }

    // ---- Backfill, through the public builder -------------------------------------------------------

    [Fact]
    public async Task A_backfilled_schedule_starts_its_cursor_in_the_past_and_replays_from_there()
    {
        // BackfillFrom is the only thing that makes a NEW registration look at the past, and it has to survive
        // the whole public path: builder, validation, serialization, the dispatcher's initial cursor and the
        // materializer's first run.
        await StartHostAsync(startHost: false);

        var from = DateTimeOffset.UtcNow.AddMinutes(-4);

        var scheduleId = await Dispatcher.Dispatch(new DurableProbeTask("backfilled"),
            r => r.Schedule()
                  .Every(1).Minutes()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(1), 20)
                  {
                      MaxPendingOccurrences = 5
                  }))
                  .BackfillFrom(from));

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc.ShouldBe(from,
            "the initial cursor is the first occurrence on or after the backfill instant, inclusive");

        await Host!.StartAsync();

        await WaitForOccurrencesAsync(scheduleId, 5);

        var slots = (await OccurrencesOfAsync(scheduleId))
                    .Select(o => o.ScheduledExecutionUtc!.Value)
                    .OrderBy(s => s)
                    .Take(5)
                    .ToArray();

        slots.ShouldBe([from, from.AddMinutes(1), from.AddMinutes(2), from.AddMinutes(3), from.AddMinutes(4)],
            "a backfill replays the past it was given, one row per slot, oldest first");
    }

    [Fact]
    public async Task A_backfill_asked_for_a_task_key_that_already_has_a_schedule_keeps_the_cursor_the_row_carries()
    {
        // BackfillFrom decides where a series STARTS, so it is a first-registration decision: an idempotent
        // re-registration of the same key keeps the cursor the row already carries, which for a durable
        // schedule is the materializer's alone. Anything that means to replay a past has to register its own
        // series — the sample's replay endpoint does exactly that.
        await StartHostAsync(startHost: false);

        var scheduleId = await Dispatcher.Dispatch(new DurableProbeTask("keyed"),
            r => r.Schedule()
                  .Every(1).Hours()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(6), 20))),
            taskKey: "durable-backfill-key");

        var cursor = (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc;
        cursor.ShouldNotBeNull();

        var reDispatched = await Dispatcher.Dispatch(new DurableProbeTask("keyed"),
            r => r.Schedule()
                  .Every(1).Hours()
                  .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromHours(6), 20)))
                  .BackfillFrom(DateTimeOffset.UtcNow.AddHours(-5)),
            taskKey: "durable-backfill-key");

        reDispatched.ShouldBe(scheduleId, "the task key addresses the same row");

        (await _shared.Get(t => t.Id == scheduleId))[0].NextRunUtc.ShouldBe(cursor,
            "the backfill is ignored on a row that already has a cursor: nothing is replayed");

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty();
    }

    // ---- The boundaries of one catch-up episode -----------------------------------------------------

    [Fact]
    public async Task A_catch_up_reports_the_episode_it_starts_and_the_moment_it_is_over()
    {
        // Between the two boundaries every event is per occurrence, and per occurrence there is no way to
        // tell where a replay begins, how big it was, or that it has drained. The backlog is replayed one at
        // a time (the default budget), so this also pins that the episode is opened ONCE across the runs it
        // takes — a busy run plans nothing at all, which must not read as the end of the replay.
        await StartHostAsync(startHost: false);

        var events = new ConcurrentQueue<EverTaskEventData>();

        Task Collect(EverTaskEventData data)
        {
            events.Enqueue(data);
            return Task.CompletedTask;
        }

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var startedText   = $"Catch-up of schedule {scheduleId} started";
        var completedText = $"Catch-up of schedule {scheduleId} completed";

        WorkerExecutor.TaskEventOccurredAsync += Collect;

        try
        {
            await Host!.StartAsync();

            await WaitForOccurrencesAsync(scheduleId, 3);
            await TaskWaitHelper.WaitForConditionAsync(() => Count(events, completedText) == 1, 30000);
        }
        finally
        {
            WorkerExecutor.TaskEventOccurredAsync -= Collect;
        }

        var started = events.Where(e => Says(e, startedText)).ToList();

        started.Count.ShouldBe(1, "one episode, however many runs draining it takes");
        started[0].TaskId.ShouldBe(scheduleId, "the episode belongs to the schedule row, not to an occurrence");
        started[0].Severity.ShouldBe(nameof(SeverityLevel.Information));
        // The replay is announced from the oldest slot it owes, which is where the cursor stood.
        started[0].Message.ShouldContain(cursor.ToString("O", CultureInfo.InvariantCulture));
        started[0].Message.ShouldContain("slot(s) are due");

        var completed = events.Single(e => Says(e, completedText));
        completed.TaskId.ShouldBe(scheduleId);
        completed.Severity.ShouldBe(nameof(SeverityLevel.Information));
        completed.Message.ShouldContain("occurrence(s) materialized since");

        var replayed = int.Parse(completed.Message.Split("completed: ")[1].Split(' ')[0], CultureInfo.InvariantCulture);
        replayed.ShouldBeGreaterThanOrEqualTo(3, "the tally is what the episode really wrote");

        return;

        static bool Says(EverTaskEventData data, string fragment) =>
            data.Message?.Contains(fragment, StringComparison.Ordinal) == true;

        static int Count(ConcurrentQueue<EverTaskEventData> events, string fragment) =>
            events.Count(e => Says(e, fragment));
    }

    // ---- At-least-once, said out loud ----------------------------------------------------------------

    [Fact]
    public async Task An_occurrence_whose_completion_write_never_landed_runs_a_second_time()
    {
        // P3 written as a test instead of only as prose. The handler produces its side effect and the process
        // dies before the completion write: the row is left non-terminal, startup recovery puts it back, and
        // the SAME occurrence runs again. That is the contract — handlers must be idempotent — and a test that
        // asserts it is what keeps the contract from being quietly "fixed" into exactly-once.
        _recorder.Hold = TimeSpan.FromSeconds(30);

        await StartHostAsync(startHost: false, configure: cfg => cfg.SetMaxDegreeOfParallelism(1));

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-2);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        await Host!.StartAsync();

        // The side effect is recorded at the top of the handler, before the hold: once the recorder has seen
        // it, the occurrence has really done its work and no outcome has been written for it.
        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.Count >= 1, 20000);

        var firstRun = _recorder.Snapshot()[0];

        await StopHostAsync();

        var interrupted = (await _shared.Get(t => t.Id == firstRun.TaskId))[0];
        interrupted.Status.ShouldBeOneOf(QueuedTaskStatus.ServiceStopped, QueuedTaskStatus.InProgress);
        interrupted.IsRecoverableForExecution(DateTimeOffset.UtcNow)
                   .ShouldBeTrue("the premise: no outcome was written, so the row is still owed a delivery");

        _recorder.Hold = TimeSpan.Zero;

        await StartHostAsync();

        await TaskWaitHelper.WaitForConditionAsync(
            () => _recorder.Snapshot().Count(e => e.TaskId == firstRun.TaskId) >= 2, 20000);

        _recorder.Snapshot().Count(e => e.TaskId == firstRun.TaskId).ShouldBeGreaterThanOrEqualTo(2,
            "at-least-once: a crash between the handler's side effect and the completion write re-runs it");
    }

    // ---- A cancel landing inside the materialization -------------------------------------------------

    [Fact]
    public async Task A_cancel_that_lands_while_an_occurrence_is_being_inserted_creates_no_occurrence()
    {
        // The narrowest window a cancel can land in: the materializer has decided its slot and is one call
        // away from writing it. The write is a compare-and-swap that refuses a cancelled schedule, so the
        // cancel wins and the series grows nothing after it.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var atInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed  = new ManualResetEventSlim(false);

        faulty.RunBefore(nameof(ITaskStorage.MaterializeOccurrence), () =>
        {
            atInsert.TrySetResult();
            proceed.Wait(TimeSpan.FromSeconds(30));
        });

        var run = Task.Run(() => Host!.Services.GetRequiredService<OccurrenceMaterializer>().RunAsync(scheduleId, null));

        try
        {
            await atInsert.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await Dispatcher.Cancel(scheduleId);

            (await _shared.Get(t => t.Id == scheduleId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
                "the premise: the cancel linearized while the insert was in flight");
        }
        finally
        {
            proceed.Set();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(30));

        (await OccurrencesOfAsync(scheduleId)).ShouldBeEmpty(
            "a cancelled schedule must not grow an occurrence, whatever a run in flight had already decided");

        (await _shared.Get(t => t.Id == scheduleId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "and the run in flight must not write over the cancellation");
    }

    [Fact]
    public async Task A_cancel_that_found_no_occurrence_yet_still_cancels_the_one_committed_behind_it()
    {
        // The other side of the same race, and the one the cancel itself owns. Its two statements are a
        // classification and a write: a materializer that had already claimed the schedule row when the
        // classification ran had not inserted its occurrence yet, so the cancel saw a row with nothing to
        // cascade to and took the simple write — leaving a non-terminal occurrence under a cancelled
        // schedule, which is exactly what recovery puts back in a queue at the next restart (M15).
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);

        var materializer = Host!.Services.GetRequiredService<OccurrenceMaterializer>();

        // The whole materialization lands INSIDE the cancel: after it has classified the schedule as one with
        // no occurrences and before its status write reaches the store. One shot — the cascade below writes
        // through this store too.
        var landed = 0;
        faulty.RunBefore(nameof(ITaskStorage.SetCancelledByUser), () =>
        {
            if (Interlocked.Exchange(ref landed, 1) != 0)
                return;

            materializer.RunAsync(scheduleId, null).GetAwaiter().GetResult();
        });

        await Dispatcher.Cancel(scheduleId);

        landed.ShouldBe(1, "the premise: the occurrence was committed inside the cancel's own window");

        var occurrence = (await OccurrencesOfAsync(scheduleId)).ShouldHaveSingleItem();

        occurrence.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "no occurrence may be left non-terminal under a cancelled schedule: the cancelled set is the exact " +
            "complement of what recovery would requeue");

        (await _shared.Get(t => t.Id == scheduleId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task A_cancel_whose_occurrence_lookup_fails_cascades_instead_of_calling_the_series_terminal()
    {
        // Both of the cancel's occurrence lookups are READS, and a read can fail. Degraded to "no
        // occurrences", the failure chose the single-row write: the cancel returned normally having cancelled
        // the schedule alone, while the occurrence it could not see stayed non-terminal — covered only by the
        // blacklist, whose entries lapse after an hour, after which the row is free to run as part of a series
        // the user had cancelled (M15).
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20), cursor);
        var occurrence = await SeedOccurrenceAsync(scheduleId, cursor, QueuedTaskStatus.Queued);

        faulty.FailAlways(nameof(ITaskStorage.GetOccurrences));

        await Dispatcher.Cancel(scheduleId);

        faulty.Calls[nameof(ITaskStorage.GetOccurrences)]
              .ShouldBeGreaterThan(0, "the premise: the lookup really ran and really failed");

        (await _shared.Get(t => t.Id == occurrence.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "a lookup that threw is not an answer a cancel may read as 'nothing to cascade to'");

        (await _shared.Get(t => t.Id == scheduleId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task A_cancel_that_lands_mid_run_is_not_absorbed_into_the_finalizations_own_expectation()
    {
        // The finalization of a spent series is compare-and-swapped on the status the run DECIDED from. Read
        // that status back at write time and a cancel that linearized in between becomes the expectation
        // itself: the swap then matches the row it was supposed to lose to, and Completed is written over the
        // status the user chose. Not hypothetical on a store that hands back live entities — the in-memory one
        // does, so "the row" and "the row the cancel just changed" are the same object.
        var faulty = new FaultInjectingTaskStorage(_shared);

        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton<ITaskStorage>(faulty);
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        // Its last run is already spent, so this run has nothing to materialize and everything to finalize.
        var definition = MinuteCatchUp(TimeSpan.FromHours(1), 20);
        definition.MaxRuns = 1;

        var cursor     = DateTimeOffset.UtcNow.AddMinutes(-3);
        var scheduleId = await SeedScheduleAsync(definition, cursor, currentRunCount: 1);

        var atRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim(false);

        // One shot only: the cancel below reads the occurrences too, and a hook that held every reader would
        // hold the cancel this test is trying to land.
        var held = 0;
        faulty.RunBefore(nameof(ITaskStorage.GetOccurrences), () =>
        {
            if (Interlocked.Exchange(ref held, 1) != 0)
                return;

            atRead.TrySetResult();
            proceed.Wait(TimeSpan.FromSeconds(30));
        });

        var run = Task.Run(() => Host!.Services.GetRequiredService<OccurrenceMaterializer>()
                                      .RunAsync(scheduleId, null));

        try
        {
            await atRead.Task.WaitAsync(TimeSpan.FromSeconds(30));

            await Dispatcher.Cancel(scheduleId);

            (await _shared.Get(t => t.Id == scheduleId))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
                "the premise: the cancel linearized after the run had read the row");
        }
        finally
        {
            proceed.Set();
        }

        await run.WaitAsync(TimeSpan.FromSeconds(30));

        var after = (await _shared.Get(t => t.Id == scheduleId))[0];

        after.Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the finalization expects the status the run decided from, so a cancel that landed after that " +
            "read wins the swap instead of becoming its expectation");
        after.NextRunUtc.ShouldBe(cursor, "and the cursor the cancel left is untouched");
    }

    // ---- ServiceStopped: the state the cancel used to leave behind (R7) -----------------------------

    [Fact]
    public async Task An_occurrence_left_ServiceStopped_by_a_shutdown_never_runs_after_its_schedule_is_cancelled()
    {
        // The exact hole R7 found: a shutdown writes ServiceStopped, TrySetQueuedIfRecoverable accepts
        // ServiceStopped, and a CancelSchedule that skipped that status left the occurrence to be requeued and
        // executed one restart after the user cancelled its series.
        await StartHostAsync(startHost: false);

        var scheduleId = await SeedScheduleAsync(MinuteCatchUp(TimeSpan.FromHours(1), 20),
            DateTimeOffset.UtcNow.AddMinutes(30));

        var stopped = await SeedOccurrenceAsync(scheduleId, DateTimeOffset.UtcNow.AddMinutes(-1),
            QueuedTaskStatus.ServiceStopped);

        stopped.IsRecoverableForExecution(DateTimeOffset.UtcNow).ShouldBeTrue(
            "the premise: recovery would put a ServiceStopped occurrence straight back in a queue");

        await Dispatcher.Cancel(scheduleId);

        (await _shared.Get(t => t.Id == stopped.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled,
            "the cancelled set is the exact complement of the requeued one");

        // The restart is the whole point: the in-memory blacklist does not survive it, so only the persisted
        // status can stop the occurrence now.
        await StopHostAsync();
        await StartHostAsync();

        await Task.Delay(2000);

        (await _shared.Get(t => t.Id == stopped.Id))[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        _recorder.Count.ShouldBe(0, "an occurrence of a cancelled schedule must never run");
    }

    // ---- Refusals ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_durable_schedule_is_refused_by_a_storage_that_cannot_support_it()
    {
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.Services.AddSingleton(Mock.Of<ITaskStorage>());
                b.Services.AddSingleton(_recorder);
            },
            startHost: false);

        var thrown = await Should.ThrowAsync<NotSupportedException>(() =>
            Dispatcher.Dispatch(new DurableProbeTask("refused"),
                r => r.Schedule().Every(1).Minutes().WithDurableOccurrences()));

        thrown.Message.ShouldContain("SupportsDurableOccurrences");
    }

    [Fact]
    public async Task A_replaying_misfire_policy_is_refused_on_an_inline_schedule()
    {
        await StartHostAsync(startHost: false);

        var definition = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(1),
            Misfire        = new MisfireSettings { Policy = MisfirePolicy.CatchUp, MaxAge = TimeSpan.FromHours(1), MaxOccurrences = 5 }
        };

        Should.Throw<InvalidOperationException>(() => definition.Validate())
              .Message.ShouldContain("durable occurrences");
    }

    // ---- Helpers that stand in for a failure the production code cannot be asked for ----------------

    /// <summary>
    /// Which occurrences of one schedule the scheduler is told to drop, and which it really dropped. Armed
    /// per SCHEDULE, so the schedule row's own re-park still goes through: dropping that too would leave the
    /// test measuring a lost re-park instead of a lost occurrence.
    /// </summary>
    private sealed class ScheduleDropOnce
    {
        private readonly HashSet<Guid> _armed = [];

        public ConcurrentBag<Guid> Dropped { get; } = [];

        public void ArmFor(Guid scheduleId)
        {
            lock (_armed) _armed.Add(scheduleId);
        }

        public bool ShouldDrop(TaskHandlerExecutor executor)
        {
            if (executor.ParentTaskId is not { } parent)
                return false;

            lock (_armed)
            {
                if (!_armed.Remove(parent))
                    return false;
            }

            Dropped.Add(executor.PersistenceId);
            return true;
        }
    }

    /// <summary>
    /// The REAL evaluator with a counter in front of the two questions a materialization run pays for: how
    /// many slots the backlog owes (the counts, which the bisection that closes a misfire range repeats) and
    /// which slots to write (one plan). What a run asks the grid IS its cost, and a walk that re-decided its
    /// policy per slot showed up here as one plan and one pile of counts apiece.
    /// </summary>
    private sealed class CountingScheduleEvaluator(IScheduleEvaluator inner) : IScheduleEvaluator
    {
        private int _plans;
        private int _counts;

        public int Plans => Volatile.Read(ref _plans);
        public int Counts => Volatile.Read(ref _counts);

        public ValueTask<NextRunResult> CalculateNextValidRunAsync(
            RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
            DateTimeOffset? referenceTime = null, bool isRecovery = false, bool computeSkippedCount = true,
            ScheduleIdentity identity = default, CancellationToken ct = default) =>
            inner.CalculateNextValidRunAsync(definition, scheduledTime, currentRun, nowUtc, referenceTime, isRecovery,
                computeSkippedCount, identity, ct);

        public ValueTask<DateTimeOffset?> NextAfterAsync(RecurringTask definition, DateTimeOffset anchor,
                                                         DateTimeOffset after, ScheduleIdentity identity = default,
                                                         CancellationToken ct = default) =>
            inner.NextAfterAsync(definition, anchor, after, identity, ct);

        public ValueTask<int> CountMissedAsync(RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after,
                                               int cap, ScheduleIdentity identity = default,
                                               CancellationToken ct = default)
        {
            Interlocked.Increment(ref _counts);
            return inner.CountMissedAsync(definition, anchor, after, cap, identity, ct);
        }

        public ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(
            RecurringTask definition, DateTimeOffset occurrence, ScheduleIdentity identity = default,
            CancellationToken ct = default) =>
            inner.NextGridOccurrenceAfterAsync(definition, occurrence, identity, ct);

        public ValueTask<DateTimeOffset?> FirstOccurrenceOnOrAfterAsync(
            RecurringTask definition, DateTimeOffset instant, ScheduleIdentity identity = default,
            CancellationToken ct = default) =>
            inner.FirstOccurrenceOnOrAfterAsync(definition, instant, identity, ct);

        public ValueTask<DateTimeOffset?> NormalizeCursorAsync(
            RecurringTask definition, DateTimeOffset cursor, int currentRunCount,
            ScheduleIdentity identity = default, CancellationToken ct = default) =>
            inner.NormalizeCursorAsync(definition, cursor, currentRunCount, identity, ct);

        public ValueTask<IReadOnlyList<DateTimeOffset>> EnumerateDueSlotsAsync(
            RecurringTask definition, DateTimeOffset cursor, DateTimeOffset nowUtc, int cap,
            ScheduleIdentity identity = default, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _plans);
            return inner.EnumerateDueSlotsAsync(definition, cursor, nowUtc, cap, identity, ct);
        }
    }

    /// <summary>
    /// The REAL scheduler, with one occurrence swallowed on the way in: the shape of a <c>Schedule()</c> that
    /// threw after the materialization had already committed. Everything else is forwarded untouched.
    /// </summary>
    /// <summary>
    /// The real scheduler minus the one capability reconciliation depends on: the shape of a custom
    /// <see cref="IScheduler"/> that never implemented <c>IsScheduled</c>, and so inherits the interface's own
    /// "assume scheduled" default together with <c>SupportsScheduleInspection == false</c>.
    /// </summary>
    private sealed class InspectionBlindScheduler(PeriodicTimerScheduler inner) : IScheduler, IDisposable
    {
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
            inner.Schedule(item, nextRecurringRun);

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Keeps the EventIds it is handed. The one-time inspection warning publishes no monitoring event — it is
    /// a log line and nothing else — so the log is where "written once, not once per run" can be observed.
    /// </summary>
    private sealed class RecordingLogger<T> : IEverTaskLogger<T>
    {
        private readonly ConcurrentBag<int> _events = [];

        public int Count(int eventId) => _events.Count(id => id == eventId);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter) => _events.Add(eventId.Id);

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }

    private sealed class SchedulingFaultInjector(PeriodicTimerScheduler inner, ScheduleDropOnce drop)
        : IScheduler, IDisposable
    {
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            if (drop.ShouldDrop(item))
                return;

            inner.Schedule(item, nextRecurringRun);
        }

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public bool IsScheduled(Guid persistenceId) => inner.IsScheduled(persistenceId);

        public bool SupportsScheduleInspection => inner.SupportsScheduleInspection;

        public void Dispose() => inner.Dispose();
    }
}
