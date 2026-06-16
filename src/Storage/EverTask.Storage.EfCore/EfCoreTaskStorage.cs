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

    /// <inheritdoc />
    public virtual async Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        // Client-side load + SaveChanges: works uniformly across all EF providers (InMemory cannot
        // ExecuteUpdate). This is the rare recovery-failure path, not a hot path.
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var task = await dbContext.QueuedTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct).ConfigureAwait(false);
        if (task == null)
            return 0;

        task.RecoveryDispatchFailureCount = (task.RecoveryDispatchFailureCount ?? 0) + 1;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return task.RecoveryDispatchFailureCount.Value;
    }

    /// <inheritdoc />
    public virtual async Task ClearRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var task = await dbContext.QueuedTasks.FirstOrDefaultAsync(x => x.Id == taskId, ct).ConfigureAwait(false);
        if (task is { RecoveryDispatchFailureCount: > 0 })
        {
            task.RecoveryDispatchFailureCount = null;
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    public virtual async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                               AuditLevel auditLevel)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        try
        {
            // Fast path: with AuditLevel.None no audit is ever created and the run counter can be
            // incremented server-side, so the whole update is a single roundtrip with no SELECT.
            // Advance by exactly one real execution (Option B): skipped occurrences never count.
            if (auditLevel == AuditLevel.None)
            {
                var rowsAffected = await dbContext.QueuedTasks
                                                  .Where(x => x.Id == taskId)
                                                  .ExecuteUpdateAsync(setters => setters
                                                                                 .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                                                 .SetProperty(t => t.NextRunUtc, nextRun)
                                                                                 .SetProperty(t => t.CurrentRunCount, t => (t.CurrentRunCount ?? 0) + 1))
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
            task.CurrentRunCount = (task.CurrentRunCount ?? 0) + 1; // one real execution (Option B)

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: do NOT swallow. A failed counter persist must propagate so WorkerExecutor does
            // not advance the schedule on unpersisted state; the recoverable row is re-run instead. The
            // consumer (WorkerService.ConsumeAsync) catches this defensively and continues with other tasks.
            logger.LogCritical(e, "Update the current run counter for Task for taskId {taskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Atomically marks a recurring occurrence Completed AND advances the run counter / next run in a
    /// SINGLE tracked SaveChanges (= one transaction), so a crash can never split the two and resurrect
    /// the finished occurrence at recovery (CU14/L29). Inherited unchanged by Sqlite and the in-memory
    /// provider. SQL Server overrides it with the <c>usp_CompleteRecurringRun</c> stored procedure, which
    /// preserves the exact same atomicity (one transaction) while collapsing it into a single roundtrip on
    /// the recurring success path.
    /// </summary>
    public virtual async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                   AuditLevel auditLevel)
    {
        logger.LogInformation("Complete recurring run for Task {taskId}", taskId);

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
            task.CurrentRunCount  = (task.CurrentRunCount ?? 0) + 1; // one real execution (Option B)

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do not swallow) so a failed completion does not advance the schedule
            // on unpersisted state — the row stays recoverable (the transaction rolled back) and is re-run.
            logger.LogCritical(e, "Unable to complete recurring run for taskId {taskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Finalizes a recurring series that ended on a SKIPPED occurrence (next slot past RunUntil): sets
    /// Completed AND clears <see cref="QueuedTask.NextRunUtc"/> in ONE tracked SaveChanges, WITHOUT
    /// advancing the run counter and WITHOUT a runs-audit row — the skipped occurrence never executed
    /// (Option B). Clearing NextRunUtc is what keeps the terminal row out of
    /// <see cref="QueuedTask.IsRecoverable"/>. Inherited unchanged by Sqlite and SQL Server (the
    /// counter/next-run procs are not involved here — this is a once-per-series terminal write).
    /// </summary>
    public virtual async Task SetRecurringSeriesCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        logger.LogInformation("Finalize recurring series (terminal skip) for Task {taskId}", taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync();

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.LogWarning("Task {taskId} not found for recurring series completion", taskId);
                return;
            }

            var now = UtcNowNormalized;

            // Status audit (when the level audits a Completed) + the status transition + the NextRunUtc
            // clear flush in ONE SaveChanges. No runs audit and no counter advance: the occurrence was
            // skipped, not executed (Option B).
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

            task.Status           = QueuedTaskStatus.Completed;
            task.Exception        = null;
            task.LastExecutionUtc = now;
            task.ExecutionTimeMs  = executionTimeMs;
            task.NextRunUtc       = null;

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate so a failed finalize does not advance the schedule on unpersisted state.
            logger.LogCritical(e, "Unable to finalize recurring series for taskId {taskId}", taskId);
            throw;
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

    // ---- Retention cleanup ------------------------------------------------------------------------
    // The base implementations are the optimized, server-side versions for transactional providers
    // (SQL Server, and any future Postgres/MySQL provider that inherits this class). They run as
    // set-based deletes / ordered offsets the database executes directly. SQLite cannot translate
    // DateTimeOffset ordering comparisons, so SqliteTaskStorage overrides every method below with a
    // client-side equivalent. The hosted AuditCleanupHostedService drives these from the policy.

    /// <summary>
    /// Deletes StatusAudit rows older than the cutoffs (errors keep <paramref name="errorCutoff"/>,
    /// successes keep <paramref name="successCutoff"/>). Batched server-side delete (bounded by CleanupBatchSize to avoid lock escalation).
    /// </summary>
    public virtual async Task<int> CleanupStatusAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                       CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await BatchDeleteAsync(dbContext.StatusAudit,
            sa => (string.IsNullOrEmpty(sa.Exception) && sa.UpdatedAtUtc < successCutoff)
               || (!string.IsNullOrEmpty(sa.Exception) && sa.UpdatedAtUtc < errorCutoff),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes RunsAudit rows older than the cutoffs. Batched server-side delete (bounded by CleanupBatchSize to avoid lock escalation).
    /// </summary>
    public virtual async Task<int> CleanupRunsAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                     CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await BatchDeleteAsync(dbContext.RunsAudit,
            ra => (string.IsNullOrEmpty(ra.Exception) && ra.ExecutedAt < successCutoff)
               || (!string.IsNullOrEmpty(ra.Exception) && ra.ExecutedAt < errorCutoff),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes execution logs older than <paramref name="cutoff"/> (by <c>TimestampUtc</c>), independently
    /// of the parent task. Batched server-side delete (bounded by CleanupBatchSize to avoid lock escalation).
    /// </summary>
    public virtual async Task<int> CleanupExecutionLogsByAge(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await BatchDeleteAsync(dbContext.TaskExecutionLogs, l => l.TimestampUtc < cutoff, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Keeps at most <paramref name="maxPerTask"/> of the most recent logs per task (by <c>TimestampUtc</c>,
    /// then <c>SequenceNumber</c>, then <c>Id</c>) and deletes the rest. Resolves the over-cap tasks with a
    /// server-side GROUP BY / HAVING and trims each with a server-side ordered offset.
    /// </summary>
    /// <remarks>
    /// The final <c>Id</c> tie-breaker (UUIDv7) gives a total order, so the survivor is deterministic and
    /// matches the read path's <c>OrderBy(Id)</c> instead of depending on the query plan. Residual caveat:
    /// the SQLite override sorts <c>Id</c> in memory (.NET <see cref="Guid"/> order) while this base sorts it
    /// in the database (<c>uniqueidentifier</c>), so on an exact <c>(TimestampUtc, SequenceNumber)</c> tie the
    /// two providers may keep different (but each internally deterministic) rows. The kept <em>count</em> is
    /// always identical; chasing byte-order parity across providers is out of scope.
    /// </remarks>
    public virtual async Task<int> CleanupExecutionLogsByCount(int maxPerTask, CancellationToken ct = default)
    {
        // <= 0 is disabled (Cluster B): keeping zero logs would let Skip(0) delete every row of every task.
        if (maxPerTask <= 0)
            return 0;

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        var overCapTasks = await dbContext.TaskExecutionLogs
            .GroupBy(l => l.TaskId)
            .Where(g => g.Count() > maxPerTask)
            .Select(g => g.Key)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var total = 0;
        foreach (var taskId in overCapTasks)
        {
            if (ct.IsCancellationRequested)
                break;

            var deletableIds = await dbContext.TaskExecutionLogs
                .Where(l => l.TaskId == taskId)
                .OrderByDescending(l => l.TimestampUtc)
                .ThenByDescending(l => l.SequenceNumber)
                .ThenByDescending(l => l.Id)   // Cluster C: total order on (Timestamp, Seq) ties; aligns with the read path's OrderBy(Id)
                .Skip(maxPerTask)
                .Select(l => l.Id)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            total += await DeleteByIdsAsync(dbContext.TaskExecutionLogs, deletableIds,
                (set, batch) => set.Where(l => batch.Contains(l.Id)), ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>
    /// Hard-deletes completed, non-recurring tasks older than <paramref name="cutoff"/> that have no
    /// surviving audit trail (deleting cascades to anything they own, execution logs included). The
    /// status/recurring/audit filters and the age comparison all translate server-side.
    /// </summary>
    /// <param name="preserveTasksWithLogs">
    /// When true, a task that still has any <c>TaskExecutionLog</c> row is NOT purged. The log-age/count
    /// passes run earlier in the same cycle, so any surviving log is one a configured log-retention window
    /// chose to keep — purging the task would cascade-delete it. The caller sets this only when a log
    /// retention is actually active; with no log retention the historic cascade-on-purge behavior stands.
    /// </param>
    public virtual async Task<int> CleanupCompletedTasks(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                         CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct);

        return await BatchDeleteAsync(dbContext.QueuedTasks,
            qt => qt.Status == QueuedTaskStatus.Completed
               && !qt.IsRecurring
               && !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id)
               && !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id)
               && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id))
               && (qt.LastExecutionUtc ?? qt.CreatedAtUtc) < cutoff,
            ct).ConfigureAwait(false);
    }

    // Bounded delete batch: large enough to be efficient, small enough to avoid lock escalation on
    // transactional providers (SQL Server escalates around ~5000 row locks per statement). Shared by the
    // age-based base deletes (BatchDeleteAsync), the count-cap trim and the SQLite client-side overrides
    // (DeleteByIdsAsync), so the whole storage layer deletes in one consistent granularity.
    internal const int CleanupBatchSize = 100;

    /// <summary>
    /// Deletes rows matching <paramref name="predicate"/> in bounded batches of <see cref="CleanupBatchSize"/>
    /// instead of a single unbounded <c>ExecuteDelete</c>, so a large backlog (a first run over an accumulated
    /// table, or a misconfigured window) cannot escalate to a table lock that stalls the live audit/log
    /// inserts done by task execution. The predicate is evaluated server-side; only matching rows are touched.
    /// SQLite overrides every cleanup method with its own client-side batched path, so this base form runs on
    /// transactional providers (SQL Server today, future Postgres/MySQL by inheritance).
    /// </summary>
    protected static async Task<int> BatchDeleteAsync<TEntity>(
        DbSet<TEntity> set, Expression<Func<TEntity, bool>> predicate, CancellationToken ct)
        where TEntity : class
    {
        var total = 0;
        int deleted;
        do
        {
            deleted = await set.Where(predicate).Take(CleanupBatchSize).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            total  += deleted;
        } while (deleted == CleanupBatchSize && !ct.IsCancellationRequested);

        return total;
    }

    /// <summary>
    /// Deletes the supplied primary keys in chunks of <see cref="CleanupBatchSize"/>, to avoid an oversized
    /// IN (...) list and lock escalation. Shared by the count-cap trim here and by the SQLite client-side
    /// overrides.
    /// </summary>
    protected static async Task<int> DeleteByIdsAsync<TEntity, TKey>(
        DbSet<TEntity> set,
        IReadOnlyList<TKey> ids,
        Func<DbSet<TEntity>, TKey[], IQueryable<TEntity>> batchQuery,
        CancellationToken ct) where TEntity : class
    {
        var total = 0;
        const int batchSize = CleanupBatchSize;

        for (var i = 0; i < ids.Count && !ct.IsCancellationRequested; i += batchSize)
        {
            var count = Math.Min(batchSize, ids.Count - i);
            var batch = new TKey[count];
            for (var j = 0; j < count; j++)
                batch[j] = ids[i + j];

            total += await batchQuery(set, batch).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<QueuedTaskStatus, int>> CountByStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        // Normalize the filter to UTC (offset 0): Npgsql maps DateTimeOffset to timestamptz and REQUIRES
        // Offset==0, so a caller passing e.g. DateTimeOffset.Now (+02:00) would throw on Postgres. This is a
        // no-op for SQL Server/SQLite and for already-UTC inputs (DateTimeOffset comparison is instant-based).
        createdAtOrAfterUtc = createdAtOrAfterUtc?.ToUniversalTime();

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
        // Normalize the filter to UTC (offset 0) — see CountByStatusAsync: required by Npgsql/timestamptz,
        // no-op for the other providers.
        createdAtOrAfterUtc = createdAtOrAfterUtc?.ToUniversalTime();

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
