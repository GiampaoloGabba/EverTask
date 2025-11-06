using EverTask.Monitor.Api.DTOs.Tasks;
using EverTask.Storage;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// Service for querying tasks from storage.
/// </summary>
public class TaskQueryService : ITaskQueryService
{
    private readonly ITaskStorage _storage;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueryService"/> class.
    /// </summary>
    public TaskQueryService(ITaskStorage storage)
    {
        _storage = storage;
    }

    /// <inheritdoc />
    public async Task<TasksPagedResponse> GetTasksAsync(TaskFilter filter, PaginationParams pagination, CancellationToken ct = default)
    {
        // Get all tasks from storage
        var allTasks = await _storage.GetAll(ct);
        var query = allTasks.AsQueryable();

        // Apply filters
        if (filter.Statuses != null && filter.Statuses.Count > 0)
        {
            query = query.Where(t => filter.Statuses.Contains(t.Status));
        }

        if (!string.IsNullOrWhiteSpace(filter.TaskType))
        {
            query = query.Where(t => t.Type.Contains(filter.TaskType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(filter.QueueName))
        {
            query = query.Where(t => t.QueueName == filter.QueueName);
        }

        if (filter.IsRecurring.HasValue)
        {
            query = query.Where(t => t.IsRecurring == filter.IsRecurring.Value);
        }

        if (filter.CreatedAfter.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc >= filter.CreatedAfter.Value);
        }

        if (filter.CreatedBefore.HasValue)
        {
            query = query.Where(t => t.CreatedAtUtc <= filter.CreatedBefore.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var searchLower = filter.SearchTerm.ToLower();
            query = query.Where(t =>
                t.Type.ToLower().Contains(searchLower) ||
                t.Handler.ToLower().Contains(searchLower) ||
                (t.TaskKey != null && t.TaskKey.ToLower().Contains(searchLower)));
        }

        // Count total before pagination
        var totalCount = query.Count();

        // Apply sorting
        query = ApplySorting(query, pagination.SortBy, pagination.SortDescending);

        // Apply pagination
        var skip = (pagination.Page - 1) * pagination.PageSize;
        var items = query
            .Skip(skip)
            .Take(pagination.PageSize)
            .Select(t => new TaskListDto(
                t.Id,
                GetShortTypeName(t.Type),
                t.Status,
                t.QueueName,
                t.TaskKey,
                t.CreatedAtUtc,
                t.LastExecutionUtc,
                t.ScheduledExecutionUtc,
                t.IsRecurring,
                t.RecurringInfo,
                t.CurrentRunCount,
                t.MaxRuns
            ))
            .ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pagination.PageSize);

        return new TasksPagedResponse(items, totalCount, pagination.Page, pagination.PageSize, totalPages);
    }

    /// <inheritdoc />
    public async Task<TaskDetailDto?> GetTaskDetailAsync(Guid id, CancellationToken ct = default)
    {
        var tasks = await _storage.Get(t => t.Id == id, ct);
        var task = tasks.FirstOrDefault();

        if (task == null)
            return null;

        var statusAudits = task.StatusAudits
            .OrderByDescending(a => a.UpdatedAtUtc)
            .Select(a => new StatusAuditDto(a.Id, a.QueuedTaskId, a.UpdatedAtUtc, a.NewStatus, a.Exception))
            .ToList();

        var runsAudits = task.RunsAudits
            .OrderByDescending(a => a.ExecutedAt)
            .Select(a => new RunsAuditDto(a.Id, a.QueuedTaskId, a.ExecutedAt, a.Status, a.Exception))
            .ToList();

        return new TaskDetailDto(
            task.Id,
            task.Type,
            task.Handler,
            task.Request,
            task.Status,
            task.QueueName,
            task.TaskKey,
            task.CreatedAtUtc,
            task.LastExecutionUtc,
            task.ScheduledExecutionUtc,
            task.Exception,
            task.IsRecurring,
            task.RecurringTask,
            task.RecurringInfo,
            task.CurrentRunCount,
            task.MaxRuns,
            task.RunUntil,
            task.NextRunUtc,
            task.AuditLevel,
            statusAudits,
            runsAudits
        );
    }

    /// <inheritdoc />
    public async Task<List<StatusAuditDto>> GetStatusAuditAsync(Guid id, CancellationToken ct = default)
    {
        var tasks = await _storage.Get(t => t.Id == id, ct);
        var task = tasks.FirstOrDefault();

        if (task == null)
            return new List<StatusAuditDto>();

        return task.StatusAudits
            .OrderByDescending(a => a.UpdatedAtUtc)
            .Select(a => new StatusAuditDto(a.Id, a.QueuedTaskId, a.UpdatedAtUtc, a.NewStatus, a.Exception))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<RunsAuditDto>> GetRunsAuditAsync(Guid id, CancellationToken ct = default)
    {
        var tasks = await _storage.Get(t => t.Id == id, ct);
        var task = tasks.FirstOrDefault();

        if (task == null)
            return new List<RunsAuditDto>();

        return task.RunsAudits
            .OrderByDescending(a => a.ExecutedAt)
            .Select(a => new RunsAuditDto(a.Id, a.QueuedTaskId, a.ExecutedAt, a.Status, a.Exception))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ExecutionLogsResponse> GetExecutionLogsAsync(Guid taskId, int skip = 0, int take = 100, string? levelFilter = null, CancellationToken ct = default)
    {
        // Get all logs for the task
        var allLogs = await _storage.GetExecutionLogsAsync(taskId, ct);

        // Apply level filter if specified
        var filteredLogs = allLogs.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(levelFilter))
        {
            filteredLogs = filteredLogs.Where(l => l.Level.Equals(levelFilter, StringComparison.OrdinalIgnoreCase));
        }

        var totalCount = filteredLogs.Count();

        // Apply pagination and map to DTOs
        var logs = filteredLogs
            .Skip(skip)
            .Take(take)
            .Select(l => new ExecutionLogDto(
                l.Id,
                l.TimestampUtc,
                l.Level,
                l.Message,
                l.ExceptionDetails,
                l.SequenceNumber
            ))
            .ToList();

        return new ExecutionLogsResponse(logs, totalCount, skip, take);
    }

    /// <inheritdoc />
    public async Task<TaskCountsDto> GetTaskCountsAsync(CancellationToken ct = default)
    {
        var allTasksList = (await _storage.GetAll(ct)).ToList();

        var all = allTasksList.Count;
        var standard = allTasksList.Count(t => !t.IsRecurring);
        var recurring = allTasksList.Count(t => t.IsRecurring);
        var failed = allTasksList.Count(t => t.Status == QueuedTaskStatus.Failed);

        return new TaskCountsDto(all, standard, recurring, failed);
    }

    private static IQueryable<QueuedTask> ApplySorting(IQueryable<QueuedTask> query, string? sortBy, bool descending)
    {
        if (string.IsNullOrWhiteSpace(sortBy))
            sortBy = "CreatedAtUtc";

        return sortBy switch
        {
            "CreatedAtUtc" => descending ? query.OrderByDescending(t => t.CreatedAtUtc) : query.OrderBy(t => t.CreatedAtUtc),
            "LastExecutionUtc" => descending ? query.OrderByDescending(t => t.LastExecutionUtc) : query.OrderBy(t => t.LastExecutionUtc),
            "ScheduledExecutionUtc" => descending ? query.OrderByDescending(t => t.ScheduledExecutionUtc) : query.OrderBy(t => t.ScheduledExecutionUtc),
            "Status" => descending ? query.OrderByDescending(t => t.Status) : query.OrderBy(t => t.Status),
            "Type" => descending ? query.OrderByDescending(t => t.Type) : query.OrderBy(t => t.Type),
            "QueueName" => descending ? query.OrderByDescending(t => t.QueueName) : query.OrderBy(t => t.QueueName),
            _ => descending ? query.OrderByDescending(t => t.CreatedAtUtc) : query.OrderBy(t => t.CreatedAtUtc)
        };
    }

    private static string GetShortTypeName(string fullTypeName)
    {
        if (string.IsNullOrWhiteSpace(fullTypeName))
            return string.Empty;

        // Try to get just the class name without assembly qualification
        var type = Type.GetType(fullTypeName);
        if (type != null)
            return type.Name;

        // Fallback: extract from string (format is "Namespace.ClassName, AssemblyName")
        var commaIndex = fullTypeName.IndexOf(',');
        var typeName = commaIndex > 0 ? fullTypeName[..commaIndex] : fullTypeName;
        var lastDotIndex = typeName.LastIndexOf('.');
        return lastDotIndex > 0 ? typeName[(lastDotIndex + 1)..] : typeName;
    }
}
