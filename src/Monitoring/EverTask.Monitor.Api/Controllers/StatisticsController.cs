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
[Route("statistics")]
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
    [HttpGet("success-rate-trend")]
    [ProducesResponseType(typeof(SuccessRateTrendDto), StatusCodes.Status200OK)]
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
    [HttpGet("task-types")]
    [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
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
    [HttpGet("execution-times")]
    [ProducesResponseType(typeof(List<ExecutionTimeDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<ExecutionTimeDto>>> GetExecutionTimes(
        [FromQuery] DateRange range = DateRange.Today,
        CancellationToken ct = default)
    {
        var result = await _statisticsService.GetExecutionTimesAsync(range, ct);
        return Ok(result);
    }
}
