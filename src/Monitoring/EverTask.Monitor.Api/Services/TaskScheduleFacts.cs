using System.Text.Json;
using EverTask.Abstractions;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Scheduler.Recurring;
using EverTask.Serialization;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// What a row says about the schedule it belongs to, read once and shared by every DTO that reports it.
/// </summary>
/// <remarks>
/// Three of the facts a durable schedule is watched by — the occurrence mode, the misfire policy and the time
/// zone — live inside the serialized definition, and two more — the occurrence metadata and the catch-up halt
/// — inside the runtime column. Reading them is a JSON parse per row, so it happens once per row and only for
/// a row that can carry them: an ordinary one-shot answers <see cref="None"/> without touching either column.
/// </remarks>
internal sealed record TaskScheduleFacts
{
    /// <summary>The answer for a row that belongs to no schedule.</summary>
    public static readonly TaskScheduleFacts None = new();

    /// <summary>How the schedule produces its occurrences, or null for a row that is not part of one.</summary>
    public OccurrenceMode? OccurrenceMode { get; private init; }

    /// <summary>The schedule's misfire policy. Only a schedule row carries the definition that states it.</summary>
    public MisfirePolicy? MisfirePolicy { get; private init; }

    /// <summary>The IANA zone the schedule's calendar is read on, or null for plain UTC.</summary>
    public string? TimeZoneId { get; private init; }

    /// <summary>The nominal slot an occurrence stands for. Null on anything that is not an occurrence.</summary>
    public DateTimeOffset? NominalSlotUtc { get; private init; }

    /// <summary>The occurrence metadata of a child row, or null on a schedule row and on a one-shot.</summary>
    public OccurrenceInfoDto? Occurrence { get; private init; }

    /// <summary>The standing catch-up halt of a durable schedule row, or null while it is running.</summary>
    public ScheduleHaltDto? Halt { get; private init; }

    /// <summary>
    /// What kind of missed work this row stands for, when it stands for any.
    /// </summary>
    public MisfireKind? MisfireKind => Occurrence?.MisfireKind;

    /// <summary>Reads the schedule facts of a row, without ever throwing on what a column holds.</summary>
    public static TaskScheduleFacts Read(QueuedTask row) =>
        row.ParentTaskId != null ? ReadOccurrence(row) :
        row.IsRecurring          ? ReadSchedule(row) :
                                   None;

    private static TaskScheduleFacts ReadOccurrence(QueuedTask row)
    {
        var info = OccurrenceRuntimeInfo.TryParse(row.RuntimeInfo);

        // An occurrence exists only because its schedule is durable, so the mode is a fact of the row's own
        // shape and needs no definition to be read from — an occurrence carries none.
        return new TaskScheduleFacts
        {
            OccurrenceMode = Abstractions.OccurrenceMode.Durable,
            TimeZoneId     = info?.TimeZoneId,
            NominalSlotUtc = info?.SlotUtc ?? row.ScheduledExecutionUtc,
            Occurrence = new OccurrenceInfoDto(
                info?.SlotUtc ?? row.ScheduledExecutionUtc,
                info?.RunNumber,
                info?.TimeZoneId,
                info?.MisfireKind,
                info?.MissedFromUtc,
                info?.MissedThroughUtc,
                info?.MissedCount,
                info?.MissedCountIsExact)
        };
    }

    /// <summary>
    /// Whether the row is a schedule that is halted RIGHT NOW: it carries the marker and it is still a series
    /// that could run.
    /// </summary>
    /// <remarks>
    /// The marker is runtime state of a live schedule, and nothing clears it when the series ends — a cancel
    /// and the recovery finalization both leave it where it is, because neither has anything left to halt.
    /// Reading the marker alone therefore kept a cancelled series alarming for ever, on a counter the docs
    /// call the one to alert on, telling an operator to resume a schedule they had deliberately ended.
    /// </remarks>
    public static bool HasStandingHalt(QueuedTask row) =>
        row.ParentTaskId == null
        && row.RuntimeInfo != null
        && QueuedTask.IsNonTerminalStatus(row.Status)
        && ScheduleRuntimeInfo.TryParse(row.RuntimeInfo)?.Halted != null;

    private static TaskScheduleFacts ReadSchedule(QueuedTask row)
    {
        var definition = TryReadDefinition(row.RecurringTask);
        var halt = QueuedTask.IsNonTerminalStatus(row.Status)
                       ? ScheduleRuntimeInfo.TryParse(row.RuntimeInfo)?.Halted
                       : null;

        // A definition this build cannot read answers NEITHER question: reporting the defaults there would
        // state that a schedule is inline and skips, which is exactly what an unreadable row cannot promise.
        return new TaskScheduleFacts
        {
            OccurrenceMode = definition?.OccurrenceMode,
            MisfirePolicy  = definition == null ? null : definition.Misfire?.Policy ?? Abstractions.MisfirePolicy.Skip,
            TimeZoneId     = definition?.TimeZoneId,
            Halt = halt == null
                       ? null
                       : new ScheduleHaltDto(halt.AtUtc, halt.Reason, halt.DetectedAtLeast, halt.IsExact,
                           halt.CursorUtc, halt.ScheduleVersion)
        };
    }

    /// <summary>
    /// The persisted definition, or null when this build cannot read it. A dashboard row that cannot say which
    /// misfire policy a schedule carries is a far better outcome than a list endpoint that fails on one row.
    /// </summary>
    private static RecurringTask? TryReadDefinition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return EverTaskJson.Deserialize<RecurringTask>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
