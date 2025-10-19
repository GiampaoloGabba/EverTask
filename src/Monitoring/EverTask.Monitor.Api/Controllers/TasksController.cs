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
    [HttpGet]
    [ProducesResponseType(typeof(TasksPagedResponse), StatusCodes.Status200OK)]
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
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TaskDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
    [HttpGet("{id:guid}/status-audit")]
    [ProducesResponseType(typeof(List<StatusAuditDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<StatusAuditDto>>> GetStatusAudit(Guid id, CancellationToken ct)
    {
        var result = await _taskQueryService.GetStatusAuditAsync(id, ct);
        return Ok(result);
    }

    /// <summary>
    /// Get execution runs audit history for a task.
    /// </summary>
    [HttpGet("{id:guid}/runs-audit")]
    [ProducesResponseType(typeof(List<RunsAuditDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<RunsAuditDto>>> GetRunsAudit(Guid id, CancellationToken ct)
    {
        var result = await _taskQueryService.GetRunsAuditAsync(id, ct);
        return Ok(result);
    }
}
