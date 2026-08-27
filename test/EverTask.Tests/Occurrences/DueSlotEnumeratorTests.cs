using System.Diagnostics;
using System.Globalization;
using EverTask.Scheduler.Occurrences;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.Occurrences;

/// <summary>
/// The misfire policy is pure arithmetic over the occurrence grid, so it is pinned here rather than through a
/// host: every combination of window, cap, overflow policy and concurrency budget is a distinct answer, and
/// there are far more of them than a set of integration tests could drive deterministically.
/// </summary>
/// <remarks>
/// The grid is a plain minute cadence — <c>GetNextOccurrence</c> is <c>current + 1 minute</c> — so a slot list
/// reads as the arithmetic it is, and the assertions are about the POLICY, not about the calendar (which has
/// its own suites).
/// </remarks>
public class DueSlotEnumeratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The default misfire threshold, so what this suite pins is what a host runs unconfigured.</summary>
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(5);

    private readonly DueSlotEnumerator _enumerator = new(ScheduleEvaluator.Default, Threshold);

    private static RecurringTask MinuteSchedule(MisfireSettings? misfire = null) => new()
    {
        MinuteInterval = new MinuteInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire = misfire
    };

    private static MisfireSettings CatchUp(TimeSpan maxAge, int maxOccurrences,
                                           CatchUpOverflowPolicy overflow = CatchUpOverflowPolicy.Halt,
                                           int maxPending = 1) => new()
    {
        Policy                = MisfirePolicy.CatchUp,
        MaxAge                = maxAge,
        MaxOccurrences        = maxOccurrences,
        OverflowPolicy        = overflow,
        MaxPendingOccurrences = maxPending
    };

    // ---- Skip -------------------------------------------------------------------------------------

    [Fact]
    public async Task Should_materialize_the_slot_when_it_is_still_the_current_one()
    {
        var plan = await _enumerator.PlanAsync(MinuteSchedule(), Now.AddSeconds(-20), Now, 0, 0);

        plan.Slots.ShouldBe([Now.AddSeconds(-20)]);
        plan.NextCursorUtc.ShouldBe(Now.AddSeconds(-20).AddMinutes(1));
        plan.Losses.ShouldBeEmpty();
        plan.StopReason.ShouldBe(DueSlotStopReason.Exhausted);
    }

    [Fact]
    public async Task Should_drop_a_stale_slot_and_jump_the_cursor_to_the_next_future_occurrence()
    {
        var cursor = Now.AddMinutes(-10);

        var plan = await _enumerator.PlanAsync(MinuteSchedule(), cursor, Now, 0, 0);

        plan.Slots.ShouldBeEmpty("the skip policy never replays a slot that is no longer the current one");
        plan.NextCursorUtc.ShouldBe(Now.AddMinutes(1));

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.SkippedByPolicy,
            "the skip policy dropped them, and no misfire window was involved");
        loss.Count.ShouldBe(11, "the ten missed slots plus the cursor itself");
        loss.FromUtc.ShouldBe(cursor);
    }

    [Fact]
    public async Task Should_hold_a_still_current_slot_instead_of_dropping_it_when_the_budget_is_full()
    {
        var cursor = Now.AddSeconds(-20);

        var plan = await _enumerator.PlanAsync(MinuteSchedule(), cursor, Now, 0, activeOccurrences: 1);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(cursor, "a slot waiting for capacity has not gone stale");
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
    }

    // ---- FireOnce ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_collapse_a_run_of_missed_slots_into_the_most_recent_one()
    {
        var cursor   = Now.AddMinutes(-5);
        var schedule = MinuteSchedule(new MisfireSettings { Policy = MisfirePolicy.FireOnce });

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe([Now], "the newest due slot is the one that fires");
        plan.NextCursorUtc.ShouldBe(Now.AddMinutes(1));
        plan.Misfire.ShouldNotBeNull();
        plan.Misfire!.Value.Kind.ShouldBe(MisfireKind.FireOnce);
        plan.Misfire.Value.MissedFromUtc.ShouldBe(cursor);
        plan.Misfire.Value.MissedThroughUtc.ShouldBe(Now);
        plan.Misfire.Value.MissedCount.ShouldBe(6, "six slots came due, and this one delivery stands for them");
    }

    [Fact]
    public async Task Should_report_no_misfire_when_fire_once_is_simply_keeping_up()
    {
        var schedule = MinuteSchedule(new MisfireSettings { Policy = MisfirePolicy.FireOnce });

        var plan = await _enumerator.PlanAsync(schedule, Now, Now, 0, 0);

        plan.Slots.ShouldBe([Now]);
        plan.Misfire.ShouldBeNull("a single on-time slot is not a collapsed run");
    }

    [Fact]
    public async Task Should_fire_nothing_when_even_the_newest_missed_slot_is_older_than_the_window()
    {
        var cursor = Now.AddMinutes(-30);
        var schedule = MinuteSchedule(new MisfireSettings
        {
            Policy = MisfirePolicy.FireOnce,
            MaxAge = TimeSpan.FromMinutes(5)
        });

        // The newest due slot is Now itself, which is inside the window: move `now` past the whole grid by
        // giving the schedule an end instead, so every due slot really is older than the window.
        schedule.RunUntil = Now.AddMinutes(-20);

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBeNull("the series ended before the window opened");
    }

    [Fact]
    public async Task Should_drop_the_whole_collapsed_run_when_it_is_older_than_the_window()
    {
        // An hourly grid whose newest due slot is half an hour old, against a ten-minute window: the run is
        // dropped and the schedule simply moves on to its next occurrence, still alive.
        var cursor = Now.AddHours(-5);
        var schedule = new RecurringTask
        {
            HourInterval   = new HourInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = new MisfireSettings { Policy = MisfirePolicy.FireOnce, MaxAge = TimeSpan.FromMinutes(10) }
        };

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now.AddMinutes(30), 0, 0);

        plan.Slots.ShouldBeEmpty();

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.MisfireWindowExceeded);
        loss.Count.ShouldBe(6, "the six hourly slots from the cursor up to now");
        loss.FromUtc.ShouldBe(cursor);

        plan.NextCursorUtc.ShouldBe(Now.AddHours(1));
    }

    // ---- CatchUp ----------------------------------------------------------------------------------

    [Fact]
    public async Task Should_replay_every_due_slot_oldest_first()
    {
        var cursor   = Now.AddMinutes(-4);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 100));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe([
            cursor, cursor.AddMinutes(1), cursor.AddMinutes(2), cursor.AddMinutes(3), cursor.AddMinutes(4)
        ]);
        plan.NextCursorUtc.ShouldBe(Now.AddMinutes(1));
        plan.StopReason.ShouldBe(DueSlotStopReason.Exhausted);
        plan.Misfire!.Value.Kind.ShouldBe(MisfireKind.CatchUp);
    }

    [Fact]
    public async Task Should_drop_the_slots_older_than_the_age_window_and_report_them()
    {
        var cursor   = Now.AddMinutes(-20);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromMinutes(5), 100, maxPending: 100));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe([
            Now.AddMinutes(-5), Now.AddMinutes(-4), Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-1), Now
        ]);
        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.MisfireWindowExceeded);
        loss.Count.ShouldBe(15, "everything from the cursor up to the window start");
        loss.IsExact.ShouldBeTrue();
        loss.FromUtc.ShouldBe(cursor);
    }

    // ---- The misfire a catch-up row carries -------------------------------------------------------

    [Fact]
    public async Task The_catch_up_misfire_range_holds_exactly_as_many_slots_as_it_counts()
    {
        // The DEFAULT budget: one slot is materialized per run, so this is the shape almost every catch-up
        // row really gets. The range and the count are two halves of one statement about the backlog and have
        // to agree — a row saying "I stand for six slots" over a range holding one is not a fact about
        // anything.
        var cursor   = Now.AddMinutes(-5);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe([cursor], "one slot at a time, at the default budget");

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.Kind.ShouldBe(MisfireKind.CatchUp);
        misfire.MissedFromUtc.ShouldBe(cursor);
        misfire.MissedThroughUtc.ShouldBe(Now, "the newest slot the backlog owes, not the one this run took");
        misfire.MissedCount.ShouldBe(6);
        misfire.MissedCountIsExact.ShouldBeTrue();

        SlotsInRange(misfire).ShouldBe(misfire.MissedCount,
            "the count is how many grid slots the range holds, both ends included");
    }

    [Fact]
    public async Task The_catch_up_misfire_range_and_count_shrink_together_as_the_backlog_drains()
    {
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100));

        // Two successive runs of the same drain: the second starts where the first left the cursor.
        var first  = await _enumerator.PlanAsync(schedule, Now.AddMinutes(-5), Now, 0, 0);
        var second = await _enumerator.PlanAsync(schedule, first.NextCursorUtc!.Value, Now, 1, 0);

        first.Misfire.ShouldNotBeNull();
        second.Misfire.ShouldNotBeNull();

        var firstMisfire  = first.Misfire!.Value;
        var secondMisfire = second.Misfire!.Value;

        secondMisfire.MissedFromUtc.ShouldBe(Now.AddMinutes(-4));
        secondMisfire.MissedThroughUtc.ShouldBe(firstMisfire.MissedThroughUtc, "the backlog still ends at now");
        secondMisfire.MissedCount.ShouldBe(firstMisfire.MissedCount - 1);

        SlotsInRange(secondMisfire).ShouldBe(secondMisfire.MissedCount);
    }

    [Fact]
    public async Task Every_row_of_one_catch_up_run_carries_the_same_range_and_count()
    {
        var cursor   = Now.AddMinutes(-5);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 3));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.Count.ShouldBe(3, "the premise: this run writes more than one row");

        // The misfire is stamped once and copied onto every row the run creates, so this IS what each of them
        // reports — including the range holding the two slots the budget left behind.
        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.MissedFromUtc.ShouldBe(cursor);
        misfire.MissedThroughUtc.ShouldBe(Now);
        misfire.MissedCount.ShouldBe(6);

        SlotsInRange(misfire).ShouldBe(misfire.MissedCount);
    }

    [Fact]
    public async Task The_fire_once_misfire_range_holds_exactly_as_many_slots_as_it_counts()
    {
        var cursor   = Now.AddMinutes(-5);
        var schedule = MinuteSchedule(new MisfireSettings { Policy = MisfirePolicy.FireOnce });

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.MissedCountIsExact.ShouldBeTrue();
        SlotsInRange(misfire).ShouldBe(misfire.MissedCount);
    }

    /// <summary>How many one-minute slots the misfire's own range holds, both ends included.</summary>
    private static int SlotsInRange(OccurrenceMisfire misfire) =>
        (int)Math.Round((misfire.MissedThroughUtc - misfire.MissedFromUtc).TotalMinutes) + 1;

    // ---- What makes a slot MISSED: the misfire threshold (M1) -------------------------------------

    /// <summary>An hourly grid, so a cursor minutes in the past owes exactly ONE slot.</summary>
    private static RecurringTask HourSchedule(MisfireSettings misfire) => new()
    {
        HourInterval   = new HourInterval(1),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    [Theory]
    [InlineData(MisfirePolicy.CatchUp)]
    [InlineData(MisfirePolicy.FireOnce)]
    public async Task A_single_slot_older_than_the_threshold_stands_for_missed_work(MisfirePolicy policy)
    {
        // The definition of a misfire is the threshold, not the number of slots (M1): a downtime of ten
        // minutes on an hourly schedule leaves exactly ONE slot owed, and it is the case a replaying policy
        // was turned on for. Deciding on "more than one slot" alone, the row said it was an ordinary
        // occurrence and left the handler to work the replay out from its own lateness.
        var cursor = Now.AddMinutes(-10);
        var settings = policy == MisfirePolicy.CatchUp
                           ? CatchUp(TimeSpan.FromHours(6), 100)
                           : new MisfireSettings { Policy = MisfirePolicy.FireOnce };

        var plan = await _enumerator.PlanAsync(HourSchedule(settings), cursor, Now, 0, 0);

        plan.Slots.ShouldBe([cursor], "the premise: one slot is owed");

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.Kind.ShouldBe(policy == MisfirePolicy.CatchUp ? MisfireKind.CatchUp : MisfireKind.FireOnce);
        misfire.MissedFromUtc.ShouldBe(cursor);
        misfire.MissedThroughUtc.ShouldBe(cursor, "one slot is its own range");
        misfire.MissedCount.ShouldBe(1);
        misfire.MissedCountIsExact.ShouldBeTrue();
    }

    [Theory]
    [InlineData(MisfirePolicy.CatchUp)]
    [InlineData(MisfirePolicy.FireOnce)]
    public async Task A_single_slot_inside_the_threshold_is_a_schedule_keeping_up(MisfirePolicy policy)
    {
        var cursor = Now.AddSeconds(-2);
        var settings = policy == MisfirePolicy.CatchUp
                           ? CatchUp(TimeSpan.FromHours(6), 100)
                           : new MisfireSettings { Policy = MisfirePolicy.FireOnce };

        var plan = await _enumerator.PlanAsync(HourSchedule(settings), cursor, Now, 0, 0);

        plan.Slots.ShouldBe([cursor]);
        plan.Misfire.ShouldBeNull("two seconds of scheduling is inside the five-second default threshold");
    }

    [Fact]
    public async Task The_threshold_is_what_decides_and_a_host_can_move_it()
    {
        // The same backlog, the same instant, two hosts: what separates "missed" from "late by a bit" is
        // SetMisfireThreshold and nothing else.
        var cursor   = Now.AddMinutes(-10);
        var schedule = HourSchedule(CatchUp(TimeSpan.FromHours(6), 100));

        var tolerant = new DueSlotEnumerator(ScheduleEvaluator.Default, TimeSpan.FromHours(1));

        (await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0)).Misfire.ShouldNotBeNull(
            "ten minutes is past the default five seconds");

        (await tolerant.PlanAsync(schedule, cursor, Now, 0, 0)).Misfire.ShouldBeNull(
            "and inside an hour of declared tolerance it is not worth reporting");
    }

    [Theory]
    [InlineData(MisfirePolicy.CatchUp)]
    [InlineData(MisfirePolicy.FireOnce)]
    public async Task A_run_of_slots_inside_the_threshold_is_still_reported(MisfirePolicy policy)
    {
        // The half of M1 the threshold does NOT get to narrow (decisions §3.5). Three per-second slots came
        // due within two seconds: fire-once is about to collapse two of them into nothing and a catch-up is
        // about to replay a run nothing ran, and P5 does not allow either to happen unreported because the
        // grid is faster than the threshold.
        var cursor = Now.AddSeconds(-2);
        var settings = policy == MisfirePolicy.CatchUp
                           ? CatchUp(TimeSpan.FromHours(6), 100, maxPending: 10)
                           : new MisfireSettings { Policy = MisfirePolicy.FireOnce };

        var schedule = new RecurringTask
        {
            SecondInterval = new SecondInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = settings
        };

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Misfire.ShouldNotBeNull("a run of slots is missed work however young it is");
        plan.Misfire!.Value.MissedCount.ShouldBe(3);
        plan.Misfire.Value.MissedFromUtc.ShouldBe(cursor);
        plan.Misfire.Value.MissedThroughUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task Should_halt_without_materializing_anything_when_the_backlog_exceeds_the_cap()
    {
        var cursor   = Now.AddMinutes(-50);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(2), 10));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.StopReason.ShouldBe(DueSlotStopReason.Halted);
        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(cursor, "a halt writes no cursor: the backlog stays exactly where it is");
        plan.DetectedAtLeast.ShouldBe(51,
            "a minute grid counts by division, so the cap costs nothing to lift and the operator is told how " +
            "many runs are really owed instead of 'cap + 1'");
        plan.IsExact.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_report_the_backlog_of_a_walked_grid_as_a_lower_bound_and_say_so()
    {
        // A cron grid is walked, not divided, so the count HAS to stop somewhere — and when it does, the
        // number travels as "at least this many" instead of pretending to be a total.
        var schedule = new RecurringTask
        {
            CronInterval   = new CronInterval("* * * * *"),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = CatchUp(TimeSpan.FromDays(30), 10)
        };

        var plan = await _enumerator.PlanAsync(schedule, Now.AddDays(-20), Now, 0, 0);

        plan.StopReason.ShouldBe(DueSlotStopReason.Halted);
        plan.DetectedAtLeast.ShouldBe(11, "the walk stops one past the cap");
        plan.IsExact.ShouldBeFalse("and the number says it is only a lower bound");
    }

    /// <summary>A walked grid one minute apart, so a backlog of N minutes is a backlog of N slots.</summary>
    private static RecurringTask WalkedMinuteSchedule(MisfireSettings misfire) => new()
    {
        CronInterval   = new CronInterval("* * * * *"),
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    [Fact]
    public async Task Should_halt_a_walked_backlog_that_exceeds_a_cap_above_ten_thousand()
    {
        // Twelve days of a per-minute schedule: 17,281 slots owed against a cap of 12,000, which is the order
        // of cap such a schedule needs to replay a downtime of days at all. The walk that answers "is the
        // backlog over the cap?" used to stop after ten thousand steps of ITS own, BELOW this cap, so the
        // answer came back as 10,001, "10,001 <= 12,000" read as "under the cap", and the breaker that exists
        // for exactly this could never fire: the schedule replayed the whole backlog with nobody asked.
        var schedule = WalkedMinuteSchedule(CatchUp(TimeSpan.FromDays(30), 12_000));

        var plan = await _enumerator.PlanAsync(schedule, Now.AddDays(-12), Now, 0, 0);

        plan.StopReason.ShouldBe(DueSlotStopReason.Halted);
        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(Now.AddDays(-12), "a halt writes no cursor");
        plan.DetectedAtLeast.ShouldBe(12_001, "one past the cap it was given, not one past the walk's own bound");
        plan.IsExact.ShouldBeFalse();
    }

    [Fact]
    public async Task Should_count_a_walked_backlog_exactly_when_it_fits_a_cap_above_ten_thousand()
    {
        // The other side of the same bound: 11,000 slots under a cap of 20,000 is not an overflow, and the
        // number that says so is a TOTAL. A walk stopping at its own ten thousand reported 10,001 here and
        // stamped it exact — on the plan, on every occurrence's MisfireInfo, and so on what the handler reads.
        var cursor   = Now.AddMinutes(-11_000);
        var schedule = WalkedMinuteSchedule(CatchUp(TimeSpan.FromDays(30), 20_000));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull, "the backlog is owed, the default budget is one");
        plan.Slots.ShouldBe([cursor], "oldest first, one at a time");

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.MissedFromUtc.ShouldBe(cursor);
        misfire.MissedThroughUtc.ShouldBe(Now);
        misfire.MissedCount.ShouldBe(11_001, "every slot of the range, both ends included");
        misfire.MissedCountIsExact.ShouldBeTrue();

        SlotsInRange(misfire).ShouldBe(misfire.MissedCount);
    }

    [Fact]
    public async Task Should_report_a_lower_bound_when_the_cap_itself_is_no_cap_at_all()
    {
        // int.MaxValue is a legal MaxOccurrences and means "never halt on the count". No backlog can exceed
        // it, so the decision is right whatever the number is — but the number still travels to the handler,
        // and a walk cannot produce the real total here at any affordable price. So it says what it is.
        var schedule = WalkedMinuteSchedule(CatchUp(TimeSpan.FromDays(30), int.MaxValue));

        var plan = await _enumerator.PlanAsync(schedule, Now.AddDays(-20), Now, 0, 0);

        plan.StopReason.ShouldNotBe(DueSlotStopReason.Halted, "nothing can exceed a cap of int.MaxValue");
        plan.Slots.ShouldBe([Now.AddDays(-20)]);

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.MissedCount.ShouldBe(10_001, "the walk's own bound, since the ask carried none");
        misfire.MissedCountIsExact.ShouldBeFalse("and 28,801 slots are really owed, so the number says 'at least'");
        SlotsInRange(misfire).ShouldBeGreaterThan(misfire.MissedCount,
            "a lower bound never claims more slots than its range holds");
    }

    [Fact]
    public async Task Should_keep_the_most_recent_slots_when_skip_oldest_runs_with_a_cap_above_ten_thousand()
    {
        // The overflow boundary is found by bisecting the instant axis, and every probe is one of these same
        // counts. Truncated below the cap, no probe could ever reach it, the search had nothing to converge
        // on, and SkipOldest kept the OLDEST slots — the exact opposite of what it is for, and silent, since
        // the slots it hands back look perfectly ordinary.
        var schedule = WalkedMinuteSchedule(CatchUp(TimeSpan.FromDays(30), 10_500, CatchUpOverflowPolicy.SkipOldest,
            maxPending: 3));

        var plan = await _enumerator.PlanAsync(schedule, Now.AddMinutes(-12_000), Now, 0, 0);

        plan.Slots.ShouldBe([Now.AddMinutes(-10_499), Now.AddMinutes(-10_498), Now.AddMinutes(-10_497)],
            "the kept window starts exactly 10,500 slots from the end, and the budget takes its first three");

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.CatchUpOverflow, "the cap dropped them, not the thirty-day window");
        loss.Count.ShouldBe(1_501, "the 12,001 owed slots less the 10,500 the cap kept");
        loss.IsExact.ShouldBeTrue();
        loss.FromUtc.ShouldBe(Now.AddMinutes(-12_000));
    }

    [Fact]
    public async Task Should_keep_exactly_the_most_recent_slots_when_the_overflow_policy_skips_the_oldest()
    {
        var cursor   = Now.AddMinutes(-50);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(2), 10, CatchUpOverflowPolicy.SkipOldest,
            maxPending: 100));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        // The bisection has to land on the same slot a full enumeration would: the ten most recent due slots
        // are Now-9m … Now.
        var expected = Enumerable.Range(0, 10).Select(i => Now.AddMinutes(-9 + i)).ToArray();

        plan.Slots.ShouldBe(expected);

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.CatchUpOverflow);
        loss.Count.ShouldBe(41, "everything before the kept window");

        plan.NextCursorUtc.ShouldBe(Now.AddMinutes(1));
    }

    [Fact]
    public async Task The_slots_the_cap_drops_are_reported_apart_from_the_slots_the_window_drops()
    {
        // The two losses of one plan, and the reason each is reported under. A two-hour window over a
        // five-hour backlog drops the first three hours; the cap of ten then drops all but the ten most
        // recent of what is left. Added into one number under one cause, the report told an operator to
        // widen a window that had produced a fraction of the loss.
        var cursor   = Now.AddHours(-5);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(2), 10, CatchUpOverflowPolicy.SkipOldest,
            maxPending: 100));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe(Enumerable.Range(0, 10).Select(i => Now.AddMinutes(-9 + i)),
            "the ten most recent slots survive both limits");

        plan.Losses.Count.ShouldBe(2, "two different rules dropped slots, so there are two reports");

        var aged = plan.Losses.Where(l => l.Reason == SlotLossReason.MisfireWindowExceeded).ShouldHaveSingleItem();
        aged.FromUtc.ShouldBe(cursor);
        aged.Count.ShouldBe(180, "the three hours that fell outside the two-hour window");

        var overflow = plan.Losses.Where(l => l.Reason == SlotLossReason.CatchUpOverflow).ShouldHaveSingleItem();
        overflow.FromUtc.ShouldBe(Now.AddHours(-2), "the overflow starts where the window left off");
        overflow.Count.ShouldBe(111, "the 121 slots inside the window, less the ten the cap kept");

        plan.Losses.Sum(l => l.Count).ShouldBe(291,
            "and the two together are the whole backlog the plan did not replay");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(23)]
    public async Task Should_place_the_skip_oldest_boundary_where_a_full_enumeration_would(int cap)
    {
        var cursor   = Now.AddMinutes(-120);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(4), cap, CatchUpOverflowPolicy.SkipOldest,
            maxPending: 1000));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        // Ground truth: walk the WHOLE grid and take the last `cap` due slots.
        var all = new List<DateTimeOffset>();
        for (var slot = cursor; slot <= Now; slot = slot.AddMinutes(1))
            all.Add(slot);

        plan.Slots.ShouldBe(all.TakeLast(cap));
    }

    [Fact]
    public async Task Should_emit_only_what_the_concurrency_budget_allows_and_say_the_window_is_full()
    {
        var cursor   = Now.AddMinutes(-9);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 3));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 1);

        plan.Slots.Count.ShouldBe(2, "three may be alive at once and one already is");
        plan.Slots.ShouldBe([cursor, cursor.AddMinutes(1)]);
        plan.NextCursorUtc.ShouldBe(cursor.AddMinutes(2), "the rest of the backlog is untouched");
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
    }

    [Fact]
    public async Task Should_leave_the_cursor_where_it_is_when_no_capacity_is_left_at_all()
    {
        var cursor   = Now.AddMinutes(-9);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 2));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 2);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(cursor);
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
    }

    [Fact]
    public async Task Should_still_advance_the_cursor_past_the_age_window_when_no_capacity_is_left()
    {
        var cursor   = Now.AddMinutes(-30);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromMinutes(5), 100, maxPending: 1));

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 1);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(Now.AddMinutes(-5),
            "dropping a slot needs no capacity: it is the materialization that does");
        plan.Losses.ShouldHaveSingleItem().Count.ShouldBe(25);
        plan.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
    }

    [Fact]
    public async Task Should_ask_the_grid_nothing_for_a_plan_that_may_create_nothing()
    {
        // Two costs a full window used to pay for nothing. The newest slot of a backlog — the end of the range
        // a misfire carries — is found by bisecting the instant axis, and it is stamped on rows this plan does
        // not grant; and the count of what is owed is the CAP decision, which is about whether a replay may
        // start, and a run with no budget starts none. Both come back at every operational retry, for as long
        // as the occurrence holding the budget runs, and over a grid of round trips both are queries.
        var counting   = new CountingEvaluator(ScheduleEvaluator.Default);
        var enumerator = new DueSlotEnumerator(counting, Threshold);
        var schedule   = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5_000, maxPending: 1));
        var cursor     = Now.AddMinutes(-500);

        var full     = await enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 1);
        var whenFull = counting.Calls;

        var open     = await enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 0);
        var whenOpen = counting.Calls - whenFull;

        full.Slots.ShouldBeEmpty();
        full.StopReason.ShouldBe(DueSlotStopReason.WindowFull);
        full.NextCursorUtc.ShouldBe(cursor, "and the backlog stays exactly where it is");
        whenFull.ShouldBe(0, "nothing it could still measure changes what a run with no budget does");

        open.Misfire.ShouldNotBeNull();
        whenOpen.ShouldBeGreaterThan(10,
            "the premise: deciding this backlog really does cost a count and a bisection, and the plan above " +
            "paid neither");
    }

    [Fact]
    public async Task Should_stop_bisecting_at_the_first_probe_that_names_the_slot()
    {
        // An instant with exactly N due slots after it HAS the slot we want as its successor, so the search is
        // over the moment a probe counts N — narrowing to the tick after that only spends probes. It is free
        // on this grid and two round trips a probe on an occurrence provider, which is where the whole
        // 64-probe budget was being spent on every plan.
        var counting   = new CountingEvaluator(ScheduleEvaluator.Default);
        var enumerator = new DueSlotEnumerator(counting, Threshold);
        var schedule   = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5, CatchUpOverflowPolicy.SkipOldest, 5));
        var cursor     = Now.AddMinutes(-500);

        var plan = await enumerator.PlanAsync(schedule, cursor, Now, 0, activeOccurrences: 0);

        plan.Slots.ShouldBe([
            Now.AddMinutes(-4), Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-1), Now
        ]);
        plan.Losses.ShouldHaveSingleItem().Reason.ShouldBe(SlotLossReason.CatchUpOverflow);
        plan.Losses[0].Count.ShouldBe(496);
        plan.Misfire!.Value.MissedThroughUtc.ShouldBe(Now);

        // Two bisections (where the kept window begins, and where the backlog it describes ends) over a range
        // of 500 minutes measured in ticks are about 38 probes each when they run to the end.
        counting.Calls.ShouldBeLessThan(64,
            "the search ends at the probe that has the answer, not at the tick that proves it is the last one");
    }

    [Theory]
    [InlineData(MisfirePolicy.CatchUp)]
    [InlineData(MisfirePolicy.FireOnce)]
    public async Task Should_drop_nothing_when_the_age_window_is_wider_than_the_calendar(MisfirePolicy policy)
    {
        // TimeSpan.MaxValue is a legal window — the public options refuse only a non-positive one — and it
        // means "never drop a slot for being old". Subtracted from now it overflows instead, and it overflows
        // again at every operational retry: the series would never materialize anything again.
        Should.NotThrow(() => new CatchUpOptions(TimeSpan.MaxValue, 20));

        var settings = policy == MisfirePolicy.CatchUp
                           ? CatchUp(TimeSpan.MaxValue, 20, maxPending: 20)
                           : new MisfireSettings { Policy = MisfirePolicy.FireOnce, MaxAge = TimeSpan.MaxValue };

        var cursor = Now.AddMinutes(-4);

        var plan = await _enumerator.PlanAsync(MinuteSchedule(settings), cursor, Now, 0, 0);

        plan.Losses.ShouldBeEmpty("a window nothing can fall outside of drops nothing");
        plan.Slots.ShouldNotBeEmpty();
        plan.Slots[0].ShouldBe(policy == MisfirePolicy.CatchUp ? cursor : Now,
            "the backlog is replayed from its oldest slot, or collapsed onto its newest one");
    }

    // ---- Bounds -----------------------------------------------------------------------------------

    [Fact]
    public async Task Should_end_the_series_in_the_same_plan_that_spends_its_last_run()
    {
        var cursor   = Now.AddMinutes(-4);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 100));
        schedule.MaxRuns = 7;

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 5, activeOccurrences: 0);

        plan.Slots.ShouldBe([cursor, cursor.AddMinutes(1)], "two runs are left of the budget");
        plan.NextCursorUtc.ShouldBeNull("the last allowed occurrence ends the series in the same commit");
        plan.RunBudgetEndsSeries.ShouldBeTrue(
            "and the plan says WHY the cursor is null: the caller may write that occurrence at a slot further " +
            "along the grid — one it named may already have a row — and a spent budget stays spent wherever " +
            "the write lands, while a grid that ended is a fact about the slot it named");
    }

    [Fact]
    public async Task The_last_run_of_a_series_is_spent_instead_of_halting_a_backlog_it_can_never_replay()
    {
        // One run left of ten, a hundred slots owed, a cap of five. The breaker exists to stop a replay
        // nobody asked for — and this series can create exactly ONE more occurrence and then close, whatever
        // the backlog holds. Halting it wrote a marker that by contract never releases itself, so an operator
        // had to resume a schedule for the sole purpose of letting it spend its last run and finish.
        var cursor   = Now.AddMinutes(-100);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5));
        schedule.MaxRuns = 10;

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 9, activeOccurrences: 0);

        plan.StopReason.ShouldNotBe(DueSlotStopReason.Halted);
        plan.Slots.ShouldBe([cursor], "the oldest slot, which is where a catch-up starts");
        plan.NextCursorUtc.ShouldBeNull("it is also the last run the budget allows");
        plan.RunBudgetEndsSeries.ShouldBeTrue("so the series closes in the same commit that creates it");
        plan.Misfire!.Value.MissedCount.ShouldBe(101,
            "and the row still says what the backlog was, which is the part the budget does not change");
    }

    [Fact]
    public async Task A_run_budget_that_still_reaches_the_cap_halts_over_the_same_backlog()
    {
        // The other side of it: the budget is what makes the difference, not the backlog. Ninety-one runs
        // left is enough to replay far more than the cap, so the breaker is exactly what it was written for.
        var cursor   = Now.AddMinutes(-100);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5));
        schedule.MaxRuns = 100;

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 9, activeOccurrences: 0);

        plan.StopReason.ShouldBe(DueSlotStopReason.Halted);
        plan.Slots.ShouldBeEmpty();
        plan.DetectedAtLeast.ShouldBe(101);
        plan.NextCursorUtc.ShouldBe(cursor, "a halt writes no cursor");
    }

    [Fact]
    public async Task A_run_budget_equal_to_the_cap_replays_what_it_can_afford_and_ends()
    {
        // The boundary: five runs left, a cap of five. The cap and the budget bound the same thing, so
        // nothing can exceed both — the episode replays the five it can pay for and the series closes.
        var cursor   = Now.AddMinutes(-100);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5, maxPending: 100));
        schedule.MaxRuns = 14;

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 9, activeOccurrences: 0);

        plan.StopReason.ShouldNotBe(DueSlotStopReason.Halted);
        plan.Slots.Count.ShouldBe(5);
        plan.Slots[0].ShouldBe(cursor);
        plan.NextCursorUtc.ShouldBeNull();
        plan.RunBudgetEndsSeries.ShouldBeTrue();
    }

    [Fact]
    public async Task Skip_oldest_still_keeps_the_newest_slot_when_one_run_is_left()
    {
        // A budget below the cap does not turn SkipOldest into "run the oldest": which end of a backlog
        // matters is the policy the caller chose, and the last run of the series owes them the newest slot.
        var cursor   = Now.AddMinutes(-100);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 5, CatchUpOverflowPolicy.SkipOldest));
        schedule.MaxRuns = 10;

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 9, activeOccurrences: 0);

        plan.Slots.ShouldBe([Now.AddMinutes(-4)], "the kept window starts five slots from the end");
        plan.NextCursorUtc.ShouldBeNull();
        plan.RunBudgetEndsSeries.ShouldBeTrue();

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.CatchUpOverflow);
        loss.Count.ShouldBe(96, "everything before the window the cap kept");
    }

    [Fact]
    public async Task Should_not_claim_a_spent_run_budget_when_it_is_the_grid_that_ends()
    {
        var cursor   = Now.AddMinutes(-3);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 100));
        schedule.RunUntil = Now.AddMinutes(-1);

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, currentRunCount: 0, activeOccurrences: 0);

        plan.NextCursorUtc.ShouldBeNull("the bound leaves no slot after the last one this plan grants");
        plan.RunBudgetEndsSeries.ShouldBeFalse("nothing about MaxRuns is what ended it");
    }

    [Fact]
    public async Task Should_report_an_ended_series_when_the_run_budget_is_already_spent()
    {
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100));
        schedule.MaxRuns = 3;

        var plan = await _enumerator.PlanAsync(schedule, Now.AddMinutes(-1), Now, currentRunCount: 3, 0);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Should_report_an_ended_series_when_the_cursor_is_already_at_the_end_bound()
    {
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100));
        schedule.RunUntil = Now.AddMinutes(-5);

        var plan = await _enumerator.PlanAsync(schedule, Now.AddMinutes(-5), Now, 0, 0);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBeNull("RunUntil is exclusive on the slot");
    }

    [Fact]
    public async Task Should_stop_the_replay_at_the_end_bound()
    {
        var cursor   = Now.AddMinutes(-10);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100, maxPending: 100));
        schedule.RunUntil = Now.AddMinutes(-7);

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.Slots.ShouldBe([cursor, cursor.AddMinutes(1), cursor.AddMinutes(2)]);
        plan.NextCursorUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Should_not_halt_over_a_slot_that_falls_exactly_on_the_exclusive_end_bound()
    {
        // Ten due slots and a cap of ten: the eleventh instant of the grid IS the end bound, and a bound the
        // whole grid treats as exclusive must not be counted as a slot here either. Counting it turned a
        // series that fits its cap exactly into a halted one — which never releases itself, by contract.
        var cursor   = Now.AddMinutes(-30);
        var schedule = MinuteSchedule(CatchUp(TimeSpan.FromDays(1), 10, maxPending: 100));
        schedule.RunUntil = Now.AddMinutes(-20);

        var plan = await _enumerator.PlanAsync(schedule, cursor, Now, 0, 0);

        plan.StopReason.ShouldNotBe(DueSlotStopReason.Halted,
            "the backlog is exactly the cap, so the breaker has nothing to trip on");
        plan.Slots.Count.ShouldBe(10);
        plan.Slots[^1].ShouldBe(Now.AddMinutes(-21), "the last real slot is the one before the bound");
        plan.NextCursorUtc.ShouldBeNull("and the series ends with it");
        plan.Misfire!.Value.MissedCount.ShouldBe(10, "the range and its count are one statement");
        plan.Misfire.Value.MissedThroughUtc.ShouldBe(Now.AddMinutes(-21));
    }

    [Fact]
    public async Task Should_do_nothing_while_the_cursor_is_still_in_the_future()
    {
        var cursor = Now.AddMinutes(5);

        var plan = await _enumerator.PlanAsync(MinuteSchedule(CatchUp(TimeSpan.FromHours(1), 100)), cursor, Now, 0, 0);

        plan.Slots.ShouldBeEmpty();
        plan.NextCursorUtc.ShouldBe(cursor);
        plan.StopReason.ShouldBe(DueSlotStopReason.Exhausted);
    }

    // ---- The bound that makes all of this affordable ----------------------------------------------

    [Fact]
    public async Task Should_decide_a_three_month_backlog_of_a_one_second_grid_without_enumerating_it()
    {
        // Eight million due slots. Every count this planner takes is capped, so the answer costs the cap and
        // not the backlog — an unbounded implementation would still be walking when the timeout fires.
        var schedule = new RecurringTask
        {
            SecondInterval = new SecondInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = CatchUp(TimeSpan.FromDays(92), 10)
        };

        var stopwatch = Stopwatch.StartNew();
        var plan      = await _enumerator.PlanAsync(schedule, Now.AddDays(-92), Now, 0, 0);
        stopwatch.Stop();

        plan.StopReason.ShouldBe(DueSlotStopReason.Halted);
        plan.DetectedAtLeast.ShouldBe(92 * 24 * 60 * 60 + 1,
            "eight million slots, counted by one division: the operator reconciling the outage reads the real " +
            "number of lost runs, not the cap");
        plan.IsExact.ShouldBeTrue();
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            "the decision must be bounded by the cap, never by the size of the backlog");
    }

    [Fact]
    public async Task Should_find_the_skip_oldest_boundary_of_a_one_second_grid_without_enumerating_it()
    {
        var schedule = new RecurringTask
        {
            SecondInterval = new SecondInterval(1),
            OccurrenceMode = OccurrenceMode.Durable,
            Misfire        = CatchUp(TimeSpan.FromDays(92), 5, CatchUpOverflowPolicy.SkipOldest, maxPending: 50)
        };

        var stopwatch = Stopwatch.StartNew();
        var plan      = await _enumerator.PlanAsync(schedule, Now.AddDays(-92), Now, 0, 0);
        stopwatch.Stop();

        plan.Slots.ShouldBe(Enumerable.Range(0, 5).Select(i => Now.AddSeconds(-4 + i)));
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5),
            "the bisection probes the instant axis; each probe is a bounded count");
    }

    // ---- A daylight-saving transition INSIDE the replayed backlog ----------------------------------

    /// <summary>
    /// A zoned calendar grid, which is the one shape none of the shortcuts above apply to: no division counts
    /// it, so every question about it is a walk (T8).
    /// </summary>
    private static RecurringTask ZonedHourly(MisfireSettings misfire) => new()
    {
        CronInterval   = new CronInterval("0 * * * *"),
        TimeZoneId     = "Europe/Rome",
        OccurrenceMode = OccurrenceMode.Durable,
        Misfire        = misfire
    };

    /// <summary>The grid itself, one step at a time — the only oracle a DST window can be checked against.</summary>
    private static DateTimeOffset[] Walk(RecurringTask schedule, DateTimeOffset from, DateTimeOffset through)
    {
        var slots      = new List<DateTimeOffset> { from };
        var occurrence = from;

        while (schedule.NextGridOccurrenceAfter(occurrence) is { } next && next <= through)
        {
            slots.Add(next);
            occurrence = next;
        }

        return [.. slots];
    }

    [Theory]
    // Spring forward: 02:00 local becomes 03:00 on 2026-03-29, so an hour of local labels does not exist.
    [InlineData("2026-03-28T23:00:00Z", "2026-03-29T03:00:00Z", "the gap")]
    // Fall back: 03:00 local becomes 02:00 on 2026-10-25, so an hour of local labels happens twice.
    [InlineData("2026-10-24T22:00:00Z", "2026-10-25T03:00:00Z", "the repeated hour")]
    public async Task A_catch_up_across_a_transition_replays_exactly_the_slots_the_zoned_grid_has(
        string cursorIso, string nowIso, string what)
    {
        var cursor = DateTimeOffset.Parse(cursorIso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var now = DateTimeOffset.Parse(nowIso, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

        var schedule = ZonedHourly(CatchUp(TimeSpan.FromDays(1), 50, maxPending: 50));

        schedule.CountsMissedInConstantTime().ShouldBeFalse(
            "the premise: a zoned calendar grid has no constant step, so the whole catch-up path is walked");

        var expected = Walk(schedule, cursor, now);

        var plan = await _enumerator.PlanAsync(schedule, cursor, now, 0, 0);

        plan.Slots.ShouldBe(expected,
            $"a backlog containing {what} owes exactly the instants the grid has, no more and no fewer");
        plan.Slots.Distinct().Count().ShouldBe(plan.Slots.Count,
            $"and {what} must not produce the same instant twice");
        plan.Losses.ShouldBeEmpty();

        plan.Misfire.ShouldNotBeNull();
        var misfire = plan.Misfire!.Value;
        misfire.MissedCount.ShouldBe(expected.Length,
            "the count a handler reads is how many slots the range really holds, transition included");
        misfire.MissedFromUtc.ShouldBe(cursor);
        misfire.MissedThroughUtc.ShouldBe(expected[^1]);
        misfire.MissedCountIsExact.ShouldBeTrue("the whole backlog fitted the cap it was counted under");
    }

    [Fact]
    public async Task A_transition_inside_the_backlog_does_not_move_where_skip_oldest_starts()
    {
        // The bisection walks the INSTANT axis while the grid it probes is a wall clock: a transition inside
        // the interval it halves is exactly where an off-by-one hour would hide.
        var cursor = new DateTimeOffset(2026, 3, 28, 20, 0, 0, TimeSpan.Zero);
        var now    = new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero);

        var schedule = ZonedHourly(CatchUp(TimeSpan.FromDays(1), 3, CatchUpOverflowPolicy.SkipOldest,
            maxPending: 50));

        var expected = Walk(schedule, cursor, now)[^3..];

        var plan = await _enumerator.PlanAsync(schedule, cursor, now, 0, 0);

        plan.Slots.ShouldBe(expected, "the three most recent slots of a zoned grid are still the three the " +
                                      "grid itself ends with");

        var loss = plan.Losses.ShouldHaveSingleItem();
        loss.Reason.ShouldBe(SlotLossReason.CatchUpOverflow);
        loss.Count.ShouldBeGreaterThan(0, "and everything before them is reported as dropped");
    }

    /// <summary>
    /// The real evaluator with a counter in front of it: what a plan COSTS is as much part of this class's
    /// contract as what it answers, and the cost is the number of questions it asks the grid.
    /// </summary>
    private sealed class CountingEvaluator(IScheduleEvaluator inner) : IScheduleEvaluator
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<NextRunResult> CalculateNextValidRunAsync(
            RecurringTask definition, DateTimeOffset scheduledTime, int currentRun, DateTimeOffset nowUtc,
            DateTimeOffset? referenceTime = null, bool isRecovery = false, bool computeSkippedCount = true,
            ScheduleIdentity identity = default, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.CalculateNextValidRunAsync(definition, scheduledTime, currentRun, nowUtc, referenceTime,
                isRecovery, computeSkippedCount, identity, ct);
        }

        public ValueTask<DateTimeOffset?> NextAfterAsync(RecurringTask definition, DateTimeOffset anchor,
                                                         DateTimeOffset after, ScheduleIdentity identity = default,
                                                         CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.NextAfterAsync(definition, anchor, after, identity, ct);
        }

        public ValueTask<int> CountMissedAsync(RecurringTask definition, DateTimeOffset anchor, DateTimeOffset after,
                                               int cap, ScheduleIdentity identity = default,
                                               CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.CountMissedAsync(definition, anchor, after, cap, identity, ct);
        }

        public ValueTask<DateTimeOffset?> NextGridOccurrenceAfterAsync(
            RecurringTask definition, DateTimeOffset occurrence, ScheduleIdentity identity = default,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.NextGridOccurrenceAfterAsync(definition, occurrence, identity, ct);
        }

        public ValueTask<DateTimeOffset?> FirstOccurrenceOnOrAfterAsync(
            RecurringTask definition, DateTimeOffset instant, ScheduleIdentity identity = default,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.FirstOccurrenceOnOrAfterAsync(definition, instant, identity, ct);
        }

        public ValueTask<IReadOnlyList<DateTimeOffset>> EnumerateDueSlotsAsync(
            RecurringTask definition, DateTimeOffset cursor, DateTimeOffset nowUtc, int cap,
            ScheduleIdentity identity = default, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return inner.EnumerateDueSlotsAsync(definition, cursor, nowUtc, cap, identity, ct);
        }
    }
}
