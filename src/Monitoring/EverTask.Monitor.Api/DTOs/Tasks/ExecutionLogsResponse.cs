namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for task execution logs.
/// </summary>
/// <param name="Logs">The log entries in the current response.</param>
/// <param name="TotalCount">Total number of log entries available.</param>
/// <param name="Skip">Number of entries skipped.</param>
/// <param name="Take">Number of entries returned.</param>
public record ExecutionLogsResponse(
    List<ExecutionLogDto> Logs,
    int TotalCount,
    int Skip,
    int Take
);
