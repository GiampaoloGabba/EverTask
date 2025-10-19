using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller for dashboard overview operations.
/// </summary>
[ApiController]
[Route("dashboard")]
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
    [HttpGet("overview")]
    [ProducesResponseType(typeof(OverviewDto), StatusCodes.Status200OK)]
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
    [HttpGet("recent-activity")]
    [ProducesResponseType(typeof(List<RecentActivityDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RecentActivityDto>>> GetRecentActivity(
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var result = await _dashboardService.GetRecentActivityAsync(limit, ct);
        return Ok(result);
    }
}
