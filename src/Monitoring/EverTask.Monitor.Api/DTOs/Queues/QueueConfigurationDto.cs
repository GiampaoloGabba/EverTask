namespace EverTask.Monitor.Api.DTOs.Queues;

/// <summary>
/// Complete queue information including configuration and runtime metrics.
/// </summary>
/// <param name="QueueName">The queue name.</param>
/// <param name="MaxDegreeOfParallelism">Maximum number of tasks that can execute concurrently.</param>
/// <param name="ChannelCapacity">Maximum queue capacity.</param>
/// <param name="QueueFullBehavior">Behavior when queue is full (e.g., Wait, DropOldest).</param>
/// <param name="DefaultTimeout">Default timeout for task execution.</param>
/// <param name="TotalTasks">Total number of tasks in the queue.</param>
/// <param name="PendingTasks">Number of pending tasks.</param>
/// <param name="InProgressTasks">Number of tasks currently executing.</param>
/// <param name="CompletedTasks">Number of successfully completed tasks.</param>
/// <param name="FailedTasks">Number of failed tasks.</param>
/// <param name="AvgExecutionTimeMs">Average execution time in milliseconds.</param>
/// <param name="SuccessRate">Success rate as a percentage (0-100).</param>
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
