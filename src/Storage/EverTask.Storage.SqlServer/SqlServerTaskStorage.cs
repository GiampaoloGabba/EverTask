using System.Data;
using System.Globalization;
using System.Linq.Expressions;
using EverTask.Abstractions;
using EverTask.Logger;
using Microsoft.Data.SqlClient;

namespace EverTask.Storage.SqlServer;

/// <summary>
/// SQL Server-specific task storage implementation. Overrides the hot-path writes — <c>SetStatus</c>,
/// <c>UpdateCurrentRun</c> and <c>CompleteRecurringRun</c> — to use stored procedures for optimal
/// performance (a single atomic roundtrip each). Everything else is inherited from the EF Core base.
/// </summary>
// The primary-ctor 'contextFactory' is deliberately re-declared as a private field: the base captures it too,
// and using the parameter directly from a method body would capture the same value twice (CS9107).
public class SqlServerTaskStorage(
    ITaskStoreDbContextFactory contextFactory,
    IEverTaskLogger<SqlServerTaskStorage> logger,
    IOptions<ITaskStoreOptions> storeOptions)
    : EfCoreTaskStorage(contextFactory, logger)
{
    private readonly ITaskStoreDbContextFactory _contextFactory = contextFactory;
    private readonly string _schema = string.IsNullOrEmpty(storeOptions.Value.SchemaName) ? "dbo" : storeOptions.Value.SchemaName!;

    // Must match the migrations' schema fallback (dbo) so the hot-path procs resolve to where they were
    // created. A null/empty SchemaName lands the procs in dbo; the old `?? "EverTask"` made runtime EXEC
    // a different schema than the procs lived in -> proc-not-found, swallowed in SetStatus, recoverable
    // row -> re-dispatch -> double execution. Mirrors PostgresTaskStorage's `?? "public"`.

    /// <summary>
    /// Sets task status using optimized stored procedure.
    /// Performs audit insert (if required by AuditLevel) + task update in a single atomic database roundtrip.
    /// </summary>
    public override async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                                            double? executionTimeMs = null, CancellationToken ct = default)
    {
        logger.SettingTaskStatus(taskId, status);

        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
            logger.StatusUpdateFailed(e, status, taskId);
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
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var sql = $"EXEC [{_schema}].[usp_UpdateCurrentRun] @TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                new SqlParameter("@TaskId", taskId),
                new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                new SqlParameter("@AuditLevel", (int)auditLevel)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do not swallow) so a failed counter persist does not advance the
            // schedule on unpersisted state; the recoverable row is re-run instead.
            logger.CurrentRunUpdateFailed(e, taskId);
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
        logger.CompletingRecurringRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        try
        {
            var sql = $"EXEC [{_schema}].[usp_CompleteRecurringRun] @TaskId, @ExecutionTimeMs, @NextRunUtc, @AuditLevel";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                new SqlParameter("@TaskId", taskId),
                new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                new SqlParameter("@AuditLevel", (int)auditLevel)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Residual D: propagate (do not swallow) — a failed completion must NOT advance the schedule on
            // unpersisted state; the recoverable row is re-run instead. Same contract as UpdateCurrentRun,
            // deliberately NOT the swallow pattern of SetStatus.
            logger.RecurringRunCompletionFailed(e, taskId);
            throw;
        }
    }

    // ---- Durable occurrences (stored procedures) --------------------------------------------------
    // Materialization runs once per occurrence and the versioned advances once per run, so they get the
    // same treatment as the three pre-existing hot writes: one procedure, one round-trip, one transaction.
    // The rarer administrative operations (requeue, halt, reschedule, conditional finalize) inherit the EF
    // base, exactly like the other once-per-series writes already do.

    /// <summary>
    /// Materializes one occurrence and advances the schedule cursor through
    /// <c>usp_MaterializeOccurrence</c>. The compare-and-swap on version and cursor lives inside the
    /// procedure, under an UPDLOCK on the schedule row, so two hosts reading the same cursor cannot both
    /// advance it — the outcome tells the loser which race it lost.
    /// </summary>
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

        var outcome = new SqlParameter("@Outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };

        var sql = $"EXEC [{_schema}].[usp_MaterializeOccurrence] @ParentId, @ExpectedScheduleVersion, " +
                  "@ExpectedCursorUtc, @NewCursorUtc, @AuditLevel, @OccurrenceId, @SlotUtc, @CreatedAtUtc, " +
                  "@Type, @Request, @Handler, @QueueName, @OccurrenceAuditLevel, @RuntimeInfo, @Outcome OUTPUT";

        await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
            sql,
            [
                new SqlParameter("@ParentId", parentId),
                new SqlParameter("@ExpectedScheduleVersion", expectedScheduleVersion),
                new SqlParameter("@ExpectedCursorUtc", (object?)expectedCursorUtc ?? DBNull.Value),
                new SqlParameter("@NewCursorUtc", (object?)newCursorUtc ?? DBNull.Value),
                new SqlParameter("@AuditLevel", (int)auditLevel),
                new SqlParameter("@OccurrenceId", occurrence.Id),
                new SqlParameter("@SlotUtc", slotUtc),
                new SqlParameter("@CreatedAtUtc", occurrence.CreatedAtUtc),
                new SqlParameter("@Type", occurrence.Type),
                new SqlParameter("@Request", occurrence.Request),
                new SqlParameter("@Handler", occurrence.Handler),
                new SqlParameter("@QueueName", (object?)occurrence.QueueName ?? DBNull.Value),
                new SqlParameter("@OccurrenceAuditLevel", (object?)occurrence.AuditLevel ?? DBNull.Value),
                new SqlParameter("@RuntimeInfo", (object?)occurrence.RuntimeInfo ?? DBNull.Value),
                outcome
            ],
            ct).ConfigureAwait(false);

        return (OccurrenceMaterializationOutcome)Convert.ToInt32(outcome.Value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override async Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        await using var dbContext = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sql = $"EXEC [{_schema}].[usp_CancelSchedule] @ParentId, @AuditLevel";

        await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
            sql,
            [
                new SqlParameter("@ParentId", parentId),
                new SqlParameter("@AuditLevel", (int)auditLevel)
            ],
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs,
                                                                   DateTimeOffset? nextRun, AuditLevel auditLevel,
                                                                   int expectedScheduleVersion)
    {
        logger.UpdatingCurrentRun(taskId);

        await using var dbContext = await _contextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var applied = new SqlParameter("@Applied", SqlDbType.Bit) { Direction = ParameterDirection.Output };

        try
        {
            var sql = $"EXEC [{_schema}].[usp_UpdateCurrentRunCas] @TaskId, @ExecutionTimeMs, @NextRunUtc, " +
                      "@AuditLevel, @ExpectedScheduleVersion, @Applied OUTPUT";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel),
                    new SqlParameter("@ExpectedScheduleVersion", expectedScheduleVersion),
                    applied
                ]).ConfigureAwait(false);
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

        var applied = new SqlParameter("@Applied", SqlDbType.Bit) { Direction = ParameterDirection.Output };

        try
        {
            var sql = $"EXEC [{_schema}].[usp_CompleteRecurringRunCas] @TaskId, @ExecutionTimeMs, @NextRunUtc, " +
                      "@AuditLevel, @ExpectedScheduleVersion, @Applied OUTPUT";

            await ((DbContext)dbContext).Database.ExecuteSqlRawAsync(
                sql,
                [
                    new SqlParameter("@TaskId", taskId),
                    new SqlParameter("@ExecutionTimeMs", executionTimeMs),
                    new SqlParameter("@NextRunUtc", (object?)nextRun ?? DBNull.Value),
                    new SqlParameter("@AuditLevel", (int)auditLevel),
                    new SqlParameter("@ExpectedScheduleVersion", expectedScheduleVersion),
                    applied
                ]).ConfigureAwait(false);
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

    // ---- Reads chosen as deadlock victims ---------------------------------------------------------
    // SQL Server takes shared locks to read, and a read that resolves a row through a nonclustered index
    // takes them in the OPPOSITE order to a write: the read locks the index entry and then the clustered
    // row it points at, while an UPDATE locks the clustered row and then every index entry that has to
    // follow it. The startup recovery page is exactly that kind of read -- it walks IX_QueuedTasks_Recovery
    // in (CreatedAtUtc, Id) order and looks the rows up in the clustered index -- and it runs while
    // occurrences and tasks are already executing and writing their Status, which IX_QueuedTasks_Recovery
    // and IX_QueuedTasks_Status both carry. The cycle that pair forms is a genuine deadlock, and the engine
    // resolves it by killing whichever transaction is cheapest to roll back: a read, having written
    // nothing, is always cheaper than the write it collided with, and any OTHER read queued behind the
    // same clustered row can be picked instead. So the victim is a read that never asked for anything but
    // a consistent answer.
    //
    // The answer SQL Server gives with error 1205 is "Rerun the transaction", and for a read that is
    // literally all it takes: a read has no effect to undo and no state to reconcile, so re-running it is
    // indistinguishable from having asked a moment later. What is NOT acceptable is the alternative --
    // letting 1205 out of the storage -- because on the recovery path it aborts the whole startup recovery
    // (WorkerService logs RecoveryFailed and the rest of the backlog waits for the next restart).
    // This applies to SQL Server alone: PostgreSQL and MySQL answer plain reads from a consistent snapshot
    // and take no shared locks, and SQLite serializes writers outright.
    //
    // Writes are deliberately NOT re-run here. Each of them is a compare-and-swap or a single-transaction
    // procedure whose caller already knows what to do with a lost race, and re-running one behind its
    // caller's back would decide that for it.

    /// <summary>SQL Server error 1205 — "was deadlocked on lock resources … Rerun the transaction".</summary>
    private const int DeadlockVictim = 1205;

    /// <summary>How many times a read is run in total before a deadlock is allowed to surface.</summary>
    private const int MaxReadAttempts = 3;

    /// <summary>Grows per attempt so two readers that collided do not immediately collide again.</summary>
    private static readonly TimeSpan RereadDelay = TimeSpan.FromMilliseconds(25);

    /// <inheritdoc />
    public override Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where,
                                           CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.Get(where, token), nameof(Get), ct);

    /// <inheritdoc />
    public override Task<QueuedTask[]> GetAll(CancellationToken ct = default) =>
        RereadOnDeadlockAsync(base.GetAll, nameof(GetAll), ct);

    /// <inheritdoc />
    /// <remarks>
    /// Only the clock-carrying overload is overridden: overriding the legacy one would make the base take
    /// this provider for a pre-4.0 storage and route every call through it, dropping the caller's
    /// <paramref name="nowUtc"/> (P9). The core calls this overload exclusively.
    /// </remarks>
    public override Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt,
                                                       Guid? lastId, int take, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.RetrievePending(nowUtc, lastCreatedAt, lastId, take, token),
            nameof(RetrievePending), ct);

    /// <inheritdoc />
    public override Task<int> GetCurrentRunCount(Guid taskId) =>
        RereadOnDeadlockAsync(_ => base.GetCurrentRunCount(taskId), nameof(GetCurrentRunCount),
            CancellationToken.None);

    /// <inheritdoc />
    public override Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetByTaskKey(taskKey, token), nameof(GetByTaskKey), ct);

    /// <inheritdoc />
    public override Task<QueuedTask[]> GetOccurrences(Guid parentId, bool nonTerminalOnly = false,
                                                      CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetOccurrences(parentId, nonTerminalOnly, token),
            nameof(GetOccurrences), ct);

    /// <inheritdoc />
    public override Task<OccurrencePage> GetOccurrencesPage(Guid parentId, bool nonTerminalOnly, int skip, int take,
                                                            CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetOccurrencesPage(parentId, nonTerminalOnly, skip, take, token),
            nameof(GetOccurrencesPage), ct);

    /// <inheritdoc />
    public override Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastRunStarts(
        IReadOnlyCollection<Guid> taskIds, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetLastRunStarts(taskIds, token), nameof(GetLastRunStarts), ct);

    /// <inheritdoc />
    public override Task<StatusAudit[]> GetStatusAudits(Guid taskId, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetStatusAudits(taskId, token), nameof(GetStatusAudits), ct);

    /// <inheritdoc />
    public override Task<RunsAudit[]> GetRunsAudits(Guid taskId, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetRunsAudits(taskId, token), nameof(GetRunsAudits), ct);

    /// <inheritdoc />
    public override Task<AuditPage<StatusAudit>> GetStatusAuditsPage(Guid taskId, int skip, int take,
                                                                     CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetStatusAuditsPage(taskId, skip, take, token),
            nameof(GetStatusAuditsPage), ct);

    /// <inheritdoc />
    public override Task<AuditPage<RunsAudit>> GetRunsAuditsPage(Guid taskId, int skip, int take,
                                                                 CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.GetRunsAuditsPage(taskId, skip, take, token),
            nameof(GetRunsAuditsPage), ct);

    /// <inheritdoc />
    public override Task<int> CountActiveOccurrences(Guid parentId, CancellationToken ct = default) =>
        RereadOnDeadlockAsync(token => base.CountActiveOccurrences(parentId, token),
            nameof(CountActiveOccurrences), ct);

    /// <summary>Runs a read, re-running it while SQL Server keeps picking it as the deadlock victim.</summary>
    private async Task<T> RereadOnDeadlockAsync<T>(Func<CancellationToken, Task<T>> read, string operation,
                                                   CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (attempt < MaxReadAttempts && IsDeadlockVictim(e))
            {
                logger.RereadAfterDeadlock(operation, attempt);

                await Task.Delay(RereadDelay * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Whether the failure is the engine reporting this session as the deadlock victim, wherever in the
    /// exception chain it landed — a query surfaces it bare, a <c>SaveChanges</c> wraps it.
    /// </summary>
    private static bool IsDeadlockVictim(Exception exception) =>
        exception is SqlException { Number: DeadlockVictim }
        || (exception.InnerException is { } inner && IsDeadlockVictim(inner));
}
