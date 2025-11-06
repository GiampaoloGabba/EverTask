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
    [HttpGet]
    [ProducesResponseType(typeof(List<QueueMetricsDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<QueueMetricsDto>>> GetQueues(CancellationToken ct)
    {
        var result = await _statisticsService.GetQueueMetricsAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Get complete configuration and metrics for all configured queues.
    /// </summary>
    [HttpGet("configurations")]
    [ProducesResponseType(typeof(List<QueueConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<QueueConfigurationDto>>> GetQueueConfigurations(CancellationToken ct)
    {
        var result = await _statisticsService.GetQueueConfigurationsAsync(ct);
        return Ok(result);
    }

    /// <summary>
    /// Get tasks in a specific queue.
    /// </summary>
    [HttpGet("{name}/tasks")]
    [ProducesResponseType(typeof(TasksPagedResponse), StatusCodes.Status200OK)]
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
