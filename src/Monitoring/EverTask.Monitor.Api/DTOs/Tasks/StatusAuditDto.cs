using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Represents a status change in a task's lifecycle.
/// </summary>
/// <param name="Id">The unique identifier of the audit entry.</param>
/// <param name="QueuedTaskId">The task identifier.</param>
/// <param name="UpdatedAtUtc">When the status change occurred.</param>
/// <param name="NewStatus">The new task status.</param>
/// <param name="Exception">The exception message if the status change was due to an error.</param>
public record StatusAuditDto(
    long Id,
    Guid QueuedTaskId,
    DateTimeOffset UpdatedAtUtc,
    QueuedTaskStatus NewStatus,
    string? Exception
);
