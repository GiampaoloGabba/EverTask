using System.Linq.Expressions;
using EverTask.Logger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

public class EfCoreTaskStorage(IServiceScopeFactory serviceScopeFactory, IEverTaskLogger<EfCoreTaskStorage> logger)
    : ITaskStorage
{
    public async Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(where)
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task Persist(QueuedTask taskEntity, CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        /*
        //For recurring tasks we need to check if the task already exists.
        //If it does and the schedule is different we need to invalidate the old task and create a new one to put in executor queue
        //This is to avoid task duplication in the executor queue
        if (taskEntity.RecurringTask != null)
        {
            var existingTask = await dbContext.QueuedTasks
                                              .AsNoTracking()
                                              .FirstOrDefaultAsync(
                                                  t => t.Handler == taskEntity.Handler, ct);
        }
        */

        dbContext.QueuedTasks.Add(taskEntity);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Task {name} persisted", taskEntity.Type);
    }

    public async Task<QueuedTask[]> RetrievePending(CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        logger.LogInformation("Retrieving Pending Tasks");

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
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

    public async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                                    CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        var ex = exception.ToDetailedString();
        var lastExecutionUtc = status != QueuedTaskStatus.Queued
                               && status != QueuedTaskStatus.InProgress
                               && status != QueuedTaskStatus.Cancelled
                               && status != QueuedTaskStatus.Pending
                                   ? DateTimeOffset.UtcNow
                                   : (DateTimeOffset?)null;


        await Audit(dbContext, taskId, status, exception, ct).ConfigureAwait(false);

        var task = await dbContext.QueuedTasks
                                  .Where(x => x.Id == taskId)
                                  .FirstOrDefaultAsync(ct)
                                  .ConfigureAwait(false);

        if (task != null)
        {
            task.Status           = status;
            task.LastExecutionUtc = lastExecutionUtc;
            task.Exception        = ex;

            try
            {
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Unable to update the status {status} for taskId {taskId}", status, taskId);
            }
        }
    }

    internal async Task Audit(ITaskStoreDbContext dbContext, Guid taskId, QueuedTaskStatus status, Exception? exception,
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

    public async Task<int> GetCurrentRunCount(Guid taskId)
    {
        logger.LogInformation("Get the current run counter for Task {taskId}", taskId);
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        var task = await dbContext.QueuedTasks
                                  .Where(x => x.Id == taskId)
                                  .FirstOrDefaultAsync()
                                  .ConfigureAwait(false);

        return task?.CurrentRunCount ?? 0;
    }

    public async Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

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
}
