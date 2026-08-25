namespace EverTask.Abstractions;

/// <summary>
/// What a schedule does with a slot that came due while nothing was there to run it — a downtime, a saturated
/// queue, a previous run that overran, a rate-limit deferral.
/// </summary>
/// <remarks>
/// <see cref="Skip"/> is the historical behaviour and stays the default. The other two REPLAY missed slots, so
/// each of them needs a durable identity per slot: selecting either one puts the schedule in
/// <see cref="OccurrenceMode.Durable"/>, where every slot becomes its own child row.
/// </remarks>
public enum MisfirePolicy
{
    /// <summary>
    /// Missed slots are dropped: at most the slot that is still the current one runs, and the schedule moves
    /// on to its next future occurrence (default, legacy behaviour).
    /// </summary>
    Skip = 0,

    /// <summary>
    /// The whole run of missed slots is collapsed into ONE occurrence, standing for the most recent of them.
    /// The handler learns the range it covers from <see cref="ITaskExecutionContext.Misfire"/>.
    /// </summary>
    FireOnce = 1,

    /// <summary>
    /// Every missed slot is replayed as its own occurrence, oldest first, inside the caps declared in
    /// <see cref="CatchUpOptions"/>.
    /// </summary>
    CatchUp = 2
}
