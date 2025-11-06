using EverTask.Monitor.Api.DTOs.Dashboard;
using EverTask.Monitor.Api.DTOs.Statistics;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller for statistics and analytics operations.
/// </summary>
[ApiController]
[Route("api/statistics")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;

    /// <summary>
    /// Initializes a new instance of the <see cref="StatisticsController"/> class.
    /// </summary>
    public StatisticsController(IStatisticsService statisticsService)
    {
        _statisticsService = statisticsService;
    }

    /// <summary>
    /// Get success rate trend over time.
    /// </summary>
    /// <param name="period">The time period for the trend analysis (default: Last7Days).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success rate trend data with timestamps and corresponding success rate percentages.</returns>
    /// <response code="200">Returns the success rate trend.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("success-rate-trend")]
    [ProducesResponseType(typeof(SuccessRateTrendDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<SuccessRateTrendDto>> GetSuccessRateTrend(
        [FromQuery] TimePeriod period = TimePeriod.Last7Days,
        CancellationToken ct = default)
    {
        var result = await _statisticsService.GetSuccessRateTrendAsync(period, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get task type distribution.
    /// </summary>
    /// <param name="range">The date range for the distribution analysis (default: Week).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Dictionary mapping task type names to their occurrence counts.</returns>
    /// <response code="200">Returns the task type distribution.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("task-types")]
    [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<Dictionary<string, int>>> GetTaskTypeDistribution(
        [FromQuery] DateRange range = DateRange.Week,
        CancellationToken ct = default)
    {
        var result = await _statisticsService.GetTaskTypeDistributionAsync(range, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get execution time trend.
    /// </summary>
    /// <param name="range">The date range for the execution time analysis (default: Today).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of average execution times aggregated by time period.</returns>
    /// <response code="200">Returns the execution time trend.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("execution-times")]
    [ProducesResponseType(typeof(List<ExecutionTimeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<ExecutionTimeDto>>> GetExecutionTimes(
        [FromQuery] DateRange range = DateRange.Today,
        CancellationToken ct = default)
    {
        var result = await _statisticsService.GetExecutionTimesAsync(range, ct);
        return Ok(result);
    }
}
