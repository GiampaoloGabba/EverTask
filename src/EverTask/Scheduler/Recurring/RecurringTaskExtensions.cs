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
    /// <returns>A NextRunResult containing the next valid run time and the count of skipped occurrences</returns>
    /// <remarks>
    /// Realignment is calendar-aware via the single <see cref="RecurringTask.NextOccurrenceStrictlyAfter"/>
    /// primitive: O(1) for cron (Cronos) and for uniform arithmetic grids (every N seconds/minutes/…), and a
    /// bounded calendar walk for non-uniform schedules (OnDays, OnHours, Month, multi-OnTimes, combinations),
    /// which are coarse by nature. It never uses the approximate flat <see cref="RecurringTask.GetMinimumInterval"/>,
    /// which diverges on uneven schedules (F8).
    /// </remarks>
    public static NextRunResult CalculateNextValidRun(
        this RecurringTask recurringTask,
        DateTimeOffset scheduledTime,
        int currentRun,
        DateTimeOffset? referenceTime = null,
        bool isRecovery = false,
        bool computeSkippedCount = true)
    {
        ArgumentNullException.ThrowIfNull(recurringTask);

        // isRecovery: on the recovery path the first run's time was already decided at dispatch, so the
        // initial-run configuration (InitialDelay/RunNow/SpecificRunTime) must not be re-applied while
        // skipping forward (L25-firstrun).
        var nextRun = recurringTask.CalculateNextRun(scheduledTime, currentRun, isRecovery);
        var now     = referenceTime ?? DateTimeOffset.UtcNow;

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
