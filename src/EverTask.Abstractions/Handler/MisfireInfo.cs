namespace EverTask.Abstractions;

/// <summary>
/// How a delivery relates to the slot it was supposed to run at.
/// </summary>
public enum MisfireKind
{
    /// <summary>The delivery is on time: it started within the configured misfire threshold of its slot.</summary>
    None = 0,

    /// <summary>The delivery started later than the misfire threshold allows, but it is still its own slot.</summary>
    Late = 1,

    /// <summary>The delivery stands for a run of missed slots, collapsed into the most recent one.</summary>
    FireOnce = 2,

    /// <summary>The delivery is one of a series of missed slots being replayed one row each.</summary>
    CatchUp = 3
}

/// <summary>
/// What a handler can learn about the lateness of the delivery it is running.
/// </summary>
/// <remarks>
/// <see cref="ITaskExecutionContext.Misfire"/> is null for a delivery that ran on time, so a handler that only
/// cares about the normal case never has to look at <see cref="Kind"/>.
/// </remarks>
public sealed record MisfireInfo
{
    /// <summary>What kind of misfire this is. Never <see cref="MisfireKind.None"/> on a reported misfire.</summary>
    public MisfireKind Kind { get; init; }

    /// <summary>
    /// The oldest slot of the run of missed slots this delivery came out of. For a
    /// <see cref="MisfireKind.FireOnce"/> delivery that whole run collapsed into this one execution; for a
    /// <see cref="MisfireKind.CatchUp"/> one it is the backlog this row is a part of.
    /// </summary>
    public DateTimeOffset? MissedFromUtc { get; init; }

    /// <summary>The newest slot of that same run.</summary>
    public DateTimeOffset? MissedThroughUtc { get; init; }

    /// <summary>
    /// How many slots that run holds, both ends included. Zero when the delivery is merely late.
    /// </summary>
    public int MissedCount { get; init; }

    /// <summary>
    /// Whether <see cref="MissedCount"/> is the real total or only a lower bound. Counting a calendar grid
    /// means walking it, and a long outage is not walked to the end just to report a number — when that
    /// happens the count says "at least this many" instead of pretending to be exact.
    /// </summary>
    public bool MissedCountIsExact { get; init; } = true;

    /// <summary>How far past its slot the delivery actually started.</summary>
    public TimeSpan Lateness { get; init; }
}
