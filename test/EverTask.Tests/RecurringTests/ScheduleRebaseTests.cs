using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// The nominal-period rebase (M18): pure arithmetic over two definitions and a cursor, with no host in sight.
/// </summary>
/// <remarks>
/// What it has to prove is a negative as much as a positive. A cursor carried LITERALLY onto a new definition
/// keeps the old instant, which is the wrong day the moment the zone moves and the wrong time the moment the
/// hour does; and a cursor recomputed "from the grid" crosses into the next period, which either skips a
/// period of work or replays one. The period is the thing that must survive.
/// </remarks>
public class ScheduleRebaseTests
{
    private const string Rome       = "Europe/Rome";
    private const string Kiritimati = "Pacific/Kiritimati";

    private static RecurringTask Daily(TimeOnly at, string? zone = null, DateTimeOffset? runUntil = null) => new()
    {
        DayInterval = new DayInterval { Interval = 1, OnTimes = [at] },
        TimeZoneId  = zone,
        RunUntil    = runUntil
    };

    private static RecurringTask Weekly(DayOfWeek day, TimeOnly at) => new()
    {
        WeekInterval = new WeekInterval { Interval = 1, OnDays = [day], OnTimes = [at] }
    };

    private static RecurringTask Monthly(int onDay, TimeOnly at) => new()
    {
        MonthInterval = new MonthInterval { Interval = 1, OnDay = onDay, OnTimes = [at] }
    };

    /// <summary>
    /// What <c>Schedule().OnDays(…).AtTimes(…)</c> builds: a DAY period that really holds several slots, since
    /// a day-of-week selector fires every listed time on every listed day.
    /// </summary>
    private static RecurringTask OnDaysAt(DayOfWeek[] days, TimeOnly[] at, string? zone = null) => new()
    {
        DayInterval = new DayInterval { Interval = 0, OnDays = days, OnTimes = at },
        TimeZoneId  = zone
    };

    /// <summary>The same for a WEEK period: several days, one time each.</summary>
    private static RecurringTask WeeklyOn(DayOfWeek[] days, TimeOnly at) => new()
    {
        WeekInterval = new WeekInterval { Interval = 1, OnDays = days, OnTimes = [at] }
    };

    /// <summary>What <c>Schedule().EveryWeek()</c> builds: a week cadence that names no day.</summary>
    private static RecurringTask EveryWeek(TimeOnly at, string? zone = null) => new()
    {
        WeekInterval = new WeekInterval { Interval = 1, OnTimes = [at] },
        TimeZoneId   = zone
    };

    /// <summary>The same, with more than one time of day inside its period.</summary>
    private static RecurringTask EveryWeekAt(TimeOnly[] at) => new()
    {
        WeekInterval = new WeekInterval { Interval = 1, OnTimes = at }
    };

    /// <summary>What <c>Schedule().EveryMonth()</c> builds: a month cadence that names no day.</summary>
    private static RecurringTask EveryMonth(TimeOnly at, string? zone = null) => new()
    {
        MonthInterval = new MonthInterval { Interval = 1, OnTimes = [at] },
        TimeZoneId    = zone
    };

    // ---- What the period preserves ----------------------------------------------------------------

