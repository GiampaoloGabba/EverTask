using EverTask.Logger;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EverTask.Storage.EfCore;

/// <summary>
/// Background service that enforces retention policies by periodically deleting old audit records,
/// execution logs and aged-out completed tasks. Register it with <c>AddAuditCleanup(policy,
/// cleanupIntervalHours)</c>, which supplies the <see cref="AuditRetentionPolicy"/> via
/// <see cref="AuditCleanupOptions"/> (the only source the service reads).
/// </summary>
/// <remarks>
/// This service only interprets the policy and orchestrates the cleanup. The actual deletes live on
/// the storage (<see cref="EfCoreTaskStorage"/>, optimized server-side for transactional providers;
/// SqliteTaskStorage overrides them client-side), so the cleanup is never constrained by one provider's
/// query-translation limits.
/// </remarks>
public sealed class AuditCleanupHostedService : BackgroundService
{
    private readonly EfCoreTaskStorage? _storage;
    private readonly IEverTaskLogger<AuditCleanupHostedService> _logger;
    private readonly AuditRetentionPolicy? _retentionPolicy;
    private readonly TimeSpan _cleanupInterval;
    private readonly TimeSpan _initialDelay;

    public AuditCleanupHostedService(
        ITaskStorage storage,
        IEverTaskLogger<AuditCleanupHostedService> logger,
        IOptions<AuditCleanupOptions> cleanupOptions)
    {
        ArgumentNullException.ThrowIfNull(storage);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Cleanup operations live on EfCoreTaskStorage (base) / SqliteTaskStorage (override).
        _storage = storage as EfCoreTaskStorage;

        var options = cleanupOptions?.Value ?? new AuditCleanupOptions();
        _retentionPolicy = options.RetentionPolicy;
        _cleanupInterval = options.CleanupInterval;
        _initialDelay    = options.InitialDelay;

        if (_storage == null)
            _logger.LogWarning("AuditCleanupHostedService requires an EF Core task storage; cleanup is disabled for the configured storage.");
        else if (_retentionPolicy == null)
            _logger.LogWarning("AuditCleanupHostedService started but no AuditRetentionPolicy configured. Service will run but perform no cleanup.");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AuditCleanupHostedService started. Cleanup interval: {Interval}", _cleanupInterval);

        // Wait for a small delay before first cleanup to allow app to fully start
        try
        {
            await Task.Delay(_initialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Service is stopping before the first cleanup
        }

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
        if (_retentionPolicy == null || _storage == null)
            return; // Nothing to do

        _logger.LogInformation("Starting audit cleanup cycle");

        WarnOnDisabledKnobs(_retentionPolicy);

        // One UtcNow per cycle so every pass shares the same age cutoffs.
        var (status, runs, logs, tasks) =
            await RunCleanupAsync(_storage, _retentionPolicy, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Cleanup complete. Deleted {StatusCount} status audits, {RunsCount} runs audits, {LogCount} execution logs, {TasksCount} completed tasks",
            status, runs, logs, tasks);
    }

    /// <summary>
    /// Interprets the retention policy and runs every cleanup pass against the storage. Testable seam:
    /// takes the storage, policy and a caller-supplied <paramref name="now"/> so age cutoffs are
    /// deterministic. Returns the rows deleted by each pass.
    /// </summary>
    internal static async Task<(int StatusAudits, int RunsAudits, int ExecutionLogs, int CompletedTasks)> RunCleanupAsync(
        EfCoreTaskStorage storage, AuditRetentionPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        // A 0 or negative retention knob is treated as DISABLED (no-op), never as a `now`/future cutoff
        // that would mass-delete on every cycle. Each pass runs only when its knob is > 0 (Cluster B).
        var statusDeleted = 0;
        if (policy.StatusAuditRetentionDays is > 0)
        {
            var (success, error) = AuditCutoffs(policy.StatusAuditRetentionDays.Value, policy.ErrorAuditRetentionDays, now);
            statusDeleted = await storage.CleanupStatusAudits(success, error, ct).ConfigureAwait(false);
        }

        var runsDeleted = 0;
        if (policy.RunsAuditRetentionDays is > 0)
        {
            var (success, error) = AuditCutoffs(policy.RunsAuditRetentionDays.Value, policy.ErrorAuditRetentionDays, now);
            runsDeleted = await storage.CleanupRunsAudits(success, error, ct).ConfigureAwait(false);
        }

        var logsDeleted = 0;
        if (policy.ExecutionLogRetentionDays is > 0)
            logsDeleted += await storage.CleanupExecutionLogsByAge(now.AddDays(-policy.ExecutionLogRetentionDays.Value), ct).ConfigureAwait(false);
        if (policy.MaxExecutionLogsPerTask is > 0)
            logsDeleted += await storage.CleanupExecutionLogsByCount(policy.MaxExecutionLogsPerTask.Value, ct).ConfigureAwait(false);

        var tasksDeleted = 0;
        if (policy.DeleteCompletedTasksAfterRetention)
        {
            // Only purge a completed task once it is older than the LONGEST configured retention window —
            // by then every audit category that could exist for it has been pruned. A 0/negative window is
            // disabled, so it does not contribute a cutoff; with no active window there is no cutoff at all
            // and nothing is deleted (G5: an AuditLevel.None completed task with no audits must not be
            // hard-deleted immediately).
            var maxRetentionDays = new[]
                {
                    policy.StatusAuditRetentionDays,
                    policy.RunsAuditRetentionDays,
                    policy.ErrorAuditRetentionDays
                }
                .Where(d => d is > 0)
                .Select(d => d!.Value)
                .DefaultIfEmpty(-1)
                .Max();

            // P0 (Cluster A): when a log retention is actually ACTIVE, the log-age/count passes above have
            // already run, so any log still present is one the policy chose to keep. Purging the task would
            // cascade-delete those logs, violating ExecutionLogRetentionDays / MaxExecutionLogsPerTask, so a
            // task that still owns logs is preserved. With no active log retention the historic
            // cascade-on-purge behavior is unchanged. "Active" means > 0 (a 0/negative knob is disabled, so
            // it must not silently freeze every completed-task purge).
            var logRetentionActive = policy.ExecutionLogRetentionDays is > 0 || policy.MaxExecutionLogsPerTask is > 0;

            if (maxRetentionDays >= 0)
                tasksDeleted = await storage.CleanupCompletedTasks(now.AddDays(-maxRetentionDays), logRetentionActive, ct).ConfigureAwait(false);
        }

        return (statusDeleted, runsDeleted, logsDeleted, tasksDeleted);
    }

    /// <summary>
    /// Emits a warning for any retention knob configured with a non-positive value. Such a value is
    /// treated as DISABLED (see <see cref="RunCleanupAsync"/>); the warning makes the silent no-op visible
    /// so a typo or a missing <c>IConfiguration</c> binding (env var absent → 0) does not look like working
    /// retention.
    /// </summary>
    private void WarnOnDisabledKnobs(AuditRetentionPolicy policy)
    {
        void Warn(string knob, int? value)
        {
            if (value is <= 0)
                _logger.LogWarning(
                    "AuditRetentionPolicy.{Knob} is {Value} (<= 0) and is treated as DISABLED. " +
                    "Provide a positive value to enable it, or null to disable it explicitly.",
                    knob, value);
        }

        Warn(nameof(policy.StatusAuditRetentionDays),  policy.StatusAuditRetentionDays);
        Warn(nameof(policy.RunsAuditRetentionDays),    policy.RunsAuditRetentionDays);
        Warn(nameof(policy.ErrorAuditRetentionDays),   policy.ErrorAuditRetentionDays);
        Warn(nameof(policy.ExecutionLogRetentionDays), policy.ExecutionLogRetentionDays);
        Warn(nameof(policy.MaxExecutionLogsPerTask),   policy.MaxExecutionLogsPerTask);
    }

    /// <summary>
    /// Computes the success and error cutoffs for an audit retention window. Errors are kept for
    /// <paramref name="errorRetentionDays"/> when set, otherwise for the same window as successes.
    /// </summary>
    private static (DateTimeOffset success, DateTimeOffset error) AuditCutoffs(
        int retentionDays, int? errorRetentionDays, DateTimeOffset now)
    {
        var success = now.AddDays(-retentionDays);
        // A 0/negative error window is disabled (no separate window): errors fall back to the success cutoff.
        var error   = errorRetentionDays is > 0 ? now.AddDays(-errorRetentionDays.Value) : success;
        return (success, error);
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
    /// Gets or sets the delay before the first cleanup cycle, to let the application finish starting.
    /// Default: 1 minute.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Gets or sets the retention policy for cleanup.
    /// If null, no cleanup will be performed.
    /// </summary>
    public AuditRetentionPolicy? RetentionPolicy { get; set; }
}
