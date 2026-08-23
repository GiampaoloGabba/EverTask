using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Extensions.Hosting;
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

    public AuditCleanupHostedService(
        ITaskStorage storage,
        IEverTaskLogger<AuditCleanupHostedService> logger,
        IOptions<AuditCleanupOptions> cleanupOptions)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(cleanupOptions);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Cleanup operations live on EfCoreTaskStorage (base) / SqliteTaskStorage (override).
        _storage = storage as EfCoreTaskStorage;

        var options = cleanupOptions.Value;
        _retentionPolicy         = options.RetentionPolicy;
        EffectiveCleanupInterval = ClampToTimerLimit(options.CleanupInterval, nameof(AuditCleanupOptions.CleanupInterval));
        EffectiveInitialDelay    = ClampToTimerLimit(options.InitialDelay, nameof(AuditCleanupOptions.InitialDelay));

        if (_storage == null)
            _logger.StorageIsNotEfCore();
        else if (_retentionPolicy == null)
            _logger.NoRetentionPolicyConfigured();
    }

    // Internal (visible to EverTask.Tests.Storage): the effective, clamped intervals.
    internal TimeSpan EffectiveCleanupInterval { get; }
    internal TimeSpan EffectiveInitialDelay    { get; }

    // Task.Delay rejects anything above TaskDelayLimit.Max (~49.7 days), and this loop runs inside a
    // BackgroundService: letting it throw would take the whole host down with the default
    // BackgroundServiceExceptionBehavior.StopHost. A quarterly interval clamped to ~7 weeks just cleans
    // a bit more often than asked, so clamp and warn. The ET0009 analyzer flags over-limit literals.
    private TimeSpan ClampToTimerLimit(TimeSpan configured, string optionName)
    {
        if (configured <= TaskDelayLimit.Max)
            return configured;

        _logger.IntervalClampedToTimerLimit(optionName, configured, TaskDelayLimit.Max);
        return TaskDelayLimit.Max;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.ServiceStarted(EffectiveCleanupInterval);

        // Wait for a small delay before first cleanup to allow app to fully start
        try
        {
            await Task.Delay(EffectiveInitialDelay, stoppingToken).ConfigureAwait(false);
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
                _logger.CleanupCycleFailed(ex);
            }

            // Wait for next cleanup cycle
            try
            {
                await Task.Delay(EffectiveCleanupInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping
                break;
            }
        }

        _logger.ServiceStopped();
    }

    private async Task PerformCleanup(CancellationToken ct)
    {
        if (_retentionPolicy == null || _storage == null)
            return; // Nothing to do

        _logger.CleanupCycleStarting();

        WarnOnDisabledKnobs(_retentionPolicy);

        // One UtcNow per cycle so every pass shares the same age cutoffs.
        var (status, runs, logs, tasks, occurrences) =
            await RunCleanupAsync(_storage, _retentionPolicy, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        _logger.CleanupComplete(status, runs, logs, tasks, occurrences);
    }

    /// <summary>
    /// Interprets the retention policy and runs every cleanup pass against the storage. Testable seam:
    /// takes the storage, policy and a caller-supplied <paramref name="now"/> so age cutoffs are
    /// deterministic. Returns the rows deleted by each pass.
    /// </summary>
    internal static async Task<(int StatusAudits, int RunsAudits, int ExecutionLogs, int CompletedTasks,
        int TerminalOccurrences)> RunCleanupAsync(
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

        // P0 (Cluster A): when a log retention is actually ACTIVE, the log passes above have already run, so
        // any log still present is one the policy chose to keep — and deleting the task it belongs to would
        // cascade-delete it. Both row-deleting passes below honour the same guard. "Active" means > 0 (a
        // 0/negative knob is disabled and must not silently freeze every purge).
        var logRetentionActive = policy.ExecutionLogRetentionDays is > 0 || policy.MaxExecutionLogsPerTask is > 0;

        // Occurrences of a durable schedule, in ANY terminal state. Runs BEFORE the completed-task purge
        // so the two never contend for the same rows, and independently of it: a failed or cancelled
        // occurrence is never eligible for that purge, yet must not accumulate forever.
        var occurrencesDeleted = 0;
        if (policy.OccurrenceRetentionDays is > 0)
            occurrencesDeleted = await storage.CleanupTerminalOccurrences(
                now.AddDays(-policy.OccurrenceRetentionDays.Value), logRetentionActive, ct).ConfigureAwait(false);

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

            if (maxRetentionDays >= 0)
                tasksDeleted = await storage.CleanupCompletedTasks(now.AddDays(-maxRetentionDays), logRetentionActive, ct).ConfigureAwait(false);
        }

        return (statusDeleted, runsDeleted, logsDeleted, tasksDeleted, occurrencesDeleted);
    }

    /// <summary>
    /// Emits a warning for any retention knob configured with a non-positive value. Such a value is
    /// treated as DISABLED (see <see cref="RunCleanupAsync"/>); the warning makes the silent no-op visible
    /// so a typo or a missing <c>IConfiguration</c> binding (env var absent → 0) does not look like working
    /// retention.
    /// </summary>
    private void WarnOnDisabledKnobs(AuditRetentionPolicy policy)
    {
        Warn(nameof(policy.StatusAuditRetentionDays),  policy.StatusAuditRetentionDays);
        Warn(nameof(policy.RunsAuditRetentionDays),    policy.RunsAuditRetentionDays);
        Warn(nameof(policy.ErrorAuditRetentionDays),   policy.ErrorAuditRetentionDays);
        Warn(nameof(policy.ExecutionLogRetentionDays), policy.ExecutionLogRetentionDays);
        Warn(nameof(policy.MaxExecutionLogsPerTask),   policy.MaxExecutionLogsPerTask);
        Warn(nameof(policy.OccurrenceRetentionDays),   policy.OccurrenceRetentionDays);
        return;

        void Warn(string knob, int? value)
        {
            if (value is <= 0)
                _logger.RetentionKnobDisabled(knob, value);
        }
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
