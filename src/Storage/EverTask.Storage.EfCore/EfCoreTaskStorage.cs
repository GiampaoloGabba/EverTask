using System.Linq.Expressions;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

public class EfCoreTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<EfCoreTaskStorage> logger)
    : ITaskStorage, ITaskStorageStatistics
{
    /// <summary>
    /// Gets the current UTC time with explicit +00:00 offset.
    /// Ensures consistent timezone handling regardless of server timezone configuration.
    /// </summary>
    private static DateTimeOffset UtcNowNormalized => new(DateTime.UtcNow, TimeSpan.Zero);

    /// <summary>
    /// Canonical recoverable predicate as an EF-translatable expression: the server-side mirror of
    /// <see cref="QueuedTask.IsRecoverable"/>, shared by <see cref="RetrievePending"/> and
    /// <see cref="TrySetQueuedIfRecoverable"/> so the two queries can never drift. SQLite cannot
    /// translate the <c>RunUntil</c> DateTimeOffset comparison and overrides both methods to
    /// evaluate the predicate client-side.
    /// </summary>
    private static Expression<Func<QueuedTask, bool>> RecoverableQuery(DateTimeOffset now) =>
        // < MaxRuns (not <=): a series at CurrentRunCount == MaxRuns is exhausted (CU11/L27); null
        // CurrentRunCount counts as 0 (L34). Mirrors QueuedTask.IsRecoverable.
        t => (t.MaxRuns == null || (t.CurrentRunCount ?? 0) < t.MaxRuns)
             && (t.RunUntil == null || t.RunUntil >= now)
             && (t.Status == QueuedTaskStatus.WaitingQueue ||
                 t.Status == QueuedTaskStatus.Queued ||
                 t.Status == QueuedTaskStatus.Pending ||
                 t.Status == QueuedTaskStatus.ServiceStopped ||
                 t.Status == QueuedTaskStatus.InProgress ||
                 (t.IsRecurring && t.NextRunUtc != null &&
                  (t.Status == QueuedTaskStatus.Completed ||
                   t.Status == QueuedTaskStatus.Failed)));

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

        var now = UtcNowNormalized;

        // Recoverable statuses (see QueuedTask.IsRecoverable for the canonical definition):
        // - WaitingQueue: persisted but never delivered to a worker queue (parked in the in-memory
        //   scheduler at shutdown, or dropped by a full queue) - without it delayed tasks are lost on restart
        // - Queued: written to the in-memory channel but not executed before shutdown
        // - InProgress / ServiceStopped: interrupted mid-execution
        // - Pending: legacy status, kept for backward compatibility
        // - Recurring tasks between two runs (Completed/Failed with a future NextRunUtc): without
        //   them a recurring task not re-registered at startup dies after the first restart
        var query = dbContext.QueuedTasks
                             .AsNoTracking()
                             .Where(RecoverableQuery(now));

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

    /// <inheritdoc />
    public virtual async Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var now = UtcNowNormalized;

        // Non-relational providers (EF Core InMemory) can translate neither the conditional UPDATE nor
        // an explicit transaction: run the transition and its audit as ONE tracked SaveChanges, which
        // the provider applies atomically. SQLite overrides this method (its client-side path is the
        // same single-SaveChanges shape).
        if (dbContext is not DbContext efContext || !efContext.Database.IsRelational())
        {
            var transitionedClientSide = await TrySetQueuedClientSideAsync(dbContext, taskId, now, auditLevel, ct).ConfigureAwait(false);
            if (!transitionedClientSide)
                logger.LogDebug("Task {taskId} is no longer recoverable, skipping SetQueued", taskId);
            return transitionedClientSide;
        }

        // Relational: the atomic conditional UPDATE and its Queued audit must commit TOGETHER (L20),
        // so a recovery transition is never persisted without its audit (a refused transition leaves
        // no trace; a failed audit rolls the transition back). The startup recovery must never
        // resurrect a task that terminally finished after its page was read (SetQueued over Completed
        // would cause a second execution). Recoverable predicate shared with RetrievePending.
        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var rowsAffected = await dbContext.QueuedTasks
            .Where(t => t.Id == taskId)
            .Where(RecoverableQuery(now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, QueuedTaskStatus.Queued), ct)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.LogDebug("Task {taskId} is no longer recoverable, skipping SetQueued", taskId);
            return false;
        }

        AddQueuedTransitionAudit(dbContext, taskId, auditLevel);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Non-atomic-UPDATE recoverable transition evaluated client-side, for providers that cannot
    /// translate the conditional UPDATE (EF Core InMemory, SQLite). Loads the task, applies the
    /// canonical <see cref="QueuedTask.IsRecoverable"/> predicate and, only if recoverable, sets it
    /// Queued AND stages its audit in the SAME SaveChanges, so the transition and its audit are
    /// written atomically (L20).
    /// </summary>
    protected static async Task<bool> TrySetQueuedClientSideAsync(ITaskStoreDbContext dbContext, Guid taskId,
                                                                  DateTimeOffset now, AuditLevel auditLevel, CancellationToken ct)
    {
        var tracked = await dbContext.QueuedTasks
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

        if (tracked == null || !tracked.IsRecoverable(now))
            return false;

        tracked.Status = QueuedTaskStatus.Queued;
        AddQueuedTransitionAudit(dbContext, taskId, auditLevel);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stages the Queued status audit for a recovery transition WITHOUT saving: it is committed in
    /// the SAME unit of work as the transition (a single SaveChanges or the wrapping transaction), so
    /// the pair is atomic. Audited only when the transition actually happens (unlike
    /// <see cref="SetStatus"/>, which audits optimistically): a refused transition leaves no trace.
    /// </summary>
    private static void AddQueuedTransitionAudit(ITaskStoreDbContext dbContext, Guid taskId, AuditLevel auditLevel)
    {
        if (!AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Queued, null))
            return;

        dbContext.StatusAudit.Add(new StatusAudit
        {
            QueuedTaskId = taskId,
            UpdatedAtUtc = UtcNowNormalized,
            NewStatus    = QueuedTaskStatus.Queued,
            Exception    = null
        });
    }

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

        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception);
        var ex          = exception.ToDetailedString();

        // LastExecutionUtc is written only on terminal transitions (a run actually finished).
        // Intermediate transitions (WaitingQueue, Queued, InProgress, Cancelled, Pending) PRESERVE
        // the previous value (COALESCE in the update below): a full-queue revert to WaitingQueue
        // must not stamp a fake execution time, and re-queueing a recurring task must not wipe
        // the timestamp of its last real run.
        var lastExecutionUtc = status != QueuedTaskStatus.WaitingQueue
                               && status != QueuedTaskStatus.Queued
                               && status != QueuedTaskStatus.InProgress
                               && status != QueuedTaskStatus.Cancelled
                               && status != QueuedTaskStatus.Pending
                                   ? UtcNowNormalized
                                   : (DateTimeOffset?)null;

        // Non-relational providers (EF Core InMemory) can translate neither ExecuteUpdate nor an explicit
        // transaction: run the audit insert and the column update as ONE tracked SaveChanges, which the
        // provider applies atomically.
        if (dbContext is not DbContext efContext || !efContext.Database.IsRelational())
        {
            await SetStatusClientSideAsync(dbContext, taskId, status, ex, executionTimeMs, lastExecutionUtc,
                                           createAudit, ct).ConfigureAwait(false);
            return;
        }

        // Relational: the StatusAudit insert and the row UPDATE must commit TOGETHER (F20), so a failure
        // in between never leaves an audit without the row update (or vice versa) — matching the
        // transactional usp_SetTaskStatus stored procedure. A failed update rolls the audit back too.
        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (createAudit)
            {
                dbContext.StatusAudit.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = UtcNowNormalized,
                    NewStatus    = status,
                    Exception    = ex
                });
            }

            var rowsAffected = await ExecuteStatusUpdateAsync(
                dbContext, taskId, status, ex, executionTimeMs, lastExecutionUtc, ct).ConfigureAwait(false);

            if (createAudit)
                await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (rowsAffected == 0)
                logger.LogWarning("Task {taskId} not found for status update to {status}", taskId, status);
        }
        catch (Exception e)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.LogCritical(e, "Unable to update the status {status} for taskId {taskId} atomically", status, taskId);
        }
    }

    /// <summary>
    /// Status transition + audit for non-relational providers (EF Core InMemory): a single tracked
    /// SaveChanges, which the provider applies atomically.
    /// </summary>
    private static async Task SetStatusClientSideAsync(
        ITaskStoreDbContext dbContext, Guid taskId, QueuedTaskStatus status, string? exception,
        double? executionTimeMs, DateTimeOffset? lastExecutionUtc, bool createAudit, CancellationToken ct)
    {
        var task = await dbContext.QueuedTasks.FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        if (task == null)
            return;

        task.Status    = status;
        task.Exception = exception;
        if (lastExecutionUtc.HasValue)
            task.LastExecutionUtc = lastExecutionUtc.Value;
        if (executionTimeMs.HasValue)
            task.ExecutionTimeMs = executionTimeMs.Value;

        if (createAudit)
        {
            dbContext.StatusAudit.Add(new StatusAudit
            {
                QueuedTaskId = taskId,
                UpdatedAtUtc = new DateTimeOffset(DateTime.UtcNow, TimeSpan.Zero),
                NewStatus    = status,
                Exception    = exception
            });
        }

        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies the status/last-execution/exception columns for a task via a single bulk UPDATE.
    /// A seam so the atomicity of audit + update can be exercised under fault injection.
    /// </summary>
    protected virtual Task<int> ExecuteStatusUpdateAsync(
        ITaskStoreDbContext dbContext, Guid taskId, QueuedTaskStatus status, string? exception,
        double? executionTimeMs, DateTimeOffset? lastExecutionUtc, CancellationToken ct)
    {
        if (executionTimeMs.HasValue)
        {
            return dbContext.QueuedTasks
                            .Where(x => x.Id == taskId)
                            .ExecuteUpdateAsync(setters => setters
                                                           .SetProperty(t => t.Status, status)
                                                           .SetProperty(t => t.LastExecutionUtc, t => lastExecutionUtc ?? t.LastExecutionUtc)
                                                           .SetProperty(t => t.Exception, exception)
                                                           .SetProperty(t => t.ExecutionTimeMs, executionTimeMs.Value), ct);
        }

        return dbContext.QueuedTasks
                        .Where(x => x.Id == taskId)
                        .ExecuteUpdateAsync(setters => setters
                                                       .SetProperty(t => t.Status, status)
                                                       .SetProperty(t => t.LastExecutionUtc, t => lastExecutionUtc ?? t.LastExecutionUtc)
                                                       .SetProperty(t => t.Exception, exception), ct);
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

    public virtual Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel) =>
        UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel, 1);

    public virtual async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                               AuditLevel auditLevel, int runsToAdvance)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        // Skipped occurrences must count toward the run counter (F7/F8): advance by 1 + skipped, never
        // below 1.
        if (runsToAdvance < 1)
            runsToAdvance = 1;

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        try
        {
            // Fast path: with AuditLevel.None no audit is ever created and the run counter can be
            // incremented server-side, so the whole update is a single roundtrip with no SELECT.
            if (auditLevel == AuditLevel.None)
            {
                var rowsAffected = await dbContext.QueuedTasks
                                                  .Where(x => x.Id == taskId)
                                                  .ExecuteUpdateAsync(setters => setters
                                                                                 .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                                                 .SetProperty(t => t.NextRunUtc, nextRun)
                                                                                 .SetProperty(t => t.CurrentRunCount, t => (t.CurrentRunCount ?? 0) + runsToAdvance))
                                                  .ConfigureAwait(false);

                if (rowsAffected == 0)
                    logger.LogWarning("Task {taskId} not found for run count update", taskId);

                return;
            }

            // Single tracked load: Status/Exception decide the audit and the same instance
            // receives the counter update (no separate projection + reload).
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.LogWarning("Task {taskId} not found for run count update", taskId);
                return;
            }

            if (AuditPolicy.ShouldCreateRunsAudit(auditLevel, task.Status, task.Exception))
            {
                task.RunsAudits.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
                    ExecutedAt      = UtcNowNormalized,
                    ExecutionTimeMs = executionTimeMs,
                    Status          = task.Status,
                    Exception       = task.Exception
                });
            }

            task.ExecutionTimeMs = executionTimeMs;
            task.NextRunUtc      = nextRun;
            task.CurrentRunCount = (task.CurrentRunCount ?? 0) + runsToAdvance;

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Update the current run counter for Task for taskId {taskId}", taskId);
        }
    }

    /// <summary>
    /// Atomically marks a recurring occurrence Completed AND advances the run counter / next run in a
    /// SINGLE tracked SaveChanges (= one transaction), so a crash can never split the two and resurrect
    /// the finished occurrence at recovery (CU14/L29). Inherited unchanged by Sqlite and SQL Server:
    /// this combined operation has no single-roundtrip stored-procedure equivalent, and atomicity — not
    /// a saved roundtrip — is the priority here.
    /// </summary>
    public virtual async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                   int runsToAdvance, AuditLevel auditLevel)
    {
        logger.LogInformation("Complete recurring run for Task {taskId}", taskId);

        if (runsToAdvance < 1)
            runsToAdvance = 1;

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.LogWarning("Task {taskId} not found for recurring completion", taskId);
                return;
            }

            var now = UtcNowNormalized;

            // Status audit (only when the level audits a successful Completed) + runs audit, the status
            // transition, and the counter/next-run advance all flush in ONE SaveChanges.
            if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null))
            {
                dbContext.StatusAudit.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Completed,
                    Exception    = null
                });
            }

            if (AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null))
            {
                task.RunsAudits.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
                    ExecutedAt      = now,
                    ExecutionTimeMs = executionTimeMs,
                    Status          = QueuedTaskStatus.Completed,
                    Exception       = null
                });
            }

            task.Status           = QueuedTaskStatus.Completed;
            task.Exception        = null;
            task.LastExecutionUtc = now;
            task.ExecutionTimeMs  = executionTimeMs;
            task.NextRunUtc       = nextRun;
            task.CurrentRunCount  = (task.CurrentRunCount ?? 0) + runsToAdvance;

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Unable to complete recurring run for taskId {taskId}", taskId);
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

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<QueuedTaskStatus, int>> CountByStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        // Set-based GROUP BY: never materializes the backlog. The filter is applied
        // conditionally (a "@p IS NULL OR …" predicate would defeat an index seek).
        IQueryable<QueuedTask> query = dbContext.QueuedTasks.AsNoTracking();
        if (createdAtOrAfterUtc != null)
            query = query.Where(t => t.CreatedAtUtc >= createdAtOrAfterUtc);

        var counts = await query
                           .GroupBy(t => t.Status)
                           .Select(g => new { Status = g.Key, Count = g.Count() })
                           .ToListAsync(ct)
                           .ConfigureAwait(false);

        return counts.ToDictionary(c => c.Status, c => c.Count);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<QueuedTaskStatus, int>>>
        CountByQueueAndStatusAsync(DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        IQueryable<QueuedTask> query = dbContext.QueuedTasks.AsNoTracking();
        if (createdAtOrAfterUtc != null)
            query = query.Where(t => t.CreatedAtUtc >= createdAtOrAfterUtc);

        var counts = await query
                           .GroupBy(t => new { t.QueueName, t.Status })
                           .Select(g => new { g.Key.QueueName, g.Key.Status, Count = g.Count() })
                           .ToListAsync(ct)
                           .ConfigureAwait(false);

        return counts
               .GroupBy(c => c.QueueName ?? string.Empty)
               .ToDictionary(
                   g => g.Key,
                   g => (IReadOnlyDictionary<QueuedTaskStatus, int>)g.ToDictionary(c => c.Status, c => c.Count));
    }
}
