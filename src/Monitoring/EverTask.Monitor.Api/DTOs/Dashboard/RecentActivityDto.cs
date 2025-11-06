using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Recent task activity for activity feed.
/// </summary>
/// <param name="TaskId">The unique identifier of the task.</param>
/// <param name="Type">The task type name.</param>
/// <param name="Status">The current task status.</param>
/// <param name="Timestamp">When the activity occurred.</param>
/// <param name="Message">A human-readable description of the activity.</param>
public record RecentActivityDto(
    Guid TaskId,
    string Type,
    QueuedTaskStatus Status,
    DateTimeOffset Timestamp,
    string Message
);
