using EverTask.Abstractions;
using EverTask.Monitor.Api.DTOs.Management;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.Api.Services;

/// <summary>
/// The management operations of the API, over <see cref="ITaskScheduleManager"/>.
/// </summary>
/// <remarks>
/// The schedule manager is OPTIONAL because the API can be registered standalone, against a storage no
/// EverTask host is running behind (<c>AddEverTaskMonitoringApiStandalone</c>): there is nothing to operate
/// on then, so the operations answer 501 rather than failing the container at build time.
/// </remarks>
/// <param name="storage">The task storage, used to resolve the row an id names.</param>
/// <param name="logger">Logger.</param>
/// <param name="scheduleManager">The schedule manager, absent when the API runs without an EverTask host.</param>
public class ManagementService(
    ITaskStorage storage,
    ILogger<ManagementService> logger,
    ITaskScheduleManager? scheduleManager = null) : IManagementService
{
    /// <inheritdoc />
    public async Task<ManagementActionDto> RequeueOccurrenceAsync(Guid taskId, CancellationToken ct = default)
    {
        if (scheduleManager is null)
            return NoScheduleManager;

        // The row is resolved here so a missing id answers 404 instead of the 409 an InvalidOperationException
        // would become: the manager raises the same exception type for "no such task" and for "not an
        // occurrence", and those are two different answers to the caller.
        if (await FindAsync(taskId, ct).ConfigureAwait(false) is null)
            return NotFound(taskId);

        try
        {
            var requeued = await scheduleManager.RequeueFailedOccurrence(taskId, ct).ConfigureAwait(false);

            if (!requeued)
            {
                return new ManagementActionDto(ManagementActionStatus.Conflict,
                    "The occurrence is not in a terminal state, so there is nothing to requeue") { TaskId = taskId };
            }

            logger.OccurrenceRequeued(taskId);

            return new ManagementActionDto(ManagementActionStatus.Succeeded,
                "The occurrence was put back in the queue") { TaskId = taskId };
        }
        catch (NotSupportedException e)
        {
            return Unsupported(e.Message, taskId);
        }
        catch (InvalidOperationException e)
        {
            return Conflict(e.Message, taskId);
        }
    }

    /// <inheritdoc />
    public Task<ManagementActionDto> ResumeScheduleAsync(Guid taskId, CancellationToken ct = default) =>
        OnScheduleAsync(taskId, async (manager, taskKey) =>
        {
            var result = await manager.ResumeSchedule(taskKey, ct).ConfigureAwait(false);

            logger.ScheduleResumed(taskKey, taskId, result.ReleasedHalt);

            return new ManagementActionDto(ManagementActionStatus.Succeeded,
                result.ReleasedHalt
                    ? "The halted catch-up was released and the schedule keeps its backlog"
                    : "The schedule was handed back to the scheduler; no halt was standing")
            {
                TaskId       = taskId,
                NextRunUtc   = result.NextRunUtc,
                ReleasedHalt = result.ReleasedHalt
            };
        }, ct);

    /// <inheritdoc />
    public Task<ManagementActionDto> CancelScheduleAsync(Guid taskId, CancellationToken ct = default) =>
        OnScheduleAsync(taskId, async (manager, taskKey) =>
        {
            await manager.CancelSchedule(taskKey, ct).ConfigureAwait(false);

            logger.ScheduleCancelled(taskKey, taskId);

            return new ManagementActionDto(ManagementActionStatus.Succeeded,
                "The schedule and every occurrence of it still pending were cancelled") { TaskId = taskId };
        }, ct);

    /// <summary>
    /// The two operations addressed by task key share everything but the call itself: the manager has to be
    /// there, the row has to exist, be a schedule, and carry the key the manager addresses it by.
    /// </summary>
    private async Task<ManagementActionDto> OnScheduleAsync(
        Guid taskId, Func<ITaskScheduleManager, string, Task<ManagementActionDto>> operation, CancellationToken ct)
    {
        if (scheduleManager is null)
            return NoScheduleManager;

        var row = await FindAsync(taskId, ct).ConfigureAwait(false);

        if (row is null)
            return NotFound(taskId);

        if (!row.IsRecurring)
            return Conflict("The row is a one-shot task, not a schedule", taskId);

        // ITaskScheduleManager addresses a schedule by the key it was dispatched under, and a schedule
        // dispatched without one cannot be named at all — from here or from application code.
        if (string.IsNullOrWhiteSpace(row.TaskKey))
        {
            return Conflict(
                "The schedule was dispatched without a task key, and a schedule is addressed by its key", taskId);
        }

        try
        {
            return await operation(scheduleManager, row.TaskKey).ConfigureAwait(false);
        }
        catch (OccurrenceProviderException e)
        {
            // Transient by contract, and nothing was written: the caller may simply try again.
            return new ManagementActionDto(ManagementActionStatus.Unavailable, e.Message) { TaskId = taskId };
        }
        catch (NotSupportedException e)
        {
            return Unsupported(e.Message, taskId);
        }
        catch (InvalidOperationException e)
        {
            return Conflict(e.Message, taskId);
        }
    }

    private async Task<QueuedTask?> FindAsync(Guid taskId, CancellationToken ct)
    {
        var rows = await storage.Get(t => t.Id == taskId, ct).ConfigureAwait(false);
        return rows.Length == 0 ? null : rows[0];
    }

    private static readonly ManagementActionDto NoScheduleManager = new(ManagementActionStatus.NotSupported,
        "No EverTask host is registered with this API, so there is nothing to manage");

    private static ManagementActionDto NotFound(Guid taskId) =>
        new(ManagementActionStatus.NotFound, "No task carries that id") { TaskId = taskId };

    private static ManagementActionDto Conflict(string message, Guid taskId) =>
        new(ManagementActionStatus.Conflict, message) { TaskId = taskId };

    private static ManagementActionDto Unsupported(string message, Guid taskId) =>
        new(ManagementActionStatus.NotSupported, message) { TaskId = taskId };
}
