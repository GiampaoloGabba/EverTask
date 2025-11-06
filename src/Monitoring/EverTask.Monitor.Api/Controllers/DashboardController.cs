using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller for dashboard overview operations.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly IDashboardService _dashboardService;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardController"/> class.
    /// </summary>
    public DashboardController(IDashboardService dashboardService)
    {
        _dashboardService = dashboardService;
    }

    /// <summary>
    /// Get dashboard overview statistics.
    /// </summary>
    /// <param name="range">The date range for filtering statistics (default: Today).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dashboard overview statistics including task counts, success rates, and queue summaries.</returns>
    /// <response code="200">Returns the dashboard overview.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(OverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<OverviewDto>> GetOverview(
        [FromQuery] DateRange range = DateRange.Today,
        CancellationToken ct = default)
    {
        var result = await _dashboardService.GetOverviewAsync(range, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get recent task activity.
    /// </summary>
    /// <param name="limit">Maximum number of recent activities to return (default: 50).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of recent task activities ordered by timestamp.</returns>
    /// <response code="200">Returns the recent activity list.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("recent-activity")]
    [ProducesResponseType(typeof(List<RecentActivityDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<RecentActivityDto>>> GetRecentActivity(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _dashboardService.GetRecentActivityAsync(limit, ct);
        return Ok(result);
    }
}
