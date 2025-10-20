namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Extension methods for RecurringTask to calculate next valid run times
/// with support for skipping missed occurrences.
/// </summary>
public static class RecurringTaskExtensions
{
    /// <summary>
    /// Calculates the next valid run time for a recurring task, automatically skipping
    /// any occurrences that are in the past (e.g., after system downtime).
    /// </summary>
    /// <param name="recurringTask">The recurring task configuration</param>
    /// <param name="scheduledTime">The scheduled time to calculate from (usually the last scheduled execution time)</param>
    /// <param name="currentRun">The current run count</param>
    /// <param name="maxIterations">Maximum number of iterations to prevent infinite loops (default: 1000)</param>
    /// <returns>A NextRunResult containing the next valid run time and information about skipped occurrences</returns>
    /// <remarks>
    /// This method maintains schedule consistency by calculating from the scheduled time rather than
    /// the current time, preventing schedule drift. If the calculated next run is in the past,
    /// it will iteratively calculate forward until finding a future occurrence or hitting the max iterations limit.
    ///
    /// See docs/recurring-task-schedule-drift-fix.md for detailed information.
    /// </remarks>
    public static NextRunResult CalculateNextValidRun(
        this RecurringTask recurringTask,
        DateTimeOffset scheduledTime,
        int currentRun,
        int maxIterations = 1000)
    {
        ArgumentNullException.ThrowIfNull(recurringTask);

        var nextRun = recurringTask.CalculateNextRun(scheduledTime, currentRun);
        var skippedOccurrences = new List<DateTimeOffset>();
        var now = DateTimeOffset.UtcNow;

        // Skip forward through any missed occurrences
        while (nextRun.HasValue && nextRun.Value < now && maxIterations-- > 0)
        {
            skippedOccurrences.Add(nextRun.Value);
            nextRun = recurringTask.CalculateNextRun(nextRun.Value, currentRun);
        }

        // If we hit the max iterations, return null to stop recurrence
        if (maxIterations <= 0 && nextRun.HasValue && nextRun.Value < now)
        {
            nextRun = null;
        }

        return new NextRunResult(nextRun, skippedOccurrences.Count, skippedOccurrences);
    }
}
