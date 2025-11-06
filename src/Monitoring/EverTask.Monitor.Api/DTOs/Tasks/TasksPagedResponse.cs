namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for task list.
/// </summary>
/// <param name="Items">The tasks in the current page.</param>
/// <param name="TotalCount">Total number of tasks matching the query.</param>
/// <param name="Page">The current page number (1-based).</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="TotalPages">Total number of pages available.</param>
public record TasksPagedResponse(
    List<TaskListDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);