    [Fact]
    public void Moving_a_daily_schedule_to_another_hour_keeps_it_on_the_same_day()
    {
        // 2026-03-10 09:00 in Rome, which is still CET: European summer time starts on the 29th.
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

        var rebased = ScheduleRebase.Rebase(Daily(new TimeOnly(9, 0), Rome), Daily(new TimeOnly(10, 0), Rome),
            cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero),
            "the pending slot moves to 10:00 on the day it was already on, not on the next one");
    }

    [Fact]
    public void Moving_a_daily_schedule_to_another_zone_keeps_its_logical_day()
    {
        // 09:00 Rome on the 10th. Kiritimati is UTC+14, so the same LOCAL day starts fourteen hours earlier:
        // the rebased instant is EARLIER than the cursor, which is exactly what reading the cursor literally
        // could never produce.
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

        var rebased = ScheduleRebase.Rebase(Daily(new TimeOnly(9, 0), Rome),
            Daily(new TimeOnly(9, 0), Kiritimati), cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 9, 19, 0, 0, TimeSpan.Zero));

        var local = TimeZoneInfo.ConvertTime(rebased, TimeZoneInfo.FindSystemTimeZoneById(Kiritimati));
        local.Date.ShouldBe(new DateTime(2026, 3, 10), "the logical day is the thing that carries over");
        local.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
    }

    [Fact]
    public void A_weekly_schedule_rebases_inside_its_own_week()
    {
        var cursor = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero); // a Wednesday

        var rebased = ScheduleRebase.Rebase(Weekly(DayOfWeek.Wednesday, new TimeOnly(9, 0)),
            Weekly(DayOfWeek.Wednesday, new TimeOnly(17, 0)), cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 11, 17, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_monthly_schedule_rebases_inside_its_own_month()
    {
        var cursor = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);

        var rebased = ScheduleRebase.Rebase(Monthly(15, new TimeOnly(9, 0)), Monthly(15, new TimeOnly(18, 0)),
            cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 15, 18, 0, 0, TimeSpan.Zero));
    }

    // The two shapes the fluent API produces by DEFAULT — EveryWeek() and EveryMonth() name no day — are the
    // ones whose grid phase lives on the cursor: WeekInterval steps current.AddDays(7 * Interval) and
    // MonthInterval steps current.AddMonths(Interval), both keeping the day they were handed. Reading the
    // whole week or month as the period and asking the grid for its first slot answers from the phase the
    // backward probe landed on, which is a different day entirely — and every occurrence after it follows.

    [Fact]
    public void A_weekly_cadence_that_names_no_day_stays_on_the_weekday_the_cursor_was_on()
    {
        var cursor = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero); // a Wednesday

        var rebased = ScheduleRebase.Rebase(EveryWeek(new TimeOnly(9, 0)), EveryWeek(new TimeOnly(10, 0)),
            cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
        rebased.DayOfWeek.ShouldBe(DayOfWeek.Wednesday,
            "the weekday is the definition's phase and it is carried by the cursor, so moving the hour must " +
            "not move the series onto the first day of the week");
    }

    [Fact]
    public void A_monthly_cadence_that_names_no_day_stays_on_the_day_of_the_month_the_cursor_was_on()
    {
        var cursor = new DateTimeOffset(2026, 3, 20, 9, 0, 0, TimeSpan.Zero);

        var rebased = ScheduleRebase.Rebase(EveryMonth(new TimeOnly(9, 0)), EveryMonth(new TimeOnly(10, 0)),
            cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 20, 10, 0, 0, TimeSpan.Zero),
            "the day of the month rides on the cursor, so it is the only thing a rebase may not move");
    }

    [Fact]
    public void A_weekly_cadence_that_names_no_day_keeps_its_logical_day_across_a_zone_change()
    {
        // 09:00 Rome on Wednesday the 11th. Kiritimati is UTC+14, so the same LOCAL day starts fourteen
        // hours earlier and the rebased instant is EARLIER than the cursor.
        var cursor = new DateTimeOffset(2026, 3, 11, 8, 0, 0, TimeSpan.Zero);

        var rebased = ScheduleRebase.Rebase(EveryWeek(new TimeOnly(9, 0), Rome),
            EveryWeek(new TimeOnly(9, 0), Kiritimati), cursor);

        var local = TimeZoneInfo.ConvertTime(rebased, TimeZoneInfo.FindSystemTimeZoneById(Kiritimati));

        local.Date.ShouldBe(new DateTime(2026, 3, 11));
        local.DayOfWeek.ShouldBe(DayOfWeek.Wednesday);
        local.TimeOfDay.ShouldBe(TimeSpan.FromHours(9));
    }

    [Fact]
    public void A_cadence_that_constrains_no_time_of_day_keeps_the_cursors_own_time()
    {
        // An empty OnTimes is the one shape that constrains nothing: the interval hands the probe's own time
        // of day straight back, so the time is part of the phase the cursor carries too.
        var cursor = new DateTimeOffset(2026, 3, 11, 6, 45, 0, TimeSpan.Zero);

        var current     = new RecurringTask { WeekInterval = new WeekInterval { Interval = 1, OnTimes = [] } };
        var replacement = new RecurringTask { WeekInterval = new WeekInterval { Interval = 1, OnTimes = [] } };

        ScheduleRebase.Rebase(current, replacement, cursor).ShouldBe(cursor);
    }

    [Fact]
    public void A_plain_cadence_keeps_its_cursor_verbatim()
    {
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 17, TimeSpan.Zero);

        var current     = new RecurringTask { SecondInterval = new SecondInterval(30), MaxRuns = 10 };
        var replacement = new RecurringTask { SecondInterval = new SecondInterval(30), MaxRuns = 99 };

        ScheduleRebase.Rebase(current, replacement, cursor).ShouldBe(cursor,
            "a constant step in elapsed time has no calendar structure to move, so the period IS the cursor");
    }

    // ---- A period that holds more than one slot ---------------------------------------------------
    //
    // "The first occurrence of the new definition inside the period" IS where the cursor stood, but only while
    // the period holds exactly one slot. As soon as it holds two — a day-of-week selector with two times, a
    // week with two days — the first one is a slot that has already RUN, and rebasing onto it replays that
    // occurrence and spends one more of MaxRuns, while RecalculateFromNow on the identical definition answers
    // the later slot. What has to survive is the cursor's POSITION inside the period, not merely the period.

    [Fact]
    public void A_day_that_holds_two_slots_rebases_onto_the_one_the_cursor_stood_at()
    {
        // Monday and Wednesday, twice each. 15:00 Rome on the Monday: the 09:00 run is done and the cursor
        // stands on the afternoon one, which the operator is moving to 16:00.
        var cursor = new DateTimeOffset(2026, 3, 9, 14, 0, 0, TimeSpan.Zero); // 15:00 Rome, a Monday

        var rebased = ScheduleRebase.Rebase(
            OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday], [new TimeOnly(9, 0), new TimeOnly(15, 0)], Rome),
            OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday], [new TimeOnly(9, 0), new TimeOnly(16, 0)], Rome),
            cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 9, 15, 0, 0, TimeSpan.Zero),
            "16:00 Rome, the slot at the cursor's own position — not 09:00, which ran six hours ago");

        rebased.ShouldBeGreaterThan(cursor,
            "and a rebase inside one zone may never move a cursor backwards onto work already done");
    }

    [Fact]
    public void A_day_that_holds_two_slots_still_rebases_from_the_first_of_them()
    {
        // The control: the same pair with the cursor on the EARLIER slot keeps answering with the earlier one,
        // so what moved above is the position and not the rule.
        var cursor = new DateTimeOffset(2026, 3, 9, 8, 0, 0, TimeSpan.Zero); // 09:00 Rome

        ScheduleRebase.Rebase(
                          OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
                              [new TimeOnly(9, 0), new TimeOnly(15, 0)], Rome),
                          OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
                              [new TimeOnly(10, 0), new TimeOnly(16, 0)], Rome), cursor)
                      .ShouldBe(new DateTimeOffset(2026, 3, 9, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_week_that_holds_two_days_rebases_onto_the_day_the_cursor_stood_on()
    {
        var cursor = new DateTimeOffset(2026, 3, 12, 17, 0, 0, TimeSpan.Zero); // Thursday

        var rebased = ScheduleRebase.Rebase(WeeklyOn([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(17, 0)),
            WeeklyOn([DayOfWeek.Monday, DayOfWeek.Thursday], new TimeOnly(18, 0)), cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 12, 18, 0, 0, TimeSpan.Zero));
        rebased.DayOfWeek.ShouldBe(DayOfWeek.Thursday,
            "the Monday of the same week is three days behind the cursor and its occurrence has already run");
    }

    [Fact]
    public void A_day_carrying_cadence_rebases_onto_the_time_the_cursor_stood_at()
    {
        // The hand-placed branch: EveryWeek() never asks the grid, it composes the slot from the period start
        // and a time of day — and it took the EARLIEST of them, which is the same rewind one level down.
        var cursor = new DateTimeOffset(2026, 3, 11, 15, 0, 0, TimeSpan.Zero); // Wednesday, the later time

        var rebased = ScheduleRebase.Rebase(EveryWeekAt([new TimeOnly(9, 0), new TimeOnly(15, 0)]),
            EveryWeekAt([new TimeOnly(9, 0), new TimeOnly(16, 0)]), cursor);

        rebased.ShouldBe(new DateTimeOffset(2026, 3, 11, 16, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_period_with_fewer_slots_than_the_cursor_had_passed_is_refused()
    {
        // Three slots a day down to two, with the cursor on the third: there is no slot at that position, and
        // answering from an earlier one replays work while answering from the next day skips a period.
        var cursor = new DateTimeOffset(2026, 3, 9, 20, 0, 0, TimeSpan.Zero); // a Monday

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(
                       OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
                           [new TimeOnly(9, 0), new TimeOnly(15, 0), new TimeOnly(20, 0)]),
                       OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
                           [new TimeOnly(9, 0), new TimeOnly(16, 0)]), cursor))
              .Message.ShouldContain("holds fewer than 3 occurrence(s)");
    }

    [Fact]
    public void A_run_until_inside_the_period_does_not_move_the_cursors_own_position()
    {
        // The position is a question about the GRID, so it is counted with the bounds ignored. Counting the old
        // definition's slots through its own bound would stop at 09:00 here, call the cursor the first slot of
        // its day and hand back the morning one — the same replay, arrived at from the other side.
        var cursor = new DateTimeOffset(2026, 3, 9, 15, 0, 0, TimeSpan.Zero);

        var current = OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
            [new TimeOnly(9, 0), new TimeOnly(15, 0)]);

        current.RunUntil = new DateTimeOffset(2026, 3, 9, 12, 0, 0, TimeSpan.Zero);

        ScheduleRebase.Rebase(current,
                          OnDaysAt([DayOfWeek.Monday, DayOfWeek.Wednesday],
                              [new TimeOnly(9, 0), new TimeOnly(16, 0)]), cursor)
                      .ShouldBe(new DateTimeOffset(2026, 3, 9, 16, 0, 0, TimeSpan.Zero));
    }

    // ---- What it refuses --------------------------------------------------------------------------

    [Fact]
    public void A_cron_schedule_is_refused()
    {
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);
        var cron   = new RecurringTask { CronInterval = new CronInterval("0 9 * * *") };

        Should.Throw<InvalidOperationException>(() => ScheduleRebase.Rebase(cron, Daily(new TimeOnly(10, 0)), cursor))
              .Message.ShouldContain("no nominal period");
    }

    [Fact]
    public void A_different_cadence_is_refused()
    {
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

        var faster = new RecurringTask { DayInterval = new DayInterval { Interval = 3, OnTimes = [new TimeOnly(9, 0)] } };

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(Daily(new TimeOnly(9, 0)), faster, cursor))
              .Message.ShouldContain("cadence");
    }

    [Fact]
    public void A_different_day_selector_is_refused()
    {
        var cursor = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(Weekly(DayOfWeek.Wednesday, new TimeOnly(9, 0)),
                       Weekly(DayOfWeek.Thursday, new TimeOnly(9, 0)), cursor))
              .Message.ShouldContain("selectors");
    }

    [Fact]
    public void A_different_period_kind_is_refused()
    {
        var cursor = new DateTimeOffset(2026, 3, 15, 9, 0, 0, TimeSpan.Zero);

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(Monthly(15, new TimeOnly(9, 0)), Daily(new TimeOnly(9, 0)), cursor))
              .Message.ShouldContain("anchored differently");
    }

    [Fact]
    public void A_period_the_new_schedule_has_no_slot_in_is_refused_instead_of_crossing_into_the_next()
    {
        // The series ends at 09:30 on the day the cursor is on, so 10:00 is past its own end: the day the
        // cursor belongs to holds no slot of the new definition, and the day after is not an answer.
        var cursor   = new DateTimeOffset(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);
        var runUntil = new DateTimeOffset(2026, 3, 10, 9, 30, 0, TimeSpan.Zero);

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(Daily(new TimeOnly(9, 0), runUntil: runUntil),
                       Daily(new TimeOnly(10, 0), runUntil: runUntil), cursor))
              .Message.ShouldContain("never crosses into the next one");
    }

    // ---- The bound the two hand-placed branches never asked the grid about -------------------------

    [Fact]
    public void A_plain_cadence_wound_down_with_RunUntil_is_refused_instead_of_running_once_more()
    {
        // Winding a series down is a RunUntil change, which RequireSameShape admits — and the plain cadence
        // keeps its cursor verbatim, so nothing on that path ever reaches the grid, the only thing that
        // applies the bound. The cursor was accepted, written and parked past the end the operator had just
        // set, while RecalculateFromNow refused the identical definition.
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 30, TimeSpan.Zero);
        var bound  = new DateTimeOffset(2026, 3, 10, 8, 0, 0, TimeSpan.Zero);

        var current     = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var replacement = new RecurringTask { SecondInterval = new SecondInterval(30), RunUntil = bound };

        Should.Throw<InvalidOperationException>(() => ScheduleRebase.Rebase(current, replacement, cursor))
              .Message.ShouldContain("RunUntil is exclusive");
    }

    [Fact]
    public void A_plain_cadence_still_rebases_when_its_new_bound_is_ahead_of_the_cursor()
    {
        // The control: the same shape with a bound the cursor has NOT reached is the ordinary case and must
        // keep answering with the cursor itself.
        var cursor = new DateTimeOffset(2026, 3, 10, 8, 0, 30, TimeSpan.Zero);

        var current     = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var replacement = new RecurringTask
        {
            SecondInterval = new SecondInterval(30),
            RunUntil       = cursor.AddSeconds(1)
        };

        ScheduleRebase.Rebase(current, replacement, cursor).ShouldBe(cursor);
    }

    [Fact]
    public void A_cadence_that_carries_its_day_on_the_cursor_is_refused_past_the_new_bound()
    {
        // EveryWeek() places its slot by hand from the period start, so it never goes through the grid
        // either: 10:00 on the cursor's own Wednesday is past a bound of 09:30 that same day.
        var cursor = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);
        var bound  = new DateTimeOffset(2026, 3, 11, 9, 30, 0, TimeSpan.Zero);

        var replacement = EveryWeek(new TimeOnly(10, 0));
        replacement.RunUntil = bound;

        Should.Throw<InvalidOperationException>(
                   () => ScheduleRebase.Rebase(EveryWeek(new TimeOnly(9, 0)), replacement, cursor))
              .Message.ShouldContain("RunUntil is exclusive");
    }

    [Fact]
    public void A_cadence_that_carries_its_day_on_the_cursor_still_rebases_inside_its_new_bound()
    {
        var cursor = new DateTimeOffset(2026, 3, 11, 9, 0, 0, TimeSpan.Zero);

        var replacement = EveryWeek(new TimeOnly(10, 0));
        replacement.RunUntil = new DateTimeOffset(2026, 3, 11, 10, 30, 0, TimeSpan.Zero);

        ScheduleRebase.Rebase(EveryWeek(new TimeOnly(9, 0)), replacement, cursor)
                      .ShouldBe(new DateTimeOffset(2026, 3, 11, 10, 0, 0, TimeSpan.Zero));
    }
}
