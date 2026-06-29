using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace EverTask.Storage.MySql;

/// <summary>
/// MySQL/MariaDB task storage.
/// <para>
/// Inherits <see cref="EfCoreTaskStorage"/>. The Microting provider maps <see cref="System.DateTimeOffset"/>
/// to <c>datetime(6)</c> (normalized to UTC) and translates every ordering/comparison the base relies on
/// server-side, so (unlike SQLite) the recovery and cleanup queries inherit the base unchanged. The ONE
/// read-path exception is <see cref="CleanupCompletedTasks"/> (a MySQL <c>DELETE ... LIMIT</c> ignores a
/// correlated <c>EXISTS</c> guard).
/// </para>
/// <para>
/// PHASE 2 (hot writes): MySQL/MariaDB have READ-ONLY CTEs and no <c>UPDATE ... RETURNING</c>, so the
/// single-roundtrip optimization for <c>SetStatus</c> / <c>UpdateCurrentRun</c> / <c>CompleteRecurringRun</c>
/// uses STORED PROCEDURES (the SQL Server template), each a single atomic transaction. The procs are created
/// by the <c>AddHotWriteStoredProcedures</c> migration; the audit decisions match <see cref="AuditPolicy"/>
/// exactly (the <c>ErrorsOnly</c> RunsAudit gate is decided server-side from the row's own Status/Exception).
/// </para>
/// </summary>
public class MySqlTaskStorage : EfCoreTaskStorage
{
    private readonly ITaskStoreDbContextFactory _contextFactory;
    private readonly IEverTaskLogger<MySqlTaskStorage> _logger;

