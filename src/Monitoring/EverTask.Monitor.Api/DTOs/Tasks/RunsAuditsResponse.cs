namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for the recorded runs of a task, newest first.
/// </summary>
/// <param name="Audits">The runs in the current response.</param>
/// <param name="TotalCount">Total number of runs the task's trail holds.</param>
/// <param name="Skip">Number of runs skipped.</param>
/// <param name="Take">Number of runs returned.</param>
public record RunsAuditsResponse(
    List<RunsAuditDto> Audits,
    int TotalCount,
    int Skip,
    int Take
);
