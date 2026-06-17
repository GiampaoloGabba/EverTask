using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.SqlServer;

/// <summary>
/// SQL Server-specific task storage implementation. Overrides the hot-path writes — <c>SetStatus</c>,
/// <c>UpdateCurrentRun</c> and <c>CompleteRecurringRun</c> — to use stored procedures for optimal
/// performance (a single atomic roundtrip each). Everything else is inherited from the EF Core base.
/// </summary>
public class SqlServerTaskStorage : EfCoreTaskStorage
{
    private readonly ITaskStoreDbContextFactory _contextFactory;
    private readonly IEverTaskLogger<SqlServerTaskStorage> _logger;
    private readonly string _schema;

    public SqlServerTaskStorage(
        ITaskStoreDbContextFactory contextFactory,
        IEverTaskLogger<SqlServerTaskStorage> logger,
        IOptions<ITaskStoreOptions> storeOptions)
        : base(contextFactory, logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
        // Must match the migrations' schema fallback (dbo) so the hot-path procs resolve to where they were
        // created. A null/empty SchemaName lands the procs in dbo; the old `?? "EverTask"` made runtime EXEC
        // a different schema than the procs lived in -> proc-not-found, swallowed in SetStatus, recoverable
        // row -> re-dispatch -> double execution. Mirrors PostgresTaskStorage's `?? "public"`.
        _schema = string.IsNullOrEmpty(storeOptions.Value.SchemaName) ? "dbo" : storeOptions.Value.SchemaName!;
    }

    /// <summary>
    /// Sets task status using optimized stored procedure.
    /// Performs audit insert (if required by AuditLevel) + task update in a single atomic database roundtrip.
    /// </summary>
    public override async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                                            double? executionTimeMs = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Set Task {TaskId} with Status {Status} using SQL Server stored procedure", taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var exString = exception.ToDetailedString();

        try
        {
            // Cast to DbContext to access Database property
            // Build SQL command with schema name (sanitized from configuration)
            var sql = $"EXEC [{_schema}].[usp_SetTaskStatus] @TaskId, @Status, @Exception, @AuditLevel, @ExecutionTimeMs";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@Status", status.ToString()),
                    new SqlParameter("@Exception", (object?)exString ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel),
                    new SqlParameter("@ExecutionTimeMs", (object?)executionTimeMs ?? DBNull.Value)
                ],
                ct
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Unable to update the status {Status} for taskId {TaskId}", status, taskId);
        }
    }

    /// <summary>
    /// Updates the current run counter using the optimized stored procedure.
    /// Performs the audit decision (read of Status/Exception), the counter update and the
    /// RunsAudit insert (if required by AuditLevel) in a single atomic database roundtrip.
    /// </summary>
    /// <remarks>
    /// The proc advances <c>CurrentRunCount</c> by exactly one real execution: occurrences skipped to
    /// realign the schedule after a downtime do NOT consume the MaxRuns budget (Option B accounting).
    /// </remarks>
    public override async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                AuditLevel auditLevel)
    {
        _logger.LogInformation("Update the current run counter for Task {TaskId} using SQL Server stored procedure", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        try
        {
            var sql = $"EXEC [{_schema}].[usp_UpdateCurrentRun] @TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel)
                ]
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do not swallow) so a failed counter persist does not advance the
            // schedule on unpersisted state; the recoverable row is re-run instead.
            _logger.LogCritical(e, "Update the current run counter for Task for taskId {TaskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Completes a recurring occurrence using the optimized stored procedure: marks the task Completed and
    /// advances the run counter / next run in a single atomic database roundtrip (one transaction), so a
    /// crash can never split the status transition from the counter advance and resurrect the finished
    /// occurrence at recovery (CU14/L29).
    /// </summary>
    /// <remarks>
    /// The proc advances <c>CurrentRunCount</c> by exactly one real execution (Option B accounting) and
    /// assigns <c>NextRunUtc</c> unconditionally: a null clears it, making a terminal series unrecoverable.
    /// </remarks>
    public override async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel)
    {
        _logger.LogInformation("Complete recurring run for Task {TaskId} using SQL Server stored procedure", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        try
        {
            var sql = $"EXEC [{_schema}].[usp_CompleteRecurringRun] @TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel)
                ]
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do not swallow) — a failed completion must NOT advance the schedule on
            // unpersisted state; the recoverable row is re-run instead. Same contract as UpdateCurrentRun,
            // deliberately NOT the swallow pattern of SetStatus.
            _logger.LogCritical(e, "Unable to complete recurring run for taskId {TaskId}", taskId);
            throw;
        }
    }
}
