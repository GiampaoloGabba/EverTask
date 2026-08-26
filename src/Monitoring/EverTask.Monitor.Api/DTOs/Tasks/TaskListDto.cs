using EverTask.Abstractions;
using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Lightweight task information for list views.
/// </summary>
/// <param name="Id">The unique identifier of the task.</param>
/// <param name="Type">The task type (short name, not assembly qualified).</param>
/// <param name="Status">The current task status.</param>
/// <param name="QueueName">The queue name (null for default queue).</param>
/// <param name="TaskKey">Optional unique identifier for task deduplication.</param>
/// <param name="CreatedAtUtc">When the task was created.</param>
/// <param name="LastExecutionUtc">When the task was last executed.</param>
/// <param name="ScheduledExecutionUtc">When the task is scheduled to execute.</param>
/// <param name="IsRecurring">Indicates whether this is a recurring task.</param>
/// <param name="RecurringInfo">Human-readable schedule description (e.g., "Every 5 minutes").</param>
/// <param name="CurrentRunCount">Number of times the task has been executed.</param>
/// <param name="MaxRuns">Maximum number of times the task should run.</param>
/// <param name="ExecutionTimeMs">Last execution time in milliseconds.</param>
/// <param name="ThrottledUntil">
/// The reserved rate-limit slot (UTC) when the task is currently parked by the rate limiter,
/// null otherwise. In-memory single-node overlay: only this process' parked tasks are visible.
/// </param>
public record TaskListDto(
    Guid Id,
    string Type,
    QueuedTaskStatus Status,
    string? QueueName,
    string? TaskKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? ScheduledExecutionUtc,
    bool IsRecurring,
    string? RecurringInfo,
    int? CurrentRunCount,
    int? MaxRuns,
    double ExecutionTimeMs,
    DateTimeOffset? ThrottledUntil = null
)
{
    // Schedule and occurrence context lives in INIT properties, never as appended positional parameters:
    // appending would change the primary constructor and Deconstruct signatures of a public record (X4).

    /// <summary>The durable schedule this task is an occurrence of, or null for anything else.</summary>
    public Guid? ParentTaskId { get; init; }

    /// <summary>
    /// How the schedule behind this row produces its occurrences: <c>Inline</c> on a legacy recurring row,
    /// <c>Durable</c> on a schedule with materialized occurrences and on every occurrence of one. Null for a
    /// task that belongs to no schedule.
    /// </summary>
    public OccurrenceMode? OccurrenceMode { get; init; }

    /// <summary>
    /// What the schedule does with a slot that came due while nothing was there to run it. Only a schedule row
    /// carries the definition that states it.
    /// </summary>
    public MisfirePolicy? MisfirePolicy { get; init; }

    /// <summary>The IANA zone the schedule's calendar is read on, or null for plain UTC.</summary>
    public string? TimeZoneId { get; init; }

    /// <summary>
    /// Version of the schedule definition this row belongs to — a schedule row or an occurrence of one, where
    /// a row that has never been rescheduled reads 0. Null for a task that belongs to no schedule, so the
    /// schedule fields are absent together on an ordinary one-shot instead of one of them reading 0.
    /// </summary>
    public int? ScheduleVersion { get; init; }

    /// <summary>The nominal slot an occurrence stands for, or null on anything that is not one.</summary>
    public DateTimeOffset? NominalSlotUtc { get; init; }

    /// <summary>
    /// When the last (or current) run of this task began, or null while nothing has run it.
    /// </summary>
    /// <remarks>
    /// The term lateness is measured against: <c>lastExecutionUtc</c> is written on terminal transitions and
    /// therefore reports when a run ENDED, so a punctual delivery with a slow handler would read as late by
    /// its whole execution time.
    /// </remarks>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <summary>
    /// What kind of missed work an occurrence stands for — a replayed slot, or a run of missed slots collapsed
    /// into one. Null for an occurrence that is simply its own slot.
    /// </summary>
    public MisfireKind? MisfireKind { get; init; }
}
