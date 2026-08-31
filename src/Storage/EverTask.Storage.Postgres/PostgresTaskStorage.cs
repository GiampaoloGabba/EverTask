using System.Globalization;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace EverTask.Storage.Postgres;

/// <summary>
/// PostgreSQL-specific task storage. Npgsql maps <see cref="System.DateTimeOffset"/> to <c>timestamptz</c>
/// and translates every ordering/comparison the base relies on server-side, so (unlike SQLite) nothing needs
/// a client-side override.
/// <para>
/// The hot writes — <c>SetStatus</c>, <c>UpdateCurrentRun</c> and <c>CompleteRecurringRun</c> — are
/// single-statement data-modifying CTEs, atomic by construction: the audit insert and the row update commit
/// together or not at all. No stored object and no migration are needed.
/// </para>
/// </summary>
// The primary-ctor parameters are deliberately re-declared as private fields: the base captures them too,
// and using a parameter directly from a method body would capture the same value twice (CS9107).
public class PostgresTaskStorage(
    ITaskStoreDbContextFactory contextFactory,
    IEverTaskLogger<PostgresTaskStorage> logger,
    IOptions<ITaskStoreOptions> storeOptions)
    : EfCoreTaskStorage(contextFactory, logger)
{
    private readonly ITaskStoreDbContextFactory _contextFactory = contextFactory;
    private readonly string _schema = string.IsNullOrEmpty(storeOptions.Value.SchemaName) ? "public" : storeOptions.Value.SchemaName!;

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
        logger.SettingTaskStatus(taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var exString    = exception.ToDetailedString();
        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception);

        // LastExecutionUtc is stamped only on terminal transitions; intermediate statuses preserve the
        // previous value (COALESCE in the proc / CASE here). Mirrors EfCoreTaskStorage.SetStatus.
        var stampLast = status != QueuedTaskStatus.WaitingQueue
                        && status != QueuedTaskStatus.Queued
                        && status != QueuedTaskStatus.InProgress
                        && status != QueuedTaskStatus.Cancelled
                        && status != QueuedTaskStatus.Pending;

        var sql = $"""

                   WITH updated AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "Status"           = @status,
                           "Exception"        = @exception,
                           "LastExecutionUtc" = CASE WHEN @stampLast THEN now() ELSE "LastExecutionUtc" END,
                           "ExecutionTimeMs"  = CASE WHEN @hasExecTime THEN @execTime ELSE "ExecutionTimeMs" END
                       WHERE "Id" = @taskId
                       RETURNING "Id"
                   )
                   INSERT INTO "{_schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
                   SELECT @taskId, now(), @status, @exception FROM updated WHERE @createAudit;
                   """;

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql, [
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("status", status.ToString()),
                new NpgsqlParameter("exception", (object?)exString ?? DBNull.Value),
                new NpgsqlParameter("stampLast", stampLast),
                new NpgsqlParameter("hasExecTime", executionTimeMs.HasValue),
                new NpgsqlParameter("execTime", executionTimeMs ?? 0d),
                new NpgsqlParameter("createAudit", createAudit)
            ], ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Same swallow contract as the base SetStatus / usp_SetTaskStatus (NOT the rethrow contract of
            // the run-counter writes below).
            logger.StatusUpdateFailed(e, status, taskId);
        }
    }

    /// <summary>
    /// Advances the run counter via a single data-modifying CTE. The RunsAudit decision for ErrorsOnly
    /// depends on the ROW's Status/Exception (NOT a constant), so it is evaluated SERVER-SIDE in the CTE —
    /// it cannot be a single C# boolean. The UPDATE never mutates Status/Exception, so its <c>RETURNING</c>
    /// yields the pre-update values the audit must record. The run counter SATURATES at int.MaxValue instead
    /// of overflowing, matching the base and the other providers; failures propagate so the scheduler never
    /// advances on unpersisted state.
    /// </summary>
    public override async Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                AuditLevel auditLevel)
    {
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var sql = $"""

                   WITH updated AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "ExecutionTimeMs" = @execTime,
                           "NextRunUtc"      = @nextRun,
                           "CurrentRunCount" = CASE WHEN COALESCE("CurrentRunCount", 0) >= 2147483647 THEN 2147483647 ELSE COALESCE("CurrentRunCount", 0) + 1 END
                       WHERE "Id" = @taskId
                       RETURNING "Status", "Exception"
                   )
                   INSERT INTO "{_schema}"."RunsAudit" ("QueuedTaskId", "ExecutedAt", "ExecutionTimeMs", "Status", "Exception")
                   SELECT @taskId, now(), @execTime, u."Status", u."Exception"
                   FROM updated u
                   -- An unknown level audits like Full, matching AuditPolicy: only ErrorsOnly (2) and None (3) skip.
                   WHERE (@auditLevel NOT IN (2, 3))
                      OR (@auditLevel = 2 AND (u."Status" = 'Failed' OR (u."Exception" IS NOT NULL AND u."Exception" <> '')));
                   """;

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql,
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", (object?)nextRun?.ToUniversalTime() ?? DBNull.Value),
                new NpgsqlParameter("auditLevel", (int)auditLevel)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Propagate (do NOT swallow) — a failed counter persist must not advance the schedule on
            // unpersisted state; the recoverable row is re-run instead.
            logger.CurrentRunUpdateFailed(e, taskId);
            throw;
        }
    }

    /// <summary>
    /// Completes a recurring occurrence via a single data-modifying CTE: marks the task Completed AND advances
    /// the run counter / next run atomically, so a crash can never split the two and resurrect the finished
    /// occurrence at recovery. The audited Status/Exception are the CONSTANTS <c>Completed</c>/<c>NULL</c>, so
    /// the audit gates depend ONLY on the AuditLevel and are computed in C# (no pre-update read needed):
    /// StatusAudit at Full only, RunsAudit at Full+Minimal — matching usp_CompleteRecurringRun and the EF base.
    /// Propagates on failure, same as
    /// <see cref="UpdateCurrentRun(Guid, double, DateTimeOffset?, AuditLevel, int)"/>.
    /// </summary>
    public override async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel)
    {
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var statusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
        var runsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

        // NextRunUtc is assigned UNCONDITIONALLY (a NULL makes the series terminal/non-recoverable; preserving
        // the old value would resurrect a finished series). ins_status runs even though the final query does
        // not reference it (Postgres executes every data-modifying CTE exactly once).
        var sql = $"""

                   WITH updated AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "Status"           = 'Completed',
                           "Exception"        = NULL,
                           "LastExecutionUtc" = now(),
                           "ExecutionTimeMs"  = @execTime,
                           "NextRunUtc"       = @nextRun,
                           "CurrentRunCount"  = CASE WHEN COALESCE("CurrentRunCount", 0) >= 2147483647 THEN 2147483647 ELSE COALESCE("CurrentRunCount", 0) + 1 END
                       WHERE "Id" = @taskId
                       RETURNING "Id"
                   ),
                   ins_status AS (
                       INSERT INTO "{_schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
                       SELECT @taskId, now(), 'Completed', NULL FROM updated WHERE @statusAudit
                       RETURNING "Id"
                   )
                   INSERT INTO "{_schema}"."RunsAudit" ("QueuedTaskId", "ExecutedAt", "ExecutionTimeMs", "Status", "Exception")
                   SELECT @taskId, now(), @execTime, 'Completed', NULL FROM updated WHERE @runsAudit;
                   """;

        try
        {
            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(sql,
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", NpgsqlDbType.TimestampTz)
                    { Value = (object?)nextRun?.ToUniversalTime() ?? DBNull.Value },
                new NpgsqlParameter("statusAudit", statusAudit),
                new NpgsqlParameter("runsAudit", runsAudit)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Propagate — a failed completion must not advance the schedule on unpersisted state.
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }
    }

    // ---- Durable occurrences (writable CTEs) ------------------------------------------------------
    // Materialization runs once per occurrence and the versioned advances once per run, so they get the same
    // treatment as the three hot writes above: ONE data-modifying CTE, hence one statement and atomic by
    // construction. The rarer administrative operations inherit the EF base, like the other once-per-series
    // writes already do.

    /// <summary>
    /// Inserts one occurrence and advances the schedule cursor in a single statement. The outcome is decided
    /// server-side in a <c>decision</c> CTE and returned, so the caller learns WHICH race it lost without a
    /// second round-trip. <c>FOR UPDATE</c> on the schedule row is what serializes two materializers reading
    /// the same cursor.
    /// </summary>
    public override async Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The INSERT below writes the canonical occurrence shape; stamping it on the caller's entity too
        // keeps the object it goes on using (scheduling the child) identical to the row that was stored.
        occurrence.ApplyOccurrenceContract(parentId, expectedScheduleVersion);
        occurrence.NormalizeTimestampsToUtc();

        if (occurrence.ScheduledExecutionUtc is not { } slotUtc)
            throw new ArgumentException("An occurrence must carry its nominal slot.", nameof(occurrence));

        var finalizeAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);

        // Outcome codes mirror OccurrenceMaterializationOutcome.
        var sql = $"""

                   WITH parent AS (
                       SELECT "Id", "ScheduleVersion", "NextRunUtc", "Status"
                       FROM "{_schema}"."QueuedTasks"
                       WHERE "Id" = @parentId
                       FOR UPDATE
                   ),
                   decision AS (
                       SELECT CASE
                           WHEN NOT EXISTS (SELECT 1 FROM parent)                                   THEN 4
                           WHEN (SELECT "Status" FROM parent) = 'Cancelled'                       THEN 4
                           WHEN (SELECT "NextRunUtc" FROM parent) IS NULL                         THEN 4
                           WHEN (SELECT "ScheduleVersion" FROM parent) <> @expectedVersion        THEN 3
                           WHEN @expectedCursor IS NULL
                                OR (SELECT "NextRunUtc" FROM parent) <> @expectedCursor           THEN 2
                           WHEN EXISTS (SELECT 1 FROM "{_schema}"."QueuedTasks"
                                        WHERE "ParentTaskId" = @parentId
                                          AND "ScheduledExecutionUtc" = @slotUtc)                 THEN 1
                           ELSE 0
                       END AS outcome
                   ),
                   inserted AS (
                       INSERT INTO "{_schema}"."QueuedTasks"
                           ("Id", "CreatedAtUtc", "ExecutionTimeMs", "ScheduledExecutionUtc", "Type", "Request",
                            "Handler", "IsRecurring", "CurrentRunCount", "QueueName", "AuditLevel", "Status",
                            "ParentTaskId", "RuntimeInfo", "ScheduleVersion")
                       SELECT @occurrenceId, @createdAtUtc, 0, @slotUtc, @type, @request,
                              @handler, false, 0, @queueName, @occurrenceAuditLevel, 'WaitingQueue',
                              @parentId, @runtimeInfo, @expectedVersion
                       FROM decision WHERE outcome = 0
                       RETURNING "Id"
                   ),
                   advanced AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "NextRunUtc"       = @newCursor,
                           "CurrentRunCount"  = CASE WHEN COALESCE("CurrentRunCount", 0) >= 2147483647
                                                       THEN 2147483647 ELSE COALESCE("CurrentRunCount", 0) + 1 END,
                           "Status"           = CASE WHEN @newCursor IS NULL THEN 'Completed' ELSE "Status" END,
                           "Exception"        = CASE WHEN @newCursor IS NULL THEN NULL ELSE "Exception" END,
                           "LastExecutionUtc" = CASE WHEN @newCursor IS NULL THEN now() ELSE "LastExecutionUtc" END
                       WHERE "Id" = @parentId AND (SELECT outcome FROM decision) = 0
                       RETURNING "Id"
                   ),
                   audited AS (
                       INSERT INTO "{_schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
                       SELECT @parentId, now(), 'Completed', NULL
                       FROM decision WHERE outcome = 0 AND @newCursor IS NULL AND @finalizeAudit
                       RETURNING "Id"
                   )
                   SELECT outcome FROM decision;
                   """;

        var outcome = await ExecuteScalarAsync(dbContext, sql,
        [
            new NpgsqlParameter("parentId", parentId),
            new NpgsqlParameter("expectedVersion", expectedScheduleVersion),
            // Explicit types: these parameters can be NULL and appear only in CASE / IS NULL positions, where
            // PostgreSQL cannot infer a type from context and rejects the statement outright.
            new NpgsqlParameter("expectedCursor", NpgsqlDbType.TimestampTz)
                { Value = (object?)expectedCursorUtc?.ToUniversalTime() ?? DBNull.Value },
            new NpgsqlParameter("newCursor", NpgsqlDbType.TimestampTz)
                { Value = (object?)newCursorUtc?.ToUniversalTime() ?? DBNull.Value },
            new NpgsqlParameter("occurrenceId", occurrence.Id),
            new NpgsqlParameter("slotUtc", slotUtc.ToUniversalTime()),
            new NpgsqlParameter("createdAtUtc", occurrence.CreatedAtUtc.ToUniversalTime()),
            new NpgsqlParameter("type", occurrence.Type),
            new NpgsqlParameter("request", occurrence.Request),
            new NpgsqlParameter("handler", occurrence.Handler),
            new NpgsqlParameter("queueName", NpgsqlDbType.Text)
                { Value = (object?)occurrence.QueueName ?? DBNull.Value },
            new NpgsqlParameter("occurrenceAuditLevel", NpgsqlDbType.Integer)
                { Value = (object?)occurrence.AuditLevel ?? DBNull.Value },
            new NpgsqlParameter("runtimeInfo", NpgsqlDbType.Text)
                { Value = (object?)occurrence.RuntimeInfo ?? DBNull.Value },
            new NpgsqlParameter("finalizeAudit", finalizeAudit)
        ], ct).ConfigureAwait(false);

        return outcome == null
                   ? OccurrenceMaterializationOutcome.ParentInactive
                   : (OccurrenceMaterializationOutcome)Convert.ToInt32(outcome, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Cancels a schedule and its still-waiting occurrences. Two statements in one transaction: the schedule
    /// row is LOCKED first, then cancelled together with its children.
    /// </summary>
    /// <remarks>
    /// The lock cannot be folded into the cancelling statement: under READ COMMITTED a statement runs on a
    /// snapshot taken BEFORE it waits on a row lock, so an occurrence a materializer commits while the cancel
    /// is blocked is invisible to it, and the schedule would end up <c>Cancelled</c> with a fresh
    /// <c>WaitingQueue</c> child free to run. Taking the materializer's own <c>FOR UPDATE</c> in a statement
    /// of its own gives the next statement a snapshot that contains the child.
    /// </remarks>
    public override async Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var             database  = ((DbContext)dbContext).Database;

        // Cancelled carries no exception, so only AuditLevel.Full audits it.
        var createAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Cancelled, null);

        await using var transaction = await database.BeginTransactionAsync(ct).ConfigureAwait(false);

        await ExecuteScalarAsync(dbContext,
            $"""SELECT "Id" FROM "{_schema}"."QueuedTasks" WHERE "Id" = @parentId FOR UPDATE""",
            [new NpgsqlParameter("parentId", parentId)], ct).ConfigureAwait(false);

        // Occurrences already InProgress own a live delivery and are left to finish on their own.
        var sql = $"""

                   WITH cancelled AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "Status" = 'Cancelled'
                       WHERE "Id" = @parentId
                          OR ("ParentTaskId" = @parentId
                              AND "Status" IN ('WaitingQueue', 'Queued', 'Pending', 'ServiceStopped'))
                       RETURNING "Id"
                   )
                   INSERT INTO "{_schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
                   SELECT "Id", now(), 'Cancelled', NULL FROM cancelled WHERE @createAudit;
                   """;

        await database.ExecuteSqlRawAsync(sql, [
            new NpgsqlParameter("parentId", parentId),
            new NpgsqlParameter("createAudit", createAudit)
        ], ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs,
                                                                   DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                   int expectedScheduleVersion)
    {
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        // Same CTE as the unversioned overload with the schedule version added to the WHERE, and a final
        // count so the caller can tell "applied" from "someone rescheduled under me".
        var sql = $"""

                   WITH updated AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "ExecutionTimeMs" = @execTime,
                           "NextRunUtc"      = @nextRun,
                           "CurrentRunCount" = CASE WHEN COALESCE("CurrentRunCount", 0) >= 2147483647
                                                      THEN 2147483647 ELSE COALESCE("CurrentRunCount", 0) + 1 END
                       WHERE "Id" = @taskId AND "ScheduleVersion" = @expectedVersion
                       RETURNING "Status", "Exception"
                   ),
                   audited AS (
                       INSERT INTO "{_schema}"."RunsAudit" ("QueuedTaskId", "ExecutedAt", "ExecutionTimeMs", "Status", "Exception")
                       SELECT @taskId, now(), @execTime, u."Status", u."Exception"
                       FROM updated u
                       -- An unknown level audits like Full, matching AuditPolicy: only ErrorsOnly (2) and None (3) skip.
                   WHERE (@auditLevel NOT IN (2, 3))
                          OR (@auditLevel = 2 AND (u."Status" = 'Failed' OR (u."Exception" IS NOT NULL AND u."Exception" <> '')))
                       RETURNING "Id"
                   )
                   SELECT count(*) FROM updated;
                   """;

        try
        {
            var applied = await ExecuteScalarAsync(dbContext, sql,
            [
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", NpgsqlDbType.TimestampTz)
                    { Value = (object?)nextRun?.ToUniversalTime() ?? DBNull.Value },
                new NpgsqlParameter("auditLevel", (int)auditLevel),
                new NpgsqlParameter("expectedVersion", expectedScheduleVersion)
            ], CancellationToken.None).ConfigureAwait(false);

            return Convert.ToInt64(applied, CultureInfo.InvariantCulture) > 0
                       ? ScheduleCasResult.Applied
                       : ScheduleCasResult.VersionMismatch;
        }
        catch (Exception e)
        {
            // Propagate, exactly like the unversioned overload.
            logger.CurrentRunUpdateFailed(e, taskId);
            throw;
        }
    }

    /// <inheritdoc />
    public override async Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs,
                                                                       DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                       int expectedScheduleVersion)
    {
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var statusAudit = AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null);
        var runsAudit   = AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null);

        var sql = $"""

                   WITH updated AS (
                       UPDATE "{_schema}"."QueuedTasks"
                       SET "Status"           = 'Completed',
                           "Exception"        = NULL,
                           "LastExecutionUtc" = now(),
                           "ExecutionTimeMs"  = @execTime,
                           "NextRunUtc"       = @nextRun,
                           "CurrentRunCount"  = CASE WHEN COALESCE("CurrentRunCount", 0) >= 2147483647
                                                       THEN 2147483647 ELSE COALESCE("CurrentRunCount", 0) + 1 END
                       WHERE "Id" = @taskId AND "ScheduleVersion" = @expectedVersion
                       RETURNING "Id"
                   ),
                   ins_status AS (
                       INSERT INTO "{_schema}"."StatusAudit" ("QueuedTaskId", "UpdatedAtUtc", "NewStatus", "Exception")
                       SELECT @taskId, now(), 'Completed', NULL FROM updated WHERE @statusAudit
                       RETURNING "Id"
                   ),
                   ins_runs AS (
                       INSERT INTO "{_schema}"."RunsAudit" ("QueuedTaskId", "ExecutedAt", "ExecutionTimeMs", "Status", "Exception")
                       SELECT @taskId, now(), @execTime, 'Completed', NULL FROM updated WHERE @runsAudit
                       RETURNING "Id"
                   )
                   SELECT count(*) FROM updated;
                   """;

        try
        {
            var applied = await ExecuteScalarAsync(dbContext, sql,
            [
                new NpgsqlParameter("taskId", taskId),
                new NpgsqlParameter("execTime", executionTimeMs),
                new NpgsqlParameter("nextRun", NpgsqlDbType.TimestampTz)
                    { Value = (object?)nextRun?.ToUniversalTime() ?? DBNull.Value },
                new NpgsqlParameter("statusAudit", statusAudit),
                new NpgsqlParameter("runsAudit", runsAudit),
                new NpgsqlParameter("expectedVersion", expectedScheduleVersion)
            ], CancellationToken.None).ConfigureAwait(false);

            return Convert.ToInt64(applied, CultureInfo.InvariantCulture) > 0
                       ? ScheduleCasResult.Applied
                       : ScheduleCasResult.VersionMismatch;
        }
        catch (Exception e)
        {
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }
    }

    /// <summary>
    /// Runs a statement that RETURNS a single value. <c>ExecuteSqlRaw</c> only reports affected rows, and for
    /// a data-modifying CTE that count belongs to the outer statement, not to the branch the caller cares
    /// about — so the outcome comes back as a scalar instead. The connection is opened and closed through EF
    /// so a pooled context is returned in the state EF expects.
    /// </summary>
    private static async Task<object?> ExecuteScalarAsync(ITaskStoreDbContext dbContext, string sql,
                                                          NpgsqlParameter[] parameters, CancellationToken ct)
    {
        var database = ((DbContext)dbContext).Database;

        await database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = database.CurrentTransaction?.GetDbTransaction();

            foreach (var parameter in parameters)
                command.Parameters.Add(parameter);

            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
