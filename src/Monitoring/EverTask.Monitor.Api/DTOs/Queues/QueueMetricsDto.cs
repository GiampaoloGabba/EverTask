namespace EverTask.Monitor.Api.DTOs.Queues;

/// <summary>
/// Detailed metrics for a queue.
/// </summary>
public record QueueMetricsDto(
    string? QueueName,
    int TotalTasks,
    int PendingTasks,
    int InProgressTasks,
    int CompletedTasks,
    int FailedTasks,
    double AvgExecutionTimeMs,
    decimal SuccessRate                   // Percentage (0-100)
);
