namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// Paginated response for the status transitions of a task, newest first.
/// </summary>
/// <param name="Audits">The transitions in the current response.</param>
/// <param name="TotalCount">Total number of transitions the task's trail holds.</param>
/// <param name="Skip">Number of transitions skipped.</param>
/// <param name="Take">Number of transitions returned.</param>
public record StatusAuditsResponse(
    List<StatusAuditDto> Audits,
    int TotalCount,
    int Skip,
    int Take
);
