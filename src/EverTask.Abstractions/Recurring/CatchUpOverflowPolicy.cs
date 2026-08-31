namespace EverTask.Abstractions;

/// <summary>
/// What a catch-up episode does when it owes more slots than <see cref="CatchUpOptions.MaxOccurrences"/>
/// allows in one episode.
/// </summary>
public enum CatchUpOverflowPolicy
{
    /// <summary>
    /// Stop the schedule and wait for an operator (default). Nothing is materialized, a durable "halted"
    /// marker is written on the schedule row, and only an explicit resume or reschedule starts it again — the
    /// passage of time never does, so a backlog nobody looked at cannot quietly turn into a flood of work.
    /// </summary>
    Halt = 0,

    /// <summary>
    /// Replay only the most recent <see cref="CatchUpOptions.MaxOccurrences"/> slots and drop the older ones,
    /// reporting how many were dropped.
    /// </summary>
    /// <remarks>
    /// Only available when the occurrence grid is deterministic, which every built-in schedule is: finding
    /// where the last N slots begin means probing the grid, and a grid that answers differently on each probe
    /// would land on the wrong slot.
    /// </remarks>
    SkipOldest = 1
}
