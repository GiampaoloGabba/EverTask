namespace EverTask.Monitor.Api.DTOs.Statistics;

/// <summary>
/// Average execution time aggregated by time period.
/// </summary>
/// <param name="Timestamp">The time bucket for this aggregation.</param>
/// <param name="AvgExecutionTimeMs">Average execution time in milliseconds for this period.</param>
public record ExecutionTimeDto(
    DateTimeOffset Timestamp,
    double AvgExecutionTimeMs
);
