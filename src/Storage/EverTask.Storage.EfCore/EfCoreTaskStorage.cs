using System.Linq.Expressions;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

public class EfCoreTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<EfCoreTaskStorage> logger)
    : ITaskStorage
{
    public virtual async Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where,
                                                CancellationToken ct = default)
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

    public virtual async Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                            CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        logger.LogInformation(
            "Retrieving Pending Tasks (keyset: lastCreatedAt={LastCreatedAt}, lastId={LastId}, take={Take})",
            lastCreatedAt, lastId, take);

        var now = DateTimeOffset.UtcNow;

        var query = dbContext.QueuedTasks
                             .AsNoTracking()
                             .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
                                         && (t.RunUntil == null || t.RunUntil >= now)
                                         && (t.Status == QueuedTaskStatus.Queued ||
                                             t.Status == QueuedTaskStatus.Pending ||
                                             t.Status == QueuedTaskStatus.ServiceStopped ||
                                             t.Status == QueuedTaskStatus.InProgress));

        if (lastCreatedAt.HasValue)
        {
            var lastTime = lastCreatedAt.Value;
            var lastGuid = lastId ?? Guid.Empty;

            query = query.Where(t =>
                t.CreatedAtUtc > lastTime ||
                (t.CreatedAtUtc == lastTime && t.Id.CompareTo(lastGuid) > 0));
        }

        return await query
                     .OrderBy(t => t.CreatedAtUtc)
                     .ThenBy(t => t.Id)
                     .Take(take)
                     .ToArrayAsync(ct)
                     .ConfigureAwait(false);
    }

    public async Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        await SetStatus(taskId, QueuedTaskStatus.Queued, null, auditLevel, null, ct).ConfigureAwait(false);

    public async Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        await SetStatus(taskId, QueuedTaskStatus.InProgress, null, auditLevel, null, ct).ConfigureAwait(false);

    public async Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) =>
        await SetStatus(taskId, QueuedTaskStatus.Completed, null, auditLevel, executionTimeMs).ConfigureAwait(false);

    public async Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) =>
        await SetStatus(taskId, QueuedTaskStatus.Cancelled, null, auditLevel).ConfigureAwait(false);

    public async Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
        await SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, auditLevel).ConfigureAwait(false);

    public virtual async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception,
                                        AuditLevel auditLevel,
                                        double? executionTimeMs = null,
                                        CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        if (ShouldCreateStatusAudit(auditLevel, status, exception))
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

        // Update task status using bulk update (no tracking overhead)
        var ex = exception.ToDetailedString();
        var lastExecutionUtc = status != QueuedTaskStatus.Queued
                               && status != QueuedTaskStatus.InProgress
                               && status != QueuedTaskStatus.Cancelled
                               && status != QueuedTaskStatus.Pending
                                   ? DateTimeOffset.UtcNow
                                   : (DateTimeOffset?)null;

        try
        {
            int rowsAffected;

            // Include ExecutionTimeMs in update if provided
            if (executionTimeMs.HasValue)
            {
                rowsAffected = await dbContext.QueuedTasks
                                               .Where(x => x.Id == taskId)
                                               .ExecuteUpdateAsync(setters => setters
                                                                              .SetProperty(t => t.Status, status)
                                                                              .SetProperty(t => t.LastExecutionUtc, lastExecutionUtc)
                                                                              .SetProperty(t => t.Exception, ex)
                                                                              .SetProperty(t => t.ExecutionTimeMs, executionTimeMs.Value), ct)
                                               .ConfigureAwait(false);
            }
            else
            {
                rowsAffected = await dbContext.QueuedTasks
                                               .Where(x => x.Id == taskId)
                                               .ExecuteUpdateAsync(setters => setters
                                                                              .SetProperty(t => t.Status, status)
                                                                              .SetProperty(t => t.LastExecutionUtc, lastExecutionUtc)
                                                                              .SetProperty(t => t.Exception, ex), ct)
                                               .ConfigureAwait(false);
            }

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

    private static bool ShouldCreateStatusAudit(AuditLevel auditLevel, QueuedTaskStatus status, Exception? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly or AuditLevel.Minimal => IsRealError(status, exception),
            AuditLevel.Full => true,
            _ => true // Default to full audit for unknown levels
        };

    private static bool ShouldCreateRunsAudit(AuditLevel auditLevel, QueuedTaskStatus status, string? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly => !string.IsNullOrEmpty(exception) || status == QueuedTaskStatus.Failed,
            AuditLevel.Minimal => true, // Minimal creates runs audit for recurring tasks (tracks last run)
            AuditLevel.Full => true,
            _ => true // Default to full audit for unknown levels
        };

    /// <summary>
    /// Determines if a task status represents a real error that should be audited.
    /// Excludes expected shutdown scenarios (ServiceStopped with OperationCanceledException).
    /// </summary>
    private static bool IsRealError(QueuedTaskStatus status, Exception? exception) =>
        status switch
        {
            // Always audit Failed status
            QueuedTaskStatus.Failed => true,
            // ServiceStopped with OperationCanceledException is expected shutdown behavior, not an error
            QueuedTaskStatus.ServiceStopped when exception is OperationCanceledException => false,
            _ => exception != null
        };

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

    public async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        // Get current status and exception for audit
        var taskInfo = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .Select(t => new { t.Status, t.Exception, t.CurrentRunCount })
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

        if (taskInfo == null)
        {
            logger.LogWarning("Task {taskId} not found for run count update", taskId);
            return;
        }

        // If we need to create runs audit, load full task with tracking
        if (ShouldCreateRunsAudit(auditLevel, taskInfo.Status, taskInfo.Exception))
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task != null)
            {
                task.RunsAudits.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
                    ExecutedAt      = DateTimeOffset.UtcNow,
                    ExecutionTimeMs = executionTimeMs,
                    Status          = task.Status,
                    Exception       = task.Exception
                });

                task.ExecutionTimeMs = executionTimeMs;
                task.NextRunUtc      = nextRun;
                task.CurrentRunCount = (task.CurrentRunCount ?? 0) + 1;

                try
                {
                    await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    logger.LogCritical(e, "Update the current run counter for Task for taskId {taskId}", taskId);
                }
            }
        }
        else
        {
            // No audit needed, use ExecuteUpdateAsync for better performance
            var newRunCount = (taskInfo.CurrentRunCount ?? 0) + 1;

            try
            {
                await dbContext.QueuedTasks
                               .Where(x => x.Id == taskId)
                               .ExecuteUpdateAsync(setters => setters
                                                              .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                              .SetProperty(t => t.NextRunUtc, nextRun)
                                                              .SetProperty(t => t.CurrentRunCount, newRunCount))
                               .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.LogCritical(e, "Update the current run counter for Task for taskId {taskId}", taskId);
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
                                                                             .SetProperty(t => t.ScheduledExecutionUtc,
                                                                                 task.ScheduledExecutionUtc)
                                                                             .SetProperty(t => t.IsRecurring,
                                                                                 task.IsRecurring)
                                                                             .SetProperty(t => t.RecurringTask,
                                                                                 task.RecurringTask)
                                                                             .SetProperty(t => t.RecurringInfo,
                                                                                 task.RecurringInfo)
                                                                             .SetProperty(t => t.MaxRuns, task.MaxRuns)
                                                                             .SetProperty(t => t.RunUntil,
                                                                                 task.RunUntil)
                                                                             .SetProperty(t => t.NextRunUtc,
                                                                                 task.NextRunUtc)
                                                                             .SetProperty(t => t.QueueName,
                                                                                 task.QueueName)
                                                                             .SetProperty(t => t.TaskKey, task.TaskKey),
                                                  ct)
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

    /// <inheritdoc />
    public async Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences,
                                               CancellationToken ct = default)
    {
        if (skippedOccurrences.Count == 0)
            return;

        logger.LogInformation("Recording {Count} skipped occurrences for task {TaskId}", skippedOccurrences.Count,
            taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        try
        {
            // Get task audit level
            var auditLevelValue = await dbContext.QueuedTasks
                                                 .AsNoTracking()
                                                 .Where(t => t.Id == taskId)
                                                 .Select(t => t.AuditLevel)
                                                 .FirstOrDefaultAsync(ct)
                                                 .ConfigureAwait(false);

            // Null means Full (backward compatibility)
            var auditLevel = auditLevelValue.HasValue ? (AuditLevel)auditLevelValue.Value : AuditLevel.Full;

            // For skipped occurrences, respect audit level (treat as informational, not error)
            if (auditLevel is AuditLevel.None or AuditLevel.ErrorsOnly)
            {
                // Skip audit creation for None and ErrorsOnly levels
                return;
            }

            // Crea messaggio e inserisci direttamente: la FK garantisce l'esistenza
            var skippedTimes = string.Join(", ", skippedOccurrences.Select(d => d.ToString("yyyy-MM-dd HH:mm:ss")));
            var skipMessage =
                $"Skipped {skippedOccurrences.Count} missed occurrence(s) to maintain schedule: {skippedTimes}";

            var runsAudit = new RunsAudit
            {
                QueuedTaskId = taskId,
                ExecutedAt   = DateTimeOffset.UtcNow,
                Status       = QueuedTaskStatus.Cancelled,
                Exception    = skipMessage
            };

            dbContext.RunsAudit.Add(runsAudit);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Se è violazione FK => task inesistente, altrimenti log generico
            logger.LogWarning(e, "Unable to record skipped occurrences for task {TaskId}", taskId);
        }
    }

    /// <inheritdoc />
    public async Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                             CancellationToken cancellationToken)
    {
        // Performance optimization: skip if no logs
        if (logs.Count == 0)
            return;

        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Bulk insert all logs in a single operation
        await dbContext.TaskExecutionLogs.AddRangeAsync(logs, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(
        Guid taskId, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = GetExecutionLogsQuery(dbContext, taskId);
        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(
        Guid taskId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = GetExecutionLogsQuery(dbContext, taskId);
        return await query
                     .Skip(skip)
                     .Take(take)
                     .ToListAsync(cancellationToken)
                     .ConfigureAwait(false);
    }

    private static IQueryable<TaskExecutionLog> GetExecutionLogsQuery(ITaskStoreDbContext dbContext, Guid taskId) =>
        dbContext.TaskExecutionLogs
                 .AsNoTracking()
                 .Where(log => log.TaskId == taskId)
                 .OrderBy(log => log.Id)      // UUIDv7 chronological order (database-friendly, SQLite-compatible)
                 .ThenBy(log => log.SequenceNumber); // preserve sequence within same timestamp
}
