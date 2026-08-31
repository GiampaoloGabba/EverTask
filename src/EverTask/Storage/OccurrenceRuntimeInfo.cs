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
    /// The time zone the schedule that produced this occurrence is read on, or null for a schedule that names
    /// none (plain UTC).
    /// </summary>
    /// <remarks>
    /// An occurrence carries NO definition of its own — it belongs to its series through its parent — so
    /// without this the zone and the local slot a handler is contractually promised (C1/T13) would be null on
    /// every durable occurrence, exactly where a calendar schedule needs them most. Copied at materialization
    /// rather than read from the parent at delivery: it is a fact of the decision that created the row, it
    /// costs no round-trip on the delivery path, and a later reschedule cannot rewrite what this occurrence
    /// already means.
    /// </remarks>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// What kind of misfire this occurrence stands for, when it stands for one — a replayed slot
    /// (<see cref="MisfireKind.CatchUp"/>) or a whole run of missed slots collapsed into it
    /// (<see cref="MisfireKind.FireOnce"/>). Null on an occurrence that is simply its own slot.
    /// </summary>
    /// <remarks>
    /// Persisted rather than derived, because it is a fact of the DECISION that created the row: by the time
    /// the occurrence runs, the backlog it belonged to no longer exists to be measured. Lateness is not stored
    /// — it is the distance between the slot and the moment the delivery actually starts, which only the
    /// delivery knows.
    /// </remarks>
    public MisfireKind? MisfireKind { get; init; }

    /// <summary>The oldest slot of the backlog this occurrence was created out of.</summary>
    public DateTimeOffset? MissedFromUtc { get; init; }

    /// <summary>The newest slot of that backlog.</summary>
    public DateTimeOffset? MissedThroughUtc { get; init; }

    /// <summary>
    /// How many grid slots that range holds, its two ends included — so this occurrence's own slot counts.
    /// </summary>
    public int? MissedCount { get; init; }

    /// <summary>
    /// Whether <see cref="MissedCount"/> is the real total or only a lower bound. Null on a row written
    /// before the distinction existed, which is read as "exact" — the historical meaning of the number.
    /// </summary>
    public bool? MissedCountIsExact { get; init; }

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
