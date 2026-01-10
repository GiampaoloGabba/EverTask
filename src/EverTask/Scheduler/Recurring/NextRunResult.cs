namespace EverTask.Scheduler.Recurring;

/// <summary>
/// Result of calculating the next valid run time for a recurring task,
/// including information about any skipped occurrences.
/// </summary>
/// <param name="NextRun">The next valid run time, or null if no more runs should occur</param>
/// <param name="SkippedCount">The number of occurrences that were skipped because they were in the past</param>
public record NextRunResult(
    DateTimeOffset? NextRun,
    int SkippedCount
);
