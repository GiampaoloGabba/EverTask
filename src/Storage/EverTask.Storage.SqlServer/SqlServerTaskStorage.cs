using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace EverTask.Storage.SqlServer;

/// <summary>
/// SQL Server-specific task storage implementation.
/// Overrides SetStatus() to use stored procedure for optimal performance (single roundtrip).
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
        _schema = storeOptions.Value.SchemaName ?? "EverTask";
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
    /// <paramref name="runsToAdvance"/> (1 + occurrences skipped during a downtime) is passed straight
    /// to the proc's <c>@RunsToAdvance</c> parameter so skipped occurrences count toward
    /// CurrentRunCount/MaxRuns (F7/F8) without leaving the single-roundtrip path.
    /// </remarks>
    public override async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                AuditLevel auditLevel, int runsToAdvance)
    {
        // Skipped occurrences must count toward the run counter; never advance by less than 1.
        if (runsToAdvance < 1)
            runsToAdvance = 1;

        _logger.LogInformation("Update the current run counter for Task {TaskId} using SQL Server stored procedure", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        try
        {
            var sql = $"EXEC [{_schema}].[usp_UpdateCurrentRun] @TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel, @RunsToAdvance";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel),
                    new SqlParameter("@RunsToAdvance", runsToAdvance)
                ]
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Update the current run counter for Task for taskId {TaskId}", taskId);
        }
    }
}
