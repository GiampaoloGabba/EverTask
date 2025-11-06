using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Represents a task execution attempt.
/// </summary>
/// <param name="Id">The unique identifier of the audit entry.</param>
/// <param name="QueuedTaskId">The task identifier.</param>
/// <param name="ExecutedAt">When the execution occurred.</param>
/// <param name="ExecutionTimeMs">Execution duration in milliseconds.</param>
/// <param name="Status">The resulting status after execution.</param>
/// <param name="Exception">The exception message if execution failed.</param>
public record RunsAuditDto(
    long Id,
    Guid QueuedTaskId,
    DateTimeOffset ExecutedAt,
    double ExecutionTimeMs,
    QueuedTaskStatus Status,
    string? Exception
);
