using System.Data;
using System.Globalization;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.EntityFrameworkCore.Storage;
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
// NOTE: not a primary constructor. The base captures contextFactory/logger too, so a primary
// constructor whose parameters are used in the body would capture them twice (CS9107).
public class MySqlTaskStorage(ITaskStoreDbContextFactory contextFactory, IEverTaskLogger<MySqlTaskStorage> logger)
    : EfCoreTaskStorage(contextFactory, logger)
{
    private readonly ITaskStoreDbContextFactory _contextFactory = contextFactory;

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
        logger.SettingTaskStatus(taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
                [
                    new MySqlParameter("@TaskId", taskId.ToString()),
                    new MySqlParameter("@Status", status.ToString()),
                    new MySqlParameter("@Exception", (object?)ex ?? DBNull.Value),
                    new MySqlParameter("@CreateAudit", createAudit),
                    new MySqlParameter("@StampLast", stampLast),
                    new MySqlParameter("@ExecutionTimeMs", (object?)executionTimeMs ?? DBNull.Value)
                ], ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Same swallow contract as the base SetStatus (NOT the rethrow contract of the run-counter writes).
            logger.StatusUpdateFailed(e, status, taskId);
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

        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                "CALL usp_UpdateCurrentRun(@TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel)",
                new MySqlParameter("@TaskId", taskId.ToString()),
                new MySqlParameter("@ExecutionTimeMs", executionTimeMs),
                new MySqlParameter("@NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                new MySqlParameter("@AuditLevel", (int)auditLevel)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do NOT swallow) — a failed counter persist must not advance the schedule on
            // unpersisted state; the recoverable row is re-run instead.
            logger.CurrentRunUpdateFailed(e, taskId);
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
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var statusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
        var runsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                "CALL usp_CompleteRecurringRun(@TaskId, @ExecutionTimeMs, @NextRunUtc, @CreateStatusAudit, @CreateRunsAudit)",
                new MySqlParameter("@TaskId", taskId.ToString()),
                new MySqlParameter("@ExecutionTimeMs", executionTimeMs),
                new MySqlParameter("@NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                new MySqlParameter("@CreateStatusAudit", statusAudit),
                new MySqlParameter("@CreateRunsAudit", runsAudit)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate — a failed completion must not advance the schedule on unpersisted state.
            logger.RecurringRunCompletionFailed(e, taskId);
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
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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

    /// <summary>
    /// MySQL/MariaDB override of the occurrence purge, for the same reason as
    /// <see cref="CleanupCompletedTasks"/>: its <c>preserveTasksWithLogs</c> and the two audit-trail guards
    /// are correlated <c>EXISTS</c> subqueries, which a
    /// <c>DELETE … LIMIT</c> does not reliably honor here — they are dropped, and occurrences that still own
    /// execution logs or audit rows are purged, cascade-deleting what a retention window meant to keep. Same
    /// shape: resolve a bounded page of ids with a <c>SELECT</c>, delete by primary key.
    /// </summary>
    public override async Task<int> CleanupTerminalOccurrences(DateTimeOffset cutoff, bool preserveTasksWithLogs,
                                                               bool preserveStatusAudits, bool preserveRunsAudits,
                                                               CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var total = 0;
        List<Guid> ids;
        do
        {
            ids = await dbContext.QueuedTasks
                .Where(qt => qt.ParentTaskId != null
                          && (qt.Status == QueuedTaskStatus.Completed
                              || qt.Status == QueuedTaskStatus.Failed
                              || qt.Status == QueuedTaskStatus.Cancelled)
                          && (!preserveTasksWithLogs || !dbContext.TaskExecutionLogs.Any(l => l.TaskId == qt.Id))
                          && (!preserveStatusAudits || !dbContext.StatusAudit.Any(sa => sa.QueuedTaskId == qt.Id))
                          && (!preserveRunsAudits || !dbContext.RunsAudit.Any(ra => ra.QueuedTaskId == qt.Id))
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

    // ---- Durable occurrences (stored procedures) --------------------------------------------------
    // Materialization runs once per occurrence and the versioned advances once per run, so they get the same
    // treatment as the three pre-existing hot writes: one procedure, one round-trip, one transaction. The
    // rarer administrative operations inherit the EF base, like the other once-per-series writes already do.

    /// <inheritdoc />
    public override async Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        if (occurrence.ScheduledExecutionUtc is not { } slotUtc)
            throw new ArgumentException("An occurrence must carry its nominal slot.", nameof(occurrence));

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The INSERT below writes the canonical occurrence shape; stamping it on the caller's entity too
        // keeps the object it goes on using (scheduling the child) identical to the row that was stored.
        occurrence.ApplyOccurrenceContract(parentId, expectedScheduleVersion);

        var outcome = new MySqlParameter("@p_Outcome", MySqlDbType.Int32) { Direction = ParameterDirection.Output };

        await CallProcedureAsync(dbContext, "usp_MaterializeOccurrence",
        [
            new MySqlParameter("@p_ParentId", parentId.ToString()),
            new MySqlParameter("@p_ExpectedScheduleVersion", expectedScheduleVersion),
            new MySqlParameter("@p_ExpectedCursorUtc", (object?)expectedCursorUtc?.UtcDateTime ?? DBNull.Value),
            new MySqlParameter("@p_NewCursorUtc", (object?)newCursorUtc?.UtcDateTime ?? DBNull.Value),
            new MySqlParameter("@p_AuditLevel", (int)auditLevel),
            new MySqlParameter("@p_OccurrenceId", occurrence.Id.ToString()),
            new MySqlParameter("@p_SlotUtc", slotUtc.UtcDateTime),
            new MySqlParameter("@p_CreatedAtUtc", occurrence.CreatedAtUtc.UtcDateTime),
            new MySqlParameter("@p_Type", occurrence.Type),
            new MySqlParameter("@p_Request", occurrence.Request),
            new MySqlParameter("@p_Handler", occurrence.Handler),
            new MySqlParameter("@p_QueueName", (object?)occurrence.QueueName ?? DBNull.Value),
            new MySqlParameter("@p_OccurrenceAuditLevel", (object?)occurrence.AuditLevel ?? DBNull.Value),
            new MySqlParameter("@p_RuntimeInfo", (object?)occurrence.RuntimeInfo ?? DBNull.Value),
            outcome
        ], ct).ConfigureAwait(false);

        return (OccurrenceMaterializationOutcome)Convert.ToInt32(outcome.Value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override async Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        await CallProcedureAsync(dbContext, "usp_CancelSchedule",
        [
            new MySqlParameter("@p_ParentId", parentId.ToString()),
            new MySqlParameter("@p_AuditLevel", (int)auditLevel)
        ], ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs,
                                                                   DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                   int expectedScheduleVersion)
    {
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var applied = new MySqlParameter("@p_Applied", MySqlDbType.Bool) { Direction = ParameterDirection.Output };

        try
        {
            await CallProcedureAsync(dbContext, "usp_UpdateCurrentRunCas",
            [
                new MySqlParameter("@p_TaskId", taskId.ToString()),
                new MySqlParameter("@p_ExecutionTimeMs", executionTimeMs),
                new MySqlParameter("@p_NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                new MySqlParameter("@p_AuditLevel", (int)auditLevel),
                new MySqlParameter("@p_ExpectedScheduleVersion", expectedScheduleVersion),
                applied
            ], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate, exactly like the unversioned overload.
            logger.CurrentRunUpdateFailed(e, taskId);
            throw;
        }

        return Convert.ToBoolean(applied.Value, CultureInfo.InvariantCulture)
                   ? ScheduleCasResult.Applied
                   : ScheduleCasResult.VersionMismatch;
    }

    /// <inheritdoc />
    public override async Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs,
                                                                       DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                       int expectedScheduleVersion)
    {
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var applied = new MySqlParameter("@p_Applied", MySqlDbType.Bool) { Direction = ParameterDirection.Output };

        try
        {
            await CallProcedureAsync(dbContext, "usp_CompleteRecurringRunCas",
            [
                new MySqlParameter("@p_TaskId", taskId.ToString()),
                new MySqlParameter("@p_ExecutionTimeMs", executionTimeMs),
                new MySqlParameter("@p_NextRunUtc", (object?)nextRun?.UtcDateTime ?? DBNull.Value),
                new MySqlParameter("@p_CreateStatusAudit",
                    AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null)),
                new MySqlParameter("@p_CreateRunsAudit",
                    AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null)),
                new MySqlParameter("@p_ExpectedScheduleVersion", expectedScheduleVersion),
                applied
            ], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }

        return Convert.ToBoolean(applied.Value, CultureInfo.InvariantCulture)
                   ? ScheduleCasResult.Applied
                   : ScheduleCasResult.VersionMismatch;
    }

    /// <summary>
    /// Calls a stored procedure through ADO instead of <c>ExecuteSqlRaw</c>.
    /// </summary>
    /// <remarks>
    /// OUT parameters are how these procedures report their outcome, and the driver only binds them with
    /// <see cref="CommandType.StoredProcedure"/>, which raw SQL cannot set. The connection is opened and
    /// closed through EF so a pooled context is returned in the state EF expects.
    /// </remarks>
    private static async Task CallProcedureAsync(ITaskStoreDbContext dbContext, string procedure,
                                                 MySqlParameter[] parameters, CancellationToken ct)
    {
        var database = ((DbContext)dbContext).Database;

        await database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = procedure;
            command.CommandType = CommandType.StoredProcedure;
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();

            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
