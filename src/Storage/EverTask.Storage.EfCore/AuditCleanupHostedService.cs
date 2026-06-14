using EverTask.Logger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EverTask.Storage.EfCore;

/// <summary>
/// Background service that enforces audit trail retention policies by periodically deleting old audit records.
/// Requires <see cref="AuditRetentionPolicy"/> to be configured in <see cref="EverTaskServiceConfiguration"/>.
/// </summary>
public sealed class AuditCleanupHostedService : BackgroundService
{
    private readonly ITaskStoreDbContextFactory _contextFactory;
    private readonly IEverTaskLogger<AuditCleanupHostedService> _logger;
    private readonly AuditRetentionPolicy? _retentionPolicy;
    private readonly TimeSpan _cleanupInterval;

    public AuditCleanupHostedService(
        ITaskStoreDbContextFactory contextFactory,
        IEverTaskLogger<AuditCleanupHostedService> logger,
        IOptions<AuditCleanupOptions> cleanupOptions)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var options = cleanupOptions?.Value ?? new AuditCleanupOptions();

        // Get retention policy from cleanup options
        _retentionPolicy = options.RetentionPolicy;

        // Get cleanup interval from options (default: 24 hours)
        _cleanupInterval = options.CleanupInterval;

        if (_retentionPolicy == null)
        {
            _logger.LogWarning("AuditCleanupHostedService started but no AuditRetentionPolicy configured. Service will run but perform no cleanup.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuditCleanupHostedService started. Cleanup interval: {Interval}", _cleanupInterval);

        // Wait for a small delay before first cleanup to allow app to fully start
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PerformCleanup(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error occurred during audit cleanup");
            }

            // Wait for next cleanup cycle
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping
                break;
            }
        }

        _logger.LogInformation("AuditCleanupHostedService stopped");
    }

    private async Task PerformCleanup(CancellationToken ct)
    {
        if (_retentionPolicy == null)
        {
            return; // No policy configured, skip cleanup
        }

        _logger.LogInformation("Starting audit cleanup cycle");

        var totalDeletedStatus = 0;
        var totalDeletedRuns = 0;

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Clean up StatusAudit records
        if (_retentionPolicy.StatusAuditRetentionDays.HasValue)
        {
            totalDeletedStatus = await CleanupStatusAudit(dbContext, _retentionPolicy, ct).ConfigureAwait(false);
        }

        // Clean up RunsAudit records
        if (_retentionPolicy.RunsAuditRetentionDays.HasValue)
        {
            totalDeletedRuns = await CleanupRunsAudit(dbContext, _retentionPolicy, ct).ConfigureAwait(false);
        }

        // Clean up completed tasks if configured
        if (_retentionPolicy.DeleteCompletedTasksAfterRetention)
        {
            var deletedTasks = await CleanupCompletedTasks(dbContext, _retentionPolicy, ct).ConfigureAwait(false);
            _logger.LogInformation("Cleanup complete. Deleted {StatusCount} status audits, {RunsCount} runs audits, {TasksCount} completed tasks",
                totalDeletedStatus, totalDeletedRuns, deletedTasks);
        }
        else
        {
            _logger.LogInformation("Cleanup complete. Deleted {StatusCount} status audits, {RunsCount} runs audits",
                totalDeletedStatus, totalDeletedRuns);
        }
    }

    private async Task<int> CleanupStatusAudit(ITaskStoreDbContext dbContext, AuditRetentionPolicy policy, CancellationToken ct)
    {
        var successCutoff = DateTimeOffset.UtcNow.AddDays(-policy.StatusAuditRetentionDays!.Value);
        var errorCutoff = policy.ErrorAuditRetentionDays.HasValue
            ? DateTimeOffset.UtcNow.AddDays(-policy.ErrorAuditRetentionDays.Value)
            : successCutoff;

        var totalDeleted = 0;
        var batchSize = 1000; // Delete in batches to avoid lock escalation

        while (!ct.IsCancellationRequested)
        {
            // Delete records older than cutoff, but keep errors longer if ErrorAuditRetentionDays is set
            var deleted = await dbContext.StatusAudit
                .Where(sa => (string.IsNullOrEmpty(sa.Exception) && sa.UpdatedAtUtc < successCutoff)
                          || (!string.IsNullOrEmpty(sa.Exception) && sa.UpdatedAtUtc < errorCutoff))
                .Take(batchSize)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            totalDeleted += deleted;

            if (deleted < batchSize)
            {
                break; // No more records to delete
            }

            // Small delay between batches to reduce load
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Deleted {Count} StatusAudit records older than {Cutoff}",
                totalDeleted, successCutoff);
        }

        return totalDeleted;
    }

    private async Task<int> CleanupRunsAudit(ITaskStoreDbContext dbContext, AuditRetentionPolicy policy, CancellationToken ct)
    {
        var successCutoff = DateTimeOffset.UtcNow.AddDays(-policy.RunsAuditRetentionDays!.Value);
        var errorCutoff = policy.ErrorAuditRetentionDays.HasValue
            ? DateTimeOffset.UtcNow.AddDays(-policy.ErrorAuditRetentionDays.Value)
            : successCutoff;

        var totalDeleted = 0;
        var batchSize = 1000;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await dbContext.RunsAudit
                .Where(ra => (string.IsNullOrEmpty(ra.Exception) && ra.ExecutedAt < successCutoff)
                          || (!string.IsNullOrEmpty(ra.Exception) && ra.ExecutedAt < errorCutoff))
                .Take(batchSize)
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);

            totalDeleted += deleted;

            if (deleted < batchSize)
            {
                break;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Deleted {Count} RunsAudit records older than {Cutoff}",
                totalDeleted, successCutoff);
        }

        return totalDeleted;
    }

    private async Task<int> CleanupCompletedTasks(ITaskStoreDbContext dbContext, AuditRetentionPolicy policy, CancellationToken ct)
    {
        var totalDeleted = await DeleteCompletedTasksAsync(dbContext, policy, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Deleted {Count} completed tasks past their retention window", totalDeleted);
        }

        return totalDeleted;
    }

    /// <summary>
    /// Hard-deletes completed, non-recurring tasks whose audit trail has aged out.
    /// Testable seam: takes the context, policy and a caller-supplied <paramref name="now"/> so the
    /// age cutoff is deterministic.
    /// </summary>
    internal static async Task<int> DeleteCompletedTasksAsync(
        ITaskStoreDbContext dbContext, AuditRetentionPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        // Age gate (G5): only purge a completed task once it is older than the LONGEST configured
        // retention window — by then every audit category that could exist for it has been pruned. With
        // no retention configured there is no defined cutoff, so nothing is deleted (a completed task
        // with no audits, e.g. AuditLevel.None, must not be hard-deleted immediately).
        var maxRetentionDays = new[]
            {
                policy.StatusAuditRetentionDays,
                policy.RunsAuditRetentionDays,
                policy.ErrorAuditRetentionDays
            }
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .DefaultIfEmpty(-1)
            .Max();

        if (maxRetentionDays < 0)
            return 0;

        var cutoff = now.AddDays(-maxRetentionDays);

        // Candidates: completed, non-recurring tasks with no surviving audit trail (no StatusAudit /
        // RunsAudit rows). Deleting a task cascades to everything it owns — audits AND captured execution
        // logs — which is the point of cleanup. Execution logs have no retention of their own yet; until a
        // dedicated execution-log retention exists (see docs/execution-log-retention.md), they are removed
        // together with the aged-out task. The status/recurring/audit filters translate on every provider;
        // the age gate is applied in memory because SQLite cannot translate DateTimeOffset comparisons (the
        // same limitation that forces SqliteTaskStorage.RetrievePending to filter dates client-side).
        var candidates = await dbContext.QueuedTasks
            .Where(qt => qt.Status == QueuedTaskStatus.Completed
                      && !qt.IsRecurring
                      && !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id)
                      && !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id))
            .Select(qt => new { qt.Id, qt.LastExecutionUtc, qt.CreatedAtUtc })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // Age gate (G5): only purge a task once its age anchor — LastExecutionUtc, falling back to
        // CreatedAtUtc when unset — is older than the cutoff.
        var deletableIds = candidates
            .Where(c => (c.LastExecutionUtc ?? c.CreatedAtUtc) < cutoff)
            .Select(c => c.Id)
            .ToList();

        var totalDeleted = 0;
        const int batchSize = 100; // chunk the DELETE to avoid oversized IN (...) and lock escalation

        for (var i = 0; i < deletableIds.Count && !ct.IsCancellationRequested; i += batchSize)
        {
            var batch = deletableIds.GetRange(i, Math.Min(batchSize, deletableIds.Count - i));

            totalDeleted += await dbContext.QueuedTasks
                .Where(qt => batch.Contains(qt.Id))
                .ExecuteDeleteAsync(ct)
                .ConfigureAwait(false);
        }

        return totalDeleted;
    }
}

/// <summary>
/// Configuration options for <see cref="AuditCleanupHostedService"/>.
/// </summary>
public sealed class AuditCleanupOptions
{
    /// <summary>
    /// Gets or sets the interval between cleanup cycles.
    /// Default: 24 hours.
    /// </summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Gets or sets the retention policy for audit trail cleanup.
    /// If null, no cleanup will be performed.
    /// </summary>
    public AuditRetentionPolicy? RetentionPolicy { get; set; }
}
