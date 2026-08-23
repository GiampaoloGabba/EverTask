namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Extension methods for RecurringTask to calculate next valid run times
/// with support for skipping missed occurrences.
/// </summary>
public static class RecurringTaskExtensions
{
    /// <summary>
    /// Tolerance in seconds for near-immediate executions.
    /// Prevents RunNow or just-scheduled tasks from being treated as "in the past".
    /// </summary>
    private const int ToleranceSeconds = 1;

    /// <summary>
    /// Calculates the next valid run time for a recurring task, automatically skipping
    /// any occurrences that are in the past (e.g., after system downtime).
    /// </summary>
    /// <param name="recurringTask">The recurring task configuration</param>
    /// <param name="scheduledTime">The scheduled time to calculate from (usually the last scheduled execution time)</param>
    /// <param name="currentRun">The current run count</param>
    /// <param name="referenceTime">Optional reference time for "now" comparison. If null, uses DateTimeOffset.UtcNow</param>
    /// <param name="isRecovery">
    /// True on the startup-recovery path, where the first run's time was already decided at dispatch: the
    /// initial-run configuration (InitialDelay/RunNow/SpecificRunTime) must not be re-applied while skipping
    /// forward, and the skip count is anchored on the stored (slipped) occurrence.
    /// </param>
    /// <param name="computeSkippedCount">
    /// When false, the logging-only skipped count is suppressed (returned as 0). Used by the rate-limit
    /// skip-ahead path, where "now" is the limiter's far-future slot and a missed count up to it is noise.
    /// </param>
    /// <returns>A NextRunResult containing the next valid run time and the count of skipped occurrences</returns>
    /// <remarks>
    /// Realignment is calendar-aware via the single <see cref="RecurringTask.NextOccurrenceStrictlyAfter"/>
    /// primitive: O(1) for cron (Cronos) and for uniform arithmetic grids (every N seconds/minutes/…), and a
    /// bounded calendar walk for non-uniform schedules (OnDays, OnHours, Month, multi-OnTimes, combinations),
    /// which are coarse by nature. It never uses the approximate flat <see cref="RecurringTask.GetMinimumInterval()"/>,
    /// which diverges on uneven schedules (F8).
    /// <para>
    /// The parameter list is frozen at the shape the previous release shipped (P6/X6): the scheduling clock
    /// travels through the overload below, because appending an optional parameter here would have replaced
    /// this method's IL signature and broken every already-compiled caller.
    /// </para>
    /// </remarks>
    public static NextRunResult CalculateNextValidRun(
        this RecurringTask recurringTask,
        DateTimeOffset scheduledTime,
        int currentRun,
        DateTimeOffset? referenceTime = null,
        bool isRecovery = false,
        bool computeSkippedCount = true) =>
        recurringTask.CalculateNextValidRun(scheduledTime, currentRun, referenceTime, isRecovery,
            computeSkippedCount, null);

    /// <summary>
    /// <see cref="CalculateNextValidRun(RecurringTask,DateTimeOffset,int,DateTimeOffset?,bool,bool)"/>
    /// evaluated against the scheduling clock (P9).
    /// </summary>
    /// <param name="recurringTask">The recurring task configuration</param>
    /// <param name="scheduledTime">The scheduled time to calculate from (usually the last scheduled execution time)</param>
    /// <param name="currentRun">The current run count</param>
    /// <param name="referenceTime">Optional reference time for "now" comparison. If null, <paramref name="nowUtc"/> is used</param>
    /// <param name="isRecovery">See the six-parameter overload.</param>
    /// <param name="computeSkippedCount">See the six-parameter overload.</param>
    /// <param name="nowUtc">
    /// The scheduling clock's "now". Used as the fallback reference when <paramref name="referenceTime"/> is
    /// absent, and handed to the first-run computation so <c>RunNow</c> resolves on the same clock. Null
    /// falls back to the real clock, for callers outside the deterministic scheduling path.
    /// </param>
    public static NextRunResult CalculateNextValidRun(
        this RecurringTask recurringTask,
        DateTimeOffset scheduledTime,
        int currentRun,
        DateTimeOffset? referenceTime,
        bool isRecovery,
        bool computeSkippedCount,
        DateTimeOffset? nowUtc)
    {
        ArgumentNullException.ThrowIfNull(recurringTask);

        // isRecovery: on the recovery path the first run's time was already decided at dispatch, so the
        // initial-run configuration (InitialDelay/RunNow/SpecificRunTime) must not be re-applied while
        // skipping forward (L25-firstrun).
        var nextRun = recurringTask.CalculateNextRun(scheduledTime, currentRun, isRecovery, nowUtc);
        var now     = referenceTime ?? nowUtc ?? DateTimeOffset.UtcNow;

        // If nextRun is not significantly in the past, return as-is
        if (!nextRun.HasValue || nextRun.Value >= now.AddSeconds(-ToleranceSeconds))
        {
            return new NextRunResult(nextRun, 0);
        }

        // nextRun is significantly in the past — realign past the downtime. ONE primitive for every schedule
        // kind (cron, uniform interval, calendar): the next run is the first real occurrence strictly after
        // `now` (calendar-aware, never flat-interval arithmetic that diverges on uneven schedules — F8). The
        // skip count is LOGGING ONLY (Option B: it never consumes MaxRuns) and is suppressed on the
        // rate-limit skip-ahead path (computeSkippedCount=false), where `now` is the limiter's far-future
        // slot and a "missed" count up to it is meaningless noise (O).
        var next = recurringTask.NextOccurrenceStrictlyAfter(nextRun.Value, now);

        // Skip-count anchor (logging-only): on RECOVERY `scheduledTime` IS the stored slipped occurrence
        // (itself missed during the downtime), so count from it to include it; otherwise it is the
        // just-executed occurrence (not missed), so count from the next occurrence (U12).
        var countAnchor = isRecovery ? scheduledTime : nextRun.Value;
        var skipped     = computeSkippedCount ? recurringTask.CountMissedOccurrences(countAnchor, now) : 0;

        return new NextRunResult(next, skipped);
    }
}
