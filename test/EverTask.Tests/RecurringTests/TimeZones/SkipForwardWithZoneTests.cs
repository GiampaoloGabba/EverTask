using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Tests.RecurringTests.TimeZones;

/// <summary>
/// The zoned variant of the skip-forward ground truth (T8). Realignment after a downtime is an OPTIMIZATION
/// of stepping the grid one occurrence at a time: with a zone in play the arithmetic shortcut no longer
/// applies, and the property that must survive that is the same one — the shortcut and the walk agree.
/// </summary>
/// <remarks>
/// The sibling of <c>RecurringCalendarSkipForwardTests</c>, which pins the same property without a zone.
/// The downtimes deliberately straddle both of Rome's 2026 transitions, where an hour-wide arithmetic error
/// would land the realignment on the wrong day.
/// </remarks>
public class SkipForwardWithZoneTests
{
    private const string RomeId = "Europe/Rome";

    private static DateTimeOffset Utc(int y, int mo, int d, int h, int mi) =>
        new(y, mo, d, h, mi, 0, TimeSpan.Zero);

    private static RecurringTask Build(Action<IIntervalSchedulerBuilder> configure, string? zoneId)
    {
        var builder = new RecurringTaskBuilder();
        configure(builder.Schedule());
        builder.RecurringTask.TimeZoneId = zoneId;
        builder.RecurringTask.Validate();

        return builder.RecurringTask;
    }

    /// <summary>
    /// Ground truth: the first occurrence strictly after <paramref name="now"/>, reached by stepping the
    /// ordinary path one occurrence at a time. Skip-forward must never diverge from it.
    /// </summary>
    private static DateTimeOffset? GroundTruth(RecurringTask task, DateTimeOffset anchor, DateTimeOffset now)
    {
        var occurrence = task.CalculateNextRun(anchor, 1);
        var guard      = 0;

        while (occurrence.HasValue && occurrence.Value <= now && guard++ < 500_000)
            occurrence = task.CalculateNextRun(occurrence.Value, 1);

        return occurrence;
    }

    public static TheoryData<string, Action<IIntervalSchedulerBuilder>> Shapes => new()
    {
        { "daily at 09:30", s => s.EveryDay().AtTime(new TimeOnly(9, 30)) },
        { "daily at 02:30 (the wall time both transitions touch)", s => s.EveryDay().AtTime(new TimeOnly(2, 30)) },
        { "Mon/Wed/Fri at 07:15",
            s => s.OnDays(DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday).AtTime(new TimeOnly(7, 15)) },
        { "every 2 weeks on Sunday at 03:00",
            s => s.Every(2).Weeks().OnDay(DayOfWeek.Sunday).AtTime(new TimeOnly(3, 0)) },
        { "the 15th at 23:59", s => s.EveryMonth().OnDay(15).AtTime(new TimeOnly(23, 59)) }
    };

    public static TheoryData<string, Action<IIntervalSchedulerBuilder>, int> ShapesAcrossDowntimes
    {
        get
        {
            var cases = new TheoryData<string, Action<IIntervalSchedulerBuilder>, int>();

            // Hours of downtime, chosen to end before, inside and well past each transition.
            foreach (var hours in new[] { 1, 25, 47, 73, 24 * 10, 24 * 45, 24 * 200 })
            {
                foreach (var row in Shapes)
                    cases.Add((string)row[0], (Action<IIntervalSchedulerBuilder>)row[1], hours);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(ShapesAcrossDowntimes))]
    public void Skip_forward_in_a_zone_equals_stepping_the_grid(
        string shape, Action<IIntervalSchedulerBuilder> configure, int downtimeHours)
    {
        var task = Build(configure, RomeId);

        // Seeded a few days before the spring transition, so the shorter downtimes end inside it and the
        // longer ones run past the autumn one.
        var anchor = Utc(2026, 3, 26, 12, 0);
        var now    = anchor.AddHours(downtimeHours);

        task.NextOccurrenceStrictlyAfter(anchor, now)
            .ShouldBe(GroundTruth(task, anchor, now),
                $"'{shape}' realigning after {downtimeHours}h must land where the walk lands");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void The_same_schedule_without_a_zone_still_takes_the_path_it_always_did(
        string shape, Action<IIntervalSchedulerBuilder> configure)
    {
        // P1: the classification must not change what a zone-less schedule computes, so the ground-truth
        // property is re-checked without one — this is the legacy grid, unchanged.
        var task = Build(configure, zoneId: null);

        var anchor = Utc(2026, 3, 26, 12, 0);
        var now    = anchor.AddDays(45);

        task.NextOccurrenceStrictlyAfter(anchor, now)
            .ShouldBe(GroundTruth(task, anchor, now), shape);
    }

    [Fact]
    public void A_zoned_realignment_lands_on_the_local_hour_and_not_an_hour_off()
    {
        // The failure a uniform-grid shortcut would produce: 24-hour arithmetic across the spring transition
        // lands at 08:30 local instead of 09:30, every day for the rest of the year.
        var task = Build(s => s.EveryDay().AtTime(new TimeOnly(9, 30)), RomeId);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(RomeId);

        var next = task.NextOccurrenceStrictlyAfter(Utc(2026, 3, 26, 8, 30), Utc(2026, 4, 10, 12, 0))
                       .ShouldNotBeNull();

        TimeZoneInfo.ConvertTime(next, zone).TimeOfDay.ShouldBe(new TimeSpan(9, 30, 0));
        next.ShouldBe(Utc(2026, 4, 11, 7, 30));
    }

    [Fact]
    public void The_missed_count_of_a_zoned_schedule_counts_real_occurrences()
    {
        // The count is logging-only, but it walks the same grid: with a zone it must be the number of local
        // 09:30s in the window, not the window divided by an assumed constant step.
        var task = Build(s => s.EveryDay().AtTime(new TimeOnly(9, 30)), RomeId);

        // From 27 March 09:30 local through 5 April 12:00 local: 27, 28, 29, 30, 31 March + 1..5 April = 10.
        var missed = task.CountMissedOccurrences(Utc(2026, 3, 27, 8, 30), Utc(2026, 4, 5, 10, 0));

        missed.ShouldBe(10);
    }

    [Fact]
    public void The_missed_count_stops_at_its_cap()
    {
        var task = Build(s => s.EveryDay().AtTime(new TimeOnly(9, 30)), RomeId);

        task.CountMissedOccurrences(Utc(2026, 3, 27, 8, 30), Utc(2027, 3, 27, 8, 30), cap: 5)
            .ShouldBe(6, "a bounded count reports cap + 1 rather than enumerating a year of slots");
    }
}
