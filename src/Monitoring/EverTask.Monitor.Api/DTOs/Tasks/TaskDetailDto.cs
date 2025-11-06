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
    List<RunsAuditDto> RunsAudits
);
