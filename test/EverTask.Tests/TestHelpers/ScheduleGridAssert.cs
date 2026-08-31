namespace EverTask.Tests.TestHelpers;

/// <summary>
/// Assertions about the occurrence grid of a recurring series, for the tests whose only observable is
/// a wall-clock stamp (a runs audit's ExecutedAt) rather than a scheduled slot. Where the SCHEDULED
/// slot is persisted (NextRunUtc), assert on the grid directly instead - there it is exact and needs
/// no tolerance.
/// </summary>
public static class ScheduleGridAssert
{
    /// <summary>
    /// Share of the cadence tolerated as off-grid residue. Half a cadence would make the slot count
    /// itself ambiguous, so it has to stay below that.
    /// </summary>
    private const double OffGridToleranceFactor = 0.4;

    /// <summary>
    /// Asserts that a gap measured between two completed runs of a recurring series lands on the
    /// series' occurrence grid: a whole number of cadences - at least <paramref name="minSlots"/> -
    /// plus a residue within <see cref="OffGridToleranceFactor"/> of one cadence.
    /// <para>
    /// Occurrences ARE lost under load: the scheduler dequeues a slot only once it is due, and
    /// CalculateNextValidRun then realigns past the missed ones onto the SAME grid. Pinning the gap to
    /// exactly one cadence is what made these assertions flaky - a single lost slot doubles it.
    /// </para>
    /// <para>
    /// What must never happen is the gap drifting OFF the grid. A re-schedule computed from the wall
    /// clock moves the grid by the execution latency at EVERY run, so anchoring the measurement on the
    /// first run of the sample makes that error accumulate run after run, while the residue of a
    /// correct schedule stays within the jitter between two single runs (ExecutedAt is stamped at
    /// completion, so the residue is the difference of two latencies, not their sum).
    /// </para>
    /// </summary>
    /// <param name="elapsed">Gap between the two <c>ExecutedAt</c> stamps.</param>
    /// <param name="cadence">The series' interval.</param>
    /// <param name="minSlots">Occurrences the gap must span at least (1 for consecutive runs).</param>
    public static void ShouldBeOnOccurrenceGrid(this TimeSpan elapsed, TimeSpan cadence, int minSlots = 1)
    {
        var slots     = (int)Math.Round(elapsed / cadence);
        var residue   = (elapsed - cadence * slots).Duration();
        var tolerance = cadence * OffGridToleranceFactor;

        slots.ShouldBeGreaterThanOrEqualTo(
            minSlots,
            $"{elapsed.TotalSeconds:0.###}s spans fewer than {minSlots} occurrence(s) of a {cadence.TotalSeconds:0.###}s cadence");

        residue.ShouldBeLessThanOrEqualTo(
            tolerance,
            $"{elapsed.TotalSeconds:0.###}s sits {residue.TotalSeconds:0.###}s off the {cadence.TotalSeconds:0.###}s grid (nearest slot: {slots})");
    }
}
