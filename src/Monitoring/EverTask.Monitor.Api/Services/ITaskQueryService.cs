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
    /// Get status audit history for a task, newest transition first, read from the storage's audit trail.
    /// </summary>
    Task<List<StatusAuditDto>> GetStatusAuditAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get execution runs audit history for a task, newest run first, read from the storage's audit trail.
    /// </summary>
    Task<List<RunsAuditDto>> GetRunsAuditAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Get paginated execution logs for a task with optional level filtering.
    /// </summary>
    Task<ExecutionLogsResponse> GetExecutionLogsAsync(Guid taskId, int skip = 0, int take = 100, string? levelFilter = null, CancellationToken ct = default);

    /// <summary>
    /// Get task counts by category for dashboard badges.
    /// </summary>
    Task<TaskCountsDto> GetTaskCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Get the materialized occurrences of a durable schedule, newest slot first.
    /// </summary>
    /// <param name="scheduleId">The schedule row whose occurrences are wanted.</param>
    /// <param name="nonTerminalOnly">Keep only the occurrences that can still lead to an execution.</param>
    /// <param name="skip">Number of occurrences to skip.</param>
    /// <param name="take">Number of occurrences to return.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OccurrencesResponse> GetOccurrencesAsync(Guid scheduleId, bool nonTerminalOnly = false, int skip = 0,
                                                  int take = 100, CancellationToken ct = default);
}
