using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Dashboard overview statistics.
/// </summary>
public record OverviewDto(
    int TotalTasksToday,
    int TotalTasksWeek,
    decimal SuccessRate,                  // Percentage (0-100)
    int FailedCount,
    double AvgExecutionTimeMs,
    Dictionary<QueuedTaskStatus, int> StatusDistribution,
    List<TasksOverTimeDto> TasksOverTime, // Hourly breakdown last 24h
    List<QueueSummaryDto> QueueSummaries
);
