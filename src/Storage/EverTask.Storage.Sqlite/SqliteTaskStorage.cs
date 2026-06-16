using EverTask.Abstractions;
using EverTask.Logger;
using EverTask.Storage.EfCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.Sqlite;

/// <summary>
/// SQLite-specific task storage implementation.
/// Overrides RetrievePending() to work around SQLite's DateTimeOffset comparison limitations.
/// </summary>
public class SqliteTaskStorage : EfCoreTaskStorage
{
    private readonly ITaskStoreDbContextFactory _contextFactory;
    private readonly IEverTaskLogger<SqliteTaskStorage> _logger;

    public SqliteTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<SqliteTaskStorage> logger)
        : base(contextFactory, logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves pending tasks using keyset pagination, applying RunUntil filtering in memory
    /// to avoid SQLite DateTimeOffset comparison issues.
    /// </summary>
    public override async Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        _logger.LogInformation("Retrieving Pending Tasks (SQLite keyset: lastCreatedAt={LastCreatedAt}, lastId={LastId}, take={Take})",
            lastCreatedAt, lastId, take);

        var now = DateTimeOffset.UtcNow;

        // Query database with filters SQLite can handle
        // Recoverable statuses: same rules as EfCoreTaskStorage.RetrievePending (see comments there)
        var tasks = await dbContext.QueuedTasks
            .AsNoTracking()
            .Where(t => (t.MaxRuns == null || (t.CurrentRunCount ?? 0) < t.MaxRuns)
                        && (t.Status == QueuedTaskStatus.WaitingQueue ||
                            t.Status == QueuedTaskStatus.Queued ||
                            t.Status == QueuedTaskStatus.Pending ||
                            t.Status == QueuedTaskStatus.ServiceStopped ||
                            t.Status == QueuedTaskStatus.InProgress ||
                            (t.IsRecurring && t.NextRunUtc != null &&
                             (t.Status == QueuedTaskStatus.Completed ||
                              t.Status == QueuedTaskStatus.Failed))))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        // Apply RunUntil filter in memory (SQLite has issues with DateTimeOffset comparisons)
        var filtered = tasks
            .Where(t => (t.RunUntil == null || t.RunUntil >= now)
                        && (!lastCreatedAt.HasValue ||
                            t.CreatedAtUtc > lastCreatedAt.Value ||
                            (t.CreatedAtUtc == lastCreatedAt.Value && lastId.HasValue && t.Id.CompareTo(lastId.Value) > 0)))
            .OrderBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .Take(take)
            .ToArray();

        return filtered;
    }

    /// <summary>
    /// Evaluates the recoverable predicate client-side. The base conditional UPDATE compares
    /// <c>RunUntil</c> (DateTimeOffset), which SQLite cannot translate — the same limitation that
    /// forces the RetrievePending override. Going through the base method would throw and fall back
    /// on every single recovered task; this override skips the untranslatable query entirely. The
    /// transition and its audit are written in a single SaveChanges (one implicit SQLite
    /// transaction), so the pair is atomic (L20).
    /// </summary>
    public override async Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var transitioned = await TrySetQueuedClientSideAsync(dbContext, taskId, DateTimeOffset.UtcNow, auditLevel, ct).ConfigureAwait(false);
        if (!transitioned)
            _logger.LogDebug("Task {taskId} is no longer recoverable, skipping SetQueued", taskId);

        return transitioned;
    }

    // ---- Retention cleanup (SQLite overrides) -----------------------------------------------------
    // SQLite cannot translate DateTimeOffset ordering comparisons (the same limitation behind the
    // RetrievePending override), so each cleanup resolves the rows to delete client-side and deletes
    // them by primary key. The optimized server-side versions live in the EfCoreTaskStorage base.

    /// <inheritdoc />
    public override async Task<int> CleanupStatusAudits(DateTimeOffset successCutoff, DateTimeOffset errorCutoff,
                                                        CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

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
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

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
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

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
        // <= 0 is disabled (Cluster B): keeping zero logs would let Skip(0) delete every row of every task.
        if (maxPerTask <= 0)
            return 0;

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var rows = await dbContext.TaskExecutionLogs
            .Select(l => new { l.Id, l.TaskId, l.TimestampUtc, l.SequenceNumber })
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = rows
            .GroupBy(r => r.TaskId)
            .SelectMany(g => g
                .OrderByDescending(x => x.TimestampUtc)
                .ThenByDescending(x => x.SequenceNumber)
                .ThenByDescending(x => x.Id)   // Cluster C: total order on (Timestamp, Seq) ties; aligns with the read path's OrderBy(Id)
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
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        // The status/recurring/audit/log filters translate; the age gate runs in memory (DateTimeOffset).
        var candidates = await dbContext.QueuedTasks
            .Where(qt => qt.Status == QueuedTaskStatus.Completed
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

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

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

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

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
