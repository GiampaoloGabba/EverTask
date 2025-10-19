using EverTask.Monitor.Api.DTOs.Tasks;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for querying tasks from storage.
/// </summary>
public interface ITaskQueryService
{
    /// <summary>
    /// Get paginated list of tasks with filters.
    /// </summary>
    Task<TasksPagedResponse> GetTasksAsync(TaskFilter filter, PaginationParams pagination, CancellationToken ct = default);

    /// <summary>
    /// Get complete task details including audits.
    /// </summary>
    Task<TaskDetailDto?> GetTaskDetailAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get status audit history for a task.
    /// </summary>
    Task<List<StatusAuditDto>> GetStatusAuditAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get execution runs audit history for a task.
    /// </summary>
    Task<List<RunsAuditDto>> GetRunsAuditAsync(Guid id, CancellationToken ct = default);
}
