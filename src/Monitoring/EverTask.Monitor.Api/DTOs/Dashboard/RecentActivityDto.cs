using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Recent task activity for activity feed.
/// </summary>
public record RecentActivityDto(
    Guid TaskId,
    string Type,
    QueuedTaskStatus Status,
    DateTimeOffset Timestamp,
    string Message
);
