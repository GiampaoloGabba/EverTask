namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Task count aggregated by time period.
/// </summary>
/// <param name="Timestamp">The time bucket for this aggregation.</param>
/// <param name="Completed">Number of completed tasks in this period.</param>
/// <param name="Failed">Number of failed tasks in this period.</param>
/// <param name="Total">Total number of tasks in this period.</param>
public record TasksOverTimeDto(
    DateTimeOffset Timestamp,
    int Completed,
    int Failed,
    int Total
);
