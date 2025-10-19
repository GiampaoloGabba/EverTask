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
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IEverTaskLogger<SqliteTaskStorage> _logger;

    public SqliteTaskStorage(IServiceScopeFactory serviceScopeFactory, IEverTaskLogger<SqliteTaskStorage> logger)
        : base(serviceScopeFactory, logger)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves pending tasks with SQLite-compatible query.
    /// Filters by status and MaxRuns in the database, then applies RunUntil filter in memory
    /// to avoid SQLite DateTimeOffset comparison issues.
    /// </summary>
    public override async Task<QueuedTask[]> RetrievePending(CancellationToken ct = default)
    {
        using var scope = _serviceScopeFactory.CreateScope();
        await using var dbContext = scope.ServiceProvider.GetRequiredService<ITaskStoreDbContext>();

        _logger.LogInformation("Retrieving Pending Tasks (SQLite)");

        var now = DateTimeOffset.UtcNow;

        // Query database with filters SQLite can handle
        var tasks = await dbContext.QueuedTasks
            .AsNoTracking()
            .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
                     && (t.Status == QueuedTaskStatus.Queued ||
                         t.Status == QueuedTaskStatus.Pending ||
                         t.Status == QueuedTaskStatus.ServiceStopped ||
                         t.Status == QueuedTaskStatus.InProgress))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        // Apply RunUntil filter in memory (SQLite has issues with DateTimeOffset comparisons)
        return tasks
            .Where(t => t.RunUntil == null || t.RunUntil >= now)
            .ToArray();
    }
}
