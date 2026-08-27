using System.Text.Json;

namespace EverTask.Storage;

/// <summary>
/// The schedule half of <see cref="QueuedTask.RuntimeInfo"/>: the runtime state of a DURABLE schedule row, as
/// opposed to <see cref="OccurrenceRuntimeInfo"/>, which is what an occurrence row states about itself.
/// </summary>
/// <remarks>
/// One column, two shapes, told apart by <see cref="QueuedTask.ParentTaskId"/> — a schedule row has none. Both
/// are read leniently for the same reason: a value written by a future version, or the other shape, must never
/// make a delivery or a materialization fail.
/// </remarks>
internal sealed record ScheduleRuntimeInfo
{
    /// <summary>The circuit breaker of an overflowing catch-up, or null while the schedule is running.</summary>
    public ScheduleHaltInfo? Halted { get; init; }

    /// <inheritdoc cref="OccurrenceRuntimeInfo.TryParse"/>
    public static ScheduleRuntimeInfo? TryParse(string? runtimeInfo)
    {
        if (string.IsNullOrWhiteSpace(runtimeInfo))
            return null;

        try
        {
            return EverTaskJson.Deserialize<ScheduleRuntimeInfo>(runtimeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The JSON written into the schedule row's <c>RuntimeInfo</c> column.</summary>
    public string Serialize() => EverTaskJson.Serialize(this);
}

/// <summary>
/// Why and when a catch-up stopped itself, kept DURABLY so the passage of time cannot restart it: aging past
/// <see cref="CatchUpOptions.MaxAge"/> would eventually bring the backlog under the cap and quietly resume a
/// schedule an operator was supposed to look at.
/// </summary>
internal sealed record ScheduleHaltInfo
{
    /// <summary>When the halt was decided.</summary>
    public DateTimeOffset AtUtc { get; init; }

    /// <summary>Human-readable reason, carried into the monitoring event and the dashboard.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// The backlog that triggered the halt. On a grid that has to be walked the counting stops one past the
    /// cap, so this is "at least this many" and <see cref="IsExact"/> says so; a grid that counts by division
    /// reports the real total, which is what an operator reconciling a long outage actually needs.
    /// </summary>
    public int DetectedAtLeast { get; init; }

    /// <summary>Whether <see cref="DetectedAtLeast"/> is the real total or only a lower bound.</summary>
    public bool IsExact { get; init; }

    /// <summary>The cursor the halt was decided against.</summary>
    public DateTimeOffset? CursorUtc { get; init; }

    /// <summary>The schedule version the halt was decided against.</summary>
    public int ScheduleVersion { get; init; }
}
