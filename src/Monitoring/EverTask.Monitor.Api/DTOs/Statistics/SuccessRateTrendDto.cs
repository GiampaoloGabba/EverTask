namespace EverTask.Monitor.Api.DTOs.Statistics;

/// <summary>
/// Success rate trend over time.
/// </summary>
public record SuccessRateTrendDto(
    List<DateTimeOffset> Timestamps,
    List<decimal> SuccessRates            // Percentages (0-100)
);
