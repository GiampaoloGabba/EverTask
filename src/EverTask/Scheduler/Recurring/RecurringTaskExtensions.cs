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
        DateTimeOffset? referenceTime = null)
    {
        ArgumentNullException.ThrowIfNull(recurringTask);

        var nextRun = recurringTask.CalculateNextRun(scheduledTime, currentRun);
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

        var elapsed      = now - nextRun;
        var skippedCount = (int)Math.Ceiling(elapsed.TotalMilliseconds / interval.TotalMilliseconds);

        // Check MaxRuns constraint
        if (recurringTask.MaxRuns.HasValue && currentRun + skippedCount >= recurringTask.MaxRuns.Value)
        {
            return new NextRunResult(null, skippedCount);
        }

        // Calculate the next future run directly
        var skippedTime      = TimeSpan.FromMilliseconds(skippedCount * interval.TotalMilliseconds);
        var candidateNextRun = nextRun.Add(skippedTime);

        // Ensure it's actually in the future (due to floating point precision)
        while (candidateNextRun <= now)
        {
            candidateNextRun = candidateNextRun.Add(interval);
            skippedCount++;

            if (recurringTask.MaxRuns.HasValue && currentRun + skippedCount >= recurringTask.MaxRuns.Value)
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
        if (recurringTask.CronInterval == null)
        {
            return new NextRunResult(nextRun, 0);
        }

        // Cronos.GetNextOccurrence is O(1) - calculates directly without iteration
        var nextCronRun = recurringTask.CronInterval.GetNextOccurrence(now);

        // Check RunUntil constraint
        if (nextCronRun.HasValue && recurringTask.RunUntil.HasValue && nextCronRun >= recurringTask.RunUntil)
        {
            nextCronRun = null;
        }

        // Estimate skipped count (not exact for cron, but gives an approximation)
        var skippedCount = 0;
        if (nextCronRun.HasValue)
        {
            var cronInterval = recurringTask.GetMinimumInterval();
            if (cronInterval.TotalMilliseconds > 0)
            {
                var elapsed = now - nextRun;
                skippedCount = Math.Max(0, (int)(elapsed.TotalMilliseconds / cronInterval.TotalMilliseconds));
            }
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
