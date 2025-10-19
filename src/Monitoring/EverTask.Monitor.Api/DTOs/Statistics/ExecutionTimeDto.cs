namespace EverTask.Monitor.Api.DTOs.Statistics;

/// <summary>
/// Average execution time aggregated by time period.
/// </summary>
public record ExecutionTimeDto(
    DateTimeOffset Timestamp,
    double AvgExecutionTimeMs
);
