using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Serialization;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Time zones through a real host: a real dispatch, the real storage row, the real worker and the real
/// startup recovery. The occurrence arithmetic is pinned exhaustively by the unit suite in
/// <c>RecurringTests/TimeZones</c>; what these tests answer is whether the zone actually travels — into the
/// row, back out of it at restart, and into what the handler reads about its own delivery.
/// </summary>
public class ScheduleTimeZoneIntegrationTests : IsolatedIntegrationTestBase
{
    private const string RomeId = "Europe/Rome";

    private static readonly TimeZoneInfo Rome = TimeZoneInfo.FindSystemTimeZoneById(RomeId);

    /// <summary>A summer instant, so the Rome offset in play (+02:00) differs from the winter one.</summary>
    private static readonly DateTimeOffset SummerNow = new(2026, 7, 1, 6, 0, 0, TimeSpan.Zero);

    /// <summary>The same wall-clock schedule in winter, where Rome is +01:00.</summary>
    private static readonly DateTimeOffset WinterNow = new(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);

    private readonly ExecutionContextRecorder _recorder = new();

    private Task<IHost> CreateHostAsync(DateTimeOffset now, bool startHost = true,
                                        Action<EverTaskServiceConfiguration>? configureEverTask = null,
                                        ITaskStorage? sharedStorage = null) =>
        CreateIsolatedHostWithBuilderAsync(
            builder =>
            {
                if (sharedStorage != null)
                    builder.Services.AddSingleton(sharedStorage);
                else
                    builder.AddMemoryStorage();

                builder.Services.AddSingleton(_recorder);
            },
            startHost,
            configureEverTask,
            clock: new FakeTimeProvider(now));

    private async Task<QueuedTask> RowOf(Guid taskId) => (await Storage.Get(t => t.Id == taskId))[0];

