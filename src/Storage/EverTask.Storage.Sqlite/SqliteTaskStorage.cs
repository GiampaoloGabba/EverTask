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
            .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
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
    /// on every single recovered task; this override skips the untranslatable query entirely.
    /// </summary>
    public override async Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        if (!await TrySetQueuedClientSideAsync(dbContext, taskId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false))
        {
            _logger.LogDebug("Task {taskId} is no longer recoverable, skipping SetQueued", taskId);
            return false;
        }

        await WriteQueuedTransitionAuditAsync(dbContext, taskId, auditLevel, ct).ConfigureAwait(false);
        return true;
    }
}
