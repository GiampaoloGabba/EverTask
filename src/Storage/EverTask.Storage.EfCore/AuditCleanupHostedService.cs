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
        if (_retentionPolicy.DeleteCompletedTasksWithAudits)
        {
            var deletedTasks = await CleanupCompletedTasks(dbContext, ct).ConfigureAwait(false);
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

    private async Task<int> CleanupCompletedTasks(ITaskStoreDbContext dbContext, CancellationToken ct)
    {
        var totalDeleted = 0;
        var batchSize = 100; // Smaller batch for task deletion (cascade delete audits)

        while (!ct.IsCancellationRequested)
        {
            // Delete non-recurring completed tasks that have no audit records
            var deleted = await dbContext.QueuedTasks
                .Where(qt => qt.Status == QueuedTaskStatus.Completed
                          && !qt.IsRecurring
                          && !qt.StatusAudits.Any()
                          && !qt.RunsAudits.Any())
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
            _logger.LogInformation("Deleted {Count} completed tasks with no audit trail", totalDeleted);
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