    public MySqlTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<MySqlTaskStorage> logger)
        : base(contextFactory, logger)
    {
        _contextFactory = contextFactory;
        _logger         = logger;
    }

    /// <summary>
    /// Sets task status via the <c>usp_SetTaskStatus</c> stored procedure (one atomic round-trip). The audit
    /// gate and the terminal-stamp flag are computed in C# from the INPUT status/exception — the audited values
    /// are inputs, exactly like the base <see cref="EfCoreTaskStorage.SetStatus"/>, so the
    /// OperationCanceled/ServiceStopped filter stays on this path via <see cref="AuditPolicy"/>. Swallows on
    /// failure (same contract as the base SetStatus).
    /// </summary>
    public override async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                                         double? executionTimeMs = null, CancellationToken ct = default)
    {
        _logger.LogInformation("Set Task {TaskId} with Status {Status} using MySQL stored procedure", taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        var ex          = exception.ToDetailedString();
        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception);

        // LastExecutionUtc is stamped only on terminal transitions; intermediate statuses preserve the previous
        // value. Mirrors EfCoreTaskStorage.SetStatus.
        var stampLast = status != QueuedTaskStatus.WaitingQueue
                        && status != QueuedTaskStatus.Queued
                        && status != QueuedTaskStatus.InProgress
                        && status != QueuedTaskStatus.Cancelled
                        && status != QueuedTaskStatus.Pending;

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                "CALL usp_SetTaskStatus(@TaskId, @Status, @Exception, @CreateAudit, @StampLast, @ExecutionTimeMs)",
                new object[]
                {
                    new MySqlParameter("@TaskId", taskId.ToString()),
                    new MySqlParameter("@Status", status.ToString()),
                    new MySqlParameter("@Exception", (object?)ex ?? DBNull.Value),
                    new MySqlParameter("@CreateAudit", createAudit),
                    new MySqlParameter("@StampLast", stampLast),
                    new MySqlParameter("@ExecutionTimeMs", (object?)executionTimeMs ?? DBNull.Value)
                }, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Same swallow contract as the base SetStatus (NOT the rethrow contract of the run-counter writes).
            _logger.LogCritical(e, "Unable to update the status {Status} for taskId {TaskId}", status, taskId);
        }
    }

    /// <summary>
    /// Advances the run counter via <c>usp_UpdateCurrentRun</c>. The RunsAudit decision for ErrorsOnly depends
    /// on the ROW's Status/Exception (NOT a constant), so it is evaluated SERVER-SIDE in the proc — it cannot be
    /// a single C# boolean. The run counter SATURATES at int.MaxValue, matching the base and the other providers;
    /// failures propagate (Residual D) so the scheduler never advances on unpersisted state.
    /// </summary>
    public override async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                AuditLevel auditLevel)
    {
        // AuditLevel.None never audits, so usp_UpdateCurrentRun's SELECT ... FOR UPDATE (there only to read the
        // row for the ErrorsOnly audit gate) is pure overhead and an unnecessary held row lock. Delegate to the
        // base no-SELECT fast path: a single ExecuteUpdate that advances the saturating counter in one statement,
        // exactly the high-frequency path AuditLevel.None exists for.
        if (auditLevel == AuditLevel.None)
        {
            await base.UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel).ConfigureAwait(false);
            return;
        }

        _logger.LogInformation("Update the current run counter for Task {TaskId} using MySQL stored procedure", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                "CALL usp_UpdateCurrentRun(@TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel)",
                new object[]
                {
                    new MySqlParameter("@TaskId", taskId.ToString()),
                    new MySqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new MySqlParameter("@NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                    new MySqlParameter("@AuditLevel", (int)auditLevel)
                }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do NOT swallow) — a failed counter persist must not advance the schedule on
            // unpersisted state; the recoverable row is re-run instead.
            _logger.LogCritical(e, "Update the current run counter for Task for taskId {TaskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// Completes a recurring occurrence via <c>usp_CompleteRecurringRun</c>: marks the task Completed AND advances
    /// the run counter / next run atomically, so a crash can never split the two and resurrect the finished
    /// occurrence at recovery. The audited Status/Exception are the CONSTANTS <c>Completed</c>/<c>NULL</c>, so the
    /// audit gates depend only on the AuditLevel and are computed in C# (StatusAudit at Full; RunsAudit at
    /// Full+Minimal). NextRunUtc is assigned unconditionally (a null makes the series terminal). Propagates on
    /// failure (Residual D), same as <see cref="UpdateCurrentRun"/>.
    /// </summary>
    public override async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel)
    {
        _logger.LogInformation("Complete recurring run for Task {TaskId} using MySQL stored procedure", taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync();

        var statusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
        var runsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                "CALL usp_CompleteRecurringRun(@TaskId, @ExecutionTimeMs, @NextRunUtc, @CreateStatusAudit, @CreateRunsAudit)",
                new object[]
                {
                    new MySqlParameter("@TaskId", taskId.ToString()),
                    new MySqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new MySqlParameter("@NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                    new MySqlParameter("@CreateStatusAudit", statusAudit),
                    new MySqlParameter("@CreateRunsAudit", runsAudit)
                }).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate — a failed completion must not advance the schedule on unpersisted state.
            _logger.LogCritical(e, "Unable to complete recurring run for taskId {TaskId}", taskId);
            throw;
        }
    }

    /// <summary>
    /// MySQL/MariaDB override of the completed-task purge. The base resolves the rows with
    /// <c>Where(predicate).Take(n).ExecuteDelete()</c> → <c>DELETE ... LIMIT</c>, but on MySQL a
    /// <c>DELETE ... LIMIT</c> does not reliably honor a correlated <c>EXISTS</c> guard in its <c>WHERE</c>:
    /// the <c>preserveTasksWithLogs</c> guard (<c>!TaskExecutionLogs.Any(...)</c>) was dropped and a completed
    /// task that still owned execution logs got purged, cascade-deleting the very logs a retention window meant
    /// to keep. The fix mirrors the SQLite override shape: resolve the matching ids with a SELECT — where the
    /// <c>EXISTS</c> subqueries AND the <c>DateTimeOffset</c> cutoff translate server-side on MySQL — then delete
    /// by primary key in bounded batches. The other <c>Cleanup*</c> methods carry no <c>EXISTS</c> guard and
    /// inherit the optimized base unchanged.
    /// </summary>
    public override async Task<int> CleanupCompletedTasks(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                          CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct);

        // Resolve a BOUNDED page of ids, delete them by PK, repeat — never materializing the whole candidate set
        // (a large first/backlog run would otherwise load millions of Guids into memory). Each iteration re-runs
        // the full predicate server-side, so a row that gains an audit/log between pages drops out of the next
        // page (keeps the per-batch re-check the base BatchDeleteAsync relies on). Deleting a page removes it from
        // the predicate, so the loop makes progress and terminates.
        var total = 0;
        List<Guid> ids;
        do
        {
            ids = await dbContext.QueuedTasks
                .Where(qt => qt.Status == QueuedTaskStatus.Completed
                          && !qt.IsRecurring
                          && !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id)
                          && !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id)
                          && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id))
                          && (qt.LastExecutionUtc ?? qt.CreatedAtUtc) < cutoff)
                .Select(qt => qt.Id)
                .Take(CleanupBatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (ids.Count == 0)
                break;

            total += await DeleteByIdsAsync(dbContext.QueuedTasks, ids,
                (set, batch) => set.Where(qt => batch.Contains(qt.Id)), ct).ConfigureAwait(false);
        } while (ids.Count == CleanupBatchSize && !ct.IsCancellationRequested);

        return total;
    }
}
