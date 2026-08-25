namespace EverTask.Abstractions;

/// <summary>
/// What a schedule looked like after <see cref="ITaskScheduleManager"/> changed it.
/// </summary>
public sealed record ScheduleUpdateResult
{
    /// <summary>The schedule row that was updated.</summary>
    public Guid TaskId { get; init; }

    /// <summary>
    /// The version the row now carries. Every advance of this schedule compares against it, so a run that
    /// finishes after this call loses and recomputes instead of writing its stale next run.
    /// </summary>
    public int ScheduleVersion { get; init; }

    /// <summary>The version the row carried before.</summary>
    public int PreviousScheduleVersion { get; init; }

    /// <summary>Where the schedule now stands: the slot it will fire next.</summary>
    public DateTimeOffset? NextRunUtc { get; init; }

    /// <summary>The cursor the schedule stood at before, or <c>null</c> if the series had ended.</summary>
    public DateTimeOffset? PreviousNextRunUtc { get; init; }

    /// <summary>The mode the cursor was decided with.</summary>
    public RescheduleMode Mode { get; init; }

    /// <summary>
    /// Slots the old definition still owed that this call dropped. Only a durable schedule can owe any — an
    /// inline one has no backlog to speak of — and only <see cref="RescheduleMode.RecalculateFromNow"/> drops
    /// them.
    /// </summary>
    public int DiscardedBacklog { get; init; }

    /// <summary>
    /// Whether <see cref="DiscardedBacklog"/> is the real total or a lower bound. A grid that has to be walked
    /// is counted under a cap, and a truncated count is never reported as a total.
    /// </summary>
    public bool DiscardedBacklogIsExact { get; init; } = true;

    /// <summary>
    /// Whether this call cleared a durable catch-up halt. A halted schedule never releases itself, so this is
    /// the only thing that puts one back to work.
    /// </summary>
    public bool ReleasedHalt { get; init; }
}
