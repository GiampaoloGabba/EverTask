namespace EverTask.Monitor.Api.DTOs.Statistics;

/// <summary>
/// Success rate trend over time.
/// </summary>
/// <param name="Timestamps">Time points for the trend data.</param>
/// <param name="SuccessRates">Success rates as percentages (0-100) corresponding to each timestamp.</param>
public record SuccessRateTrendDto(
    List<DateTimeOffset> Timestamps,
    List<decimal> SuccessRates
);
