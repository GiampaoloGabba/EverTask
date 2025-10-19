namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Summary statistics for a queue.
/// </summary>
public record QueueSummaryDto(
    string? QueueName,                    // null = default queue
    int PendingCount,
    int InProgressCount,
    int CompletedCount,
    int FailedCount
);
