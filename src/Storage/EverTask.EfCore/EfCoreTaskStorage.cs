using System.Linq.Expressions;
using EverTask.Logger;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using EverTask.Storage;

namespace EverTask.EfCore;

public class EfCoreTaskStorage(IServiceScopeFactory serviceScopeFactory, IEverTaskLogger<EfCoreTaskStorage> logger) : ITaskStorage
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

    public async Task PersistTask(QueuedTask taskEntity, CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        dbContext.QueuedTasks.Add(taskEntity);

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Task {name} persisted", taskEntity.Type);
    }

    public async Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default)
    {
        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        logger.LogInformation("Retrieving Pending Tasks");

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(t => t.Status == QueuedTaskStatus.Queued ||
                                          t.Status == QueuedTaskStatus.Pending ||
                                          t.Status == QueuedTaskStatus.InProgress)
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task SetTaskQueued(Guid taskId, CancellationToken ct = default) =>
        await SetTaskStatus(taskId, QueuedTaskStatus.Queued, null, ct).ConfigureAwait(false);

    public async Task SetTaskInProgress(Guid taskId, CancellationToken ct = default) =>
        await SetTaskStatus(taskId, QueuedTaskStatus.InProgress, null, ct).ConfigureAwait(false);

    public async Task SetTaskCompleted(Guid taskId, CancellationToken ct = default) =>
        await SetTaskStatus(taskId, QueuedTaskStatus.Completed, null, ct).ConfigureAwait(false);

    public async Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                                    CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        using var       scope     = serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        var ex = exception.ToDetailedString();
        var lastExecutionUtc = status != QueuedTaskStatus.Queued && status != QueuedTaskStatus.InProgress
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
        dbContext.QueuedTaskStatusAudit.Add(statusAudit);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Unable to save the audit status for taskId {taskId}", taskId);
        }
    }
}
