using EverTask.Abstractions;
using EverTask.Logger;

namespace EverTask.Storage.Sqlite;

/// <summary>
/// SQLite-specific task storage implementation.
/// Overrides RetrievePending() to work around SQLite's DateTimeOffset comparison limitations.
/// </summary>
// The primary-ctor 'contextFactory' is deliberately re-declared as a private field: the base captures it too,
// and using the parameter directly from a method body would capture the same value twice (CS9107).
public class SqliteTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<SqliteTaskStorage> logger)
    : EfCoreTaskStorage(contextFactory, logger)
{
    private readonly ITaskStoreDbContextFactory _contextFactory = contextFactory;

    /// <inheritdoc />
    public override Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                       CancellationToken ct = default) =>
        RetrievePending(DateTimeOffset.UtcNow, lastCreatedAt, lastId, take, ct);

    /// <summary>
    /// Retrieves pending tasks using keyset pagination, applying the temporal half of the recovery filter in
    /// memory to avoid SQLite's DateTimeOffset comparison limits.
    /// </summary>
    /// <remarks>
    /// Only the STATUS set is pushed down. The <c>MaxRuns</c> gate stays client-side too, because the second
    /// recovery category — a series to finalize — is precisely a row whose run budget is spent, and a
    /// server-side <c>MaxRuns</c> prefilter would drop exactly the rows this page must return.
    /// </remarks>
    public override async Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt,
                                                             Guid? lastId, int take, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        logger.RetrievingPendingTasks(lastCreatedAt, lastId, take);

        // Recoverable statuses: same rules as EfCoreTaskStorage.RetrievePending (see comments there).
        // Cancelled is the only status neither category can contain, so it is the one thing pruned here.
        var tasks = await dbContext.QueuedTasks
            .AsNoTracking()
            .Where(t => t.Status == QueuedTaskStatus.WaitingQueue ||
                        t.Status == QueuedTaskStatus.Queued ||
                        t.Status == QueuedTaskStatus.Pending ||
                        t.Status == QueuedTaskStatus.ServiceStopped ||
                        t.Status == QueuedTaskStatus.InProgress ||
                        (t.IsRecurring && t.NextRunUtc != null &&
                         (t.Status == QueuedTaskStatus.Completed ||
                          t.Status == QueuedTaskStatus.Failed)))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        // Evaluated client-side: rows with work left to execute, plus recurring series that only need
        // finalizing. Canonical predicates on QueuedTask — the same ones the other providers translate.
        var filtered = tasks
            .Where(t => (t.IsRecoverableForExecution(nowUtc) || t.IsRecurringSeriesToFinalize())
                        && (!lastCreatedAt.HasValue ||
                            t.CreatedAtUtc > lastCreatedAt.Value ||
                            (t.CreatedAtUtc == lastCreatedAt.Value && lastId.HasValue && t.Id.CompareTo(lastId.Value) > 0)))
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .Take(take)
            .ToArray();

        return filtered;
    }

    /// <inheritdoc />
    public override Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel,
                                                         CancellationToken ct = default) =>
        TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, taskId, auditLevel, ct);

    /// <summary>
    /// Compare-and-swaps the recoverable transition, with only the untranslatable half of the predicate
    /// decided in memory: the row is read first for the <c>RunUntil</c> term SQLite cannot translate, while
    /// everything else stays in the WHERE clause of the UPDATE next to a by-value re-assertion of the two
    /// columns that half was decided from. The write is therefore a real check-and-set: a Cancel landing
    /// between the read and the write leaves no row to update and this caller loses, instead of overwriting
    /// it with Queued. The transition and its audit commit together.
    /// </summary>
    public override async Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId,
                                                               AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             efContext = RequireRelational(dbContext);

        // Read OUTSIDE the write transaction: a SELECT inside it would hold SQLite's shared lock until the
        // commit, and the concurrent writer this compare-and-swap exists to lose against would hit
        // "database is locked" instead of simply winning.
        var observed = await dbContext.QueuedTasks
                                      .AsNoTracking()
                                      .FirstOrDefaultAsync(t => t.Id == taskId, ct)
                                      .ConfigureAwait(false);

        if (observed == null || !observed.IsRecoverableForExecution(nowUtc))
        {
            logger.TaskNoLongerRecoverable(taskId);
            return false;
        }

        // Verbatim, NOT normalized to UTC: SQLite stores a DateTimeOffset as its formatted text, so equality
        // is a byte comparison against exactly what was read back.
        var expectedNextRun  = observed.NextRunUtc;
        var expectedRunUntil = observed.RunUntil;

        await using var transaction = await efContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var transitioned = await dbContext.QueuedTasks
                                          .Where(t => t.Id == taskId)
                                          .Where(RecoverableStatusAndBudget)
                                          .Where(t => t.NextRunUtc == expectedNextRun && t.RunUntil == expectedRunUntil)
                                          .ExecuteUpdateAsync(
                                              s => s.SetProperty(t => t.Status, QueuedTaskStatus.Queued), ct)
                                          .ConfigureAwait(false);

        if (transitioned == 0)
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            logger.TaskNoLongerRecoverable(taskId);
            return false;
        }

        await CommitQueuedTransitionAsync(dbContext, transaction, taskId, auditLevel, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// The occurrence page ordered, counted and sliced by SQLite, like every other provider.
    /// </summary>
    /// <remarks>
    /// EF Core refuses to translate an <c>ORDER BY</c> over a <see cref="DateTimeOffset"/> here, and reading
    /// the whole series to slice it in memory is exactly the pathology a page exists to prevent, so the slice
    /// is written as SQL. SQLite keeps a <c>DateTimeOffset</c> as ISO-8601 text with a fixed date-and-time
    /// prefix, so its plain text ordering IS the slot ordering — the same representational equality the
    /// occurrence unique index rests on here.
    /// </remarks>
    public override async Task<OccurrencePage> GetOccurrencesPage(Guid parentId, bool nonTerminalOnly, int skip,
                                                                  int take, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Same non-terminal set as EfCoreTaskStorage.NonTerminalOccurrence, inlined because the page below
        // has to name it in SQL too — keep the two halves and the base in sync.
        var query = dbContext.QueuedTasks.AsNoTracking().Where(t => t.ParentTaskId == parentId);

        if (nonTerminalOnly)
            query = query.Where(t => t.Status == QueuedTaskStatus.WaitingQueue
                                     || t.Status == QueuedTaskStatus.Queued
                                     || t.Status == QueuedTaskStatus.Pending
                                     || t.Status == QueuedTaskStatus.InProgress
                                     || t.Status == QueuedTaskStatus.ServiceStopped);

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        if (take <= 0)
            return new OccurrencePage([], total);

        // Interpolated, never concatenated: every value below travels as a parameter EF types itself, so the
        // parent id is bound exactly as the rest of the provider binds it. The status is compared as TEXT
        // because that is how the model stores it (`HasConversion<string>()`), not as the enum's number.
        var rows = await dbContext.QueuedTasks
                                  .FromSql(
                                      $"""
                                       SELECT * FROM "QueuedTasks"
                                       WHERE "ParentTaskId" = {parentId}
                                         AND ({!nonTerminalOnly}
                                              OR "Status" IN ({nameof(QueuedTaskStatus.WaitingQueue)},
                                                              {nameof(QueuedTaskStatus.Queued)},
                                                              {nameof(QueuedTaskStatus.Pending)},
                                                              {nameof(QueuedTaskStatus.InProgress)},
                                                              {nameof(QueuedTaskStatus.ServiceStopped)}))
                                       ORDER BY "ScheduledExecutionUtc" DESC
                                       LIMIT {take} OFFSET {skip}
                                       """)
                                  .AsNoTracking()
                                  .ToArrayAsync(ct)
                                  .ConfigureAwait(false);

        return new OccurrencePage(rows, total);
    }

    /// <summary>
    /// Occurrence cleanup with the age gate expressed against SQLite's normalized UTC text representation.
    /// Each statement deletes at most one bounded page and reasserts every retention guard at deletion time.
    /// </summary>
    public override async Task<int> CleanupTerminalOccurrences(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                               bool preserveStatusAudits, bool preserveRunsAudits,
                                                               CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var cutoffUtc = cutoff.ToUniversalTime();
        var total     = 0;
        int deleted;

        do
        {
            deleted = await ((DbContext)dbContext).Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "QueuedTasks"
                WHERE "Id" IN (
                    SELECT candidate."Id"
                    FROM "QueuedTasks" AS candidate
                    WHERE candidate."ParentTaskId" IS NOT NULL
                      AND candidate."Status" IN ({nameof(QueuedTaskStatus.Completed)},
                                                  {nameof(QueuedTaskStatus.Failed)},
                                                  {nameof(QueuedTaskStatus.Cancelled)})
                      AND COALESCE(candidate."LastExecutionUtc", candidate."CreatedAtUtc") < {cutoffUtc}
                      AND ({!preserveTasksWithLogs} OR NOT EXISTS (
                          SELECT 1 FROM "TaskExecutionLogs" AS logs WHERE logs."TaskId" = candidate."Id"))
                      AND ({!preserveStatusAudits} OR NOT EXISTS (
                          SELECT 1 FROM "StatusAudit" AS status_audit
                          WHERE status_audit."QueuedTaskId" = candidate."Id"))
                      AND ({!preserveRunsAudits} OR NOT EXISTS (
                          SELECT 1 FROM "RunsAudit" AS runs_audit
                          WHERE runs_audit."QueuedTaskId" = candidate."Id"))
                    LIMIT {CleanupBatchSize}
                )
                  AND "ParentTaskId" IS NOT NULL
                  AND "Status" IN ({nameof(QueuedTaskStatus.Completed)},
                                     {nameof(QueuedTaskStatus.Failed)},
                                     {nameof(QueuedTaskStatus.Cancelled)})
                  AND COALESCE("LastExecutionUtc", "CreatedAtUtc") < {cutoffUtc}
                  AND ({!preserveTasksWithLogs} OR NOT EXISTS (
                      SELECT 1 FROM "TaskExecutionLogs" AS logs WHERE logs."TaskId" = "QueuedTasks"."Id"))
                  AND ({!preserveStatusAudits} OR NOT EXISTS (
                      SELECT 1 FROM "StatusAudit" AS status_audit
                      WHERE status_audit."QueuedTaskId" = "QueuedTasks"."Id"))
                  AND ({!preserveRunsAudits} OR NOT EXISTS (
                      SELECT 1 FROM "RunsAudit" AS runs_audit
                      WHERE runs_audit."QueuedTaskId" = "QueuedTasks"."Id"))
                """, ct).ConfigureAwait(false);

            total += deleted;
        } while (deleted == CleanupBatchSize && !ct.IsCancellationRequested);

        return total;
    }

    // ---- Retention cleanup (SQLite overrides) -----------------------------------------------------
    // SQLite cannot translate DateTimeOffset ordering comparisons (the same limitation behind the
    // RetrievePending override), so each cleanup resolves the rows to delete client-side and deletes
    // them by primary key. The optimized server-side versions live in the EfCoreTaskStorage base.

    /// <inheritdoc />
    public override async Task<int> CleanupStatusAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                        CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var ids = (await dbContext.StatusAudit
                .Select(sa => new { sa.Id, sa.UpdatedAtUtc, HasException = sa.Exception != null && sa.Exception != "" })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(c => (!c.HasException && c.UpdatedAtUtc < successCutoff)
                     || (c.HasException && c.UpdatedAtUtc < errorCutoff))
            .Select(c => c.Id)
            .ToList();

        return await DeleteByIdsAsync(dbContext.StatusAudit, ids,
            (set, batch) => set.Where(sa => batch.Contains(sa.Id)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<int> CleanupRunsAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                      CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var ids = (await dbContext.RunsAudit
                .Select(ra => new { ra.Id, ra.ExecutedAt, HasException = ra.Exception != null && ra.Exception != "" })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(c => (!c.HasException && c.ExecutedAt < successCutoff)
                     || (c.HasException && c.ExecutedAt < errorCutoff))
            .Select(c => c.Id)
            .ToList();

        return await DeleteByIdsAsync(dbContext.RunsAudit, ids,
            (set, batch) => set.Where(ra => batch.Contains(ra.Id)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<int> CleanupExecutionLogsByAge(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var ids = (await dbContext.TaskExecutionLogs
                .Select(l => new { l.Id, l.TimestampUtc })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(l => l.TimestampUtc < cutoff)
            .Select(l => l.Id)
            .ToList();

        return await DeleteByIdsAsync(dbContext.TaskExecutionLogs, ids,
            (set, batch) => set.Where(l => batch.Contains(l.Id)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<int> CleanupExecutionLogsByCount(int maxPerTask, CancellationToken ct = default)
    {
        // <= 0 is disabled: keeping zero logs would let Skip(0) delete every row of every task.
        if (maxPerTask <= 0)
            return 0;

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await dbContext.TaskExecutionLogs
            .Select(l => new { l.Id, l.TaskId, l.TimestampUtc, l.SequenceNumber })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = rows
            .GroupBy(r => r.TaskId)
            .SelectMany(g => g
                .OrderByDescending(x => x.TimestampUtc)
                .ThenByDescending(x => x.SequenceNumber)
                .ThenByDescending(x => x.Id)   // total order on (Timestamp, Seq) ties; aligns with the read path's OrderBy(Id)
                .Skip(maxPerTask))
            .Select(x => x.Id)
            .ToList();

        return await DeleteByIdsAsync(dbContext.TaskExecutionLogs, ids,
            (set, batch) => set.Where(l => batch.Contains(l.Id)), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<int> CleanupCompletedTasks(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                          CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The status/recurring/audit/log filters translate; the age gate runs in memory (DateTimeOffset).
        var candidates = await dbContext.QueuedTasks
            .Where(qt => qt.Status == QueuedTaskStatus.Completed
                      && qt.ParentTaskId == null
                      && !qt.IsRecurring
                      && !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id)
                      && !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id)
                      && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id)))
            .Select(qt => new { qt.Id, qt.LastExecutionUtc, qt.CreatedAtUtc })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = candidates
            .Where(c => (c.LastExecutionUtc ?? c.CreatedAtUtc) < cutoff)
            .Select(c => c.Id)
            .ToList();

        return await DeleteByIdsAsync(dbContext.QueuedTasks, ids,
            (set, batch) => set.Where(qt => batch.Contains(qt.Id)), ct).ConfigureAwait(false);
    }

    // ---- Statistics (SQLite overrides) ------------------------------------------------------------
    // The createdAt filter compares CreatedAtUtc (DateTimeOffset), which SQLite cannot translate (the
    // same limitation behind the RetrievePending override). With no filter the base server-side GROUP BY
    // translates fine and is reused; with a filter, project the rows and group/filter client-side.

    /// <inheritdoc />
    public override async Task<IReadOnlyDictionary<QueuedTaskStatus, int>> CountByStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        if (createdAtOrAfterUtc == null)
            return await base.CountByStatusAsync(null, ct).ConfigureAwait(false);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await dbContext.QueuedTasks
            .AsNoTracking()
            .Select(t => new { t.CreatedAtUtc, t.Status })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(r => r.CreatedAtUtc >= createdAtOrAfterUtc.Value)
            .GroupBy(r => r.Status)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <inheritdoc />
    public override async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<QueuedTaskStatus, int>>>
        CountByQueueAndStatusAsync(DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        if (createdAtOrAfterUtc == null)
            return await base.CountByQueueAndStatusAsync(null, ct).ConfigureAwait(false);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await dbContext.QueuedTasks
            .AsNoTracking()
            .Select(t => new { t.CreatedAtUtc, t.QueueName, t.Status })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Where(r => r.CreatedAtUtc >= createdAtOrAfterUtc.Value)
            .GroupBy(r => r.QueueName ?? string.Empty)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<QueuedTaskStatus, int>)g
                    .GroupBy(r => r.Status)
                    .ToDictionary(s => s.Key, s => s.Count()));
    }
}
