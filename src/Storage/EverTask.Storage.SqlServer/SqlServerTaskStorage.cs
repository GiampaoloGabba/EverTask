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
                                            CancellationToken ct = default)
    {
        _logger.LogInformation("Set Task {TaskId} with Status {Status} using SQL Server stored procedure", taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var exString = exception.ToDetailedString();

        try
        {
            // Cast to DbContext to access Database property
            // Build SQL command with schema name (sanitized from configuration)
            var sql = $"EXEC [{_schema}].[usp_SetTaskStatus] @TaskId, @Status, @Exception, @AuditLevel";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@Status", status.ToString()),
                    new SqlParameter("@Exception", (object?)exString ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel)
                ],
                ct
            ).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.LogCritical(e, "Unable to update the status {Status} for taskId {TaskId}", status, taskId);
        }
    }
}
