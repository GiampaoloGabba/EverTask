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
    /// Uses O(1) math for both simple intervals and cron expressions (via Cronos.GetNextOccurrence).
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

        // nextRun is in the past - need to skip forward. computeSkippedCount=false (the rate-limit
        // skip-ahead path, where `now` is the limiter's far-future slot) suppresses the LOGGING-ONLY
        // cron occurrence walk: counting "missed" occurrences up to a far slot is meaningless noise
        // and, on a sub-minute cron skipped hours ahead, up to maxCronSkipIterations of pure waste (O).
        return HasCronExpression(recurringTask)
                   ? CalculateNextValidRunForCron(recurringTask, nextRun.Value, now, computeSkippedCount)
                   : CalculateNextValidRunForSimpleInterval(recurringTask, nextRun.Value, now);
    }

    /// <summary>
    /// Calculates next valid run for simple intervals (Second/Minute/Hour/Day/Week/Month) using O(1) math.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="NextRunResult.SkippedCount"/> is reported for logging only: occurrences
    /// skipped to realign the schedule after a downtime do NOT consume the <c>MaxRuns</c> budget
    /// (Option B accounting — only real executions count). The single <c>MaxRuns</c> gate lives in
    /// <see cref="RecurringTask.CalculateNextRun"/>, evaluated on the real run count alone before any
    /// skip-forward begins.
    /// </remarks>
    private static NextRunResult CalculateNextValidRunForSimpleInterval(
        RecurringTask recurringTask,
        DateTimeOffset nextRun,
        DateTimeOffset now)
    {
        var interval = recurringTask.GetMinimumInterval();
        if (interval.TotalMilliseconds <= 0)
        {
            return new NextRunResult(nextRun, 0);
        }

        var elapsed = now - nextRun;

        // long math + saturation: a sub-second interval over a huge elapsed span would overflow an int
        // skip count and turn the forward-skip into an unbounded one-at-a-time loop (L36).
        var skippedCountLong = (long)Math.Ceiling(elapsed.TotalMilliseconds / interval.TotalMilliseconds);
        var skippedCount     = (int)Math.Min(skippedCountLong, int.MaxValue);

        // Jump directly to the skipped time using the long count (no per-occurrence iteration)
        var candidateNextRun = nextRun.AddMilliseconds(skippedCountLong * interval.TotalMilliseconds);

        // Bounded floating-point correction only (never an unbounded skip-forward loop)
        var corrections = 0;
        while (candidateNextRun <= now && corrections++ < 1000)
        {
            candidateNextRun = candidateNextRun.Add(interval);
            if (skippedCount < int.MaxValue) skippedCount++;
        }

        // Check RunUntil constraint
        return recurringTask.RunUntil.HasValue && candidateNextRun >= recurringTask.RunUntil.Value
                   ? new NextRunResult(null, skippedCount)
                   : new NextRunResult(candidateNextRun, skippedCount);
    }

    /// <summary>
    /// Calculates next valid run for cron expressions using Cronos.GetNextOccurrence (O(1)).
    /// </summary>
    /// <remarks>
    /// As with the simple-interval path, the returned skip count is logging-only and does NOT consume
    /// the <c>MaxRuns</c> budget (Option B accounting). The exact cron occurrence walk is kept so the
    /// skip count logged after a downtime is accurate even on uneven schedules.
    /// </remarks>
    private static NextRunResult CalculateNextValidRunForCron(
        RecurringTask recurringTask,
        DateTimeOffset nextRun,
        DateTimeOffset now,
        bool computeSkippedCount = true)
    {
        var cron = recurringTask.CronInterval;
        if (cron == null)
        {
            return new NextRunResult(nextRun, 0);
        }

        // The next valid run is O(1): the first cron occurrence strictly after `now` (which on the
        // rate-limit skip-ahead path is the limiter's far-future slot). Computing it directly keeps the
        // next run independent of the missed-occurrence count below — walking the whole schedule from
        // `nextRun` up to a far `now` just to FIND the next run would be O(occurrences-until-now).
        var nextCronRun = cron.GetNextOccurrence(now);

        // Count the missed cron occurrences in (nextRun, now] by walking the actual schedule, instead of
        // dividing the elapsed span by an approximate min-interval which diverges on uneven gaps (F8).
        // This count is LOGGING ONLY under Option B (it never affects MaxRuns), so the cap is a pure cost
        // bound — beyond it the count is under-reported but the next run (above) is unaffected. Bounded so
        // a sub-minute cron skipped to a far slot cannot iterate unboundedly (sibling of L36).
        // O: on the rate-limit skip-ahead path (computeSkippedCount=false) the walk is skipped entirely —
        // the "missed" count up to the limiter's far slot is meaningless and the walk would be pure waste.
        const int maxCronSkipIterations = 10_000;

        var skippedCount = 1; // nextRun itself is a missed occurrence
        var occurrence   = nextRun;

        for (var i = 0; computeSkippedCount && i < maxCronSkipIterations; i++)
        {
            var following = cron.GetNextOccurrence(occurrence);
            // Stop at the schedule end or the first occurrence past `now` (an occurrence exactly on `now`
            // is treated as due/missed, mirroring the simple-interval `candidate <= now` loop).
            if (following == null || following.Value > now)
                break;

            occurrence = following.Value;
            skippedCount++;
        }

        if (!computeSkippedCount)
            skippedCount = 0;

        // Check RunUntil constraint
        if (nextCronRun.HasValue && recurringTask.RunUntil.HasValue && nextCronRun >= recurringTask.RunUntil)
        {
            nextCronRun = null;
        }

        // Skipped occurrences are reported for logging only and do NOT consume the MaxRuns budget
        // (Option B): the MaxRuns gate is evaluated on real executions in CalculateNextRun.
        return new NextRunResult(nextCronRun, skippedCount);
    }

    /// <summary>
    /// Checks if the recurring task uses a cron expression.
    /// </summary>
    private static bool HasCronExpression(RecurringTask recurringTask) =>
        recurringTask.CronInterval != null &&
        !string.IsNullOrEmpty(recurringTask.CronInterval.CronExpression);
}
