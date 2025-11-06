namespace EverTask.Monitor.Api.DTOs.Queues;

/// <summary>
/// Detailed metrics for a queue.
/// </summary>
/// <param name="QueueName">The queue name (null represents the default queue).</param>
/// <param name="TotalTasks">Total number of tasks in the queue.</param>
/// <param name="PendingTasks">Number of pending tasks.</param>
/// <param name="InProgressTasks">Number of tasks currently executing.</param>
/// <param name="CompletedTasks">Number of successfully completed tasks.</param>
/// <param name="FailedTasks">Number of failed tasks.</param>
/// <param name="AvgExecutionTimeMs">Average execution time in milliseconds.</param>
/// <param name="SuccessRate">Success rate as a percentage (0-100).</param>
public record QueueMetricsDto(
    string? QueueName,
    int TotalTasks,
    int PendingTasks,
    int InProgressTasks,
    int CompletedTasks,
    int FailedTasks,
    double AvgExecutionTimeMs,
    decimal SuccessRate
);
