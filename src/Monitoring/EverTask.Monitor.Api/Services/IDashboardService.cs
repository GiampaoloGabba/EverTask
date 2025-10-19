using EverTask.Monitor.Api.DTOs.Dashboard;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for dashboard overview data.
/// </summary>
public interface IDashboardService
{
    /// <summary>
    /// Get dashboard overview statistics.
    /// </summary>
    Task<OverviewDto> GetOverviewAsync(DateRange range, CancellationToken ct = default);

    /// <summary>
    /// Get recent task activity.
    /// </summary>
    Task<List<RecentActivityDto>> GetRecentActivityAsync(int limit = 50, CancellationToken ct = default);
}
