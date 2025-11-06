using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Monitor.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace EverTask.Monitor.Api.Controllers;

/// <summary>
/// Controller for task query operations.
/// </summary>
[ApiController]
[Route("api/tasks")]
public class TasksController : ControllerBase
{
    private readonly ITaskQueryService _taskQueryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="TasksController"/> class.
    /// </summary>
    public TasksController(ITaskQueryService taskQueryService)
    {
        _taskQueryService = taskQueryService;
    }

    /// <summary>
    /// Get paginated list of tasks with filters.
    /// </summary>
    /// <param name="filter">Filter criteria for tasks (status, type, queue, dates, search term).</param>
    /// <param name="pagination">Pagination parameters including page number, page size, and sorting options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated list of tasks matching the filter criteria.</returns>
    /// <response code="200">Returns the paginated task list.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet]
    [ProducesResponseType(typeof(TasksPagedResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TasksPagedResponse>> GetTasks(
        [FromQuery] TaskFilter filter,
        [FromQuery] PaginationParams pagination,
        CancellationToken ct)
    {
        var result = await _taskQueryService.GetTasksAsync(filter, pagination, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get complete task details.
    /// </summary>
    /// <param name="id">The unique task identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Complete task details including request data, configuration, and audit history.</returns>
    /// <response code="200">Returns the task details.</response>
    /// <response code="404">Task not found.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TaskDetailDto>> GetTaskDetail(Guid id, CancellationToken ct)
    {
        var result = await _taskQueryService.GetTaskDetailAsync(id, ct);
        if (result == null)
            return NotFound();
        return Ok(result);
    }

    /// <summary>
    /// Get status audit history for a task.
    /// </summary>
    /// <param name="id">The unique task identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chronological list of status changes for the task.</returns>
    /// <response code="200">Returns the status audit history.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("{id:guid}/status-audit")]
    [ProducesResponseType(typeof(List<StatusAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<StatusAuditDto>>> GetStatusAudit(Guid id, CancellationToken ct)
    {
        var result = await _taskQueryService.GetStatusAuditAsync(id, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get execution runs audit history for a task.
    /// </summary>
    /// <param name="id">The unique task identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Chronological list of execution attempts with timing and outcome information.</returns>
    /// <response code="200">Returns the execution runs audit history.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("{id:guid}/runs-audit")]
    [ProducesResponseType(typeof(List<RunsAuditDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<List<RunsAuditDto>>> GetRunsAudit(Guid id, CancellationToken ct)
    {
        var result = await _taskQueryService.GetRunsAuditAsync(id, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get paginated execution logs for a task with optional level filtering.
    /// </summary>
    /// <param name="id">The unique task identifier.</param>
    /// <param name="skip">Number of log entries to skip (default: 0).</param>
    /// <param name="take">Number of log entries to return (default: 100).</param>
    /// <param name="level">Optional log level filter (Trace, Debug, Information, Warning, Error, Critical).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Paginated list of execution log entries captured during task execution.</returns>
    /// <response code="200">Returns the execution logs.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("{id:guid}/execution-logs")]
    [ProducesResponseType(typeof(ExecutionLogsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<ExecutionLogsResponse>> GetExecutionLogs(
        Guid id,
        [FromQuery] int skip = 0,
        [FromQuery] int take = 100,
        [FromQuery] string? level = null,
        CancellationToken ct = default)
    {
        var result = await _taskQueryService.GetExecutionLogsAsync(id, skip, take, level, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get task counts by category for dashboard badges.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Task counts grouped by category (all, standard, recurring, failed).</returns>
    /// <response code="200">Returns the task counts.</response>
    /// <response code="401">Unauthorized - JWT token required.</response>
    [HttpGet("counts")]
    [ProducesResponseType(typeof(TaskCountsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<TaskCountsDto>> GetTaskCounts(CancellationToken ct)
    {
        var result = await _taskQueryService.GetTaskCountsAsync(ct);
        return Ok(result);
    }
}
