using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq.Expressions;
using System.Reflection;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.EntityFrameworkCore.Storage;

namespace EverTask.Storage.EfCore;

public class EfCoreTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<EfCoreTaskStorage> logger)
    : ITaskStorage, ITaskStorageStatistics
{
    private readonly Lazy<(QueuedTaskInsert? Insert, string? Reason)> _queuedTaskInsert = new(() =>
    {
        using var dbContext = contextFactory.CreateDbContext();
        try
        {
            return (new QueuedTaskInsert((DbContext)dbContext), null);
        }
        catch (QueuedTaskInsertUnavailableException e)
        {
            logger.PersistInsertFallback(e.Message);
            return (null, e.Message);
        }
    });

    /// <summary>Current UTC time with an explicit +00:00 offset, whatever the server timezone is.</summary>
    private static DateTimeOffset UtcNowNormalized => new(DateTime.UtcNow, TimeSpan.Zero);

    /// <summary>
    /// The half of the recovery filter EVERY relational provider can translate: the recoverable status set
    /// and the run budget. Split out of <see cref="RecoverableForExecutionQuery"/> so SQLite, which has to
    /// decide the temporal half in memory, can still assert this half in the WHERE clause of its conditional
    /// UPDATE instead of trusting a preceding SELECT for the whole predicate.
    /// </summary>
    protected static readonly Expression<Func<QueuedTask, bool>> RecoverableStatusAndBudget =
        // < MaxRuns (not <=): a series at CurrentRunCount == MaxRuns is exhausted; a null CurrentRunCount
        // counts as 0. Mirrors QueuedTask.IsRecoverableForExecution.
        t => (t.MaxRuns == null || (t.CurrentRunCount ?? 0) < t.MaxRuns)
             && (t.Status == QueuedTaskStatus.WaitingQueue ||
                 t.Status == QueuedTaskStatus.Queued ||
                 t.Status == QueuedTaskStatus.Pending ||
                 t.Status == QueuedTaskStatus.ServiceStopped ||
                 t.Status == QueuedTaskStatus.InProgress ||
                 (t.IsRecurring && t.NextRunUtc != null &&
                  (t.Status == QueuedTaskStatus.Completed ||
                   t.Status == QueuedTaskStatus.Failed)));

    /// <summary>
    /// The execution half of the recovery filter as an EF-translatable expression: the server-side mirror of
    /// <see cref="QueuedTask.IsRecoverableForExecution"/>, shared by <see cref="RetrievePending"/> and
    /// <see cref="TrySetQueuedIfRecoverable"/> so the two queries can never drift. SQLite cannot translate
    /// the <c>RunUntil</c> DateTimeOffset comparison and overrides both methods to evaluate it client-side.
    /// The last branch keeps a recurring series recoverable when the slot it had already scheduled precedes
    /// the boundary that elapsed during the downtime.
    /// </summary>
    private static Expression<Func<QueuedTask, bool>> RecoverableForExecutionQuery(DateTimeOffset now) =>
        Compose(RecoverableStatusAndBudget,
            t => t.RunUntil == null
                 || t.RunUntil >= now
                 || (t.IsRecurring && t.NextRunUtc != null && t.RunUntil != null && t.NextRunUtc < t.RunUntil),
            Expression.AndAlso);

    /// <summary>
    /// A recurring series with a cursor but nothing left to run, which recovery must FINALIZE rather than
    /// execute. Mirrors <see cref="QueuedTask.IsRecurringSeriesToFinalize"/>.
    /// </summary>
    private static readonly Expression<Func<QueuedTask, bool>> SeriesToFinalizeQuery =
        t => t.IsRecurring
             && t.NextRunUtc != null
             && t.Status != QueuedTaskStatus.Cancelled
             && ((t.RunUntil != null && t.NextRunUtc >= t.RunUntil)
                 || (t.MaxRuns != null && (t.CurrentRunCount ?? 0) >= t.MaxRuns));

    /// <summary>
    /// What a recovery page returns: the union of the two categories. Composed from the two expressions
    /// above rather than re-spelled, so neither copy can drift from the other.
    /// </summary>
    protected static Expression<Func<QueuedTask, bool>> RecoveryPageQuery(DateTimeOffset now) =>
        Compose(RecoverableForExecutionQuery(now), SeriesToFinalizeQuery, Expression.OrElse);

    /// <summary>
    /// Combines two single-parameter predicates by rebinding the right-hand parameter onto the left-hand one,
    /// producing a plain expression tree EF translates like a hand-written predicate (unlike
    /// <c>Expression.Invoke</c>, which it cannot).
    /// </summary>
    private static Expression<Func<QueuedTask, bool>> Compose(
        Expression<Func<QueuedTask, bool>> left, Expression<Func<QueuedTask, bool>> right,
        Func<Expression, Expression, BinaryExpression> combine)
    {
        var parameter = left.Parameters[0];
        var rebound   = new ParameterRebinder(right.Parameters[0], parameter).Visit(right.Body);

        return Expression.Lambda<Func<QueuedTask, bool>>(combine(left.Body, rebound), parameter);
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }

    public virtual async Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where,
                                                CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(where)
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public virtual async Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .ToArrayAsync(ct)
                              .ConfigureAwait(false);
    }

    public async Task Persist(QueuedTask taskEntity, CancellationToken ct = default)
    {
        // A cursor compare-and-swap matches the STORED representation, and SQLite compares a DateTimeOffset
        // as the text it stored, offset included: a row written at +02:00 would lose every compare-and-swap
        // against the same instant in UTC (#37).
        taskEntity.NormalizeTimestampsToUtc();

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        if (dbContext is DbContext efContext
            && efContext.Database.IsRelational()
            && _queuedTaskInsert.Value.Insert is { } insert)
        {
            await efContext.Database.ExecuteSqlRawAsync(
                    insert.Sql, (IEnumerable<object>)insert.Parameters(efContext, taskEntity), ct)
                .ConfigureAwait(false);
        }
        else
        {
            dbContext.QueuedTasks.Add(taskEntity);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        logger.TaskPersisted(taskEntity.Type);
    }

    /// <summary>
    /// Which pre-4.0 signatures the CONCRETE storage type overrides, computed once per type.
    /// </summary>
    /// <remarks>
    /// A provider written before the clock-carrying overloads existed overrides only the legacy signatures.
    /// The core calls the <c>nowUtc</c> overloads exclusively and a base-class virtual binds statically, so
    /// without this probe the base would answer them itself and the derived override would silently become
    /// dead code.
    /// </remarks>
    private readonly record struct LegacyOverrides(bool RetrievePending, bool TrySetQueuedIfRecoverable);

    private static readonly ConcurrentDictionary<Type, LegacyOverrides> LegacyOverridesByType = new();

    // Cached per instance too (the storage is a singleton): the dictionary is the per-TYPE memo, this field
    // keeps the recovery path from hashing a Type on every page and every re-queue. A benign race recomputes
    // the same value.
    private LegacyOverrides? _legacyOverrides;

    private LegacyOverrides Legacy =>
        _legacyOverrides ??= LegacyOverridesByType.GetOrAdd(GetType(), static type => new LegacyOverrides(
            OverridesBaseMethod(type, nameof(RetrievePending),
                [typeof(DateTimeOffset?), typeof(Guid?), typeof(int), typeof(CancellationToken)]),
            OverridesBaseMethod(type, nameof(TrySetQueuedIfRecoverable),
                [typeof(Guid), typeof(AuditLevel), typeof(CancellationToken)])));

    private static bool OverridesBaseMethod(Type storageType, string name, Type[] parameterTypes) =>
        storageType.GetMethod(name, BindingFlags.Public | BindingFlags.Instance, null, parameterTypes, null)
            is { } method && method.DeclaringType != typeof(EfCoreTaskStorage);

    /// <inheritdoc />
    public virtual Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                      CancellationToken ct = default) =>
        // Straight to the implementation, never back through the nowUtc overload: a derived class that
        // overrides only this signature and calls base would otherwise bounce between the two forever.
        RetrievePendingCore(UtcNowNormalized, lastCreatedAt, lastId, take, ct);

    /// <inheritdoc />
    public virtual Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt,
                                                      Guid? lastId, int take, CancellationToken ct = default) =>
        Legacy.RetrievePending
            ? RetrievePending(lastCreatedAt, lastId, take, ct)
            : RetrievePendingCore(nowUtc, lastCreatedAt, lastId, take, ct);

    private async Task<QueuedTask[]> RetrievePendingCore(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt,
                                                         Guid? lastId, int take, CancellationToken ct)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        logger.RetrievingPendingTasks(lastCreatedAt, lastId, take);

        // Offset 0 required by Npgsql's timestamptz mapping; a no-op for the other providers and for a
        // caller already on UTC (DateTimeOffset comparison is instant-based).
        var now = nowUtc.ToUniversalTime();

        // Whatever this filter excludes is silently lost on restart. The canonical status list lives in
        // QueuedTask.IsRecoverableForExecution; the page is the union of it and the series to finalize.
        var query = dbContext.QueuedTasks
                             .AsNoTracking()
                             .Where(RecoveryPageQuery(now));

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
    public virtual Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel,
                                                        CancellationToken ct = default) =>
        TrySetQueuedIfRecoverableCore(UtcNowNormalized, taskId, auditLevel, ct);

    /// <inheritdoc />
    public virtual Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                                        CancellationToken ct = default) =>
        Legacy.TrySetQueuedIfRecoverable
            ? TrySetQueuedIfRecoverable(taskId, auditLevel, ct)
            : TrySetQueuedIfRecoverableCore(nowUtc, taskId, auditLevel, ct);

    private async Task<bool> TrySetQueuedIfRecoverableCore(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                                           CancellationToken ct)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var now = nowUtc.ToUniversalTime();

        // Non-relational providers (EF Core InMemory) can translate neither the conditional UPDATE nor
        // an explicit transaction: run the transition and its audit as ONE tracked SaveChanges, which
        // the provider applies atomically. SQLite overrides this method: it is relational, so it keeps
        // the conditional UPDATE and only moves the untranslatable temporal term out of it.
        if (dbContext is not DbContext efContext || !efContext.Database.IsRelational())
        {
            var transitionedClientSide = await TrySetQueuedClientSideAsync(dbContext, taskId, now, auditLevel, ct).ConfigureAwait(false);
            if (!transitionedClientSide)
                logger.TaskNoLongerRecoverable(taskId);
            return transitionedClientSide;
        }

        // Relational: when requested, the Queued audit must commit with the conditional UPDATE. Without an
        // audit the single UPDATE is already atomic and needs no explicit transaction.
        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Queued, null);
        await using var transaction = createAudit
                                          ? await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                                          : null;

        var rowsAffected = await dbContext.QueuedTasks
            .Where(t => t.Id == taskId)
            .Where(RecoverableForExecutionQuery(now))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, QueuedTaskStatus.Queued), ct)
            .ConfigureAwait(false);

        if (rowsAffected == 0)
        {
            if (transaction != null)
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.TaskNoLongerRecoverable(taskId);
            return false;
        }

        if (transaction != null)
            await CommitQueuedTransitionAsync(dbContext, transaction, taskId, auditLevel, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Commits a recovery transition the conditional UPDATE has already applied, together with its Queued
    /// status audit, so the pair lands or is rolled back as one and a refused transition leaves no trace.
    /// The shape a provider overriding the transition has to reuse.
    /// </summary>
    protected static Task CommitQueuedTransitionAsync(ITaskStoreDbContext dbContext,
                                                      IDbContextTransaction transaction, Guid taskId,
                                                      AuditLevel auditLevel, CancellationToken ct) =>
        CommitWithStatusAuditAsync(dbContext, transaction, taskId, QueuedTaskStatus.Queued, null, auditLevel,
            UtcNowNormalized, ct);

    /// <summary>
    /// Recoverable transition evaluated client-side, for the providers that can express NO conditional
    /// UPDATE at all (EF Core InMemory): the transition and its audit go in the SAME SaveChanges, so they
    /// are written atomically.
    /// </summary>
    /// <remarks>
    /// The read and the write are two steps, so this is NOT a compare-and-swap: a transition that
    /// linearizes in between is overwritten. Every relational provider must put its condition in the WHERE
    /// clause instead.
    /// </remarks>
    protected static async Task<bool> TrySetQueuedClientSideAsync(ITaskStoreDbContext dbContext, Guid taskId,
                                                                  DateTimeOffset now, AuditLevel auditLevel, CancellationToken ct)
    {
        var tracked = await dbContext.QueuedTasks
            .FirstOrDefaultAsync(t => t.Id == taskId, ct).ConfigureAwait(false);

        if (tracked == null || !tracked.IsRecoverableForExecution(now))
            return false;

        tracked.Status = QueuedTaskStatus.Queued;
        AddQueuedTransitionAudit(dbContext, taskId, auditLevel);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stages the Queued status audit for a recovery transition WITHOUT saving: it is committed in the SAME
    /// unit of work as the transition. Audited only when the transition actually happens (unlike
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
        logger.SettingTaskStatus(taskId, status);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception);
        var ex          = exception.ToDetailedString();

        var lastExecutionUtc = LastExecutionUtcFor(status, UtcNowNormalized);

        // Non-relational providers (EF Core InMemory) can translate neither ExecuteUpdate nor an explicit
        // transaction: run the audit insert and the column update as ONE tracked SaveChanges, which the
        // provider applies atomically.
        if (dbContext is not DbContext efContext || !efContext.Database.IsRelational())
        {
            await SetStatusClientSideAsync(dbContext, taskId, status, ex, executionTimeMs, lastExecutionUtc,
                                           createAudit, ct).ConfigureAwait(false);
            return;
        }

        // Relational: when requested, the StatusAudit insert and row UPDATE must commit together. Without an
        // audit the single UPDATE is already atomic and needs no explicit transaction.
        await using var transaction = createAudit
                                          ? await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
                                          : null;
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

            if (transaction != null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (rowsAffected == 0)
                logger.TaskNotFoundForStatusUpdate(taskId, status);
        }
        catch (Exception e)
        {
            if (transaction != null)
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.StatusUpdateFailed(e, status, taskId);
        }
    }

    /// <summary>
    /// When a transition stamps the end of a run, and when it PRESERVES the timestamp of the last one: only
    /// a terminal transition means a run actually finished, so a full-queue revert to WaitingQueue must not
    /// stamp a fake execution time and a re-queued recurring task must keep the timestamp of its last run.
    /// </summary>
    private static DateTimeOffset? LastExecutionUtcFor(QueuedTaskStatus status, DateTimeOffset now) =>
        status is QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Queued or QueuedTaskStatus.InProgress
            or QueuedTaskStatus.Cancelled or QueuedTaskStatus.Pending
            ? null
            : now;

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
    /// Applies the status/last-execution/exception columns for a task via a single bulk UPDATE. Virtual so
    /// a provider — or a fault-injecting test — can substitute the statement.
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


    /// <inheritdoc />
    public virtual async Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        // Client-side load + SaveChanges: works uniformly across all EF providers (InMemory cannot
        // ExecuteUpdate). This is the rare recovery-failure path, not a hot path.
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            // Fast path: with AuditLevel.None no audit is ever created and the run counter can be
            // incremented server-side, so the whole update is a single roundtrip with no SELECT.
            // Exactly one real execution is counted — a skipped occurrence never is.
            if (auditLevel == AuditLevel.None)
            {
                var rowsAffected = await dbContext.QueuedTasks
                                                  .Where(x => x.Id == taskId)
                                                  .ExecuteUpdateAsync(setters => setters
                                                                                 .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                                                 .SetProperty(t => t.NextRunUtc, nextRun)
                                                                                 .SetProperty(t => t.CurrentRunCount, t => t.CurrentRunCount >= int.MaxValue ? int.MaxValue : (t.CurrentRunCount ?? 0) + 1))
                                                  .ConfigureAwait(false);

                if (rowsAffected == 0)
                    logger.TaskNotFoundForRunCountUpdate(taskId);

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
                logger.TaskNotFoundForRunCountUpdate(taskId);
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
            // Saturating: at int.MaxValue the counter FREEZES rather than wrapping to int.MinValue and
            // corrupting the run accounting of an unbounded series. Every path that counts a run — here, the
            // fast path above, CompleteRecurringRun, the server-side procs/CTEs, MemoryTaskStorage — caps it
            // the same way.
            task.CurrentRunCount = task.CurrentRunCount >= int.MaxValue ? int.MaxValue : (task.CurrentRunCount ?? 0) + 1;

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Do NOT swallow: a failed counter persist must propagate so WorkerExecutor does not advance the
            // schedule on unpersisted state; the recoverable row is re-run instead.
            logger.CurrentRunUpdateFailed(e, taskId);
            throw;
        }
    }

    /// <summary>
    /// Atomically marks a recurring occurrence Completed AND advances the run counter / next run in a SINGLE
    /// tracked SaveChanges, so a crash can never split the two and resurrect the finished occurrence at
    /// recovery. SQL Server overrides it with the <c>usp_CompleteRecurringRun</c> stored procedure, which
    /// keeps the same atomicity in a single roundtrip.
    /// </summary>
    public virtual async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                   AuditLevel auditLevel)
    {
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.TaskNotFoundForRecurringCompletion(taskId);
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
            task.CurrentRunCount  = task.CurrentRunCount >= int.MaxValue ? int.MaxValue : (task.CurrentRunCount ?? 0) + 1; // one real execution; saturating, see UpdateCurrentRun

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Propagate: a failed completion must not advance the schedule on unpersisted state — the row
            // stays recoverable (the transaction rolled back) and is re-run.
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual async Task<ScheduleCasResult> RecordRecurringRunForExclusionRetry(
        Guid taskId, double executionTimeMs, DateTimeOffset retainedCursorUtc, AuditLevel auditLevel,
        bool markCompleted, string runtimeInfo, int? expectedScheduleVersion = null)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var task = await dbContext.QueuedTasks
                                  .Where(x => x.Id == taskId &&
                                              (!expectedScheduleVersion.HasValue ||
                                               x.ScheduleVersion == expectedScheduleVersion.Value))
                                  .FirstOrDefaultAsync()
                                  .ConfigureAwait(false);

        if (task == null)
            return ScheduleCasResult.VersionMismatch;

        var now = UtcNowNormalized;

        if (markCompleted)
        {
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
        }
        else if (AuditPolicy.ShouldCreateRunsAudit(auditLevel, task.Status, task.Exception))
        {
            task.RunsAudits.Add(new RunsAudit
            {
                QueuedTaskId    = taskId,
                ExecutedAt      = now,
                ExecutionTimeMs = executionTimeMs,
                Status          = task.Status,
                Exception       = task.Exception
            });
        }

        task.ExecutionTimeMs = executionTimeMs;
        task.NextRunUtc      = retainedCursorUtc;
        task.CurrentRunCount = task.CurrentRunCount >= int.MaxValue
                                   ? int.MaxValue
                                   : (task.CurrentRunCount ?? 0) + 1;
        task.RuntimeInfo     = runtimeInfo;

        await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        return ScheduleCasResult.Applied;
    }

    /// <summary>
    /// Finalizes a recurring series that ended on a SKIPPED occurrence (next slot past RunUntil): sets
    /// Completed AND clears <see cref="QueuedTask.NextRunUtc"/> in ONE tracked SaveChanges, WITHOUT
    /// advancing the run counter and WITHOUT a runs-audit row — the skipped occurrence never executed.
    /// Clearing NextRunUtc is what keeps the terminal row out of <see cref="QueuedTask.IsRecoverable"/>.
    /// </summary>
    public virtual async Task SetRecurringSeriesCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        logger.FinalizingRecurringSeries(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync()
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.TaskNotFoundForRecurringSeriesCompletion(taskId);
                return;
            }

            var now = UtcNowNormalized;

            // Status audit (when the level audits a Completed) + the status transition + the NextRunUtc
            // clear flush in ONE SaveChanges. No runs audit and no counter advance: the occurrence was
            // skipped, not executed.
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
            // Propagate: a failed finalize must not advance the schedule on unpersisted state.
            logger.RecurringSeriesFinalizationFailed(e, taskId);
            throw;
        }
    }

    /// <summary>
    /// Terminally poisons a recurring row during recovery: sets Failed AND clears
    /// <see cref="QueuedTask.NextRunUtc"/> in ONE tracked SaveChanges, so the row stops satisfying
    /// <see cref="QueuedTask.IsRecoverable"/> and is never resurrected. Errors are logged, not rethrown
    /// (like <see cref="SetStatus"/>): a failed poison must not break the recovery of sibling tasks — the
    /// row stays recoverable and is retried at the next restart, so returning normally is not a success
    /// signal.
    /// </summary>
    public virtual async Task SetRecurringTaskPoisoned(Guid taskId, Exception exception, AuditLevel auditLevel,
                                                       CancellationToken ct = default)
    {
        logger.PoisoningRecurringTask(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        try
        {
            var task = await dbContext.QueuedTasks
                                      .Where(x => x.Id == taskId)
                                      .FirstOrDefaultAsync(ct)
                                      .ConfigureAwait(false);

            if (task == null)
            {
                logger.TaskNotFoundForRecurringPoison(taskId);
                return;
            }

            var now = UtcNowNormalized;
            var ex  = exception.ToDetailedString();

            // Status audit (Failed always audits) + the status transition + the NextRunUtc clear flush in ONE
            // SaveChanges. Clearing NextRunUtc atomically with Failed is what keeps the poisoned recurring row
            // out of IsRecoverable, else recovery revives and re-poisons it at every restart.
            if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Failed, exception))
            {
                dbContext.StatusAudit.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Failed,
                    Exception    = ex
                });
            }

            task.Status           = QueuedTaskStatus.Failed;
            task.Exception        = ex;
            task.LastExecutionUtc = now;
            task.NextRunUtc       = null;

            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Swallow (like SetStatus): a failed poison must not abort the recovery of other tasks. The row
            // stays recoverable and is retried at the next restart.
            logger.RecurringTaskPoisonFailed(e, taskId);
        }
    }

    public virtual async Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await dbContext.QueuedTasks
                              .AsNoTracking()
                              .Where(t => t.TaskKey == taskKey)
                              .FirstOrDefaultAsync(ct)
                              .ConfigureAwait(false);
    }

    public virtual async Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        // Same reason as Persist: the other public write, and it rewrites the cursor itself (#37).
        task.NormalizeTimestampsToUtc();

        logger.UpdatingTask(task.Id, task.TaskKey);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
                                                                             .SetProperty(t => t.RuntimeInfo,
                                                                                 task.RuntimeInfo)
                                                                             .SetProperty(t => t.TaskKey, task.TaskKey),
                                                  ct)
                                              .ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                logger.TaskNotFoundForUpdate(task.Id);
            }
        }
        catch (Exception e)
        {
            logger.TaskUpdateFailed(e, task.Id);
            throw;
        }
    }

    public virtual async Task Remove(Guid taskId, CancellationToken ct = default)
    {
        logger.RemovingTask(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        try
        {
            // Occurrences go first and in the SAME transaction: the self-referencing foreign key is
            // Restrict, so a schedule with children cannot be deleted on its own, and deleting the children
            // in a separate transaction would leave them orphaned if the second delete never ran.
            await using var transaction = await BeginTransactionOrNullAsync(dbContext, ct).ConfigureAwait(false);

            await dbContext.QueuedTasks
                           .Where(t => t.ParentTaskId == taskId)
                           .ExecuteDeleteAsync(ct)
                           .ConfigureAwait(false);

            var rowsAffected = await dbContext.QueuedTasks
                                              .Where(t => t.Id == taskId)
                                              .ExecuteDeleteAsync(ct)
                                              .ConfigureAwait(false);

            if (transaction != null)
                await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (rowsAffected == 0)
            {
                logger.TaskNotFoundForRemoval(taskId);
            }
        }
        catch (Exception e)
        {
            logger.TaskRemoveFailed(e, taskId);
            throw;
        }
    }

    // ---- Durable occurrences and schedule versioning ----------------------------------------------
    // Each operation is ONE conditional UPDATE (the compare-and-swap) plus, where an audit or an insert
    // belongs to it, a single transaction around the pair. The condition lives in the WHERE clause, never in
    // a preceding SELECT: a read-then-write would let two writers both pass the check.

    /// <inheritdoc />
    public virtual bool SupportsDurableOccurrences => IsRelationalProvider;

    /// <inheritdoc />
    public virtual bool SupportsScheduleVersioning => IsRelationalProvider;

    /// <summary>
    /// Whether the configured EF Core provider is a relational one, resolved once and cached.
    /// </summary>
    /// <remarks>
    /// Both capabilities answer from here: every operation they advertise opens with
    /// <see cref="RequireRelational"/>, and this class still supports EF Core InMemory through the
    /// client-side fallbacks in <c>TrySetQueuedIfRecoverable</c> and <c>SetStatus</c>. Answering an
    /// unconditional <c>true</c> there would turn a clean refusal at dispatch into a
    /// <see cref="NotSupportedException"/> later, at materialization.
    /// </remarks>
    private bool IsRelationalProvider => _isRelationalProvider.Value;

    private readonly Lazy<bool> _isRelationalProvider = new(() =>
    {
        var dbContext = contextFactory.CreateDbContext();
        try
        {
            return dbContext is DbContext efContext && efContext.Database.IsRelational();
        }
        finally
        {
            (dbContext as IDisposable)?.Dispose();
        }
    });

    /// <inheritdoc />
    public virtual async Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var newCursor = newCursorUtc?.ToUniversalTime();
        var now       = UtcNowNormalized;

        // The row this base INSERTS is the caller's entity, so the contract shape has to be stamped on it
        // explicitly — the procedures and the writable CTE spell the same shape out in their column list.
        occurrence.ApplyOccurrenceContract(parentId, expectedScheduleVersion);
        occurrence.NormalizeTimestampsToUtc();

        // A schedule with no cursor is over — finalized, or poisoned — so a NULL expected cursor can never
        // describe a live one. Left to EF the compare-and-swap becomes "NextRunUtc IS NULL" and matches
        // exactly those rows, inserting an occurrence on a finished series and giving it a cursor back.
        if (expectedCursorUtc?.ToUniversalTime() is not { } expectedCursor)
            return await ClassifyMaterializationLossAsync(dbContext, parentId, expectedScheduleVersion, ct)
                .ConfigureAwait(false);

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // A null new cursor ENDS the series, and it must end in the same commit that creates its last
        // occurrence: a separate finalization would leave a window where a crash resurrects a finished series.
        var advanced = newCursor == null
            ? await CursorCas(dbContext, parentId, expectedScheduleVersion, expectedCursor)
                    .ExecuteUpdateAsync(s => s
                                             .SetProperty(t => t.NextRunUtc, (DateTimeOffset?)null)
                                             .SetProperty(t => t.Status, QueuedTaskStatus.Completed)
                                             .SetProperty(t => t.Exception, (string?)null)
                                             .SetProperty(t => t.LastExecutionUtc, now)
                                             .SetProperty(t => t.CurrentRunCount, t => t.CurrentRunCount >= int.MaxValue ? int.MaxValue : (t.CurrentRunCount ?? 0) + 1), ct)
                    .ConfigureAwait(false)
            : await CursorCas(dbContext, parentId, expectedScheduleVersion, expectedCursor)
                    .ExecuteUpdateAsync(s => s
                                             .SetProperty(t => t.NextRunUtc, newCursor)
                                             .SetProperty(t => t.CurrentRunCount, t => t.CurrentRunCount >= int.MaxValue ? int.MaxValue : (t.CurrentRunCount ?? 0) + 1), ct)
                    .ConfigureAwait(false);

        if (advanced == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return await ClassifyMaterializationLossAsync(dbContext, parentId, expectedScheduleVersion, ct)
                .ConfigureAwait(false);
        }

        if (newCursor == null && AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null))
        {
            dbContext.StatusAudit.Add(new StatusAudit
            {
                QueuedTaskId = parentId,
                UpdatedAtUtc = now,
                NewStatus    = QueuedTaskStatus.Completed,
                Exception    = null
            });
        }

        dbContext.QueuedTasks.Add(occurrence);

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException e) when (IsOccurrenceUniqueViolation(e))
        {
            // Someone materialized this exact slot already: the whole transaction, cursor advance included,
            // rolls back, so the caller can simply re-read and decide again.
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return OccurrenceMaterializationOutcome.AlreadyExists;
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return OccurrenceMaterializationOutcome.Created;
    }

    /// <inheritdoc />
    public virtual async Task<bool> TrySetRecurringSeriesCompleted(
        Guid taskId, DateTimeOffset? expectedCursorUtc, QueuedTaskStatus expectedStatus,
        int expectedScheduleVersion, double executionTimeMs, AuditLevel auditLevel, CancellationToken ct = default)
    {
        logger.FinalizingRecurringSeries(taskId);

        // A schedule with no cursor is already over, so no live series can be described by a null expectation.
        // Left to EF the comparison becomes "NextRunUtc IS NULL" and matches exactly the rows that are
        // already finalized or poisoned — the same guard MaterializeOccurrence applies.
        if (expectedCursorUtc is not { } expected)
            return false;

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var cursor = expected.ToUniversalTime();
        var now    = UtcNowNormalized;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var rows = await dbContext.QueuedTasks
                                  .Where(t => t.Id == taskId
                                              && t.Status == expectedStatus
                                              && t.NextRunUtc == cursor
                                              && t.ScheduleVersion == expectedScheduleVersion)
                                  .ExecuteUpdateAsync(s => s
                                                           .SetProperty(t => t.Status, QueuedTaskStatus.Completed)
                                                           .SetProperty(t => t.Exception, (string?)null)
                                                           .SetProperty(t => t.LastExecutionUtc, now)
                                                           .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                           .SetProperty(t => t.NextRunUtc, (DateTimeOffset?)null), ct)
                                  .ConfigureAwait(false);

        if (rows == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        // No runs audit and no counter advance: the remaining slots were never executed.
        await CommitWithStatusAuditAsync(dbContext, transaction, taskId, QueuedTaskStatus.Completed, null,
            auditLevel, now, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public virtual async Task<bool> TryAdvanceScheduleCursor(Guid parentId, int expectedScheduleVersion,
                                                             DateTimeOffset expectedCursorUtc,
                                                             DateTimeOffset newCursorUtc,
                                                             CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        RequireRelational(dbContext);

        var newCursor = newCursorUtc.ToUniversalTime();

        // One conditional UPDATE, no transaction and no audit: skipping a slot changes nothing but where the
        // schedule is pointing. The compare-and-swap is the whole guard — a materialization that advanced the
        // cursor first makes this a no-op, and the caller re-reads.
        var rows = await CursorCas(dbContext, parentId, expectedScheduleVersion, expectedCursorUtc.ToUniversalTime())
                         .ExecuteUpdateAsync(s => s.SetProperty(t => t.NextRunUtc, newCursor), ct)
                         .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public virtual async Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var now = UtcNowNormalized;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var parentCancelled = await dbContext.QueuedTasks
                                             .Where(t => t.Id == parentId)
                                             .ExecuteUpdateAsync(
                                                 s => s.SetProperty(t => t.Status, QueuedTaskStatus.Cancelled), ct)
                                             .ConfigureAwait(false);

        // Occurrences already InProgress own a live delivery and are left to finish on their own; every other
        // non-terminal one is cancelled, so a materializer racing this cancel can only see an inactive
        // schedule and never adds one more. ServiceStopped belongs in that set: startup recovery puts a
        // ServiceStopped row back in a queue, so leaving it out would run an occurrence of a schedule the user
        // cancelled, one restart later. The cancelled set is the exact complement of the requeued one.
        var candidateChildren = await dbContext.QueuedTasks
                                               .Where(t => t.ParentTaskId == parentId
                                                           && (t.Status == QueuedTaskStatus.WaitingQueue
                                                               || t.Status == QueuedTaskStatus.Queued
                                                               || t.Status == QueuedTaskStatus.Pending
                                                               || t.Status == QueuedTaskStatus.ServiceStopped))
                                               .Select(t => t.Id)
                                               .ToListAsync(ct)
                                               .ConfigureAwait(false);

        if (candidateChildren.Count > 0)
        {
            // The status predicate is repeated on the UPDATE, not just on the id lookup: an occurrence that
            // starts executing between the two must still be left alone.
            await dbContext.QueuedTasks
                           .Where(t => candidateChildren.Contains(t.Id)
                                       && (t.Status == QueuedTaskStatus.WaitingQueue
                                           || t.Status == QueuedTaskStatus.Queued
                                           || t.Status == QueuedTaskStatus.Pending
                                           || t.Status == QueuedTaskStatus.ServiceStopped))
                           .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, QueuedTaskStatus.Cancelled), ct)
                           .ConfigureAwait(false);
        }

        if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Cancelled, null))
        {
            // Audit what the UPDATE really changed, not what the lookup found: ExecuteUpdate reports a count
            // and no ids, and an occurrence that reached InProgress between the two statements is skipped by
            // the UPDATE and must not get a Cancelled audit row for a status it never took. None of the
            // candidates was Cancelled when the lookup ran, so "Cancelled now" means this transaction did it.
            List<Guid> cancelledChildren = [];
            if (candidateChildren.Count > 0)
            {
                cancelledChildren = await dbContext.QueuedTasks
                                                   .Where(t => candidateChildren.Contains(t.Id)
                                                               && t.Status == QueuedTaskStatus.Cancelled)
                                                   .Select(t => t.Id)
                                                   .ToListAsync(ct)
                                                   .ConfigureAwait(false);
            }

            // Only audit the schedule row if it actually exists: an audit for a missing row would violate
            // the foreign key and take the whole transaction down.
            var audited = parentCancelled > 0 ? cancelledChildren.Append(parentId) : cancelledChildren;

            foreach (var id in audited)
            {
                dbContext.StatusAudit.Add(new StatusAudit
                {
                    QueuedTaskId = id,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Cancelled,
                    Exception    = null
                });
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<bool> RequeueTerminal(Guid taskId, AuditLevel auditLevel,
                                                    CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var now = UtcNowNormalized;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Identity, history and audit trail survive: only the status, the recorded error and the failure
        // counter are reset. The counter goes with the error for the same reason it exists — a row requeued
        // still carrying the attempts that poisoned it is poisoned again by its first failure, with none of
        // the retries the ceiling grants.
        var rows = await dbContext.QueuedTasks
                                  .Where(t => t.Id == taskId
                                              && (t.Status == QueuedTaskStatus.Failed
                                                  || t.Status == QueuedTaskStatus.Cancelled))
                                  .ExecuteUpdateAsync(s => s
                                                           .SetProperty(t => t.Status, QueuedTaskStatus.Queued)
                                                           .SetProperty(t => t.Exception, (string?)null)
                                                           .SetProperty(t => t.RecoveryDispatchFailureCount,
                                                               (int?)null), ct)
                                  .ConfigureAwait(false);

        if (rows == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await CommitWithStatusAuditAsync(dbContext, transaction, taskId, QueuedTaskStatus.Queued, null,
            auditLevel, now, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public virtual async Task<bool> TryRequeueStaleOccurrence(Guid childId, QueuedTaskStatus expectedStatus,
                                                              AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var now = UtcNowNormalized;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The expected status IS the claim: a cancel, or a delivery that picked the occurrence up between
        // the caller's read and this write, changes it and this caller correctly loses.
        var rows = await dbContext.QueuedTasks
                                  .Where(t => t.Id == childId && t.Status == expectedStatus)
                                  .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, QueuedTaskStatus.Queued), ct)
                                  .ConfigureAwait(false);

        if (rows == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await CommitWithStatusAuditAsync(dbContext, transaction, childId, QueuedTaskStatus.Queued, null,
            auditLevel, now, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public virtual async Task<bool> UpdateSchedule(Guid taskId, int expectedScheduleVersion,
                                                   DateTimeOffset? expectedCursorUtc, string recurringTaskJson,
                                                   string? recurringInfo, DateTimeOffset? nextRunUtc, int? maxRuns,
                                                   DateTimeOffset? runUntil, string? runtimeInfo,
                                                   CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        RequireRelational(dbContext);

        var nextRun = nextRunUtc?.ToUniversalTime();
        var until   = runUntil?.ToUniversalTime();
        var cursor  = expectedCursorUtc?.ToUniversalTime();

        // Cancelled is refused apart from the version and the cursor because it is the one state NEITHER of
        // them answers for: a cancel writes the status and leaves both exactly as they were, so a reschedule
        // that read the row first matches on both and writes a live definition over a series an operator has
        // ended. Every other status stays a legitimate target — a schedule that is running is rescheduled.
        var candidates = dbContext.QueuedTasks
                                  .Where(t => t.Id == taskId
                                              && t.ScheduleVersion == expectedScheduleVersion
                                              && t.Status != QueuedTaskStatus.Cancelled);

        // Two predicates rather than one over a nullable parameter: a null expectation means "the series has
        // ended", which in SQL is IS NULL and never an equality — comparing against a null parameter matches
        // no row at all and would turn a legitimate reschedule of a finished series into a lost race.
        candidates = cursor is { } expected
                         ? candidates.Where(t => t.NextRunUtc == expected)
                         : candidates.Where(t => t.NextRunUtc == null);

        var rows = await candidates
                         .ExecuteUpdateAsync(s => s
                                                  .SetProperty(t => t.RecurringTask, recurringTaskJson)
                                                  .SetProperty(t => t.RecurringInfo, recurringInfo)
                                                  .SetProperty(t => t.NextRunUtc, nextRun)
                                                  .SetProperty(t => t.MaxRuns, maxRuns)
                                                  .SetProperty(t => t.RunUntil, until)
                                                  .SetProperty(t => t.RuntimeInfo, runtimeInfo)
                                                  .SetProperty(t => t.ScheduleVersion,
                                                      expectedScheduleVersion + 1), ct)
                         .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public virtual async Task<bool> TryReviveCancelledSchedule(Guid taskId, int expectedScheduleVersion,
                                                               AuditLevel auditLevel,
                                                               CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var now = UtcNowNormalized;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The version moves in the same statement as the status: a revival replaces the definition of a row
        // that keeps its id, so the deliveries of the series the cancel ended are addressed by the very id the
        // new registration uses, and the version is the only thing that tells them apart.
        var rows = await dbContext.QueuedTasks
                                  .Where(t => t.Id == taskId
                                              && t.Status == QueuedTaskStatus.Cancelled
                                              && t.ScheduleVersion == expectedScheduleVersion)
                                  .ExecuteUpdateAsync(s => s
                                                           .SetProperty(t => t.Status,
                                                               QueuedTaskStatus.WaitingQueue)
                                                           .SetProperty(t => t.Exception, (string?)null)
                                                           .SetProperty(t => t.ScheduleVersion,
                                                               expectedScheduleVersion + 1), ct)
                                  .ConfigureAwait(false);

        if (rows == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            return false;
        }

        await CommitWithStatusAuditAsync(dbContext, transaction, taskId, QueuedTaskStatus.WaitingQueue, null,
            auditLevel, now, ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public virtual async Task<bool> TrySetTerminalOutcome(Guid taskId, QueuedTaskStatus status,
                                                          Exception? exception, int expectedScheduleVersion,
                                                          AuditLevel auditLevel, CancellationToken ct = default)
    {
        logger.SettingTaskStatus(taskId, status);

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var now              = UtcNowNormalized;
        var ex               = exception.ToDetailedString();
        var lastExecutionUtc = LastExecutionUtcFor(status, now);

        var candidates = dbContext.QueuedTasks
                                  .Where(t => t.Id == taskId && t.ScheduleVersion == expectedScheduleVersion);

        // A cancellation over a cancellation is the ending of the very run the cancel could not stop, and it
        // says what the row already says. Anything else would erase the operator's decision.
        if (status != QueuedTaskStatus.Cancelled)
            candidates = candidates.Where(t => t.Status != QueuedTaskStatus.Cancelled);

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await candidates
                             .ExecuteUpdateAsync(s => s
                                                      .SetProperty(t => t.Status, status)
                                                      .SetProperty(t => t.Exception, ex)
                                                      .SetProperty(t => t.LastExecutionUtc,
                                                          t => lastExecutionUtc ?? t.LastExecutionUtc), ct)
                             .ConfigureAwait(false);

            if (rows == 0)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                return false;
            }

            // The audit says what the UPDATE did, not what the caller asked for: a refused write leaves no
            // trace of a status the row never took.
            await CommitWithStatusAuditAsync(dbContext, transaction, taskId, status, exception, auditLevel, now, ct)
                .ConfigureAwait(false);

            return true;
        }
        catch (Exception e)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.StatusUpdateFailed(e, status, taskId);
            return false;
        }
    }

    /// <inheritdoc />
    public virtual async Task<bool> TryHaltSchedule(Guid parentId, int expectedScheduleVersion,
                                                    DateTimeOffset? expectedCursorUtc, QueuedTaskStatus expectedStatus,
                                                    string runtimeInfo, CancellationToken ct = default)
    {
        // Same refusal as the finalization above: a null expectation would translate to "NextRunUtc IS NULL"
        // and halt a series that has already ended, instead of losing the compare-and-swap.
        if (expectedCursorUtc is not { } expected)
            return false;

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        RequireRelational(dbContext);

        var cursor = expected.ToUniversalTime();

        // Full compare-and-swap: a halt decided against a cursor another writer has since advanced describes
        // a state that no longer exists, and writing it would freeze a schedule that is in fact progressing.
        var rows = await dbContext.QueuedTasks
                                  .Where(t => t.Id == parentId
                                              && t.ScheduleVersion == expectedScheduleVersion
                                              && t.NextRunUtc == cursor
                                              && t.Status == expectedStatus)
                                  .ExecuteUpdateAsync(s => s.SetProperty(t => t.RuntimeInfo, runtimeInfo), ct)
                                  .ConfigureAwait(false);

        return rows > 0;
    }

    /// <inheritdoc />
    public virtual async Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs,
                                                                  DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                  int expectedScheduleVersion)
    {
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var next = nextRun?.ToUniversalTime();

        try
        {
            await using var transaction = await efContext.Database.BeginTransactionAsync().ConfigureAwait(false);

            // The ErrorsOnly runs-audit gate depends on the ROW's own status/exception, so they are read
            // inside the transaction, before the update leaves them untouched.
            var audited = await dbContext.QueuedTasks
                                         .AsNoTracking()
                                         .Where(t => t.Id == taskId)
                                         .Select(t => new { t.Status, t.Exception })
                                         .FirstOrDefaultAsync()
                                         .ConfigureAwait(false);

            var rows = await dbContext.QueuedTasks
                                      .Where(t => t.Id == taskId && t.ScheduleVersion == expectedScheduleVersion)
                                      .ExecuteUpdateAsync(s => s
                                                               .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                               .SetProperty(t => t.NextRunUtc, next)
                                                               .SetProperty(t => t.CurrentRunCount, t => t.CurrentRunCount >= int.MaxValue ? int.MaxValue : (t.CurrentRunCount ?? 0) + 1))
                                      .ConfigureAwait(false);

            if (rows == 0)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                return ScheduleCasResult.VersionMismatch;
            }

            if (audited != null && AuditPolicy.ShouldCreateRunsAudit(auditLevel, audited.Status, audited.Exception))
            {
                dbContext.RunsAudit.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
                    ExecutedAt      = UtcNowNormalized,
                    ExecutionTimeMs = executionTimeMs,
                    Status          = audited.Status,
                    Exception       = audited.Exception
                });

                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await transaction.CommitAsync().ConfigureAwait(false);
            return ScheduleCasResult.Applied;
        }
        catch (Exception e)
        {
            // Propagate, exactly like the unversioned overload — a failed counter persist must not let the
            // scheduler advance on unpersisted state.
            logger.CurrentRunUpdateFailed(e, taskId);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual async Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs,
                                                                      DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                      int expectedScheduleVersion)
    {
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await contextFactory.CreateDbContextAsync().ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        var next = nextRun?.ToUniversalTime();
        var now  = UtcNowNormalized;

        try
        {
            await using var transaction = await efContext.Database.BeginTransactionAsync().ConfigureAwait(false);

            var rows = await dbContext.QueuedTasks
                                      .Where(t => t.Id == taskId && t.ScheduleVersion == expectedScheduleVersion)
                                      .ExecuteUpdateAsync(s => s
                                                               .SetProperty(t => t.Status, QueuedTaskStatus.Completed)
                                                               .SetProperty(t => t.Exception, (string?)null)
                                                               .SetProperty(t => t.LastExecutionUtc, now)
                                                               .SetProperty(t => t.ExecutionTimeMs, executionTimeMs)
                                                               .SetProperty(t => t.NextRunUtc, next)
                                                               .SetProperty(t => t.CurrentRunCount, t => t.CurrentRunCount >= int.MaxValue ? int.MaxValue : (t.CurrentRunCount ?? 0) + 1))
                                      .ConfigureAwait(false);

            if (rows == 0)
            {
                await transaction.RollbackAsync().ConfigureAwait(false);
                return ScheduleCasResult.VersionMismatch;
            }

            // The audited status and exception are the CONSTANTS Completed/null here, so both gates depend
            // on the level alone — no pre-update read is needed.
            var stageStatusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
            var stageRunsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

            if (stageStatusAudit)
            {
                dbContext.StatusAudit.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Completed,
                    Exception    = null
                });
            }

            if (stageRunsAudit)
            {
                dbContext.RunsAudit.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
                    ExecutedAt      = now,
                    ExecutionTimeMs = executionTimeMs,
                    Status          = QueuedTaskStatus.Completed,
                    Exception       = null
                });
            }

            if (stageStatusAudit || stageRunsAudit)
                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await transaction.CommitAsync().ConfigureAwait(false);
            return ScheduleCasResult.Applied;
        }
        catch (Exception e)
        {
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }
    }

    /// <inheritdoc />
    public virtual async Task<QueuedTask[]> GetOccurrences(Guid parentId, bool nonTerminalOnly = false,
                                                           CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.QueuedTasks.AsNoTracking().Where(t => t.ParentTaskId == parentId);

        if (nonTerminalOnly)
            query = query.Where(NonTerminalOccurrence);

        return await query.ToArrayAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<Guid[]> GetOccurrenceIds(Guid parentId, bool nonTerminalOnly = false,
                                                       CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.QueuedTasks.AsNoTracking().Where(t => t.ParentTaskId == parentId);

        if (nonTerminalOnly)
            query = query.Where(NonTerminalOccurrence);

        return await query.Select(t => t.Id).ToArrayAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<OccurrencePage> GetOccurrencesPage(Guid parentId, bool nonTerminalOnly, int skip,
                                                                 int take, CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.QueuedTasks.AsNoTracking().Where(t => t.ParentTaskId == parentId);

        if (nonTerminalOnly)
            query = query.Where(NonTerminalOccurrence);

        // The count is asked of the database too: a page exists so the series is never materialized, and
        // counting it in memory would materialize it anyway.
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        // A page of nothing is answered without a second round trip, and never as a FETCH clause: a
        // zero-row FETCH is a syntax error on some engines, not an empty result.
        if (take <= 0)
            return new OccurrencePage([], total);

        var rows = await query
                         .OrderByDescending(t => t.ScheduledExecutionUtc)
                         .Skip(skip)
                         .Take(take)
                         .ToArrayAsync(ct)
                         .ConfigureAwait(false);

        return new OccurrencePage(rows, total);
    }

    /// <inheritdoc />
    public virtual async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastRunStarts(
        IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return ReadOnlyDictionary<Guid, DateTimeOffset>.Empty;

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var ids = taskIds as Guid[] ?? taskIds.ToArray();

        // One correlated subquery per row over the (QueuedTaskId) index of the audit table, ordered by the
        // audit IDENTITY and never by its timestamp: SQLite refuses a DateTimeOffset in an ORDER BY, and the
        // audits of one row are inserted in transition order, so the newest id IS the newest transition.
        var starts = await dbContext.QueuedTasks
                                    .AsNoTracking()
                                    .Where(t => ids.Contains(t.Id))
                                    .Select(t => new
                                    {
                                        t.Id,
                                        StartedAtUtc = t.StatusAudits
                                                        .Where(a => a.NewStatus == QueuedTaskStatus.InProgress)
                                                        .OrderByDescending(a => a.Id)
                                                        .Select(a => (DateTimeOffset?)a.UpdatedAtUtc)
                                                        .FirstOrDefault()
                                    })
                                    .ToArrayAsync(ct)
                                    .ConfigureAwait(false);

        return starts.Where(s => s.StartedAtUtc.HasValue)
                     .ToDictionary(s => s.Id, s => s.StartedAtUtc!.Value);
    }

    /// <inheritdoc />
    public virtual async Task<AuditPage<StatusAudit>> GetStatusAuditsPage(Guid taskId, int skip, int take,
                                                                          CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.StatusAudit.AsNoTracking().Where(a => a.QueuedTaskId == taskId);

        return await PageAsync(query, a => a.Id, skip, take, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async Task<AuditPage<RunsAudit>> GetRunsAuditsPage(Guid taskId, int skip, int take,
                                                                      CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.RunsAudit.AsNoTracking().Where(a => a.QueuedTaskId == taskId);

        return await PageAsync(query, a => a.Id, skip, take, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The paging both audit trails share: the count and the slice are asked of the DATABASE, because a page
    /// exists so the whole trail is never materialized. Ordered on the audit IDENTITY, never on its
    /// timestamp: SQLite refuses a <see cref="DateTimeOffset"/> in an <c>ORDER BY</c>, and the audits of one
    /// row are inserted in transition order, so the newest id IS the newest entry — and the order is total,
    /// so a page boundary can never repeat or drop an entry.
    /// </summary>
    private static async Task<AuditPage<TAudit>> PageAsync<TAudit>(IQueryable<TAudit> query,
                                                                   Expression<Func<TAudit, long>> auditId,
                                                                   int skip, int take, CancellationToken ct)
        where TAudit : class
    {
        var total = await query.CountAsync(ct).ConfigureAwait(false);

        // A page of nothing is answered without a second round trip, and never as a FETCH clause: a zero-row
        // FETCH is a syntax error on some engines, not an empty result.
        if (take <= 0)
            return new AuditPage<TAudit>([], total);

        var audits = await query
                           .OrderByDescending(auditId)
                           .Skip(skip)
                           .Take(take)
                           .ToArrayAsync(ct)
                           .ConfigureAwait(false);

        return new AuditPage<TAudit>(audits, total);
    }

    /// <summary>Server-side mirror of <see cref="QueuedTask.IsNonTerminalStatus"/>.</summary>
    private static readonly Expression<Func<QueuedTask, bool>> NonTerminalOccurrence =
        t => t.Status == QueuedTaskStatus.WaitingQueue
             || t.Status == QueuedTaskStatus.Queued
             || t.Status == QueuedTaskStatus.Pending
             || t.Status == QueuedTaskStatus.InProgress
             || t.Status == QueuedTaskStatus.ServiceStopped;

    /// <summary>
    /// The compare-and-swap predicate every cursor advance shares. The expected cursor is deliberately NOT
    /// nullable: a null one would be translated to <c>NextRunUtc IS NULL</c> and match the finalized and
    /// poisoned rows this predicate exists to exclude, so callers decide that case before they get here.
    /// </summary>
    private static IQueryable<QueuedTask> CursorCas(ITaskStoreDbContext dbContext, Guid parentId,
                                                    int expectedScheduleVersion, DateTimeOffset expectedCursorUtc) =>
        dbContext.QueuedTasks
                 .Where(t => t.Id == parentId
                             && t.ScheduleVersion == expectedScheduleVersion
                             && t.NextRunUtc == expectedCursorUtc
                             && t.Status != QueuedTaskStatus.Cancelled);

    /// <summary>
    /// Reads back a schedule whose cursor compare-and-swap found no row, and reports WHY. Purely
    /// diagnostic for the caller's next decision — nothing was written either way.
    /// </summary>
    private static async Task<OccurrenceMaterializationOutcome> ClassifyMaterializationLossAsync(
        ITaskStoreDbContext dbContext, Guid parentId, int expectedScheduleVersion, CancellationToken ct)
    {
        var current = await dbContext.QueuedTasks
                                     .AsNoTracking()
                                     .FirstOrDefaultAsync(t => t.Id == parentId, ct)
                                     .ConfigureAwait(false);

        // Gone, cancelled, or already finalized (a finished series has no cursor left to advance).
        if (current == null || current.Status == QueuedTaskStatus.Cancelled || current.NextRunUtc == null)
            return OccurrenceMaterializationOutcome.ParentInactive;

        return current.ScheduleVersion != expectedScheduleVersion
                   ? OccurrenceMaterializationOutcome.VersionMismatch
                   : OccurrenceMaterializationOutcome.CursorMoved;
    }

    /// <summary>
    /// Stages the status audit of a transition that already succeeded and commits the pair, so a refused
    /// transition leaves no audit trace and a failed audit rolls the transition back.
    /// </summary>
    private static async Task CommitWithStatusAuditAsync(
        ITaskStoreDbContext dbContext, IDbContextTransaction transaction, Guid taskId, QueuedTaskStatus status,
        Exception? exception, AuditLevel auditLevel, DateTimeOffset now, CancellationToken ct)
    {
        if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception))
        {
            dbContext.StatusAudit.Add(new StatusAudit
            {
                QueuedTaskId = taskId,
                UpdatedAtUtc = now,
                NewStatus    = status,
                Exception    = exception.ToDetailedString()
            });

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Recognises the unique-index violation of (parent, slot) — by CONSTRAINT NAME, or by the columns SQLite
    /// names instead of the index. Never by a generic duplicate-key code, which would also swallow a
    /// <c>TaskKey</c> collision and report it as a slot that already exists.
    /// </summary>
    protected virtual bool IsOccurrenceUniqueViolation(DbUpdateException exception)
    {
        for (Exception? e = exception; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("UX_QueuedTasks_Occurrence", StringComparison.OrdinalIgnoreCase))
                return true;

            // SQLite reports "UNIQUE constraint failed: QueuedTasks.ParentTaskId, ..." with no index name.
            if (e.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase)
                && e.Message.Contains(nameof(QueuedTask.ParentTaskId), StringComparison.Ordinal)
                && e.Message.Contains(nameof(QueuedTask.ScheduledExecutionUtc), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the context as a relational one, or refuses. Every caller is a conditional UPDATE inside a
    /// transaction; a non-relational EF provider can express neither, and emulating one with a
    /// read-then-write is exactly the race they exist to close.
    /// </summary>
    protected static DbContext RequireRelational(ITaskStoreDbContext dbContext)
    {
        if (dbContext is DbContext efContext && efContext.Database.IsRelational())
            return efContext;

        throw new NotSupportedException(
            "Compare-and-swap storage operations (durable occurrences, schedule versioning, the recovery " +
            "transition) require a relational EF Core provider.");
    }

    /// <summary>
    /// Starts a transaction on a relational provider, or returns null on one that has no transactions (the
    /// caller's writes are then applied as-is, which is all such a provider can offer).
    /// </summary>
    private static async Task<IDbContextTransaction?> BeginTransactionOrNullAsync(ITaskStoreDbContext dbContext,
                                                                                  CancellationToken ct) =>
        dbContext is DbContext efContext && efContext.Database.IsRelational()
            ? await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;

    /// <inheritdoc />
    public async Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                             CancellationToken cancellationToken)
    {
        if (logs.Count == 0)
            return;

        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        await dbContext.TaskExecutionLogs.AddRangeAsync(logs, cancellationToken).ConfigureAwait(false);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(
        Guid taskId, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var query = GetExecutionLogsQuery(dbContext, taskId);
        return await query.ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(
        Guid taskId, int skip, int take, CancellationToken cancellationToken)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

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
    // The base implementations are the server-side, set-based ones. SQLite cannot translate DateTimeOffset
    // ordering comparisons, so SqliteTaskStorage overrides every method below with a client-side equivalent.

    /// <summary>
    /// Deletes StatusAudit rows older than the cutoffs (errors keep <paramref name="errorCutoff"/>,
    /// successes keep <paramref name="successCutoff"/>). Batched server-side delete (bounded by CleanupBatchSize to avoid lock escalation).
    /// </summary>
    public virtual async Task<int> CleanupStatusAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                       CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
    /// matches the read path's <c>OrderBy(Id)</c> instead of depending on the query plan. The SQLite override
    /// sorts <c>Id</c> in memory while this base sorts it in the database, so on an exact
    /// <c>(TimestampUtc, SequenceNumber)</c> tie the two may keep different rows; the kept count is identical.
    /// </remarks>
    public virtual async Task<int> CleanupExecutionLogsByCount(int maxPerTask, CancellationToken ct = default)
    {
        // <= 0 is disabled: keeping zero logs would let Skip(0) delete every row of every task.
        if (maxPerTask <= 0)
            return 0;

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
                .ThenByDescending(l => l.Id)   // total order on (Timestamp, Seq) ties; aligns with the read path's OrderBy(Id)
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
    /// <param name="cutoff">Age threshold: only tasks last executed (or created) strictly before it are purged.</param>
    /// <param name="preserveTasksWithLogs">
    /// When true, a task that still has any <c>TaskExecutionLog</c> row is NOT purged: the log passes run
    /// earlier in the same cycle, so a surviving log is one a log-retention window chose to keep and
    /// purging the task would cascade-delete it. Set only when a log retention is actually active.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<int> CleanupCompletedTasks(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                         CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await BatchDeleteAsync(dbContext.QueuedTasks,
            qt => qt.Status == QueuedTaskStatus.Completed
               && qt.ParentTaskId == null
               && !qt.IsRecurring
               && !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id)
               && !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id)
               && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id))
               && (qt.LastExecutionUtc ?? qt.CreatedAtUtc) < cutoff,
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes occurrence rows (those that name a schedule) in ANY terminal state — Completed, Failed or
    /// Cancelled — older than <paramref name="cutoff"/>. Batched server-side delete.
    /// </summary>
    /// <remarks>
    /// The ordinary completed-task purge only removes <c>Completed</c> rows with no audit trail left, which
    /// would let a busy schedule's failed and cancelled occurrences accumulate without bound. What drives a
    /// durable schedule forward is its cursor, never its past occurrence rows, so pruning them loses no
    /// state. Schedule rows themselves are recurring and are never touched here.
    /// </remarks>
    /// <param name="cutoff">Age threshold: only occurrences last executed (or created) strictly before it are purged.</param>
    /// <param name="preserveTasksWithLogs">
    /// Same guard as <see cref="CleanupCompletedTasks"/>: deleting a row cascades to its
    /// <c>TaskExecutionLog</c> rows, so a shorter occurrence window would destroy logs a longer log window
    /// is holding.
    /// </param>
    /// <param name="preserveStatusAudits">
    /// The same guard for the status trail: <c>FK_StatusAudit_QueuedTasks</c> cascades on delete, so purging
    /// an occurrence destroys the transitions the status-audit window kept.
    /// </param>
    /// <param name="preserveRunsAudits">
    /// The same guard for the runs trail (<c>FK_RunsAudit_QueuedTasks</c>, cascading likewise). One flag per
    /// trail, never one for both: each is set only when its OWN window is active, and since every occurrence
    /// owns the <c>StatusAudit</c> row its materialization wrote, a shared flag would purge nothing at all.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public virtual async Task<int> CleanupTerminalOccurrences(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                              bool preserveStatusAudits, bool preserveRunsAudits,
                                                              CancellationToken ct = default)
    {
        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        return await BatchDeleteAsync(dbContext.QueuedTasks,
            qt => qt.ParentTaskId != null
               && (qt.Status == QueuedTaskStatus.Completed
                   || qt.Status == QueuedTaskStatus.Failed
                   || qt.Status == QueuedTaskStatus.Cancelled)
               && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id))
               && (!preserveStatusAudits || !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id))
               && (!preserveRunsAudits || !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id))
               && (qt.LastExecutionUtc ?? qt.CreatedAtUtc) < cutoff,
            ct).ConfigureAwait(false);
    }

    // Bounded delete batch: small enough to avoid lock escalation on transactional providers (SQL Server
    // escalates around ~5000 row locks per statement). protected internal so a derived provider in another
    // assembly can bound its own select-then-delete loop the same way.
    protected internal const int CleanupBatchSize = 100;

    /// <summary>
    /// Deletes rows matching <paramref name="predicate"/> in bounded batches of <see cref="CleanupBatchSize"/>
    /// instead of a single unbounded <c>ExecuteDelete</c>, so a large backlog cannot escalate to a table lock
    /// that stalls the live audit/log inserts done by task execution.
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

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Set-based GROUP BY: never materializes the backlog. The filter is applied
        // conditionally (a "@p IS NULL OR …" predicate would defeat an index seek).
        var query = dbContext.QueuedTasks.AsNoTracking();
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

        await using var dbContext = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = dbContext.QueuedTasks.AsNoTracking();
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
