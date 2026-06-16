using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EverTask.Storage.Postgres;

/// <summary>
/// PostgreSQL-specific task storage.
/// <para>
/// PHASE 1: inherits <see cref="EfCoreTaskStorage"/> for everything else. Npgsql maps
/// <see cref="System.DateTimeOffset"/> to <c>timestamptz</c> and translates every ordering/comparison the
/// base relies on server-side, so (unlike SQLite) NO client-side override is needed for RetrievePending,
/// TrySetQueuedIfRecoverable, the Cleanup* methods, or the date-filtered statistics.
/// </para>
/// <para>
/// PHASE 2 (perf): overrides the three hot writes — <c>SetStatus</c>, <c>UpdateCurrentRun</c> and
/// <c>CompleteRecurringRun</c> — with single-statement, single-roundtrip data-modifying CTEs (Postgres'
/// analog of SQL Server's stored procedures). A data-modifying CTE is ONE statement, hence atomic by
/// construction: the audit insert and the row update commit together or not at all, matching the base
/// transactional contract. No stored object and no migration are needed (the SQL lives here in versioned C#).
/// </para>
/// </summary>
public class PostgresTaskStorage : EfCoreTaskStorage
{
    private readonly ITaskStoreDbContextFactory _contextFactory;
    private readonly IEverTaskLogger<PostgresTaskStorage> _logger;
    private readonly string _schema;

    public PostgresTaskStorage(
        ITaskStoreDbContextFactory contextFactory,
        IEverTaskLogger<PostgresTaskStorage> logger,
        IOptions<ITaskStoreOptions> storeOptions)
        : base(contextFactory, logger)
    {
        _contextFactory = contextFactory;
        _logger         = logger;
        _schema         = string.IsNullOrEmpty(storeOptions.Value.SchemaName) ? "public" : storeOptions.Value.SchemaName!;
    }

    /// <summary>
    /// Sets task status via a single data-modifying CTE: the conditional StatusAudit insert and the row
    /// update execute as ONE atomic statement. The audit decision is computed in C# from the INPUT status +
    /// exception (the audited values are inputs, not the row's current state), exactly like the base
    /// <see cref="EfCoreTaskStorage.SetStatus"/>; the OperationCanceled/ServiceStopped filter therefore stays
    /// on this path (via <see cref="AuditPolicy.ShouldCreateStatusAudit"/>). Swallows on failure — same
    /// contract as the base SetStatus and the SQL Server usp_SetTaskStatus.
    /// </summary>
    public override async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                                         double? executionTimeMs = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Set Task {TaskId} with Status {Status} using PostgreSQL writable CTE", taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var exString    = exception.ToDetailedString();
        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception);

        // LastExecutionUtc is stamped only on terminal transitions; intermediate statuses preserve the
        // previous value (COALESCE in the proc / CASE here). Mirrors EfCoreTaskStorage.SetStatus.
        var stampLast = status != QueuedTaskStatus.WaitingQueue
                        && status != QueuedTaskStatus.Queued
                        && status != QueuedTaskStatus.InProgress
                        && status != QueuedTaskStatus.Cancelled
                        && status != QueuedTaskStatus.Pending;

        var sql = $@"
