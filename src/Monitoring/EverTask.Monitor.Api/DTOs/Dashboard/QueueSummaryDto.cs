namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Summary statistics for a queue.
/// </summary>
/// <param name="QueueName">The queue name (null represents the default queue).</param>
/// <param name="PendingCount">Number of pending tasks in the queue.</param>
/// <param name="InProgressCount">Number of tasks currently executing.</param>
/// <param name="CompletedCount">Number of successfully completed tasks.</param>
/// <param name="FailedCount">Number of failed tasks.</param>
public record QueueSummaryDto(
    string? QueueName,
    int PendingCount,
    int InProgressCount,
    int CompletedCount,
    int FailedCount
);
