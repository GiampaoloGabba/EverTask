using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Skip-forward (post-downtime realignment) MUST land on a real calendar occurrence for
/// non-uniform schedules — not on an arbitrary multiple of an approximate flat interval.
///
/// The flagship invariant verified here: <c>CalculateNextValidRun(..).NextRun</c> must equal the
/// occurrence you would reach by stepping the NORMAL path (<c>CalculateNextRun(occ, 1)</c>, i.e.
/// <c>GetNextOccurrence</c>) one occurrence at a time from the first occurrence until strictly after
/// <c>now</c>. Skip-forward is an optimization of that walk; it must never diverge from it.
///
/// Today (flat <c>GetMinimumInterval()</c> arithmetic) the calendar cases below diverge:
/// - OnDays(Mon,Wed,Fri) -> DayInterval(Interval=0) -> 5-minute default interval
/// - WeekInterval + OnDays -> 7*Interval-day flat interval
/// - MonthInterval -> 30-day approximation (drifts off the configured day-of-month)
/// - HourInterval + OnHours -> 1-hour flat interval (ignores the uneven 3h/3h/3h/15h gaps)
/// </summary>
public class RecurringCalendarSkipForwardTests
{
    // 2026-01-01 is a Thursday. So: Jan 5 = Mon, Jan 7 = Wed, Jan 8 = Thu, Jan 9 = Fri.
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    /// <summary>
    /// Ground truth: the first occurrence strictly after <paramref name="now"/> reached by stepping the
    /// normal calendar path one occurrence at a time. This is, by definition, the correct skip-forward
    /// target — robust to any quirk in <c>GetNextOccurrence</c> because the production skip-forward must
    /// stay consistent with the very same primitive.
    /// </summary>
    private static DateTimeOffset? GroundTruthNextRun(RecurringTask task, DateTimeOffset scheduledTime,
                                                      DateTimeOffset now)
    {
        var occ = task.CalculateNextRun(scheduledTime, 1); // first occurrence (same seed CalculateNextValidRun uses)
        var guard = 0;
        while (occ.HasValue && occ.Value <= now && guard++ < 5_000_000)
            occ = task.CalculateNextRun(occ.Value, 1); // calendar step via the normal path

        return occ;
    }

    [Fact]
    public void OnDays_MonWedFri_skips_forward_to_the_next_listed_weekday_not_an_arbitrary_5min_mark()
    {
        // .Every().Days().OnDays(Mon,Wed,Fri) builds DayInterval(0, days); flat GetMinimumInterval == 5 min.
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            {
                OnTimes = new[] { new TimeOnly(9, 0) }
            }
        };

