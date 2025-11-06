using EverTask.Monitor.Api.DTOs.Queues;
using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller for queue metrics operations.
/// </summary>
[ApiController]
[Route("api/queues")]
public class QueuesController : ControllerBase
{
    private readonly IStatisticsService _statisticsService;
    private readonly ITaskQueryService _taskQueryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueuesController"/> class.
    /// </summary>
    public QueuesController(IStatisticsService statisticsService, ITaskQueryService taskQueryService)
    {
        _statisticsService = statisticsService;
        _taskQueryService = taskQueryService;
    }

    /// <summary>
    /// Get metrics for all queues.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of metrics for all queues including task counts, execution times, and success rates.</returns>
    /// <response code="200">Returns the queue metrics.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet]
    [ProducesResponseType(typeof(List<QueueMetricsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<QueueMetricsDto>>> GetQueues(CancellationToken ct)
    {
        var result = await _statisticsService.GetQueueMetricsAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Get complete configuration and metrics for all configured queues.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of queue configurations including parallelism settings, capacity, timeouts, and runtime metrics.</returns>
    /// <response code="200">Returns the queue configurations.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("configurations")]
    [ProducesResponseType(typeof(List<QueueConfigurationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<QueueConfigurationDto>>> GetQueueConfigurations(CancellationToken ct)
    {
        var result = await _statisticsService.GetQueueConfigurationsAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Get tasks in a specific queue.
    /// </summary>
    /// <param name="name">The queue name.</param>
    /// <param name="pagination">Pagination parameters including page number, page size, and sorting options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated list of tasks in the specified queue.</returns>
    /// <response code="200">Returns the paginated task list.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("{name}/tasks")]
    [ProducesResponseType(typeof(TasksPagedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TasksPagedResponse>> GetQueueTasks(
        string name,
        [FromQuery] PaginationParams pagination,
        CancellationToken ct)
    {
        var filter = new TaskFilter { QueueName = name };
        var result = await _taskQueryService.GetTasksAsync(filter, pagination, ct);
        return Ok(result);
    }
}
