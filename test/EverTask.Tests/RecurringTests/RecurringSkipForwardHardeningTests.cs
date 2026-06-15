using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;

namespace EverTask.Tests.RecurringTests;

/// <summary>
/// Round-2 hardening: the defects surfaced by the adversarial review of the skip-forward fix
/// (see review/recurring-skipforward-deep-review.md). Each test pins the corrected behavior of an
/// <c>internal</c> primitive (InternalsVisibleTo("EverTask.Tests")).
/// </summary>
public class RecurringSkipForwardHardeningTests
{
    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi, int s = 0) =>
        new(y, mo, d, h, mi, s, TimeSpan.Zero);

    // ---------------------------------------------------------------- IsUniformGrid (U2, U10)

    [Fact]
    public void U2_multi_field_pure_cadence_combo_is_a_uniform_grid()
    {
        // Minute(5)+Second(30) is a constant 5m30s grid -> must take the O(1) path, not the walk.
        new RecurringTask { MinuteInterval = new MinuteInterval(5), SecondInterval = new SecondInterval(30) }
            .IsUniformGrid().ShouldBeTrue();

        new RecurringTask { HourInterval = new HourInterval(2), MinuteInterval = new MinuteInterval(15) }
            .IsUniformGrid().ShouldBeTrue();
    }

    [Fact]
    public void U10_onDays_with_zero_interval_is_never_uniform_even_combined_with_a_subfield()
    {
        // OnDays rides on DayInterval(Interval=0); the predicate must inspect OnDays regardless of Interval.
        new RecurringTask
        {
            DayInterval    = new DayInterval(0, new[] { DayOfWeek.Monday }),
            MinuteInterval = new MinuteInterval(5)
        }.IsUniformGrid().ShouldBeFalse();

        new RecurringTask { DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday }) }
            .IsUniformGrid().ShouldBeFalse();
    }

    // ---------------------------------------------------------------- Walk cap / RunUntil (U1, U3)

    [Fact]
    public void U1_walk_cap_never_returns_a_past_occurrence()
    {
        // A calendar schedule whose span exceeds the walk cap must return null (stop) — NEVER a stale past
        // value that would be scheduled immediately and re-fire, consuming MaxRuns.
        var task   = new RecurringTask { HourInterval = new HourInterval(1, new[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23 }) };
        var anchor = Utc(2026, 1, 1, 0, 0);
        var after  = anchor.AddHours(2_000_050); // > MaxNextRunWalkIterations

        var result = task.NextOccurrenceStrictlyAfter(anchor, after);

        (result == null || result.Value > after).ShouldBeTrue();
    }

    [Fact]
    public void U3_uniform_series_ended_returns_null_without_a_stale_value()
    {
        // Uniform grid + RunUntil already past `after`: O(1) null, never a stale past value from a capped walk.
        var task   = new RecurringTask { SecondInterval = new SecondInterval(1), RunUntil = Utc(2026, 1, 1, 0, 0).AddSeconds(2_050_000) };
        var anchor = Utc(2026, 1, 1, 0, 0);
        var after  = anchor.AddSeconds(2_100_000);

        task.NextOccurrenceStrictlyAfter(anchor, after).ShouldBeNull();
    }

    [Fact]
    public void U3_uniform_with_runUntil_still_returns_a_continuing_occurrence_in_O1()
    {
        // Series NOT ended: the next grid point before RunUntil is returned (sanity that the guard didn't over-reach).
        var task   = new RecurringTask { MinuteInterval = new MinuteInterval(5), RunUntil = Utc(2026, 1, 1, 13, 0) };
        var anchor = Utc(2026, 1, 1, 10, 0);
        var after  = Utc(2026, 1, 1, 12, 32);

        var result = task.NextOccurrenceStrictlyAfter(anchor, after);

        result.ShouldBe(Utc(2026, 1, 1, 12, 35));
    }

    // ---------------------------------------------------------------- Off-grid first-run anchor (U7)

    [Fact]
    public void U7_off_grid_anchor_with_snapping_interval_lands_on_the_real_grid()
    {
        // Minute(5) snapping to :30 seconds, anchored at an off-grid first-run time (e.g. SpecificRunTime
        // 10:00:00, off the :30 phase). The step must be measured from the steady grid (10:05:30 -> 10:10:30
        // = 5min), not the irregular first gap (10:00:00 -> 10:05:30 = 5min30s), so the result is a real :30
        // occurrence (10:25:30), not a phantom 10:27:30 from a phase-shifted grid.
        var task   = new RecurringTask { MinuteInterval = new MinuteInterval(5) { OnSecond = 30 } };
        var anchor = Utc(2026, 1, 1, 10, 0, 0);
        var after  = Utc(2026, 1, 1, 10, 22, 0);

        var result = task.NextOccurrenceStrictlyAfter(anchor, after);

        result.ShouldBe(Utc(2026, 1, 1, 10, 25, 30));
    }

    // ---------------------------------------------------------------- Skip-count logging (U8, U9, U10)

    [Fact]
    public void U9_count_clamps_to_runUntil_instead_of_overcounting_phantoms()
    {
        // RunUntil 10s after the anchor; counting to a far `after` must NOT report ~100 phantom occurrences.
        var task   = new RecurringTask { SecondInterval = new SecondInterval(1), RunUntil = Utc(2026, 1, 1, 0, 0, 0).AddSeconds(10) };
        var anchor = Utc(2026, 1, 1, 0, 0, 0);
        var after  = anchor.AddSeconds(100);

        task.CountMissedOccurrences(anchor, after).ShouldBeLessThanOrEqualTo(12);
    }

    [Fact]
    public void U10_false_uniform_schedule_counts_via_the_calendar_walk()
    {
        // DayInterval(0,OnDays)+Minute(5) is NOT a uniform grid: the count must come from the calendar walk,
        // not a garbage division.
        var task   = new RecurringTask
        {
            DayInterval    = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }) { OnTimes = new[] { new TimeOnly(9, 0) } }
        };
        var anchor = Utc(2026, 1, 5, 9, 0);   // Monday
        var after  = Utc(2026, 1, 16, 12, 0); // ~Friday two weeks on

        // Ground truth via the normal path: Mon,Wed,Fri,Mon,Wed,Fri = 6 occurrences in [anchor, after].
        task.CountMissedOccurrences(anchor, after).ShouldBe(6);
    }

    [Fact]
    public void U8_uniform_count_matches_the_walk_at_an_exact_grid_boundary()
    {
        // `after` exactly on a grid point: uniform division must agree with the calendar walk (inclusive of
        // the anchor and the boundary occurrence).
        var task   = new RecurringTask { MinuteInterval = new MinuteInterval(5) };
        var anchor = Utc(2026, 1, 1, 10, 5);
        var after  = Utc(2026, 1, 1, 10, 25); // exact grid point

        task.CountMissedOccurrences(anchor, after).ShouldBe(5); // 10:05,10:10,10:15,10:20,10:25
    }

    // ---------------------------------------------------------------- Overflow (U14)

    [Fact]
    public void U14_uniform_jump_near_max_value_does_not_throw()
    {
        var task   = new RecurringTask { DayInterval = new DayInterval(1) };
        var anchor = new DateTimeOffset(9990, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var after  = DateTimeOffset.MaxValue.AddDays(-2);

        DateTimeOffset? result = null;
        Should.NotThrow(() => result = task.NextOccurrenceStrictlyAfter(anchor, after));
        (result == null || result.Value > after).ShouldBeTrue();
    }

    // ---------------------------------------------------------------- Grace-window decision (U4, U5)

    [Fact]
    public void U4_just_due_calendar_occurrence_is_still_current_on_recovery()
    {
        // OnDays(Mon,Wed,Fri)@09:00; the Wednesday occurrence slipped 5 hours. The next slot (Friday) is not
        // due yet, so the slipped Wednesday is STILL the current one -> recovery must execute it, not skip it.
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }) { OnTimes = new[] { new TimeOnly(9, 0) } }
        };
        var occurrence = Utc(2026, 1, 7, 9, 0);  // Wednesday 09:00
        var now        = Utc(2026, 1, 7, 14, 0); // 5h later, still before Friday

        task.IsOccurrenceStillCurrent(occurrence, now).ShouldBeTrue();

        // The flat GetMinimumInterval heuristic (5-minute default for OnDays) would WRONGLY skip it:
        (now - occurrence > task.GetMinimumInterval()).ShouldBeTrue();
    }

    [Fact]
    public void U5_superseded_calendar_occurrence_is_not_current_on_recovery()
    {
        // Same schedule; now it is Saturday (past Friday's slot). The Wednesday occurrence is superseded ->
        // recovery must skip forward, not execute the stale one.
        var task = new RecurringTask
        {
            DayInterval = new DayInterval(0, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday }) { OnTimes = new[] { new TimeOnly(9, 0) } }
        };
        var occurrence = Utc(2026, 1, 7, 9, 0);   // Wednesday
        var now        = Utc(2026, 1, 10, 12, 0); // Saturday (Friday 09:00 already passed)

        task.IsOccurrenceStillCurrent(occurrence, now).ShouldBeFalse();
    }
}
