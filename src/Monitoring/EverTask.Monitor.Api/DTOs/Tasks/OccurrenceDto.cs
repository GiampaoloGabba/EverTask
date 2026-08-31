using EverTask.Storage;

namespace EverTask.Monitor.Api.DTOs.Tasks;

/// <summary>
/// One materialized occurrence of a durable schedule, as the schedule's detail view lists it.
/// </summary>
/// <param name="Id">The occurrence's own task id: it is a real one-shot row, with its own status and audit.</param>
/// <param name="ParentTaskId">The schedule row this occurrence belongs to.</param>
/// <param name="Status">The current status of the occurrence.</param>
/// <param name="Occurrence">The slot, run number and misfire metadata the row carries.</param>
/// <param name="CreatedAtUtc">When the occurrence was materialized.</param>
/// <param name="LastExecutionUtc">When it last ran, or null while it has not run yet.</param>
/// <param name="ExecutionTimeMs">Its last execution time in milliseconds.</param>
/// <param name="Exception">The last exception message, if any.</param>
/// <param name="ScheduleVersion">The schedule version this occurrence was materialized against.</param>
public record OccurrenceDto(
    Guid Id,
    Guid ParentTaskId,
    QueuedTaskStatus Status,
    OccurrenceInfoDto Occurrence,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastExecutionUtc,
    double ExecutionTimeMs,
    string? Exception,
    int ScheduleVersion
)
{
    /// <inheritdoc cref="TaskListDto.StartedAtUtc"/>
    public DateTimeOffset? StartedAtUtc { get; init; }
}
