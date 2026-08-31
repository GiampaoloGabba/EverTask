using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// The evaluator is the single seam every component asks about a schedule's grid. In this phase it must be
/// behaviour-neutral — the exact answers the pure primitives already gave — plus one genuinely new question:
/// the NATURAL successor, computed while ignoring the termination bounds.
/// </summary>
public class ScheduleEvaluatorTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly IScheduleEvaluator Evaluator = ScheduleEvaluator.Default;

    public static TheoryData<string, RecurringTask> Schedules => new()
    {
        { "every 30 seconds", new RecurringTask { SecondInterval = new SecondInterval(30) } },
        { "every 5 minutes at :15", new RecurringTask { MinuteInterval = new MinuteInterval(5) { OnSecond = 15 } } },
        { "every 2 hours on 8 and 20", new RecurringTask { HourInterval = new HourInterval(2, [8, 20]) } },
        {
            "daily on Mon and Fri at 09:30",
            new RecurringTask { DayInterval = new DayInterval(1, [DayOfWeek.Monday, DayOfWeek.Friday]) { OnTimes = [new TimeOnly(9, 30)] } }
        },
        {
            "every 2 weeks on Tue and Thu at 06:00",
            new RecurringTask { WeekInterval = new WeekInterval(2, [DayOfWeek.Tuesday, DayOfWeek.Thursday]) { OnTimes = [new TimeOnly(6, 0)] } }
        },
        {
            "monthly on day 15 at 23:59",
            new RecurringTask { MonthInterval = new MonthInterval(1) { OnDay = 15, OnTimes = [new TimeOnly(23, 59)] } }
        },
        {
            "quarterly on the first Monday, in selected months",
            new RecurringTask { MonthInterval = new MonthInterval(3, [1, 4, 7, 10]) { OnFirst = DayOfWeek.Monday } }
        },
        // Combined interval fields: the cascade Month → Week → Day → Hour → Minute → Second refines one
        // answer with the next, and nothing stops a persisted schedule from carrying several of them. They
        // are the shapes where the O(1) uniform jump and the calendar walk can disagree, so the seam has to
        // be held to the grid on them too, not only on the one-field forms a builder emits.
        {
            "hour and minute cadences combined",
            new RecurringTask { HourInterval = new HourInterval(3), MinuteInterval = new MinuteInterval(10) }
        },
        {
            "monthly day refined by an hour selector",
            new RecurringTask
            {
                MonthInterval = new MonthInterval(1) { OnDay = 15 },
                HourInterval  = new HourInterval(0, [8, 20])
            }
        },
        { "cron every 5 minutes", new RecurringTask { CronInterval = new CronInterval("*/5 * * * *") } }
    };

    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task The_evaluator_answers_exactly_what_the_pure_primitives_answer(string shape, RecurringTask task)
    {
        var after = Anchor.AddHours(3);

        (await Evaluator.NextAfterAsync(task, Anchor, after))
            .ShouldBe(task.NextOccurrenceStrictlyAfter(Anchor, after), $"next-after diverged for '{shape}'");

        (await Evaluator.CountMissedAsync(task, Anchor, after, int.MaxValue))
            .ShouldBe(task.CountMissedOccurrences(Anchor, after), $"missed count diverged for '{shape}'");

        var expected = task.CalculateNextValidRun(Anchor, 1, after);
        var actual   = await Evaluator.CalculateNextValidRunAsync(task, Anchor, 1, after, after);
        actual.NextRun.ShouldBe(expected.NextRun, $"next valid run diverged for '{shape}'");
        actual.SkippedCount.ShouldBe(expected.SkippedCount, $"skipped count diverged for '{shape}'");
    }

    [Fact]
    public async Task The_natural_successor_ignores_the_termination_bounds()
    {
        // The bounded successor collapses "the slot is still current" and "the series simply ended" into the
        // same null, and reading that null as the former is what used to execute a months-old slot at
        // restart. The natural successor separates them.
        var bounded = new RecurringTask
        {
            DayInterval = new DayInterval(30, []),
            RunUntil    = Anchor.AddDays(10),
            MaxRuns     = 1
        };

        bounded.NextOccurrenceStrictlyAfter(Anchor, Anchor)
               .ShouldBeNull("the next occurrence falls past RunUntil, so the bounded answer is null");

        // A day cadence with no time-of-day lands on midnight, so the successor is the 30-days-out day start.
        (await Evaluator.NextGridOccurrenceAfterAsync(bounded, Anchor))
            .ShouldBe(new DateTimeOffset(2026, 6, 9, 0, 0, 0, TimeSpan.Zero),
                "the grid itself still has a successor, 30 days out");
    }

    [Fact]
    public async Task Asking_for_the_natural_successor_does_not_mutate_the_schedule()
    {
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(1, []),
            RunUntil    = Anchor.AddDays(3),
            MaxRuns     = 5
        };

        await Evaluator.NextGridOccurrenceAfterAsync(task, Anchor);

        task.RunUntil.ShouldBe(Anchor.AddDays(3));
        task.MaxRuns.ShouldBe(5);
    }

    [Fact]
    public async Task The_natural_successor_is_null_when_the_grid_cannot_produce_one()
    {
        // No cadence at all: nothing to walk, so no grace is granted and the caller falls through to the
        // ordinary skip-forward.
        var task = new RecurringTask();

        (await Evaluator.NextGridOccurrenceAfterAsync(task, Anchor)).ShouldBeNull();
    }

    [Fact]
    public async Task The_missed_count_stops_at_the_cap_instead_of_enumerating_the_whole_window()
    {
        // A one-second grid over three months is millions of slots; the count exists for diagnostics, so it
        // reports "at least cap + 1" rather than walking them.
        var task  = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var after = Anchor.AddDays(92);

        (await Evaluator.CountMissedAsync(task, Anchor, after, 100)).ShouldBe(101);
        (await Evaluator.CountMissedAsync(task, Anchor, after, int.MaxValue))
            .ShouldBe(task.CountMissedOccurrences(Anchor, after), "no cap keeps the historical answer");
    }

    [Fact]
    public async Task A_cap_above_the_walks_own_bound_is_still_the_bound_that_applies()
    {
        // The walk keeps a step bound of its own for the ask that carries NO cap. It must not double as a
        // second bound on a caller that passed one: stopping there answers "10,001" to a question asked with
        // a cap of 12,000, and a caller reading "10,001 <= 12,000" concludes it holds the real total. A
        // catch-up cap above ten thousand is not exotic — replaying a month of a five-minute schedule needs
        // one — and that answer is what its circuit breaker decides on.
        var task  = new RecurringTask { CronInterval = new CronInterval("* * * * *") };
        var after = Anchor.AddMinutes(11_000);

        (await Evaluator.CountMissedAsync(task, Anchor, after, 12_000))
            .ShouldBe(11_001, "the whole backlog fits the cap, so the answer is the real total");

        (await Evaluator.CountMissedAsync(task, Anchor, after, 10_500))
            .ShouldBe(10_501, "one past the cap it was GIVEN — the only bound a capped ask reports against");

        (await Evaluator.CountMissedAsync(task, Anchor, after, int.MaxValue - 1))
            .ShouldBe(11_001, "one below int.MaxValue is still a cap, and a cap is always spent in full");

        task.CountMissedOccurrences(Anchor, after)
            .ShouldBe(10_001, "the uncapped ask keeps the historical step bound, and its number a log line");
    }

    [Fact]
    public async Task The_skip_forward_count_says_whether_it_is_a_total_or_a_lower_bound()
    {
        // The realignment past a downtime reports how many runs it cost, and that number reaches a log line
        // and a monitoring event. A grid that counts by division answers the real total however long the
        // outage was; a WALKED one stops at its own bound, and the difference has to travel with the number
        // instead of leaving an operator to read "10,001 runs lost" as a fact.
        var uniform = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var walked  = new RecurringTask { CronInterval = new CronInterval("* * * * *") };

        var now = Anchor.AddDays(92);

        var byDivision = await Evaluator.CalculateNextValidRunAsync(uniform, Anchor, 1, now, isRecovery: true);

        byDivision.SkippedCount.ShouldBeGreaterThan(1_000_000, "three months of a one-second grid, counted");
        byDivision.SkippedCountIsExact.ShouldBeTrue("a subtraction has no bound to stop at");

        var byWalking = await Evaluator.CalculateNextValidRunAsync(walked, Anchor, 1, now, isRecovery: true);

        byWalking.SkippedCount.ShouldBe(10_001, "one past the walk's own bound, which is where it stops");
        byWalking.SkippedCountIsExact.ShouldBeFalse("so the number is 'at least this many' and says so");

        // And a walk that finishes inside its bound is a real total, like every count below it.
        var inside = await Evaluator.CalculateNextValidRunAsync(walked, Anchor, 1, Anchor.AddMinutes(30),
            isRecovery: true);

        inside.SkippedCount.ShouldBe(31);
        inside.SkippedCountIsExact.ShouldBeTrue();
    }

    [Fact]
    public async Task The_capped_count_matches_the_uncapped_one_below_the_cap()
    {
        var task  = new RecurringTask { DayInterval = new DayInterval(1, []) { OnTimes = [new TimeOnly(9, 0)] } };
        var after = Anchor.AddDays(4);

        var uncapped = task.CountMissedOccurrences(Anchor, after);
        uncapped.ShouldBeLessThan(100);

        (await Evaluator.CountMissedAsync(task, Anchor, after, 100)).ShouldBe(uncapped);
    }

    // ---------------------------------------------------------------- due slots (V3)

    /// <summary>
    /// Ground truth for the due set, written the long way: step the pure primitive one occurrence at a time
    /// from the cursor and keep whatever is not later than now. Whatever the seam does internally, it has to
    /// agree with the grid every other answer comes from.
    /// </summary>
    private static List<DateTimeOffset> DueSlotsByHand(RecurringTask task, DateTimeOffset cursor,
                                                       DateTimeOffset nowUtc)
    {
        var slots = new List<DateTimeOffset>();
        var slot  = cursor;

        while (slot <= nowUtc && !(task.RunUntil is { } end && slot >= end))
        {
            slots.Add(slot);

            if (task.NextOccurrenceStrictlyAfter(slot, slot) is not { } next || next <= slot)
                break;

            slot = next;
        }

        return slots;
    }

    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task The_due_slots_are_the_grid_walked_by_hand(string shape, RecurringTask task)
    {
        var now = Anchor.AddHours(3);

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor, now, int.MaxValue))
            .ShouldBe(DueSlotsByHand(task, Anchor, now), $"the due set diverged from the grid for '{shape}'");
    }

    [Fact]
    public async Task Nothing_is_due_while_the_cursor_is_still_in_the_future()
    {
        var task = new RecurringTask { SecondInterval = new SecondInterval(30) };

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor.AddMinutes(1), Anchor, int.MaxValue))
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task The_cursor_slot_itself_is_due_the_moment_it_arrives()
    {
        var task = new RecurringTask { MinuteInterval = new MinuteInterval(5) };

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor, Anchor, int.MaxValue))
            .ShouldBe([Anchor], "a slot is due at its own instant, and it is the oldest one");
    }

    [Fact]
    public async Task The_due_slots_stop_at_the_cap_instead_of_enumerating_the_whole_backlog()
    {
        // The reason the cap is not optional: three months of a one-second grid is roughly eight million
        // slots, and the catch-up window is a handful of them.
        var task = new RecurringTask { SecondInterval = new SecondInterval(1) };
        var now  = Anchor.AddDays(92);

        var slots = await Evaluator.EnumerateDueSlotsAsync(task, Anchor, now, 100);

        slots.Count.ShouldBe(100);
        slots[0].ShouldBe(Anchor, "oldest first");
        slots[99].ShouldBe(Anchor.AddSeconds(99));
    }

    [Fact]
    public async Task An_empty_cap_asks_for_nothing_and_walks_nothing()
    {
        var task = new RecurringTask { SecondInterval = new SecondInterval(1) };

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor, Anchor.AddDays(92), 0)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_due_slots_end_at_RunUntil_and_never_reach_it()
    {
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10),
            RunUntil       = Anchor.AddMinutes(30)
        };

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor, Anchor.AddHours(2), int.MaxValue))
            .ShouldBe([Anchor, Anchor.AddMinutes(10), Anchor.AddMinutes(20)],
                "RunUntil is exclusive, so the slot landing exactly on the boundary is not owed");
    }

    [Fact]
    public async Task A_cursor_already_past_RunUntil_owes_nothing()
    {
        // The row a series leaves behind when its boundary elapsed during a downtime: the cursor is still
        // set and long due, but every slot from it on is outside the series.
        var task = new RecurringTask
        {
            MinuteInterval = new MinuteInterval(10),
            RunUntil       = Anchor
        };

        (await Evaluator.EnumerateDueSlotsAsync(task, Anchor, Anchor.AddHours(2), int.MaxValue)).ShouldBeEmpty();
    }
}
