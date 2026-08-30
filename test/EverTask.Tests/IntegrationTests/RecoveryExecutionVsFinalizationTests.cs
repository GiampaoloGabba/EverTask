using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// X3, end-to-end on a real host and real storage: startup recovery now separates the rows that still have
/// work to EXECUTE from the recurring series that only need FINALIZING, and decides the grace window on the
/// NATURAL successor. These are the two latent defects the split fixes — a series whose <c>RunUntil</c>
/// elapsed during a downtime stayed <c>Queued</c> forever, and a months-old slot could be executed at
/// restart because "no successor before RunUntil" was read as "still current".
/// </summary>
public class RecoveryExecutionVsFinalizationTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    /// <summary>
    /// A calendar month is the widest grace window the natural successor can grant, and "a month from the
    /// stored slot" is not a fixed number of days — so the scenario only reads the same way every day of the
    /// year if "now" is fixed too.
    /// </summary>
    private static readonly DateTimeOffset FrozenNow = new(2026, 5, 20, 12, 0, 0, TimeSpan.Zero);

    private async Task StartHostWithoutConsumersAsync(TimeProvider? clock = null) =>
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false,
            clock: clock);

    private async Task<QueuedTask> SeedSeriesAsync(RecurringTask recurring, DateTimeOffset nextRunUtc,
                                                   QueuedTaskStatus status = QueuedTaskStatus.Completed,
                                                   int currentRunCount = 1,
                                                   DateTimeOffset? createdAtUtc = null)
    {
        var seeded = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(ResilienceRecurringTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new ResilienceRecurringTask()),
            Handler         = "seeded-by-test",
            Status          = status,
            IsRecurring     = true,
            RecurringTask   = JsonConvert.SerializeObject(recurring),
            NextRunUtc      = nextRunUtc,
            RunUntil        = recurring.RunUntil,
            MaxRuns         = recurring.MaxRuns,
            CurrentRunCount = currentRunCount,
            CreatedAtUtc    = createdAtUtc ?? DateTimeOffset.UtcNow.AddDays(-200)
        };

        await Storage.Persist(seeded);
        return seeded;
    }

    private async Task<QueuedTask> WaitForRowAsync(Guid id, Func<QueuedTask, bool> predicate, int timeoutMs = 8000)
    {
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        var row      = (await Storage.Get(t => t.Id == id))[0];

        while (DateTimeOffset.UtcNow < deadline && !predicate(row))
        {
            await Task.Delay(50);
            row = (await Storage.Get(t => t.Id == id))[0];
        }

        return row;
    }

    [Fact]
    public async Task Should_skip_forward_over_an_excluded_weekend_during_recovery()
    {
        var monday = new DateTimeOffset(2026, 8, 31, 13, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var clock  = new FakeTimeProvider(monday);

        await StartHostWithoutConsumersAsync(clock);

        var recurring = DailyAtNoonExceptWeekends();
        var seeded = await SeedSeriesAsync(recurring, friday, QueuedTaskStatus.Queued,
            createdAtUtc: monday.AddDays(-30));

        await Host!.StartAsync();
        await Task.Delay(300);

        _state.ExecutedIndexes.ShouldBeEmpty("the stale Friday slot and excluded weekend slots do not run");

        clock.Advance(TimeSpan.FromHours(23));
        await WaitForRowAsync(seeded.Id, row => (row.CurrentRunCount ?? 0) == 2, 20000);

        _state.ExecutedIndexes.Count.ShouldBe(1,
            "the first delivery after realignment is Tuesday's grid slot");
    }

    [Fact]
    public async Task Should_grant_recovery_grace_when_the_natural_successor_crosses_excluded_days()
    {
        var sunday = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var friday = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

        await StartHostWithoutConsumersAsync(new FakeTimeProvider(sunday));

        var seeded = await SeedSeriesAsync(DailyAtNoonExceptWeekends(), friday, QueuedTaskStatus.Queued,
            createdAtUtc: sunday.AddDays(-30));

        await Host!.StartAsync();
        await WaitForRowAsync(seeded.Id, row => (row.CurrentRunCount ?? 0) == 2, 20000);

        _state.ExecutedIndexes.Count.ShouldBe(1,
            "Monday is the natural successor, so Friday remains the current slot throughout the weekend");
    }

    [Fact]
    public async Task Should_preserve_a_RunAt_override_on_an_excluded_day_during_recovery()
    {
        var saturday = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var sunday   = saturday.AddDays(1);
        var recurring = DailyAtNoonExceptWeekends();
        recurring.SpecificRunTime = saturday;

        await StartHostWithoutConsumersAsync(new FakeTimeProvider(sunday));

        var seeded = await SeedSeriesAsync(recurring, saturday, QueuedTaskStatus.Queued, currentRunCount: 0,
            createdAtUtc: sunday.AddDays(-30));

        await Host!.StartAsync();
        await WaitForRowAsync(seeded.Id, row => (row.CurrentRunCount ?? 0) == 1, 20000);

        _state.ExecutedIndexes.Count.ShouldBe(1,
            "the Saturday cursor came from RunAt and is not a recurring grid slot to filter");
    }

    private static RecurringTask DailyAtNoonExceptWeekends() => new()
    {
        DayInterval = new DayInterval(1) { OnTimes = [new TimeOnly(12, 0)] },
        Exclusions = new ScheduleExclusions
        {
            Days = [DayOfWeek.Saturday, DayOfWeek.Sunday]
        }
    };

    [Fact]
    public async Task A_series_whose_RunUntil_elapsed_during_the_downtime_is_finalized_instead_of_staying_queued()
    {
        await StartHostWithoutConsumersAsync();

        var now = DateTimeOffset.UtcNow;
        // The pending slot sits AT or past the boundary: nothing is left to run, so the row is a
        // finalization. Before the split it matched no predicate at all and stayed Queued forever.
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
            RunUntil    = now.AddDays(-10)
        };
        var seeded = await SeedSeriesAsync(recurring, now.AddDays(-5), status: QueuedTaskStatus.Queued);

        seeded.IsRecurringSeriesToFinalize().ShouldBeTrue();
        seeded.IsRecoverableForExecution(now).ShouldBeFalse();

        await Host!.StartAsync();

        var row = await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);

        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.NextRunUtc.ShouldBeNull("the cursor is what kept the zombie alive across restarts");
        row.CurrentRunCount.ShouldBe(1, "finalizing is not a run");
        _state.ExecutedIndexes.ShouldBeEmpty("no handler may run for a series that is over");
        (row.RecoveryDispatchFailureCount ?? 0).ShouldBe(0);
    }

    [Fact]
    public async Task A_finalized_series_records_the_transition_and_no_run()
    {
        await StartHostWithoutConsumersAsync();

        var now = DateTimeOffset.UtcNow;
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
            RunUntil    = now.AddDays(-10)
        };
        var seeded = await SeedSeriesAsync(recurring, now.AddDays(-5), status: QueuedTaskStatus.Queued);

        await Host!.StartAsync();
        await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);

        var row = (await Storage.Get(t => t.Id == seeded.Id))[0];
        row.StatusAudits.Select(a => a.NewStatus)
           .ShouldContain(QueuedTaskStatus.Completed, "the finalization must be visible in the audit trail");
        row.StatusAudits.Select(a => a.NewStatus)
           .ShouldNotContain(QueuedTaskStatus.Queued,
               "the row is finalized without ever being handed to a worker queue, so no phantom Queued");
        row.RunsAudits.ShouldBeEmpty("no run happened");
    }

    [Fact]
    public async Task An_occurrence_scheduled_before_an_elapsed_RunUntil_is_still_executed()
    {
        await StartHostWithoutConsumersAsync();

        var now = DateTimeOffset.UtcNow;
        // The occurrence was scheduled BEFORE the boundary and the boundary elapsed during the downtime.
        // Grouping the temporal term is what keeps this slot recoverable; the natural successor (30 days
        // after the slot) is still in the future, so it is also still the CURRENT slot.
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(30, []),
            RunUntil    = now.AddDays(-10)
        };
        var seeded = await SeedSeriesAsync(recurring, now.AddDays(-20), status: QueuedTaskStatus.Queued);

        seeded.IsRecoverableForExecution(now).ShouldBeTrue();
        seeded.IsRecurringSeriesToFinalize().ShouldBeFalse();

        await Host!.StartAsync();

        await WaitForRowAsync(seeded.Id, _ => !_state.ExecutedIndexes.IsEmpty);

        _state.ExecutedIndexes.Count.ShouldBe(1,
            "the occurrence already scheduled before the boundary must not be silently lost");

        var row = await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);
        row.NextRunUtc.ShouldBeNull("and the series ends right after it, since the boundary has passed");
    }

    [Fact]
    public async Task A_monthly_occurrence_scheduled_before_an_elapsed_RunUntil_is_still_executed()
    {
        // The same shape as the test above on a genuine CALENDAR cadence, which is the case the natural
        // successor exists for: a month is not a fixed span, so no flat "one period" arithmetic can decide
        // this. Slot on 10 May, boundary on 15 May, restart on 20 May: the slot precedes the boundary, so it
        // is still owed, and its natural successor — 10 June, computed with the bounds ignored — is a month
        // away, so the grace window is that wide and the slot is still the current one.
        var clock = new FakeTimeProvider(FrozenNow);
        await StartHostWithoutConsumersAsync(clock);

        var recurring = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 10, OnTimes = [new TimeOnly(9, 0)] },
            RunUntil      = new DateTimeOffset(2026, 5, 15, 0, 0, 0, TimeSpan.Zero)
        };
        var slot = new DateTimeOffset(2026, 5, 10, 9, 0, 0, TimeSpan.Zero);

        var seeded = await SeedSeriesAsync(recurring, slot, status: QueuedTaskStatus.Queued,
            createdAtUtc: FrozenNow.AddDays(-200));

        seeded.IsRecoverableForExecution(FrozenNow).ShouldBeTrue();
        seeded.IsRecurringSeriesToFinalize().ShouldBeFalse();
        recurring.MonthInterval!.GetNextOccurrence(slot)
                 .ShouldBe(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
                     "the natural successor is a calendar month out, and it is in the future");

        await Host!.StartAsync();

        await WaitForRowAsync(seeded.Id, _ => !_state.ExecutedIndexes.IsEmpty);

        _state.ExecutedIndexes.Count.ShouldBe(1,
            "a monthly occurrence owed since before the boundary must not be dropped at restart");

        var row = await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);
        row.NextRunUtc.ShouldBeNull("and the series ends right after it, since the boundary has passed");
    }

    [Fact]
    public async Task A_series_whose_run_budget_is_spent_is_finalized_without_running_anything()
    {
        // The other half of category (ii), and the one no end-to-end test reached: the cursor is not merely
        // non-null, it is in the FUTURE — nothing about the clock says this row is over. Only the spent run
        // budget does, and before the split such a row matched no predicate at all and stayed Queued for ever.
        var clock = new FakeTimeProvider(FrozenNow);
        await StartHostWithoutConsumersAsync(clock);

        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
            MaxRuns     = 3
        };

        var seeded = await SeedSeriesAsync(recurring, FrozenNow.AddMinutes(5), status: QueuedTaskStatus.Queued,
            currentRunCount: 3, createdAtUtc: FrozenNow.AddDays(-200));

        seeded.IsRecurringSeriesToFinalize().ShouldBeTrue();
        seeded.IsRecoverableForExecution(FrozenNow)
              .ShouldBeFalse("the run budget is ANDed in front of everything else");

        await Host!.StartAsync();

        var row = await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);

        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        row.NextRunUtc.ShouldBeNull("the future cursor is exactly what kept this zombie recoverable");
        row.CurrentRunCount.ShouldBe(3, "finalizing consumes no run");
        _state.ExecutedIndexes.ShouldBeEmpty("the handler must never be reached for a spent series");
        (row.RecoveryDispatchFailureCount ?? 0).ShouldBe(0, "the end of a series is not a recovery failure");

        row.StatusAudits.Select(a => a.NewStatus).ShouldBe([QueuedTaskStatus.Completed],
            "one transition, Queued → Completed, and nothing before it: the row never reached a worker queue");
        row.RunsAudits.ShouldBeEmpty("no run happened");
    }

    [Fact]
    public async Task A_stale_slot_whose_successor_is_already_due_is_not_executed_at_restart()
    {
        await StartHostWithoutConsumersAsync();

        var now = DateTimeOffset.UtcNow;
        // Daily schedule, slot three months old: its natural successor came due long ago, so the slot is
        // stale. The bounded successor is null here (every later occurrence is past RunUntil), and reading
        // that null as "still current" is what used to execute a months-old occurrence at restart.
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
            RunUntil    = now.AddDays(-60)
        };
        var seeded = await SeedSeriesAsync(recurring, now.AddDays(-90), status: QueuedTaskStatus.Queued);

        seeded.IsRecoverableForExecution(now).ShouldBeTrue("the slot precedes the boundary");

        await Host!.StartAsync();

        var row = await WaitForRowAsync(seeded.Id, r => r.NextRunUtc == null);

        row.NextRunUtc.ShouldBeNull();
        row.Status.ShouldBe(QueuedTaskStatus.Completed);
        _state.ExecutedIndexes.ShouldBeEmpty("a three-month-old slot must never run at restart");
    }

    [Fact]
    public async Task A_cancelled_series_is_neither_executed_nor_finalized()
    {
        await StartHostWithoutConsumersAsync();

        var now = DateTimeOffset.UtcNow;
        var recurring = new RecurringTask
        {
            DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] },
            RunUntil    = now.AddDays(-10)
        };
        var seeded = await SeedSeriesAsync(recurring, now.AddDays(-5), status: QueuedTaskStatus.Cancelled);

        await Host!.StartAsync();
        await Task.Delay(500);

        var row = (await Storage.Get(t => t.Id == seeded.Id))[0];
        row.Status.ShouldBe(QueuedTaskStatus.Cancelled, "the user's choice is already terminal");
        row.NextRunUtc.ShouldNotBeNull("nothing rewrote the cancelled row");
        _state.ExecutedIndexes.ShouldBeEmpty();
    }
}
