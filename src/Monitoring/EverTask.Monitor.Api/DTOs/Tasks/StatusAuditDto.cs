using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Represents a status change in a task's lifecycle.
/// </summary>
public record StatusAuditDto(
    long Id,
    Guid QueuedTaskId,
    DateTimeOffset UpdatedAtUtc,
    QueuedTaskStatus NewStatus,
    string? Exception
);
