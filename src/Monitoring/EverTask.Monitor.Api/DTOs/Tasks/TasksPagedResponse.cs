namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for task list.
/// </summary>
public record TasksPagedResponse(
    List<TaskListDto> Items,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages
);
