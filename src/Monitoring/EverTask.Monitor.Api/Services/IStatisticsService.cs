using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Queues;
using EverTask.Monitor.Api.DTOs.Statistics;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for advanced statistics and analytics.
/// </summary>
public interface IStatisticsService
{
    /// <summary>
    /// Get success rate trend over time.
    /// </summary>
    Task<SuccessRateTrendDto> GetSuccessRateTrendAsync(TimePeriod period, CancellationToken ct = default);

    /// <summary>
    /// Get detailed metrics for all queues.
    /// </summary>
    Task<List<QueueMetricsDto>> GetQueueMetricsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get task type distribution (count by task type).
    /// </summary>
    Task<Dictionary<string, int>> GetTaskTypeDistributionAsync(DateRange range, CancellationToken ct = default);

    /// <summary>
    /// Get execution time trend over time.
    /// </summary>
    Task<List<ExecutionTimeDto>> GetExecutionTimesAsync(DateRange range, CancellationToken ct = default);
}
