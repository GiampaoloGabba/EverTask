using System.Linq.Expressions;
using EverTask.Logger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

public class EfCoreTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<EfCoreTaskStorage> logger)
    : ITaskStorage
{
    public virtual async Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(where)
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public virtual async Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task Persist(QueuedTask taskEntity, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        dbContext.QueuedTasks.Add(taskEntity);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Task {name} persisted", taskEntity.Type);
    }

    public virtual async Task<QueuedTask[]> RetrievePending(CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        logger.LogInformation("Retrieving Pending Tasks");

        var now = DateTimeOffset.UtcNow;

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
                                          && (t.RunUntil == null || t.RunUntil >= now)
                                          && (t.Status == QueuedTaskStatus.Queued ||
                                              t.Status == QueuedTaskStatus.Pending ||
                                              t.Status == QueuedTaskStatus.ServiceStopped ||
                                              t.Status == QueuedTaskStatus.InProgress))
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task SetQueued(Guid taskId, CancellationToken ct = default) =>
        await SetStatus(taskId, QueuedTaskStatus.Queued, null, ct).ConfigureAwait(false);

    public async Task SetInProgress(Guid taskId, CancellationToken ct = default) =>
        await SetStatus(taskId, QueuedTaskStatus.InProgress, null, ct).ConfigureAwait(false);

    public async Task SetCompleted(Guid taskId) =>
        await SetStatus(taskId, QueuedTaskStatus.Completed).ConfigureAwait(false);

    public async Task SetCancelledByUser(Guid taskId) =>
        await SetStatus(taskId, QueuedTaskStatus.Cancelled).ConfigureAwait(false);

    public async Task SetCancelledByService(Guid taskId, Exception exception) =>
        await SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception).ConfigureAwait(false);

    public virtual async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                                    CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var ex = exception.ToDetailedString();
        var lastExecutionUtc = status != QueuedTaskStatus.Queued
                               && status != QueuedTaskStatus.InProgress
                               && status != QueuedTaskStatus.Cancelled
                               && status != QueuedTaskStatus.Pending
                                   ? DateTimeOffset.UtcNow
                                   : (DateTimeOffset?)null;


        await Audit(dbContext, taskId, status, exception, ct).ConfigureAwait(false);

        try
        {
            var rowsAffected = await dbContext.QueuedTasks
                                              .Where(x => x.Id == taskId)
                                              .ExecuteUpdateAsync(setters => setters
                                                  .SetProperty(t => t.Status, status)
                                                  .SetProperty(t => t.LastExecutionUtc, lastExecutionUtc)
                                                  .SetProperty(t => t.Exception, ex), ct)
                                              .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                logger.LogWarning("Task {taskId} not found for status update to {status}", taskId, status);
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Unable to update the status {status} for taskId {taskId}", status, taskId);
        }
    }

    private async Task Audit(ITaskStoreDbContext dbContext, Guid taskId, QueuedTaskStatus status, Exception? exception,
                             CancellationToken ct)
    {
        var detailedException = exception.ToDetailedString();
        var statusAudit = new StatusAudit
        {
            QueuedTaskId = taskId,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            NewStatus    = status,
            Exception    = detailedException
        };
        dbContext.StatusAudit.Add(statusAudit);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to save the audit status for taskId {taskId}", taskId);
        }
    }

    public virtual async Task<int> GetCurrentRunCount(Guid taskId)
    {
        logger.LogInformation("Get the current run counter for Task {taskId}", taskId);
        await using var dbContext = await contextFactory.CreateDbContextAsync();

        var task = await dbContext.QueuedTasks
                                  .Where(x => x.Id == taskId)
                                  .FirstOrDefaultAsync()
                                  .ConfigureAwait(false);

        return task?.CurrentRunCount ?? 0;
    }

    public async Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        var task = await dbContext.QueuedTasks
                                  .Where(x => x.Id == taskId)
                                  .FirstOrDefaultAsync()
                                  .ConfigureAwait(false);

        if (task != null)
        {

            task.RunsAudits.Add(new RunsAudit
            {
                QueuedTaskId = taskId,
                ExecutedAt   = DateTimeOffset.UtcNow,
                Status       = task.Status,
                Exception    = task.Exception
            });

            task.NextRunUtc = nextRun;
            var currentRun = task.CurrentRunCount ?? 0;
            task.CurrentRunCount = currentRun + 1;

            try
            {
                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Update the current run counter for Task  for taskId {taskId}", taskId);
            }
        }
    }

    public virtual async Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(t => t.TaskKey == taskKey)
                              .FirstOrDefaultAsync(ct)
                              .ConfigureAwait(false);
    }

    public virtual async Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Updating task {taskId} with key {taskKey}", task.Id, task.TaskKey);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        try
        {
            var rowsAffected = await dbContext.QueuedTasks
                                              .Where(t => t.Id == task.Id)
                                              .ExecuteUpdateAsync(setters => setters
                                                  .SetProperty(t => t.Type, task.Type)
                                                  .SetProperty(t => t.Request, task.Request)
                                                  .SetProperty(t => t.Handler, task.Handler)
                                                  .SetProperty(t => t.ScheduledExecutionUtc, task.ScheduledExecutionUtc)
                                                  .SetProperty(t => t.IsRecurring, task.IsRecurring)
                                                  .SetProperty(t => t.RecurringTask, task.RecurringTask)
                                                  .SetProperty(t => t.RecurringInfo, task.RecurringInfo)
                                                  .SetProperty(t => t.MaxRuns, task.MaxRuns)
                                                  .SetProperty(t => t.RunUntil, task.RunUntil)
                                                  .SetProperty(t => t.NextRunUtc, task.NextRunUtc)
                                                  .SetProperty(t => t.QueueName, task.QueueName)
                                                  .SetProperty(t => t.TaskKey, task.TaskKey), ct)
                                              .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                logger.LogWarning("Task {taskId} not found for update", task.Id);
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Unable to update task {taskId}", task.Id);
            throw;
        }
    }

    public virtual async Task Remove(Guid taskId, CancellationToken ct = default)
    {
        logger.LogInformation("Removing task {taskId}", taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        try
        {
            var rowsAffected = await dbContext.QueuedTasks
                                              .Where(t => t.Id == taskId)
                                              .ExecuteDeleteAsync(ct)
                                              .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                logger.LogWarning("Task {taskId} not found for removal", taskId);
            }
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Unable to remove task {taskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Records information about skipped recurring task occurrences in the audit trail.
    /// This creates a RunsAudit entry with details about which scheduled runs were skipped.
    /// </summary>
    /// <param name="taskId">The ID of the recurring task</param>
    /// <param name="skippedOccurrences">List of DateTimeOffset values representing skipped execution times</param>
    /// <param name="ct">Optional cancellation token</param>
    /// <returns>A task representing the asynchronous operation</returns>
    /// <remarks>
    /// This method is called when a recurring task resumes after downtime and needs to skip
    /// past occurrences to maintain its schedule. The audit entry uses QueuedTaskStatus.Completed
    /// with exception details containing the skip information for tracking purposes.
    /// </remarks>
    public virtual async Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences, CancellationToken ct = default)
    {
        if (skippedOccurrences.Count == 0)
            return;

        logger.LogInformation("Recording {count} skipped occurrences for task {taskId}", skippedOccurrences.Count, taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync(ct)
                                      .ConfigureAwait(false);

            if (task != null)
            {
                // Create a summary of skipped times
                var skippedTimes = string.Join(", ", skippedOccurrences.Select(d => d.ToString("yyyy-MM-dd HH:mm:ss")));
                var skipMessage = $"Skipped {skippedOccurrences.Count} missed occurrence(s) to maintain schedule: {skippedTimes}";

                // Add a RunsAudit entry documenting the skips
                var runsAudit = new RunsAudit
                {
                    QueuedTaskId = taskId,
                    ExecutedAt   = DateTimeOffset.UtcNow,
                    Status       = QueuedTaskStatus.Completed, // Using Completed as the base status
                    Exception    = skipMessage // Store skip info in Exception field for audit trail
                };

                dbContext.RunsAudit.Add(runsAudit);
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            else
            {
                logger.LogWarning("Task {taskId} not found when trying to record skipped occurrences", taskId);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to record skipped occurrences for task {taskId}", taskId);
        }
    }
}
