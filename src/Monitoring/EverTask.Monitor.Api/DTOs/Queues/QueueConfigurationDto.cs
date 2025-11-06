namespace EverTask.Monitor.Api.DTOs.Queues;

/// <summary>
/// Complete queue information including configuration and runtime metrics.
/// </summary>
public record QueueConfigurationDto(
    string QueueName,
    int MaxDegreeOfParallelism,
    int ChannelCapacity,
    string QueueFullBehavior,
    TimeSpan? DefaultTimeout,
    int TotalTasks,
    int PendingTasks,
    int InProgressTasks,
    int CompletedTasks,
    int FailedTasks,
    double AvgExecutionTimeMs,
    decimal SuccessRate
);