WITH updated AS (
    UPDATE ""{_schema}"".""QueuedTasks""
    SET ""Status""           = @status,
        ""Exception""        = @exception,
        ""LastExecutionUtc"" = CASE WHEN @stampLast THEN now() ELSE ""LastExecutionUtc"" END,
        ""ExecutionTimeMs""  = CASE WHEN @hasExecTime THEN @execTime ELSE ""ExecutionTimeMs"" END
    WHERE ""Id"" = @taskId
    RETURNING ""Id""
)
INSERT INTO ""{_schema}"".""StatusAudit"" (""QueuedTaskId"", ""UpdatedAtUtc"", ""NewStatus"", ""Exception"")
SELECT @taskId, now(), @status, @exception FROM updated WHERE @createAudit;";

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql, new object[]
            {
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("status", status.ToString()),
                new NpgsqlParameter("exception", (object?)exString ?? DBNull.Value),
                new NpgsqlParameter("stampLast", stampLast),
                new NpgsqlParameter("hasExecTime", executionTimeMs.HasValue),
                new NpgsqlParameter("execTime", executionTimeMs ?? 0d),
                new NpgsqlParameter("createAudit", createAudit)
            }, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Same swallow contract as the base SetStatus / usp_SetTaskStatus (NOT the rethrow contract of
            // the run-counter writes below).
            _logger.LogCritical(e, "Unable to update the status {Status} for taskId {TaskId}", status, taskId);
        }
    }

    /// <summary>
    /// Advances the run counter via a single data-modifying CTE. The RunsAudit decision for ErrorsOnly
    /// depends on the ROW's Status/Exception (NOT a constant), so it is evaluated SERVER-SIDE in the CTE —
    /// it cannot be a single C# boolean. The UPDATE never mutates Status/Exception, so its <c>RETURNING</c>
    /// yields the pre-update values the audit must record (faithful to usp_UpdateCurrentRun). The
    /// <c>COALESCE(CurrentRunCount,0)+1</c> on an <c>integer</c> column raises SQLSTATE 22003 at int.MaxValue,
    /// aborting the statement — propagated (Residual D) so the scheduler never advances on unpersisted state.
    /// </summary>
    public override async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                AuditLevel auditLevel)
    {
        _logger.LogInformation("Update the current run counter for Task {TaskId} using PostgreSQL writable CTE", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var sql = $@"
WITH updated AS (
    UPDATE ""{_schema}"".""QueuedTasks""
    SET ""ExecutionTimeMs"" = @execTime,
        ""NextRunUtc""      = @nextRun,
        ""CurrentRunCount"" = COALESCE(""CurrentRunCount"", 0) + 1
    WHERE ""Id"" = @taskId
    RETURNING ""Status"", ""Exception""
)
INSERT INTO ""{_schema}"".""RunsAudit"" (""QueuedTaskId"", ""ExecutedAt"", ""ExecutionTimeMs"", ""Status"", ""Exception"")
SELECT @taskId, now(), @execTime, u.""Status"", u.""Exception""
FROM updated u
WHERE (@auditLevel IN (0, 1))
   OR (@auditLevel = 2 AND (u.""Status"" = 'Failed' OR (u.""Exception"" IS NOT NULL AND u.""Exception"" <> '')));";

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql, new object[]
            {
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", (object?)nextRun?.ToUniversalTime() ?? DBNull.Value),
                new NpgsqlParameter("auditLevel", (int)auditLevel)
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do NOT swallow) — a failed counter persist must not advance the schedule
            // on unpersisted state; the recoverable row is re-run instead.
            _logger.LogCritical(e, "Update the current run counter for Task for taskId {TaskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Completes a recurring occurrence via a single data-modifying CTE: marks the task Completed AND advances
    /// the run counter / next run atomically, so a crash can never split the two and resurrect the finished
    /// occurrence at recovery. The audited Status/Exception are the CONSTANTS <c>Completed</c>/<c>NULL</c>, so
    /// the audit gates depend ONLY on the AuditLevel and are computed in C# (no pre-update read needed):
    /// StatusAudit at Full only, RunsAudit at Full+Minimal — matching usp_CompleteRecurringRun and the EF base.
    /// Propagates on failure (Residual D), same as <see cref="UpdateCurrentRun"/>.
    /// </summary>
    public override async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel)
    {
        _logger.LogInformation("Complete recurring run for Task {TaskId} using PostgreSQL writable CTE", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var statusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
        var runsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

        // NextRunUtc is assigned UNCONDITIONALLY (a NULL makes the series terminal/non-recoverable; preserving
        // the old value would resurrect a finished series). ins_status runs even though the final query does
        // not reference it (Postgres executes every data-modifying CTE exactly once).
        var sql = $@"
WITH updated AS (
    UPDATE ""{_schema}"".""QueuedTasks""
    SET ""Status""           = 'Completed',
        ""Exception""        = NULL,
        ""LastExecutionUtc"" = now(),
        ""ExecutionTimeMs""  = @execTime,
        ""NextRunUtc""       = @nextRun,
        ""CurrentRunCount""  = COALESCE(""CurrentRunCount"", 0) + 1
    WHERE ""Id"" = @taskId
    RETURNING ""Id""
),
ins_status AS (
    INSERT INTO ""{_schema}"".""StatusAudit"" (""QueuedTaskId"", ""UpdatedAtUtc"", ""NewStatus"", ""Exception"")
    SELECT @taskId, now(), 'Completed', NULL FROM updated WHERE @statusAudit
    RETURNING ""Id""
)
INSERT INTO ""{_schema}"".""RunsAudit"" (""QueuedTaskId"", ""ExecutedAt"", ""ExecutionTimeMs"", ""Status"", ""Exception"")
SELECT @taskId, now(), @execTime, 'Completed', NULL FROM updated WHERE @runsAudit;";

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql, new object[]
            {
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", (object?)nextRun?.ToUniversalTime() ?? DBNull.Value),
                new NpgsqlParameter("statusAudit", statusAudit),
                new NpgsqlParameter("runsAudit", runsAudit)
            }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate — a failed completion must not advance the schedule on unpersisted state.
            _logger.LogCritical(e, "Unable to complete recurring run for taskId {TaskId}", taskId);
            throw;
        }
    }
}
