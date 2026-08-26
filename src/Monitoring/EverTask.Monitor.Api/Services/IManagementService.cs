using EverTask.Monitor.Api.DTOs.Management;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// The three operations an operator actually wants from the dashboard, over <c>ITaskScheduleManager</c>.
/// </summary>
/// <remarks>
/// Nothing here decides WHO may call it: authorization belongs to the pipeline
/// (<c>JwtAuthenticationMiddleware</c>, the operate role and the host's own hook), so a caller that reaches
/// this service has already been allowed to operate.
/// </remarks>
public interface IManagementService
{
    /// <summary>
    /// Puts a terminal occurrence back in the queue, keeping its id, its history and its audit trail.
    /// </summary>
    Task<ManagementActionDto> RequeueOccurrenceAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Releases a durable catch-up that halted itself, keeping the schedule's cursor and therefore its
    /// backlog. The one call that puts a halted schedule back to work.
    /// </summary>
    Task<ManagementActionDto> ResumeScheduleAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Cancels a schedule and every occurrence of it still pending. Terminal: the schedule has to be
    /// dispatched again afterwards.
    /// </summary>
    Task<ManagementActionDto> CancelScheduleAsync(Guid taskId, CancellationToken ct = default);
}
