using System.Text.Json;

namespace EverTask.Storage;

/// <summary>
/// The occurrence half of <see cref="QueuedTask.RuntimeInfo"/>: what a materialized occurrence row states
/// about itself — the slot it stands for and the run of the series it is.
/// </summary>
/// <remarks>
/// These two facts belong to the ROW, not to the executor that happens to deliver it (C1/C4). The row's own
/// counter cannot answer either: an occurrence is a one-shot, so its <see cref="QueuedTask.CurrentRunCount"/>
/// is zero and reading it would report every recovered occurrence as run 1, whichever run of the series it
/// really is. The slot is read from here for the same reason it is persisted here at all — the executor's
/// <c>ExecutionTime</c> is the moment the scheduler fires the delivery, which the rate-limit gate replaces
/// with the slot it reserved.
/// <para>
/// Serialized with <see cref="EverTaskJson"/> like every other persisted shape, and read leniently: a schedule
/// row's own runtime state lives in the same column, and neither it nor a value written by a future version
/// may make a delivery fail.
/// </para>
/// </remarks>
internal sealed record OccurrenceRuntimeInfo
{
    /// <summary>The nominal slot of the occurrence, equal to the row's <c>ScheduledExecutionUtc</c>.</summary>
    public DateTimeOffset? SlotUtc { get; init; }

    /// <summary>The 1-based run of the series this occurrence is.</summary>
    public int? RunNumber { get; init; }

    /// <summary>
    /// Reads the occurrence metadata of a row, or null when there is none to read.
    /// </summary>
    /// <remarks>
    /// Unparseable JSON is NOT an error here: the caller keeps the values the row's own columns give it, which
    /// is the behavior of every row written before the metadata existed. Losing a delivery over a diagnostic
    /// field would be a far worse outcome than reporting the column-derived slot.
    /// </remarks>
    public static OccurrenceRuntimeInfo? TryParse(string? runtimeInfo)
    {
        if (string.IsNullOrWhiteSpace(runtimeInfo))
            return null;

        try
        {
            return EverTaskJson.Deserialize<OccurrenceRuntimeInfo>(runtimeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
