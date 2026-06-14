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
        bool isRecovery = false)
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

        // nextRun is in the past - need to skip forward
        return HasCronExpression(recurringTask)
                   ? CalculateNextValidRunForCron(recurringTask, nextRun.Value, now, currentRun)
                   : CalculateNextValidRunForSimpleInterval(recurringTask, nextRun.Value, now, currentRun);
    }

    /// <summary>
    /// Calculates next valid run for simple intervals (Second/Minute/Hour/Day/Week/Month) using O(1) math.
    /// </summary>
    private static NextRunResult CalculateNextValidRunForSimpleInterval(
        RecurringTask recurringTask,
        DateTimeOffset nextRun,
        DateTimeOffset now,
        int currentRun)
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

        // Check MaxRuns constraint (long comparison so currentRun + skippedCount cannot overflow)
        if (recurringTask.MaxRuns.HasValue && currentRun + skippedCountLong >= recurringTask.MaxRuns.Value)
        {
            return new NextRunResult(null, skippedCount);
        }

        // Jump directly to the skipped time using the long count (no per-occurrence iteration)
        var candidateNextRun = nextRun.AddMilliseconds(skippedCountLong * interval.TotalMilliseconds);

        // Bounded floating-point correction only (never an unbounded skip-forward loop)
        var corrections = 0;
        while (candidateNextRun <= now && corrections++ < 1000)
        {
            candidateNextRun = candidateNextRun.Add(interval);
            if (skippedCount < int.MaxValue) skippedCount++;

            if (recurringTask.MaxRuns.HasValue && currentRun + (long)skippedCount >= recurringTask.MaxRuns.Value)
            {
                return new NextRunResult(null, skippedCount);
            }
        }

        // Check RunUntil constraint
        return recurringTask.RunUntil.HasValue && candidateNextRun >= recurringTask.RunUntil.Value
                   ? new NextRunResult(null, skippedCount)
                   : new NextRunResult(candidateNextRun, skippedCount);
    }

    /// <summary>
    /// Calculates next valid run for cron expressions using Cronos.GetNextOccurrence (O(1)).
    /// </summary>
    private static NextRunResult CalculateNextValidRunForCron(
        RecurringTask recurringTask,
        DateTimeOffset nextRun,
        DateTimeOffset now,
        int currentRun)
    {
        var cron = recurringTask.CronInterval;
        if (cron == null)
        {
            return new NextRunResult(nextRun, 0);
        }

        // Count the REAL number of cron occurrences missed between `nextRun` (the first missed
        // occurrence, already in the past) and `now` by walking the actual cron schedule, instead of
        // dividing the elapsed span by an approximate min-interval which diverges on uneven gaps (F8).
        // Bounded so a sub-minute cron over a huge downtime cannot iterate unboundedly (sibling of
        // L36): beyond the cap the next run falls back to the first occurrence after `now`.
        const int maxCronSkipIterations = 100_000;

        var             skippedCount = 1; // nextRun itself is a missed occurrence
        var             occurrence   = nextRun;
        DateTimeOffset? nextCronRun  = null;
        var             hitCap       = true;

        for (var i = 0; i < maxCronSkipIterations; i++)
        {
            var following = cron.GetNextOccurrence(occurrence);
            if (following == null)
            {
                hitCap = false; // the cron schedule is exhausted (no further occurrence)
                break;
            }

            if (following.Value > now)
            {
                nextCronRun = following; // first STRICTLY-future occurrence is the next valid run
                hitCap      = false;
                break;
            }

            // An occurrence falling exactly on `now` is treated as due/missed (counted as skipped),
            // mirroring the simple-interval path's `candidate <= now` loop, so the next run is always
            // strictly in the future.

            occurrence = following.Value;
            skippedCount++;
        }

        if (hitCap)
            nextCronRun = cron.GetNextOccurrence(now);

        // Check RunUntil constraint
        if (nextCronRun.HasValue && recurringTask.RunUntil.HasValue && nextCronRun >= recurringTask.RunUntil)
        {
            nextCronRun = null;
        }

        // Check MaxRuns constraint
        return recurringTask.MaxRuns.HasValue && currentRun + skippedCount >= recurringTask.MaxRuns.Value
                   ? new NextRunResult(null, skippedCount)
                   : new NextRunResult(nextCronRun, skippedCount);
    }

    /// <summary>
    /// Checks if the recurring task uses a cron expression.
    /// </summary>
    private static bool HasCronExpression(RecurringTask recurringTask) =>
        recurringTask.CronInterval != null &&
        !string.IsNullOrEmpty(recurringTask.CronInterval.CronExpression);
}
