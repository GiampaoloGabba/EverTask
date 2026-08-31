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
)
{
    /// <summary>
    /// How many further nominal wall-clock slots a daylight-saving transition folded into
    /// <see cref="NextRun"/>. Zero for a schedule with no time zone, for cron (whose transition rules are
    /// Cronos's own) and for every occurrence no transition touched.
    /// </summary>
    /// <remarks>
    /// Logging only, like <see cref="SkippedCount"/>: the collapsed slots ARE the one occurrence, so they
    /// consume exactly one run of the budget. An <c>init</c> property in the body rather than a positional
    /// parameter, so the primary constructor and the generated <c>Deconstruct</c> keep the arity an already
    /// compiled consumer calls.
    /// </remarks>
    public int CollapsedSlotCount { get; init; }

    /// <summary>
    /// Whether <see cref="SkippedCount"/> is the real total or only a lower bound. A grid that has to be
    /// WALKED is counted under a bound, so a downtime longer than that answers "at least this many".
    /// </summary>
    /// <remarks>
    /// Defaults to <c>true</c>, which is what a grid that counts by division answers and what every count
    /// below its bound answers. An <c>init</c> property in the body rather than a positional parameter, so the
    /// primary constructor and the generated <c>Deconstruct</c> keep the arity an already compiled consumer
    /// calls.
    /// </remarks>
    public bool SkippedCountIsExact { get; init; } = true;
}
