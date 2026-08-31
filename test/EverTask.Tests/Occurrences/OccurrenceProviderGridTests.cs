using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Tests.Occurrences;

/// <summary>
/// The grid a schedule gets when its occurrences come from an <see cref="INextOccurrenceProvider"/> (V1/V3).
/// </summary>
/// <remarks>
/// Everything here runs against a REAL container: the evaluator, the registry and the provider are the
/// registered ones, resolved per call in their own scope, and what a test steers is the calendar the provider
/// reads — never the seam under test. There is no host, because nothing here needs a delivery: these are the
/// answers the grid gives, and what the pipeline does with them is
/// <see cref="IntegrationTests.OccurrenceProviderIntegrationTests"/>.
/// </remarks>
public class OccurrenceProviderGridTests
{
    private const string Key              = "probe";
    private const string DeterministicKey = "probe-deterministic";
    private const string AsyncScopeKey    = "probe-async-scope";

    private static readonly DateTimeOffset Origin = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    #region What the provider is asked

    [Fact]
    public async Task The_provider_answers_the_grid_and_is_told_which_schedule_is_asking()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromHours(1), Origin);

        var definition = ProviderSchedule(config: "{\"calendar\":\"IT\"}", zone: "Europe/Rome");
        var scheduleId = Guid.NewGuid();

        var next = await evaluator.NextAfterAsync(definition, Origin, Origin,
            new ScheduleIdentity(scheduleId, "digest", 7));

        next.ShouldBe(Origin.AddHours(1));

        var request = probe.Snapshot().ShouldHaveSingleItem();
        request.ProviderKey.ShouldBe(Key);
        request.Config.ShouldBe("{\"calendar\":\"IT\"}");
        request.AfterUtc.ShouldBe(Origin);
        request.TimeZoneId.ShouldBe("Europe/Rome");
        request.TaskKey.ShouldBe("digest");
        request.ScheduleId.ShouldBe(scheduleId);
        request.RunNumber.ShouldBe(7);
    }

    [Fact]
    public async Task A_provider_that_has_no_further_occurrence_ends_the_series()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = _ => null;

        var result = await evaluator.CalculateNextValidRunAsync(ProviderSchedule(), Origin, 3, Origin);

        result.NextRun.ShouldBeNull();
    }

    [Fact]
    public async Task An_answer_at_or_before_the_instant_asked_about_is_refused_as_a_broken_contract()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = request => request.AfterUtc;

        var failure = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin));

        failure.ProviderKey.ShouldBe(Key);
        failure.Message.ShouldContain("strictly later");
    }

    [Fact]
    public async Task An_unregistered_key_is_a_configuration_error_and_not_a_transient_one()
    {
        var (evaluator, _, _) = BuildGrid();

        var definition = ProviderSchedule();
        definition.Provider!.Key = "nobody-registers-this";

        var refusal = await Should.ThrowAsync<ArgumentException>(async () =>
            await evaluator.NextAfterAsync(definition, Origin, Origin));

        refusal.Message.ShouldContain("nobody-registers-this");
        refusal.Message.ShouldContain(Key, Case.Sensitive);
    }

    [Fact]
    public async Task A_shutdown_that_cancels_the_call_is_not_counted_as_a_provider_failure()
    {
        var (evaluator, probe, provider) = BuildGrid();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        probe.Fault = () => new OperationCanceledException(cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, default, cts.Token));

        // Nothing was recorded against the schedule: a shutdown is not an outage of its calendar.
        var retries = provider.GetRequiredService<OccurrenceProviderRetryRegistry>();
        retries.RecordFailure(Guid.NewGuid()).Failures.ShouldBe(1);
    }

    [Fact]
    public async Task A_provider_whose_dependencies_are_async_disposable_answers_like_any_other()
    {
        var (evaluator, probe, provider) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromHours(1), Origin);

        var definition = ProviderSchedule(key: AsyncScopeKey);
        var identity   = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);

        // The provider is resolved in a scope, and its calendar implements only IAsyncDisposable: a
        // synchronous disposal of that scope throws AFTER the answer is already in hand, which the classifier
        // above would read as a provider that failed. The schedule would then re-park and ask again for ever
        // while every log line blamed a provider that never failed.
        (await evaluator.NextAfterAsync(definition, Origin, Origin, identity)).ShouldBe(Origin.AddHours(1));

        (await evaluator.NextAfterAsync(definition, Origin.AddHours(1), Origin.AddHours(1), identity))
            .ShouldBe(Origin.AddHours(2));

        // And nothing was recorded against the schedule, which is the other half of the same statement.
        var retries = provider.GetRequiredService<OccurrenceProviderRetryRegistry>();
        retries.RecordFailure(identity.ScheduleId).Failures.ShouldBe(1);
    }

    [Fact]
    public async Task Each_call_builds_the_provider_in_a_scope_of_its_own_and_releases_it()
    {
        var (evaluator, probe, provider) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromHours(1), Origin);

        var definition = ProviderSchedule(key: AsyncScopeKey);
        var identity   = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);

        // "Resolved in a fresh scope per call" is what lets a provider depend on scoped services at all, and
        // it is invisible from the answers: a container scope built once and reused would return exactly the
        // same slots. What tells the two apart is WHICH objects answered — two calls, two calendars, and both
        // released when the call that built them was over. A reused scope shows up as the same instance twice
        // and nothing disposed; a scope never disposed shows up as two instances and no disposals, which is a
        // DbContext and its connection leaked per occurrence of every provider-driven schedule.
        await evaluator.NextAfterAsync(definition, Origin, Origin, identity);
        await evaluator.NextAfterAsync(definition, Origin.AddHours(1), Origin.AddHours(1), identity);

        var ledger = provider.GetRequiredService<ProviderScopeLedger>();

        ledger.Built.Count.ShouldBe(2, "the calendar really answered both calls");
        ledger.Built.Distinct().Count().ShouldBe(2, "and it was a different one each time");
        ledger.Disposals.ShouldBe(2, "each scope was released when its own question was answered");
    }

    #endregion

    #region The bounds

    [Fact]
    public async Task RunUntil_bounds_the_provider_grid_while_the_natural_successor_ignores_it()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromHours(1), Origin);

        var definition = ProviderSchedule();
        definition.RunUntil = Origin.AddMinutes(30);

        (await evaluator.NextAfterAsync(definition, Origin, Origin)).ShouldBeNull();

        // The recovery grace window asks the grid itself, bounds ignored — that is how it tells "the slot is
        // still current" from "the series is over".
        (await evaluator.NextGridOccurrenceAfterAsync(definition, Origin)).ShouldBe(Origin.AddHours(1));
    }

    [Fact]
    public async Task The_first_occurrence_on_or_after_an_instant_includes_that_instant()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromHours(1), Origin);

        // Origin is itself a slot of that grid: the backfill cursor is the one question answered inclusively.
        (await evaluator.FirstOccurrenceOnOrAfterAsync(ProviderSchedule(), Origin)).ShouldBe(Origin);

        (await evaluator.FirstOccurrenceOnOrAfterAsync(ProviderSchedule(), Origin.AddMinutes(1)))
            .ShouldBe(Origin.AddHours(1));
    }

    [Fact]
    public async Task A_count_over_a_provider_grid_stops_at_the_cap_it_was_given()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // Twenty slots are due, but the caller asked for at most five: the answer is cap + 1, which is what
        // tells "more than five" from "exactly five" without walking the backlog.
        var counted = await evaluator.CountMissedAsync(ProviderSchedule(), Origin, Origin.AddMinutes(20), 5);

        counted.ShouldBe(6);
    }

    [Fact]
    public async Task An_uncapped_count_falls_back_to_the_walk_a_provider_can_afford()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromSeconds(1), Origin);

        // int.MaxValue means "no cap", which is not a thing a grid made of round trips can be asked for.
        var counted = await evaluator.CountMissedAsync(ProviderSchedule(), Origin, Origin.AddDays(1), int.MaxValue);

        counted.ShouldBe(ProviderScheduleGrid.MaxDiagnosticWalk + 1);
        probe.Calls.ShouldBeLessThanOrEqualTo(ProviderScheduleGrid.MaxDiagnosticWalk + 1);
    }

    [Fact]
    public async Task The_due_slots_of_a_provider_are_walked_oldest_first_and_stop_at_now()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(10), Origin);

        var slots = await evaluator.EnumerateDueSlotsAsync(ProviderSchedule(), Origin, Origin.AddMinutes(25), 10);

        slots.ShouldBe([Origin, Origin.AddMinutes(10), Origin.AddMinutes(20)]);
    }

    [Fact]
    public async Task Keeping_the_newest_slots_of_a_provider_backlog_costs_probes_and_not_an_enumeration()
    {
        var (evaluator, probe, _) = BuildGrid();

        // A daily grid ten days behind: eleven slots are due, the cap keeps three. The range is wide in TICKS
        // and short in SLOTS, which is exactly the shape that separates a bisection that stops when it has
        // the answer from one that narrows to the tick — the second spends its whole 64-probe budget, and
        // every probe here is a pair of round trips against the application's own calendar.
        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromDays(1), Origin);

        var definition = ProviderSchedule(key: DeterministicKey);
        definition.OccurrenceMode = OccurrenceMode.Durable;
        definition.Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromDays(365),
            MaxOccurrences        = 3,
            OverflowPolicy        = CatchUpOverflowPolicy.SkipOldest,
            MaxPendingOccurrences = 3
        };

        var now  = Origin.AddDays(10);
        var plan = await new DueSlotEnumerator(evaluator, TimeSpan.FromSeconds(5))
                         .PlanAsync(definition, Origin, now, 0, 0,
                             new ScheduleIdentity(Guid.NewGuid(), "digest", 1));

        // The answer first: a cheap bound over a wrong result would be no improvement at all.
        plan.Slots.ShouldBe([Origin.AddDays(8), Origin.AddDays(9), Origin.AddDays(10)]);
        plan.Losses.ShouldHaveSingleItem().Count.ShouldBe(8);
        plan.Misfire!.Value.MissedThroughUtc.ShouldBe(now);

        probe.Calls.ShouldBeLessThan(60,
            "two bisections narrowed to the tick over a ten-day range are about forty probes each, and each " +
            "probe asks the provider more than once");
    }

    [Fact]
    public async Task Keeping_the_newest_slots_of_a_provider_backlog_never_costs_more_than_walking_it_once()
    {
        var (evaluator, probe, _) = BuildGrid();

        // The other half of the bound, and the one a cap makes expensive: bounding the NUMBER of probes says
        // nothing about what a probe costs, and each of them counts up to the cap one round trip at a time.
        // Sized the way the docs say to size a cap — "the way you would size a batch" — the two multiplied:
        // this backlog against this cap cost 6,118 questions, an order of magnitude more than the cap the
        // reader was told to size. The probes walk the same stretch of chain over and over, so the walk is
        // remembered across them, and the same plan now costs 2,032.
        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        var definition = ProviderSchedule(key: DeterministicKey);
        definition.OccurrenceMode = OccurrenceMode.Durable;
        definition.Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromDays(30),
            MaxOccurrences        = 500,
            OverflowPolicy        = CatchUpOverflowPolicy.SkipOldest,
            MaxPendingOccurrences = 1
        };

        const int dueSlots = 3001;

        var now  = Origin.AddMinutes(dueSlots - 1);
        var plan = await new DueSlotEnumerator(evaluator, TimeSpan.FromSeconds(5))
                         .PlanAsync(definition, Origin, now, 0, 0,
                             new ScheduleIdentity(Guid.NewGuid(), "digest", 1));

        // The answer first, as above: the newest 500 slots begin here, and the budget of one takes the oldest
        // of them.
        plan.Slots.ShouldBe([now.AddMinutes(-499)]);
        plan.Losses.ShouldHaveSingleItem().Reason.ShouldBe(SlotLossReason.CatchUpOverflow);

        probe.Calls.ShouldBeLessThan(dueSlots,
            "a search that never asks about the same instant twice costs at most one question per slot it " +
            "looks at: the bisection exists to be cheaper than enumerating the backlog, and it must never " +
            "cost more");
    }

    [Fact]
    public async Task The_realignment_past_a_downtime_lands_on_the_next_slot_the_provider_offers()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(10), Origin);

        var now = Origin.AddHours(1);

        // The stored cursor is an hour behind: the same skip-forward every other grid gets, with the count of
        // what the downtime cost travelling for the log line.
        var result = await evaluator.CalculateNextValidRunAsync(ProviderSchedule(), Origin, 1, now,
            isRecovery: true);

        result.NextRun.ShouldBe(now.AddMinutes(10));
        result.SkippedCount.ShouldBe(7);
        result.SkippedCountIsExact.ShouldBeTrue("seven slots is well inside what a provider grid may walk");
    }

    [Fact]
    public async Task A_downtime_longer_than_the_walk_a_provider_can_afford_reports_a_lower_bound()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // A day of downtime on a per-minute calendar: 1,440 slots were really missed. The count goes to a log
        // line and to nothing else, so it stops at the bound a grid of round trips can afford — and the whole
        // point is that it says so. Reported as a total, "251" is simply a wrong number in an operator's
        // incident log, and a lie that gets less true the longer the outage was.
        var now = Origin.AddDays(1);

        var asked  = probe.Calls;
        var result = await evaluator.CalculateNextValidRunAsync(ProviderSchedule(), Origin, 1, now,
            isRecovery: true);

        result.NextRun.ShouldBe(now.AddMinutes(1), "the realignment itself is unaffected by the bound");
        result.SkippedCount.ShouldBe(ProviderScheduleGrid.MaxDiagnosticWalk + 1,
            "one past the bound, which is where a bounded count stops");
        result.SkippedCountIsExact.ShouldBeFalse("so it travels as 'at least this many' and never as a total");

        (probe.Calls - asked).ShouldBeLessThan(ProviderScheduleGrid.MaxDiagnosticWalk * 2,
            "and the bound is what it exists for: a day of a per-minute calendar is not walked one query at " +
            "a time to produce a log line");
    }

    #endregion

    #region What a replay costs

    [Fact]
    public async Task A_replay_measures_its_backlog_once_instead_of_re_walking_it_for_every_occurrence()
    {
        var (evaluator, probe, _) = BuildGrid();

        // A minute grid an hour behind, replayed the way the materializer replays one: a budget of one, so
        // each run creates a single occurrence and the next kick plans again from the cursor it left. The cap
        // is what an EPISODE may owe, and answering "is the backlog bigger than it?" costs one query per slot
        // — so asking it again per occurrence made the replay quadratic: sixty owed slots cost sixty queries
        // to materialize the first, fifty-nine for the second, all of it before a single row was written.
        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        const int backlog = 60;

        var definition = CatchUpProviderSchedule();
        var now        = Origin.AddMinutes(backlog - 1);

        // The old shape, exactly: an enumerator that remembers nothing between two runs.
        var perRunBefore = probe.Calls;
        var perRun       = await ReplayAsync(definition, Origin, now, backlog, () => Planner(evaluator));
        var perRunCalls  = probe.Calls - perRunBefore;

        // The one the materializer holds, which lives as long as the host does.
        var kept           = Planner(evaluator);
        var keptBefore     = probe.Calls;
        var continued      = await ReplayAsync(definition, Origin, now, backlog, () => kept);
        var continuedCalls = probe.Calls - keptBefore;

        // The answers first: a cheaper plan that decides differently would be no improvement at all. Slot by
        // slot, count by count, exactness by exactness — including the backlog each occurrence is stamped
        // with, which shrinks by one per run.
        continued.ShouldBe(perRun);
        continued.Count.ShouldBe(backlog);
        continued[0].Count.ShouldBe(backlog, "the first occurrence stands for the whole backlog");
        continued[^2].Count.ShouldBe(2, "the next to last stands for itself and the slot still owed");
        continued[^1].Count.ShouldBe(0, "the last slot came due at this very instant: nothing was missed");

        continuedCalls.ShouldBeLessThan(perRunCalls / 4,
            "the backlog is walked once and continued from where it was left, not re-walked per occurrence");
        continuedCalls.ShouldBeLessThan(backlog * 6,
            "and what a replay costs is bounded by the backlog itself, never by the backlog squared");
    }

    [Fact]
    public async Task A_plan_that_may_create_nothing_asks_the_calendar_nothing()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // The other half of the same cost: while a long occurrence holds the only slot of the budget, the
        // operational retry comes back every minute, and each of those runs used to walk the whole backlog to
        // materialize nothing.
        var definition = CatchUpProviderSchedule();
        var cursor     = Origin;
        var now        = Origin.AddHours(6);

        var before = probe.Calls;

        var plan = await Planner(evaluator).PlanAsync(definition, cursor, now, 0, activeOccurrences: 1,
            new ScheduleIdentity(Guid.NewGuid(), "digest", 1));

        plan.Slots.ShouldBeEmpty();
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
        plan.NextCursorUtc.ShouldBe(cursor, "the backlog stays exactly where it is");

        (probe.Calls - before).ShouldBe(0, "a run with no budget starts no replay, so it decides nothing");
    }

    [Fact]
    public async Task A_full_window_still_drops_what_the_age_window_has_left_behind()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // Dropping a slot for its age needs no capacity — materializing one does. So the early exit above sits
        // AFTER the age window, and a schedule whose budget is full still moves its cursor out of a window
        // that has gone past it, and still says how many runs that cost (P5).
        var definition = CatchUpProviderSchedule();
        definition.Misfire!.MaxAge = TimeSpan.FromMinutes(10);

        var now = Origin.AddMinutes(60);

        var plan = await Planner(evaluator).PlanAsync(definition, Origin, now, 0, activeOccurrences: 1,
            new ScheduleIdentity(Guid.NewGuid(), "digest", 1));

        plan.Slots.ShouldBeEmpty();
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
        plan.NextCursorUtc.ShouldBe(now.AddMinutes(-10), "the first slot still inside the window");

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.MisfireWindowExceeded);
        loss.Count.ShouldBe(50, "everything from the cursor up to the window, and the operator is told");
    }

    [Fact]
    public async Task A_backlog_that_grows_past_the_cap_between_two_runs_still_trips_the_breaker()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // A measurement carried forward must not become a verdict carried forward. Three slots against a cap
        // of five is an episode that runs; twenty minutes later the same schedule owes more than the cap, and
        // the breaker has to fire on the reading that continues the first one exactly as it would on a fresh
        // walk of the whole backlog.
        var definition = CatchUpProviderSchedule(maxOccurrences: 5);
        var identity   = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);
        var planner    = Planner(evaluator);

        var first = await planner.PlanAsync(definition, Origin, Origin.AddMinutes(2), 0, 0, identity);

        first.StopReason.ShouldBe(DueSlotStopReason.WindowFull, "three slots owed against a budget of one");
        first.Slots.ShouldBe([Origin]);

        var second = await planner.PlanAsync(definition, first.NextCursorUtc!.Value, Origin.AddMinutes(20), 1, 0,
            identity);

        second.StopReason.ShouldBe(DueSlotStopReason.Halted);
        second.Slots.ShouldBeEmpty();
        second.DetectedAtLeast.ShouldBe(6, "one past the cap, which is where a bounded count stops");
        second.IsExact.ShouldBeFalse();

        // And it is the same verdict, number for number, that a planner with nothing remembered gives.
        var fresh = await Planner(evaluator).PlanAsync(definition, first.NextCursorUtc.Value, Origin.AddMinutes(20),
            1, 0, identity);

        fresh.StopReason.ShouldBe(second.StopReason);
        fresh.DetectedAtLeast.ShouldBe(second.DetectedAtLeast);
        fresh.IsExact.ShouldBe(second.IsExact);
    }

    [Fact]
    public async Task A_cursor_that_is_not_where_the_last_plan_left_it_is_measured_again()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Answer = OccurrenceProviderProbe.GridOf(TimeSpan.FromMinutes(1), Origin);

        // A reading describes the backlog behind ONE cursor. A row rewound by hand, a reschedule, another host
        // winning the write, a run that walked past slots that already had occurrences: the cursor then is not
        // the one the reading resumes at, and the only sound answer is to measure again.
        var definition = CatchUpProviderSchedule();
        var identity   = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);
        var planner    = Planner(evaluator);
        var now        = Origin.AddMinutes(30);

        await planner.PlanAsync(definition, Origin.AddMinutes(10), now, 0, 0, identity);

        // Back to a cursor twenty slots BEHIND the one that plan left.
        var rewound = await planner.PlanAsync(definition, Origin, now, 0, 0, identity);
        var fresh   = await Planner(evaluator).PlanAsync(definition, Origin, now, 0, 0, identity);

        rewound.Slots.ShouldBe(fresh.Slots);
        rewound.NextCursorUtc.ShouldBe(fresh.NextCursorUtc);
        rewound.Misfire!.Value.MissedCount.ShouldBe(31, "the whole backlog again, counted from the rewound slot");
        rewound.Misfire.Value.MissedCount.ShouldBe(fresh.Misfire!.Value.MissedCount);
        rewound.Misfire.Value.MissedThroughUtc.ShouldBe(fresh.Misfire.Value.MissedThroughUtc);
    }

    #endregion

    #region A provider that cannot answer

    [Fact]
    public async Task A_provider_that_throws_is_transient_and_the_wait_grows_with_its_failures()
    {
        var (evaluator, probe, _) = BuildGrid();

        probe.Fault = () => new InvalidOperationException("the holiday table is unreachable");

        var identity = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);

        var first = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        first.RetryAfter.ShouldBe(TimeSpan.FromMinutes(1));
        first.ConsecutiveFailures.ShouldBe(1);
        first.InnerException.ShouldBeOfType<InvalidOperationException>();

        var second = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        second.RetryAfter.ShouldBe(TimeSpan.FromMinutes(2));
        second.ConsecutiveFailures.ShouldBe(2);
    }

    [Fact]
    public async Task One_answer_forgets_every_failure_before_it()
    {
        var (evaluator, probe, _) = BuildGrid();

        var identity = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);

        probe.Fault = () => new InvalidOperationException("down");

        await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));
        await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        probe.Fault = null;
        (await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity)).ShouldNotBeNull();

        probe.Fault = () => new InvalidOperationException("down again");

        var afterHealing = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        afterHealing.ConsecutiveFailures.ShouldBe(1);
        afterHealing.RetryAfter.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task The_backoff_a_host_configures_is_the_one_that_applies()
    {
        var (evaluator, probe, _) = BuildGrid(configure: cfg => cfg.SetOccurrenceProviderRetry(retry =>
        {
            retry.InitialBackoff = TimeSpan.FromSeconds(10);
            retry.MaxBackoff     = TimeSpan.FromSeconds(15);
        }));

        probe.Fault = () => new InvalidOperationException("down");

        var identity = new ScheduleIdentity(Guid.NewGuid(), "digest", 1);

        var first = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        first.RetryAfter.ShouldBe(TimeSpan.FromSeconds(10));

        var second = await Should.ThrowAsync<OccurrenceProviderException>(async () =>
            await evaluator.NextAfterAsync(ProviderSchedule(), Origin, Origin, identity));

        // Doubled to twenty, capped at the ceiling the host asked for.
        second.RetryAfter.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_backoff_that_is_not_positive_is_refused(int seconds)
    {
        var options = new OccurrenceProviderRetryOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.InitialBackoff = TimeSpan.FromSeconds(seconds));
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxBackoff = TimeSpan.FromSeconds(seconds));
    }

    [Fact]
    public void A_backoff_longer_than_a_day_is_refused()
    {
        var options = new OccurrenceProviderRetryOptions();

        Should.Throw<ArgumentOutOfRangeException>(() => options.InitialBackoff = TimeSpan.FromDays(2));
        Should.Throw<ArgumentOutOfRangeException>(() => options.MaxBackoff = TimeSpan.FromDays(2));

        // Both ends of the accepted range are accepted.
        options.InitialBackoff = TimeSpan.FromTicks(1);
        options.MaxBackoff     = TimeSpan.FromDays(1);
    }

    #endregion

    #region What the definition itself says

    [Fact]
    public void The_arithmetic_grid_refuses_to_answer_for_a_provider_definition()
    {
        // Silence would be the dangerous answer: with no interval and no cron the cascade reports "no
        // occurrence, ever", which every primitive would read as a series that has ended.
        var refusal = Should.Throw<NotSupportedException>(() =>
            ProviderSchedule().CalculateNextRun(Origin, 0));

        refusal.Message.ShouldContain(Key);
    }

    [Fact]
    public void A_provider_schedule_is_calendar_anchored_takes_a_zone_and_has_no_period()
    {
        var definition = ProviderSchedule(zone: "Europe/Rome");

        definition.Semantics.ShouldBe(ScheduleSemantics.Calendar);
        definition.PeriodKind.ShouldBe(SchedulePeriodKind.None);
        definition.IsUniformGrid().ShouldBeFalse();
        definition.CountsMissedInConstantTime().ShouldBeFalse();

        // A zone on it is legitimate — it travels to the provider, which is what reads the calendar on it.
        Should.NotThrow(definition.Validate);
    }

    [Fact]
    public void A_provider_and_an_interval_are_exclusive()
    {
        var definition = ProviderSchedule();
        definition.DayInterval = new DayInterval(1);

        var refusal = Should.Throw<InvalidOperationException>(definition.Validate);

        refusal.Message.ShouldContain("replaces the grid");
    }

    [Fact]
    public void A_provider_with_no_key_is_corrupt_schedule_metadata()
    {
        var definition = ProviderSchedule();
        definition.Provider!.Key = "   ";

        Should.Throw<ArgumentException>(definition.Validate);
    }

    [Fact]
    public void A_provider_definition_round_trips_and_leaves_every_other_schedule_byte_identical()
    {
        var json = EverTaskJson.Serialize(ProviderSchedule(config: "{\"v\":1}"));

        json.ShouldContain("\"Provider\":");
        json.ShouldContain("\"Key\":\"probe\"");

        var back = EverTaskJson.Deserialize<RecurringTask>(json)!;

        back.Provider!.Key.ShouldBe(Key);
        back.Provider.Config.ShouldBe("{\"v\":1}");

        // The member is omitted while it is null, which is what keeps every schedule written before providers
        // existed serializing to exactly the bytes it always did.
        EverTaskJson.Serialize(new RecurringTask { DayInterval = new DayInterval(1) })
                    .ShouldNotContain("Provider");
    }

    [Fact]
    public void A_provider_schedule_says_so_in_its_description()
    {
        var definition = ProviderSchedule(config: "IT");
        definition.MaxRuns = 3;

        var described = definition.ToString();

        described.ShouldContain("provider 'probe'");
        described.ShouldContain("IT");
        described.ShouldContain("up to 3 times");
    }

    #endregion

    private static RecurringTask ProviderSchedule(string? config = null, string? zone = null, string key = Key) =>
        new()
        {
            Provider   = new ProviderSettings { Key = key, Config = config },
            TimeZoneId = zone
        };

    /// <summary>A durable catch-up over a provider grid: the shape a replay of a downtime has.</summary>
    private static RecurringTask CatchUpProviderSchedule(int maxOccurrences = 5_000, int maxPending = 1,
                                                         string key = Key)
    {
        var definition = ProviderSchedule(key: key);

        definition.OccurrenceMode = OccurrenceMode.Durable;
        definition.Misfire = new MisfireSettings
        {
            Policy                = MisfirePolicy.CatchUp,
            MaxAge                = TimeSpan.FromDays(30),
            MaxOccurrences        = maxOccurrences,
            MaxPendingOccurrences = maxPending
        };

        return definition;
    }

    /// <summary>The planner with the default host threshold, so what these tests pin is what a host runs.</summary>
    private static DueSlotEnumerator Planner(IScheduleEvaluator evaluator) =>
        new(evaluator, TimeSpan.FromSeconds(5));

    /// <summary>
    /// The materializer's serial replay at the level of the planner: one plan per run, the cursor the last one
    /// left, until the backlog is gone.
    /// </summary>
    /// <param name="planner">
    /// Where the plan comes from — the same instance for every run (what the host holds) or a new one each
    /// time (a planner that remembers nothing).
    /// </param>
    private static async Task<IReadOnlyList<PlannedRun>> ReplayAsync(RecurringTask definition, DateTimeOffset cursor,
                                                                     DateTimeOffset nowUtc, int runs,
                                                                     Func<DueSlotEnumerator> planner)
    {
        var answers    = new List<PlannedRun>();
        var scheduleId = Guid.NewGuid();

        for (var run = 0; run < runs; run++)
        {
            var plan = await planner()
                             .PlanAsync(definition, cursor, nowUtc, run, 0,
                                 new ScheduleIdentity(scheduleId, "digest", run + 1));

            answers.Add(new PlannedRun(plan.StopReason, string.Join(',', plan.Slots.Select(s => s.UtcTicks)),
                plan.Misfire?.MissedFromUtc, plan.Misfire?.MissedThroughUtc, plan.Misfire?.MissedCount ?? 0,
                plan.Misfire?.MissedCountIsExact ?? false, plan.NextCursorUtc));

            if (plan.NextCursorUtc is not { } next)
                break;

            cursor = next;
        }

        return answers;
    }

    /// <summary>Everything one plan of a replay answered, so two replays can be compared as a whole.</summary>
    private readonly record struct PlannedRun(DueSlotStopReason StopReason, string Slots, DateTimeOffset? From,
                                              DateTimeOffset? Through, int Count, bool IsExact,
                                              DateTimeOffset? NextCursor);

    /// <summary>
    /// A real container with the real evaluator, the real registry and the two probe providers registered the
    /// way an application registers its own.
    /// </summary>
    private static (IScheduleEvaluator Evaluator, OccurrenceProviderProbe Probe, IServiceProvider Services)
        BuildGrid(Action<EverTaskServiceConfiguration>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        services.AddEverTask(cfg =>
                {
                    cfg.RegisterTasksFromAssembly(typeof(ProviderScheduleTask).Assembly);
                    configure?.Invoke(cfg);
                })
                .AddMemoryStorage()
                .AddOccurrenceProvider<ProbeOccurrenceProvider>(Key)
                .AddOccurrenceProvider<DeterministicProbeProvider>(DeterministicKey)
                .AddOccurrenceProvider<AsyncCalendarProvider>(AsyncScopeKey);

        services.AddSingleton<OccurrenceProviderProbe>();
        services.AddSingleton<ProviderScopeLedger>();

        // Scoped and async-disposable, like the DbContext it stands for: the provider is built per call, so
        // this is built and released per call with it.
        services.AddScoped<AsyncOnlyCalendar>();

        var provider = services.BuildServiceProvider();

        return (provider.GetRequiredService<IScheduleEvaluator>(),
                provider.GetRequiredService<OccurrenceProviderProbe>(),
                provider);
    }
}
