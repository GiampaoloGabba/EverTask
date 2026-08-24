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

    /// <summary>The first slot this delivery stands for, when it covers more than its own.</summary>
    public DateTimeOffset? MissedFromUtc { get; init; }

    /// <summary>The last slot this delivery stands for, when it covers more than its own.</summary>
    public DateTimeOffset? MissedThroughUtc { get; init; }

    /// <summary>How many slots were missed. Zero when the delivery is merely late.</summary>
    public int MissedCount { get; init; }

    /// <summary>How far past its slot the delivery actually started.</summary>
    public TimeSpan Lateness { get; init; }
}
