namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for task execution logs.
/// </summary>
public record ExecutionLogsResponse(
    List<ExecutionLogDto> Logs,
    int TotalCount,
    int Skip,
    int Take
);