        var scheduledTime = Utc(2026, 1, 5, 9, 0);  // Monday
        var now           = Utc(2026, 1, 8, 12, 0); // Thursday (no listed slot today after 12:00)

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.DayOfWeek.ShouldBe(DayOfWeek.Friday);
        result.NextRun.Value.ShouldBe(Utc(2026, 1, 9, 9, 0));      // Friday 09:00
        result.NextRun.Value.ShouldBe(GroundTruthNextRun(task, scheduledTime, now)!.Value);
    }

    [Fact]
    public void WeekInterval_OnDays_skips_forward_to_the_next_listed_weekday()
    {
        var task = new RecurringTask
        {
            WeekInterval = new WeekInterval(1, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            {
                OnTimes = new[] { new TimeOnly(9, 0) }
            }
        };

        var scheduledTime = Utc(2026, 1, 5, 9, 0);  // Monday
        var now           = Utc(2026, 1, 8, 12, 0); // Thursday

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.DayOfWeek.ShouldBe(DayOfWeek.Friday);
        result.NextRun.Value.ShouldBe(GroundTruthNextRun(task, scheduledTime, now)!.Value);
    }

    [Fact]
    public void MonthInterval_OnDay15_does_not_drift_off_the_15th_after_a_long_downtime()
    {
        // Monthly on the 15th at 10:00. Flat 30-day arithmetic drifts earlier than the 15th over months.
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 15, OnTimes = new[] { new TimeOnly(10, 0) } }
        };

        var scheduledTime = Utc(2026, 1, 15, 10, 0);
        var now           = Utc(2026, 12, 10, 12, 0); // ~10 months later, before the December occurrence

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.Day.ShouldBe(15);     // today flat math lands on the 12th
        result.NextRun.Value.Month.ShouldBe(12);
        result.NextRun.Value.ShouldBe(Utc(2026, 12, 15, 10, 0));
        result.NextRun.Value.ShouldBe(GroundTruthNextRun(task, scheduledTime, now)!.Value);
    }

    [Fact]
    public void HourInterval_OnHours_skips_forward_to_a_listed_hour_not_an_arbitrary_one()
    {
        // Fires at 09/12/15/18 only (gaps 3h/3h/3h/15h). Flat 1-hour math lands on an invalid hour.
        var task = new RecurringTask
        {
            HourInterval = new HourInterval(1, new[] { 9, 12, 15, 18 })
        };

        var scheduledTime = Utc(2026, 1, 1, 9, 0);
        var now           = Utc(2026, 1, 1, 16, 30); // between 15:00 and 18:00

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldNotBeNull();
        new[] { 9, 12, 15, 18 }.ShouldContain(result.NextRun!.Value.Hour); // today flat math lands on 17:00
        result.NextRun.Value.ShouldBe(Utc(2026, 1, 1, 18, 0));
        result.NextRun.Value.ShouldBe(GroundTruthNextRun(task, scheduledTime, now)!.Value);
    }

    public static IEnumerable<object[]> SkipForwardConsistencyCases()
    {
        // schedule, scheduledTime, now — skip-forward result must equal the iterated normal path.
        yield return new object[]
        {
            new RecurringTask { DayInterval = new DayInterval(0, new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday }) { OnTimes = new[] { new TimeOnly(8, 0) } } },
            Utc(2026, 1, 6, 8, 0), Utc(2026, 1, 19, 11, 0)
        };
        yield return new object[]
        {
            new RecurringTask { WeekInterval = new WeekInterval(2, new[] { DayOfWeek.Monday }) { OnTimes = new[] { new TimeOnly(7, 30) } } },
            Utc(2026, 1, 5, 7, 30), Utc(2026, 2, 20, 9, 0)
        };
        yield return new object[]
        {
            new RecurringTask { MonthInterval = new MonthInterval(1) { OnDay = 31, OnTimes = new[] { new TimeOnly(0, 0) } } },
            Utc(2026, 1, 31, 0, 0), Utc(2026, 7, 5, 0, 0)
        };
        yield return new object[]
        {
            new RecurringTask { MonthInterval = new MonthInterval(3) { OnDay = 1, OnTimes = new[] { new TimeOnly(6, 0) } } },
            Utc(2026, 1, 1, 6, 0), Utc(2027, 2, 10, 0, 0)
        };
        yield return new object[]
        {
            new RecurringTask { HourInterval = new HourInterval(1, new[] { 0, 6, 12, 18 }) },
            Utc(2026, 1, 1, 0, 0), Utc(2026, 1, 3, 13, 0)
        };
        // Multi-field combinations: not classified as a uniform grid -> routed through the calendar walk,
        // which must still match the iterated normal path exactly.
        yield return new object[]
        {
            new RecurringTask { DayInterval = new DayInterval(2), HourInterval = new HourInterval(6) },
            Utc(2026, 1, 1, 0, 0), Utc(2026, 1, 15, 5, 0)
        };
        yield return new object[]
        {
            new RecurringTask { MinuteInterval = new MinuteInterval(5), SecondInterval = new SecondInterval(30) },
            Utc(2026, 1, 1, 0, 0), Utc(2026, 1, 1, 0, 40)
        };
        // Uniform schedules: must stay consistent too (guards against regressions on the fast path).
        yield return new object[]
        {
            new RecurringTask { MinuteInterval = new MinuteInterval(5) },
            Utc(2026, 1, 10, 10, 0), Utc(2026, 1, 10, 10, 22)
        };
        yield return new object[]
        {
            new RecurringTask { HourInterval = new HourInterval(2) },
            Utc(2026, 1, 10, 0, 0), Utc(2026, 1, 11, 7, 0)
        };
    }

    [Theory]
    [MemberData(nameof(SkipForwardConsistencyCases))]
    public void SkipForward_is_consistent_with_the_iterated_normal_path(
        RecurringTask task, DateTimeOffset scheduledTime, DateTimeOffset now)
    {
        var expected = GroundTruthNextRun(task, scheduledTime, now);

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldBe(expected);
    }

    [Fact]
    public void Calendar_skip_forward_stops_at_RunUntil_when_no_occurrence_remains()
    {
        // Monthly on the 15th, but the series ends before the realigned occurrence would fall.
        var task = new RecurringTask
        {
            MonthInterval = new MonthInterval(1) { OnDay = 15, OnTimes = new[] { new TimeOnly(10, 0) } },
            RunUntil      = Utc(2026, 12, 1, 0, 0)
        };

        var scheduledTime = Utc(2026, 1, 15, 10, 0);
        var now           = Utc(2026, 12, 10, 12, 0); // next 15th (Dec 15) is past RunUntil

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldBeNull();
    }

    [Fact]
    public void Calendar_skip_does_not_consume_MaxRuns_OptionB()
    {
        // OnDays(Mon,Wed,Fri): occurrences skipped to realign after a downtime are LOGGING ONLY and must
        // NOT consume the MaxRuns budget — the calendar walk path must honor Option B like the uniform one.
        RecurringTask Build() => new()
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday })
            {
                OnTimes = new[] { new TimeOnly(9, 0) }
            },
            MaxRuns = 5
        };

        var scheduledTime = Utc(2026, 1, 5, 9, 0);  // Monday
        var now           = Utc(2026, 1, 8, 12, 0); // Thursday

        // 4 real runs (< MaxRuns): the series continues to the next listed weekday despite skipping.
        var below = Build().CalculateNextValidRun(scheduledTime, 4, now);
        below.NextRun.ShouldNotBeNull();
        below.NextRun!.Value.ShouldBe(Utc(2026, 1, 9, 9, 0)); // Friday

        // 5 real runs (== MaxRuns): exhausted, regardless of skipped occurrences.
        var atLimit = Build().CalculateNextValidRun(scheduledTime, 5, now);
        atLimit.NextRun.ShouldBeNull();
    }

    [Fact]
    public void Uniform_skip_forward_preserves_phase_and_lands_on_grid()
    {
        // High-frequency uniform schedule over a long downtime must stay O(1) AND land exactly on the grid
        // (the self-verified arithmetic fast path), preserving the :30-second phase.
        var task = new RecurringTask { SecondInterval = new SecondInterval(45) };

        var scheduledTime = Utc(2026, 1, 1, 0, 0, 30);
        var now           = scheduledTime.AddYears(1); // ~700k occurrences — must not walk

        var result = task.CalculateNextValidRun(scheduledTime, 1, now);

        result.NextRun.ShouldNotBeNull();
        result.NextRun!.Value.ShouldBeGreaterThan(now);
        // On-grid: distance from the anchor occurrence is an exact multiple of 45 seconds.
        var firstOccurrence = task.CalculateNextRun(scheduledTime, 1)!.Value;
        ((result.NextRun.Value - firstOccurrence).Ticks % TimeSpan.FromSeconds(45).Ticks).ShouldBe(0);
    }

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);
}