    [Fact]
    public async Task A_zoned_schedule_persists_its_zone_and_parks_the_slot_the_zone_puts_it_at()
    {
        await CreateHostAsync(SummerNow);

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId),
            taskKey: "tz-summer");

        var row = await RowOf(id);

        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.TimeZoneId.ShouldBe(RomeId);
        row.RecurringInfo.ShouldBe("every 1 day(s) at 09:00 (Europe/Rome)");
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 2, 7, 0, 0, TimeSpan.Zero),
            "09:00 in Rome is 07:00Z while the zone is on summer time");
    }

    [Fact]
    public async Task The_same_zoned_schedule_parks_an_hour_later_in_winter()
    {
        // The pair the whole feature exists for: one definition, two instants, because the offset is read
        // from the zone at the slot rather than frozen when the schedule was written.
        await CreateHostAsync(WinterNow);

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId),
            taskKey: "tz-winter");

        (await RowOf(id)).NextRunUtc.ShouldBe(new DateTimeOffset(2026, 1, 6, 8, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_schedule_without_a_zone_is_persisted_and_parked_exactly_as_before()
    {
        // P1: the same dispatch, minus the zone, must produce the row it always produced — including a
        // schedule JSON with no new property in it.
        await CreateHostAsync(SummerNow);

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
            taskKey: "tz-absent");

        var row = await RowOf(id);

        row.RecurringTask!.Contains("TimeZoneId").ShouldBeFalse();
        row.RecurringInfo.ShouldBe("every 1 day(s) at 09:00");
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero), "09:00 UTC");
    }

    [Fact]
    public async Task A_handler_reads_the_zone_and_the_local_slot_of_its_own_delivery()
    {
        await CreateHostAsync(SummerNow);

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.RunNow().Then().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(RomeId),
            taskKey: "tz-context");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.For("Handle").Length >= 1,
            TestEnvironment.GetTimeout(8000, 30000));

        var snapshot = _recorder.For("Handle")[0];

        snapshot.TimeZoneId.ShouldBe(RomeId);
        snapshot.ScheduledAtUtc.ShouldBe(SummerNow);
        snapshot.ScheduledAtLocal.ShouldBe(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.FromHours(2)),
            "the local reading carries the offset, which is what tells the two passes of a fall-back apart");

        // The worker's own next-occurrence computation runs on the zone too, not just the dispatcher's.
        var row = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == id)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 1,
            timeoutMs: TestEnvironment.GetTimeout(8000, 30000));

        row.ShouldNotBeNull();
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 2, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_handler_of_a_schedule_without_a_zone_still_reads_nulls()
    {
        await CreateHostAsync(SummerNow);

        await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.RunNow().Then().EveryDay().AtTime(new TimeOnly(9, 0)),
            taskKey: "tz-context-absent");

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.For("Handle").Length >= 1,
            TestEnvironment.GetTimeout(8000, 30000));

        var snapshot = _recorder.For("Handle")[0];

        snapshot.TimeZoneId.ShouldBeNull();
        snapshot.ScheduledAtLocal.ShouldBeNull();
    }

    [Fact]
    public async Task The_default_zone_is_stamped_on_a_calendar_schedule_that_did_not_name_one()
    {
        await CreateHostAsync(SummerNow, configureEverTask: cfg => cfg.SetDefaultScheduleTimeZone(Rome));

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)),
            taskKey: "tz-default");

        var row = await RowOf(id);

        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.TimeZoneId.ShouldBe(RomeId,
            "the default is written INTO the definition, so the row keeps meaning this after the default changes");
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 2, 7, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_default_zone_leaves_a_plain_cadence_alone()
    {
        // A cadence is the same set of instants in every zone, and a zone on one is refused at validation —
        // so stamping the default onto it would turn a global default into a dispatch failure.
        await CreateHostAsync(SummerNow, configureEverTask: cfg => cfg.SetDefaultScheduleTimeZone(Rome));

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().Every(30).Minutes(),
            taskKey: "tz-default-cadence");

        var row = await RowOf(id);

        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.TimeZoneId.ShouldBeNull();
        row.NextRunUtc.ShouldBe(SummerNow.AddMinutes(30));
    }

    [Fact]
    public async Task The_default_zone_is_stamped_on_a_day_cadence_too()
    {
        // Decisions §3.4: a day, week or month CADENCE is calendar-anchored — it snaps to midnight when no
        // time of day was named — so the default zone reaches it and local midnight, not UTC midnight, is
        // what the row is parked at.
        await CreateHostAsync(SummerNow, configureEverTask: cfg => cfg.SetDefaultScheduleTimeZone(Rome));

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().Every(3).Days(),
            taskKey: "tz-default-day-cadence");

        var row = await RowOf(id);

        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.TimeZoneId.ShouldBe(RomeId);
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 3, 22, 0, 0, TimeSpan.Zero),
            "midnight on 4 July in Rome, an hour before the UTC midnight the zone-less grid would have used");
    }

    [Fact]
    public async Task A_day_cadence_takes_an_explicit_zone_instead_of_refusing_it()
    {
        // The symmetric half: InTimeZone on `Every(n).Days()` is accepted, where a cadence in seconds,
        // minutes or hours would have thrown when the schedule was built.
        await CreateHostAsync(SummerNow);

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().Every(3).Days().InTimeZone(RomeId),
            taskKey: "tz-day-cadence-explicit");

        var row = await RowOf(id);

        row.RecurringInfo.ShouldBe("every 3 day(s) at 00:00 (Europe/Rome)",
            "the midnight the interval defaulted to is exactly what the zone is being asked to read");
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 3, 22, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_schedule_handed_straight_to_the_dispatcher_is_persisted_with_the_IANA_id()
    {
        // T2 through the entry point the fluent builder does not own: ExecuteDispatch is public and takes a
        // RecurringTask built by hand, so nothing normalized the zone before this dispatch. A Windows id
        // written into the row verbatim resolves to nothing on a Linux replica of the same deployment.
        await CreateHostAsync(SummerNow);

        var dispatcher = (EverTask.Dispatcher.Dispatcher)Host!.Services.GetRequiredService<ITaskDispatcher>();

        var schedule = new RecurringTask
        {
            DayInterval = new Scheduler.Recurring.Intervals.DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId  = "W. Europe Standard Time"
        };

        var id = await dispatcher.ExecuteDispatch(new ContextRecurringTask(), null, schedule);

        var row = await RowOf(id);

        EverTaskJson.Deserialize<RecurringTask>(row.RecurringTask!)!.TimeZoneId.ShouldBe("Europe/Berlin",
            "the CLDR mapping of that Windows zone, which is the spelling every host resolves");
        row.RecurringInfo.ShouldBe("every 1 day(s) at 09:00 (Europe/Berlin)");
        row.NextRunUtc.ShouldBe(new DateTimeOffset(2026, 7, 2, 7, 0, 0, TimeSpan.Zero),
            "09:00 in Berlin is 07:00Z while the zone is on summer time");
    }

    [Fact]
    public async Task An_explicit_zone_wins_over_the_default()
    {
        await CreateHostAsync(SummerNow, configureEverTask: cfg => cfg.SetDefaultScheduleTimeZone(Rome));

        var id = await Dispatcher.Dispatch(new ContextRecurringTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Asia/Kolkata"),
            taskKey: "tz-explicit");

        EverTaskJson.Deserialize<RecurringTask>((await RowOf(id)).RecurringTask!)!
                    .TimeZoneId.ShouldBe("Asia/Kolkata");
    }

    [Fact]
    public async Task Dispatching_a_plain_cadence_with_a_zone_fails_before_anything_is_persisted()
    {
        await CreateHostAsync(SummerNow);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Dispatcher.Dispatch(new ContextRecurringTask(),
                r => r.Schedule().Every(30).Minutes().InTimeZone(RomeId),
                taskKey: "tz-cadence"));

        (await Storage.GetAll()).ShouldBeEmpty("a schedule that cannot run must not leave a row behind");
    }

    [Fact]
    public async Task Dispatching_with_an_unresolvable_zone_fails_at_the_builder()
    {
        await CreateHostAsync(SummerNow);

        await Should.ThrowAsync<ArgumentException>(() =>
            Dispatcher.Dispatch(new ContextRecurringTask(),
                r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Mars/Olympus_Mons"),
                taskKey: "tz-unresolvable"));

        (await Storage.GetAll()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_restart_recomputes_a_zoned_schedule_on_its_own_zone()
    {
        // The row is the only thing that survives: if the zone did not round-trip through it, the realigned
        // cursor would come back an hour off and stay there.
        await CreateHostAsync(WinterNow, startHost: false);

        var taskId = Guid.NewGuid();
        var schedule = new RecurringTask
        {
            DayInterval = new Scheduler.Recurring.Intervals.DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] },
            TimeZoneId  = RomeId
        };

        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(schedule),
            NextRunUtc      = WinterNow.AddDays(-3), // well past: recovery has to realign
            CurrentRunCount = 3,
            CreatedAtUtc    = WinterNow.AddDays(-10)
        });

        await Host!.StartAsync();

        // Recovery re-parks the row in the scheduler rather than rewriting it, so the realigned slot is
        // observed where it matters: the delivery it produces once the clock reaches it.
        //
        // Wait for THIS row's registration, not merely for a timer to exist: the scheduler arms its own idle
        // wait the moment it starts, so under load "one pending timer" can be satisfied before recovery has
        // realigned anything — and the advance below then moves the clock past 09:00 while the realignment is
        // still choosing its slot, which lands it a day late.
        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        await TaskWaitHelper.WaitForConditionAsync(() => scheduler.IsScheduled(taskId),
            TestEnvironment.GetTimeout(10000, 30000));

        var clock = (FakeTimeProvider)Clock;
        (await clock.WaitForPendingTimersAsync(1)).ShouldBeTrue("the recovered occurrence must be parked");
        clock.Advance(TimeSpan.FromDays(2));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.For("Handle").Length >= 1,
            TestEnvironment.GetTimeout(10000, 30000));

        var snapshot = _recorder.For("Handle")[0];

        snapshot.ScheduledAtUtc.ShouldBe(new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero),
            "realignment lands on the first 09:00 Rome after the restart, which in January is 08:00Z");
        snapshot.ScheduledAtLocal.ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.FromHours(1)));
        snapshot.TimeZoneId.ShouldBe(RomeId, "the zone came back out of the row");
        snapshot.RunNumber.ShouldBe(4, "the fourth run of a series that had already recorded three");
    }

    [Fact]
    public async Task A_restart_under_a_default_zone_leaves_a_row_that_named_none_on_UTC()
    {
        // T4, the half that decides whether turning the default on is safe: the default is stamped onto a
        // schedule when it is BUILT, so a row dispatched before it existed keeps meaning UTC. Re-applying it
        // on the way back out of storage would move every stored calendar schedule by the zone's offset, at
        // the restart that followed the configuration change and without anything in the row saying so.
        await CreateHostAsync(WinterNow, startHost: false,
            configureEverTask: cfg => cfg.SetDefaultScheduleTimeZone(Rome));

        var taskId   = Guid.NewGuid();
        var schedule = new RecurringTask
        {
            DayInterval = new Scheduler.Recurring.Intervals.DayInterval(1) { OnTimes = [new TimeOnly(9, 0)] }
        };

        await Storage.Persist(new QueuedTask
        {
            Id              = taskId,
            Type            = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request         = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,
            IsRecurring     = true,
            RecurringTask   = EverTaskJson.Serialize(schedule),
            NextRunUtc      = WinterNow.AddDays(-3), // well past: recovery has to realign
            CurrentRunCount = 3,
            CreatedAtUtc    = WinterNow.AddDays(-10)
        });

        await Host!.StartAsync();

        var scheduler = Host!.Services.GetRequiredService<IScheduler>();
        await TaskWaitHelper.WaitForConditionAsync(() => scheduler.IsScheduled(taskId),
            TestEnvironment.GetTimeout(10000, 30000));

        var clock = (FakeTimeProvider)Clock;
        (await clock.WaitForPendingTimersAsync(1)).ShouldBeTrue("the recovered occurrence must be parked");
        clock.Advance(TimeSpan.FromDays(2));

        await TaskWaitHelper.WaitForConditionAsync(() => _recorder.For("Handle").Length >= 1,
            TestEnvironment.GetTimeout(10000, 30000));

        var snapshot = _recorder.For("Handle")[0];

        snapshot.TimeZoneId.ShouldBeNull("the row named no zone, and recovery does not name one for it");
        snapshot.ScheduledAtLocal.ShouldBeNull();
        snapshot.ScheduledAtUtc.ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero),
            "09:00 still means 09:00 UTC, not the 08:00Z the Rome default would have made of it");

        var row = await RowOf(taskId);
        row.RecurringTask!.Contains("TimeZoneId").ShouldBeFalse("and the stored definition is not rewritten");
    }

    [Fact]
    public async Task A_row_whose_zone_no_longer_resolves_is_poisoned_terminally()
    {
        // T10: the same verdict corrupt schedule metadata gets. Leaving it recoverable would mean a row that
        // fails every restart forever; running it without the zone would mean silently wrong times.
        await CreateHostAsync(WinterNow, startHost: false);

        var taskId = Guid.NewGuid();

        await Storage.Persist(new QueuedTask
        {
            Id            = taskId,
            Type          = typeof(ContextRecurringTask).AssemblyQualifiedName!,
            Request       = EverTaskJson.Serialize(new ContextRecurringTask()),
            Handler       = "seeded-by-test",
            Status        = QueuedTaskStatus.Completed,
            IsRecurring   = true,
            RecurringTask =
                """{"DayInterval":{"Interval":1,"OnTimes":["09:00:00"],"OnDays":[]},"TimeZoneId":"Mars/Olympus_Mons"}""",
            NextRunUtc    = WinterNow.AddMinutes(-1),
            CreatedAtUtc  = WinterNow.AddMinutes(-30)
        });

        await Host!.StartAsync();

        var poisoned = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == taskId)).FirstOrDefault(),
            task => task?.Status == QueuedTaskStatus.Failed,
            timeoutMs: TestEnvironment.GetTimeout(10000, 30000));

        poisoned.ShouldNotBeNull();
        poisoned.NextRunUtc.ShouldBeNull("a poisoned recurring row must not come back at the next restart");
        _recorder.Snapshots.ShouldBeEmpty("and its handler must never run on a zone that is not there");
    }
}
