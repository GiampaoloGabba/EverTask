using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Dashboard overview statistics.
/// </summary>
/// <param name="TotalTasksToday">Total number of tasks executed today.</param>
/// <param name="TotalTasksWeek">Total number of tasks executed in the last 7 days.</param>
/// <param name="SuccessRate">Overall success rate as a percentage (0-100).</param>
/// <param name="FailedCount">Total number of failed tasks.</param>
/// <param name="AvgExecutionTimeMs">Average execution time in milliseconds.</param>
/// <param name="StatusDistribution">Task count grouped by status.</param>
/// <param name="TasksOverTime">Hourly task breakdown for the last 24 hours.</param>
/// <param name="QueueSummaries">Summary statistics for each queue.</param>
public record OverviewDto(
    int TotalTasksToday,
    int TotalTasksWeek,
    decimal SuccessRate,
    int FailedCount,
    double AvgExecutionTimeMs,
    Dictionary<QueuedTaskStatus, int> StatusDistribution,
    List<TasksOverTimeDto> TasksOverTime,
    List<QueueSummaryDto> QueueSummaries
);
