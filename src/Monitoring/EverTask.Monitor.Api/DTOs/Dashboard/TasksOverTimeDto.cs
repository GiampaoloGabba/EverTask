namespace EverTask.Monitor.Api.DTOs.Dashboard;

/// <summary>
/// Task count aggregated by time period.
/// </summary>
public record TasksOverTimeDto(
    DateTimeOffset Timestamp,
    int Completed,
    int Failed,
    int Total
);
