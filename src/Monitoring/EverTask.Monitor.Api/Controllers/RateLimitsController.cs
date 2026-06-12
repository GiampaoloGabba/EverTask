using EverTask.Monitor.Api.DTOs.RateLimits;
using EverTask.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller exposing the keyed rate limiter state (single-node, in-memory view).
/// </summary>
[ApiController]
[Route("api/rate-limits")]
public class RateLimitsController : ControllerBase
{
    private readonly IRateLimiterIntrospection? _rateLimiter;

    /// <summary>
    /// Initializes a new instance of the <see cref="RateLimitsController"/> class.
    /// </summary>
    /// <param name="rateLimiter">
    /// Optional rate-limiter introspection (absent in standalone API mode).
    /// </param>
    public RateLimitsController(IRateLimiterIntrospection? rateLimiter = null)
    {
        _rateLimiter = rateLimiter;
    }

    /// <summary>
    /// Get the current keyed rate limiter snapshot: parked (throttled) task counts, tracked
    /// keys, fail-open counter and per-(queue, key) buckets with their next reserved slots.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rate limiter snapshot. Single-node: reflects this process only.</returns>
    /// <response code="200">Returns the rate limiter snapshot.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet]
    [ProducesResponseType(typeof(RateLimitsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<RateLimitsDto> GetRateLimits(CancellationToken ct = default)
    {
        if (_rateLimiter == null)
        {
            return Ok(new RateLimitsDto(false, 0, 0, 0, 0, []));
        }

        var keys = _rateLimiter.GetParkedSnapshot()
                               .Select(s => new RateLimitKeyDto(s.QueueName, s.Key, s.ParkedCount, s.NextSlotUtc))
                               .OrderByDescending(k => k.ParkedCount)
                               .ToList();

        return Ok(new RateLimitsDto(
            true,
            _rateLimiter.ParkedTaskCount,
            _rateLimiter.MaxParkedTasks,
            _rateLimiter.TrackedKeyCount,
            _rateLimiter.FailOpenCount,
            keys));
    }
}
