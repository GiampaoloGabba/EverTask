using EverTask.Abstractions;
using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Complete task information including audits and serialized request.
/// </summary>
/// <param name="Id">The unique identifier of the task.</param>
/// <param name="Type">The full task type name.</param>
/// <param name="Handler">The full handler type name.</param>
/// <param name="Request">The task request serialized as JSON.</param>
/// <param name="Status">The current task status.</param>
/// <param name="QueueName">The queue name (null for default queue).</param>
/// <param name="TaskKey">Optional unique identifier for task deduplication.</param>
/// <param name="CreatedAtUtc">When the task was created.</param>
/// <param name="LastExecutionUtc">When the task was last executed.</param>
/// <param name="ScheduledExecutionUtc">When the task is scheduled to execute.</param>
/// <param name="Exception">The last exception message, if any.</param>
/// <param name="IsRecurring">Indicates whether this is a recurring task.</param>
/// <param name="RecurringTask">The recurring configuration serialized as JSON.</param>
/// <param name="RecurringInfo">Human-readable schedule description.</param>
/// <param name="CurrentRunCount">Number of times the task has been executed.</param>
/// <param name="MaxRuns">Maximum number of times the task should run.</param>
/// <param name="RunUntil">The deadline for recurring task execution.</param>
/// <param name="NextRunUtc">When the next execution is scheduled.</param>
/// <param name="AuditLevel">The audit retention policy level.</param>
/// <param name="ExecutionTimeMs">Last execution time in milliseconds.</param>
/// <param name="StatusAudits">History of status changes.</param>
/// <param name="RunsAudits">History of execution attempts.</param>
/// <param name="ThrottledUntil">
/// The reserved rate-limit slot (UTC) when the task is currently parked by the rate limiter,
/// null otherwise. In-memory single-node overlay: only this process' parked tasks are visible.
/// </param>
public record TaskDetailDto(
    Guid Id,
    string Type,
    string Handler,
    string Request,
    QueuedTaskStatus Status,
    string? QueueName,
    string? TaskKey,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    DateTimeOffset? ScheduledExecutionUtc,
    string? Exception,
    bool IsRecurring,
    string? RecurringTask,
    string? RecurringInfo,
    int? CurrentRunCount,
    int? MaxRuns,
    DateTimeOffset? RunUntil,
    DateTimeOffset? NextRunUtc,
    int? AuditLevel,
    double ExecutionTimeMs,
    List<StatusAuditDto> StatusAudits,
    List<RunsAuditDto> RunsAudits,
    DateTimeOffset? ThrottledUntil = null
)
{
    // Schedule and occurrence context lives in INIT properties, never as appended positional parameters:
    // appending would change the primary constructor and Deconstruct signatures of a public record (X4).

    /// <summary>
    /// How many status transitions the task's trail holds in total. <c>StatusAudits</c> carries only the
    /// first page of it — a schedule that has run for a year holds one transition per state per run — so
    /// this is what tells a consumer there is more, and <c>GET /tasks/{id}/status-audit</c> is where the
    /// rest is asked for.
    /// </summary>
    public int StatusAuditsTotalCount { get; init; }

    /// <summary>
    /// How many runs the task's trail holds in total. The <c>RunsAudits</c> half of
    /// <see cref="StatusAuditsTotalCount"/>.
    /// </summary>
    public int RunsAuditsTotalCount { get; init; }

    /// <inheritdoc cref="TaskListDto.ParentTaskId"/>
    public Guid? ParentTaskId { get; init; }

    /// <inheritdoc cref="TaskListDto.OccurrenceMode"/>
    public OccurrenceMode? OccurrenceMode { get; init; }

    /// <inheritdoc cref="TaskListDto.MisfirePolicy"/>
    public MisfirePolicy? MisfirePolicy { get; init; }

    /// <inheritdoc cref="TaskListDto.TimeZoneId"/>
    public string? TimeZoneId { get; init; }

    /// <inheritdoc cref="TaskListDto.ScheduleVersion"/>
    public int? ScheduleVersion { get; init; }

    /// <inheritdoc cref="TaskListDto.NominalSlotUtc"/>
    public DateTimeOffset? NominalSlotUtc { get; init; }

    /// <inheritdoc cref="TaskListDto.StartedAtUtc"/>
    public DateTimeOffset? StartedAtUtc { get; init; }

    /// <inheritdoc cref="TaskListDto.MisfireKind"/>
    public MisfireKind? MisfireKind { get; init; }

    /// <summary>
    /// The occurrence metadata this row carries — slot, run number, and the backlog it was created out of.
    /// Null on a schedule row and on an ordinary one-shot.
    /// </summary>
    public OccurrenceInfoDto? Occurrence { get; init; }

    /// <summary>
    /// The standing catch-up halt of a durable schedule, or null while it is running. A halt never releases
    /// itself: only <c>ResumeSchedule</c> or a <c>Reschedule</c> clears it.
    /// </summary>
    public ScheduleHaltDto? Halt { get; init; }
}
