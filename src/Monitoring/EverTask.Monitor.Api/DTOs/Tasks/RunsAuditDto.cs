using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Represents a task execution attempt.
/// </summary>
public record RunsAuditDto(
    long Id,
    Guid QueuedTaskId,
    DateTimeOffset ExecutedAt,
    QueuedTaskStatus Status,
    string? Exception
);
