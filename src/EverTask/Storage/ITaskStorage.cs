using System.Linq.Expressions;

namespace EverTask.Storage;

/// <summary>
/// Represents a storage interface for tasks.
/// </summary>
public interface ITaskStorage
{
    /// <summary>
    /// True when this storage implements the atomic durable-occurrence operations
    /// (<see cref="MaterializeOccurrence"/> and friends). Dispatching a durable schedule against a storage
    /// that returns false fails fast instead of degrading to a non-atomic emulation.
    /// </summary>
    /// <remarks>
    /// Capability and implementation are inseparable: overriding the operations without flipping this flag
    /// (or the reverse) is what a "quasi-atomic" fallback would look like, and that is exactly what would
    /// leave an occurrence inserted without its cursor advance — or a cursor advanced with no occurrence.
    /// </remarks>
    bool SupportsDurableOccurrences => false;

    /// <summary>
    /// True when this storage implements the compare-and-swap overloads guarded by
    /// <see cref="QueuedTask.ScheduleVersion"/>. Runtime schedule management requires it: without a real CAS
    /// a reschedule could report success while a completion in flight silently overwrote it.
    /// </summary>
    bool SupportsScheduleVersioning => false;

    /// <summary>
    /// Retrieves an array of queued tasks based on a specified condition.
    /// </summary>
    /// <param name="where">Expression to filter the queued tasks.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of queued tasks that match the condition.</returns>
    Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all queued tasks.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of all queued tasks.</returns>
    Task<QueuedTask[]> GetAll(CancellationToken ct = default);

    /// <summary>
    /// Persists a task in the queue.
    /// </summary>
    /// <param name="executor">The queued task to be persisted.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Persist(QueuedTask executor, CancellationToken ct = default);

    /// <summary>
    /// Retrieves pending tasks using keyset pagination ordered by creation timestamp.
    /// </summary>
    /// <param name="lastCreatedAt">
    /// The creation timestamp of the last processed task. Pass <c>null</c> to retrieve the first page.
    /// </param>
    /// <param name="lastId">
    /// The identifier of the last processed task (used as tie-breaker when timestamps are equal).
    /// </param>
    /// <param name="take">Maximum number of tasks to retrieve.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of pending tasks (up to <paramref name="take"/> count).</returns>
    Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default);

    /// <summary>
    /// Same as <see cref="RetrievePending(DateTimeOffset?,Guid?,int,CancellationToken)"/>, but evaluated
    /// against the caller's clock instead of the storage's own. This is the overload the core always calls.
    /// </summary>
    /// <remarks>
    /// Scheduling decisions must all resolve "now" from one <see cref="TimeProvider"/>, so a test can drive
    /// the whole pipeline deterministically and a clock skew between the host and the database cannot make
    /// recovery disagree with the scheduler. The default here simply delegates to the legacy signature: a
    /// custom storage that overrode that one keeps its own implementation (and its own atomicity) instead of
    /// being bypassed — at the cost of resolving the clock itself, which is documented. The built-in
    /// providers override this overload and honour <paramref name="nowUtc"/>.
    /// </remarks>
    Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                       CancellationToken ct = default) =>
        RetrievePending(lastCreatedAt, lastId, take, ct);

    /// <summary>
    /// Sets a task's status to queued.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to queued ONLY if it is still in a recoverable status (the same
    /// statuses <see cref="RetrievePending"/> returns). Used by the startup recovery so that a
    /// row whose live copy terminally finished between the recovery's page read and its
    /// re-dispatch is never resurrected (SetQueued over Completed = a second execution).
    /// </summary>
    /// <remarks>
    /// Implementations should make the check-and-set atomic (a conditional UPDATE). The default
    /// implementation is a best-effort, NON-atomic guard for custom storages that have not overridden
    /// it: it reads the row and applies the canonical recoverable predicate
    /// (<see cref="QueuedTask.IsRecoverable"/>), transitioning to Queued only if it is still
    /// recoverable. It is deliberately NOT an unconditional <see cref="SetQueued"/> — that would
    /// re-introduce the recovery double-execution (a row that terminally finished after the page-read
    /// resurrected to Queued and executed a second time). Built-in providers override this with an
    /// atomic check-and-set.
    /// </remarks>
    /// <returns>True when the task was set to queued; false when it was skipped because it is
    /// no longer recoverable.</returns>
    async Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        var existing = (await Get(t => t.Id == taskId, ct).ConfigureAwait(false)).FirstOrDefault();
        if (existing is null || !existing.IsRecoverable(DateTimeOffset.UtcNow))
            return false;

        await SetQueued(taskId, auditLevel, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Same as <see cref="TrySetQueuedIfRecoverable(Guid,AuditLevel,CancellationToken)"/>, evaluated against
    /// the caller's clock. This is the overload the core always calls; see the
    /// <see cref="RetrievePending(DateTimeOffset,DateTimeOffset?,Guid?,int,CancellationToken)"/> remarks for
    /// why the legacy signature stays intact and is what this default delegates to.
    /// </summary>
    Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                         CancellationToken ct = default) =>
        TrySetQueuedIfRecoverable(taskId, auditLevel, ct);

    /// <summary>
    /// Sets a task's status to in progress.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to completed.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="executionTimeMs">The execution time in milliseconds.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel);

    /// <summary>
    /// Sets a task's status to manually cancelled by the user.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel);

    /// <summary>
    /// Sets a task's status to SystemStopped, indicating that the task was cancelled by the background service while stopping.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="exception">The exception that caused the task to be cancelled.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel);

    /// <summary>
    /// Sets the status of a task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="status">The new status of the task.</param>
    /// <param name="exception">Optional exception related to the task status change.</param>
    /// <param name="auditLevel">Audit level for this task (determines if audit record should be created).</param>
    /// <param name="executionTimeMs">Optional execution time in milliseconds (used when completing tasks).</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                       double? executionTimeMs = null, CancellationToken ct = default);

    /// <summary>
    /// Get the current run counter for this task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <returns>The current run count for this task.</returns>
    Task<int> GetCurrentRunCount(Guid taskId);

    /// <summary>
    /// Advances the run counter by exactly one real execution and updates the next run / execution time.
    /// Occurrences skipped to realign the schedule after a downtime do NOT count toward the counter:
    /// <c>CurrentRunCount</c> tracks real executions only (== <see cref="QueuedTask.RunsAudits"/> rows),
    /// so <see cref="QueuedTask.MaxRuns"/> means "run this many times".
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="executionTimeMs">The execution time in milliseconds.</param>
    /// <param name="nextRun">The next run date.</param>
    /// <param name="auditLevel">Audit level for this task (determines if audit record should be created).</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel);

    /// <summary>
    /// Marks a recurring occurrence <see cref="QueuedTaskStatus.Completed"/> AND advances the run
    /// counter / next run in a SINGLE atomic operation. The two used to be separate writes
    /// (<see cref="SetCompleted"/> then <see cref="UpdateCurrentRun(Guid,double,DateTimeOffset?,AuditLevel)"/>),
    /// so a crash between them left the row Completed but not advanced — recovery then re-dispatched the
    /// already-finished occurrence and a MaxRuns-bounded series ran one extra time (CU14/L29).
    /// </summary>
    /// <remarks>
    /// Default interface member: the non-atomic two-write fallback, for custom storages that have not
    /// overridden it (same behaviour as before — graceful degradation). Built-in providers override it
    /// with a single transactional write.
    /// </remarks>
    /// <param name="taskId">The ID of the recurring task.</param>
    /// <param name="executionTimeMs">The execution time in milliseconds.</param>
    /// <param name="nextRun">The next run date (null when the series is exhausted).</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    async Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                    AuditLevel auditLevel)
    {
        await SetCompleted(taskId, executionTimeMs, auditLevel).ConfigureAwait(false);
        await UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel).ConfigureAwait(false);
    }

    /// <summary>
    /// Finalizes a recurring series that ENDED on a skipped occurrence (its next slot fell past
    /// <see cref="QueuedTask.RunUntil"/>): sets <see cref="QueuedTaskStatus.Completed"/> AND clears
    /// <see cref="QueuedTask.NextRunUtc"/> in ONE atomic write, WITHOUT advancing the run counter and
    /// WITHOUT writing a runs-audit row (the skipped occurrence never executed — Option B).
    /// </summary>
    /// <remarks>
    /// A Completed recurring row left with a non-null <see cref="QueuedTask.NextRunUtc"/> stays
    /// <see cref="QueuedTask.IsRecoverable"/> and is resurrected by recovery while <c>RunUntil &gt;= now</c> —
    /// so a plain <see cref="SetCompleted"/> here would revive the finished series. This is the terminal
    /// counterpart of <see cref="CompleteRecurringRun"/> for the no-run skip path.
    /// <para>
    /// Default interface member: a non-atomic two-write fallback (<see cref="SetCompleted"/> then clear
    /// <see cref="QueuedTask.NextRunUtc"/> via <see cref="UpdateTask"/>) for custom storages that have not
    /// overridden it. Built-in providers override it with a single transactional write.
    /// </para>
    /// </remarks>
    /// <param name="taskId">The ID of the recurring task.</param>
    /// <param name="executionTimeMs">The execution time in milliseconds.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    async Task SetRecurringSeriesCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        await SetCompleted(taskId, executionTimeMs, auditLevel).ConfigureAwait(false);

        var rows = await Get(t => t.Id == taskId).ConfigureAwait(false);
        var row  = rows.Length > 0 ? rows[0] : null;
        if (row is { NextRunUtc: not null })
        {
            row.NextRunUtc = null;
            await UpdateTask(row).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Poisons a RECURRING task TERMINALLY during startup recovery: sets <see cref="QueuedTaskStatus.Failed"/>
    /// AND clears <see cref="QueuedTask.NextRunUtc"/> in ONE atomic write, so the row stops satisfying
    /// <see cref="QueuedTask.IsRecoverable"/> and is never resurrected by recovery (P0-1).
    /// </summary>
    /// <remarks>
    /// A plain <see cref="SetStatus"/>(Failed) leaves <see cref="QueuedTask.NextRunUtc"/> set, and a recurring
    /// Failed row with a non-null NextRunUtc stays <see cref="QueuedTask.IsRecoverable"/> — so recovery revives
    /// it and re-poisons it at every restart (an infinite re-poison loop), or, if the underlying cause healed,
    /// re-dispatches and EXECUTES it once per restart (violating at-most-once-after-poison). Clearing NextRunUtc
    /// atomically with Failed is what terminalizes the series.
    /// <para>
    /// Use ONLY on the recovery POISON (terminalization) paths. A recurring run's TRANSIENT failure must keep
    /// going through <see cref="SetStatus"/>(Failed) WITHOUT clearing NextRunUtc, so the occurrence stays
    /// recoverable and the series retries the next slot.
    /// </para>
    /// <para>
    /// Default interface member: a non-atomic two-write fallback (<see cref="SetStatus"/>(Failed) then clear
    /// <see cref="QueuedTask.NextRunUtc"/> via <see cref="UpdateTask"/>) for custom storages that have not
    /// overridden it. Built-in providers (Memory/EfCore and the relational providers by inheritance) override
    /// it with a single transactional write — mirroring <see cref="SetRecurringSeriesCompleted"/>.
    /// </para>
    /// </remarks>
    /// <param name="taskId">The ID of the recurring task to poison.</param>
    /// <param name="exception">The exception recorded as the poison reason.</param>
    /// <param name="auditLevel">Audit level for this task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    async Task SetRecurringTaskPoisoned(Guid taskId, Exception exception, AuditLevel auditLevel,
                                        CancellationToken ct = default)
    {
        await SetStatus(taskId, QueuedTaskStatus.Failed, exception, auditLevel, null, ct).ConfigureAwait(false);

        var rows = await Get(t => t.Id == taskId, ct).ConfigureAwait(false);
        var row  = rows.Length > 0 ? rows[0] : null;
        if (row is { NextRunUtc: not null })
        {
            row.NextRunUtc = null;
            await UpdateTask(row, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Increments and returns the persistent count of failed startup-recovery re-dispatch attempts for
    /// a task (L18). The caller poisons the task (marks it <see cref="QueuedTaskStatus.Failed"/>) once the
    /// returned count reaches its configured limit, so a persistently failing re-dispatch is not retried
    /// at every restart forever (and the failure is no longer masked by a success summary log).
    /// </summary>
    /// <remarks>
    /// Default interface member: a no-op returning 0, so a custom storage that does not persist the
    /// counter degrades gracefully to the previous behaviour (keeps retrying — no poison, no regression).
    /// Built-in providers (Memory/EfCore/Sqlite/SqlServer) persist the counter.
    /// </remarks>
    Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>
    /// Clears the recovery-failure counter after a successful re-dispatch, so transient failures do not
    /// accumulate across restarts toward the poison limit (L18). Default interface member: no-op.
    /// </summary>
    Task ClearRecoveryFailure(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    /// Retrieves a task by its unique task key.
    /// </summary>
    /// <param name="taskKey">The unique task key.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The queued task with the specified key, or null if not found.</returns>
    Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task in storage.
    /// </summary>
    /// <param name="task">The task to update with new values.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateTask(QueuedTask task, CancellationToken ct = default);

    /// <summary>
    /// Removes a task from storage.
    /// </summary>
    /// <param name="taskId">The ID of the task to remove.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Remove(Guid taskId, CancellationToken ct = default);

    // ---- Durable occurrences and schedule versioning ----------------------------------------------
    // Every operation below is ATOMIC by contract: it either applies completely or leaves the store
    // untouched, and the compare-and-swap ones report the loss instead of forcing a stale write. The
    // defaults throw NotSupportedException on purpose — a "best effort" emulation on top of two separate
    // writes is precisely the crash window these operations exist to close, so a storage either implements
    // them (and advertises SupportsDurableOccurrences / SupportsScheduleVersioning) or refuses the feature.

    /// <summary>
    /// Inserts one occurrence of a durable schedule AND advances that schedule's cursor in a single
    /// transaction, guarded by a compare-and-swap on <paramref name="expectedScheduleVersion"/> and
    /// <paramref name="expectedCursorUtc"/>.
    /// </summary>
    /// <remarks>
    /// The outcome is decided in this order, and every implementation owes callers the same one: a schedule
    /// row that is gone, <c>Cancelled</c> or already cursorless is
    /// <see cref="OccurrenceMaterializationOutcome.ParentInactive"/>; a different version is
    /// <see cref="OccurrenceMaterializationOutcome.VersionMismatch"/>; a cursor that differs from
    /// <paramref name="expectedCursorUtc"/> — a <c>null</c> expected cursor INCLUDED, since a live schedule
    /// always has one — is <see cref="OccurrenceMaterializationOutcome.CursorMoved"/>; a slot already taken
    /// is <see cref="OccurrenceMaterializationOutcome.AlreadyExists"/>. None of the four writes anything;
    /// only <see cref="OccurrenceMaterializationOutcome.Created"/> does.
    /// <para>
    /// The row written is <see cref="QueuedTask.ApplyOccurrenceContract"/> applied to
    /// <paramref name="occurrence"/>, on EVERY backend: a fresh one-shot at
    /// <paramref name="expectedScheduleVersion"/>, with the definition, the cursor, the bounds and the task
    /// key cleared. An implementation that inserts the caller's entity as it stands must stamp that shape
    /// first, or the same call would persist a materially different row than the providers that spell it out
    /// in their INSERT column list.
    /// </para>
    /// </remarks>
    /// <param name="parentId">The schedule row that owns the occurrence.</param>
    /// <param name="expectedScheduleVersion">Schedule version the decision was computed against.</param>
    /// <param name="expectedCursorUtc">Cursor (<c>NextRunUtc</c>) the decision was computed against.</param>
    /// <param name="occurrence">The child row to insert; its slot must be set and its parent must match.</param>
    /// <param name="newCursorUtc">
    /// The cursor to leave behind. <c>null</c> ends the series: the schedule row is marked Completed with a
    /// cleared cursor IN THE SAME COMMIT, so no crash can leave a finished series recoverable.
    /// </param>
    /// <param name="auditLevel">Audit level of the schedule.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(MaterializeOccurrence)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Conditional counterpart of <see cref="SetRecurringSeriesCompleted"/>: finalizes the series only while
    /// it still carries the expected cursor, status and schedule version.
    /// </summary>
    /// <remarks>
    /// The unconditional variant overwrites whatever it finds, so a <c>Cancel</c> or a reschedule that
    /// linearized just before it would be silently replaced by <c>Completed</c>. Recovery finalization uses
    /// this one and simply gives up when it loses.
    /// <para>
    /// A null <paramref name="expectedCursorUtc"/> always loses: a schedule with no cursor is already over, so
    /// there is no live series such an expectation could describe. Read literally it would match precisely the
    /// finalized and poisoned rows, which is why every implementation refuses it up front.
    /// </para>
    /// </remarks>
    /// <returns>True when the series was finalized; false when the compare-and-swap lost.</returns>
    Task<bool> TrySetRecurringSeriesCompleted(
        Guid taskId, DateTimeOffset? expectedCursorUtc, QueuedTaskStatus expectedStatus,
        int expectedScheduleVersion, double executionTimeMs, AuditLevel auditLevel,
        CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement conditional series finalization. Use a built-in provider, or " +
            $"implement {nameof(TrySetRecurringSeriesCompleted)} atomically.");

    /// <summary>
    /// Cancels a durable schedule AND every occurrence of it still pending, in one transaction, so a
    /// materializer racing the cancel can only observe the schedule as already inactive. Occurrences already
    /// executing are left alone and run to their own end.
    /// </summary>
    /// <remarks>
    /// Audits only the rows it really changed. A schedule a concurrent <c>Remove</c> already deleted is a
    /// silent no-op — never an error, and never a status audit for a task that no longer exists.
    /// </remarks>
    Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(CancelSchedule)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Puts a terminal row (<c>Failed</c> or <c>Cancelled</c>) back into <c>Queued</c>, clearing its error
    /// while keeping its identity, history and audit trail. Refuses anything that is not terminal.
    /// </summary>
    /// <returns>True when the row was requeued.</returns>
    Task<bool> RequeueTerminal(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(RequeueTerminal)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Compare-and-swap requeue of an occurrence found stranded in a non-terminal status with no live
    /// delivery behind it: it returns to <c>Queued</c> only while it still holds
    /// <paramref name="expectedStatus"/>, so a concurrent cancel or a delivery that just picked it up wins.
    /// </summary>
    /// <returns>True when this caller won the compare-and-swap and now owns the redelivery.</returns>
    Task<bool> TryRequeueStaleOccurrence(Guid childId, QueuedTaskStatus expectedStatus, AuditLevel auditLevel,
                                         CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(TryRequeueStaleOccurrence)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Replaces a schedule's definition, cursor and bounds and bumps its
    /// <see cref="QueuedTask.ScheduleVersion"/>, only while it still carries
    /// <paramref name="expectedScheduleVersion"/>. Two concurrent reschedules cannot both win.
    /// </summary>
    /// <returns>True when the new definition was written.</returns>
    Task<bool> UpdateSchedule(Guid taskId, int expectedScheduleVersion, string recurringTaskJson,
                              string? recurringInfo, DateTimeOffset? nextRunUtc, int? maxRuns,
                              DateTimeOffset? runUntil, string? runtimeInfo, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement schedule versioning. Use a built-in provider, or implement " +
            $"{nameof(UpdateSchedule)} atomically and set {nameof(SupportsScheduleVersioning)} to true.");

    /// <summary>
    /// Persists a durable "halted" marker in <see cref="QueuedTask.RuntimeInfo"/>, guarded by a full
    /// compare-and-swap on version, cursor and status.
    /// </summary>
    /// <remarks>
    /// The full CAS is the point: a halt decided against a cursor another writer has since advanced describes
    /// a state that no longer exists and must not be written. Halting is durable so an operator, not the
    /// passage of time, is what resumes the schedule.
    /// <para>
    /// A null <paramref name="expectedCursorUtc"/> always loses, for the same reason as in
    /// <see cref="TrySetRecurringSeriesCompleted"/>: an ended series is not a schedule to halt.
    /// </para>
    /// </remarks>
    /// <returns>True when the marker was written.</returns>
    Task<bool> TryHaltSchedule(Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc,
                               QueuedTaskStatus expectedStatus, string runtimeInfo,
                               CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(TryHaltSchedule)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// <see cref="UpdateCurrentRun(Guid,double,DateTimeOffset?,AuditLevel)"/> guarded by a compare-and-swap
    /// on the schedule version: a run that finishes after a reschedule reports
    /// <see cref="ScheduleCasResult.VersionMismatch"/> instead of writing its stale next run.
    /// </summary>
    Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                             AuditLevel auditLevel, int expectedScheduleVersion) =>
        throw new NotSupportedException(
            "This storage does not implement schedule versioning. Use a built-in provider, or implement the " +
            $"compare-and-swap overload of {nameof(UpdateCurrentRun)}.");

    /// <summary>
    /// <see cref="CompleteRecurringRun"/> guarded by a compare-and-swap on the schedule version.
    /// </summary>
    Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                 AuditLevel auditLevel, int expectedScheduleVersion) =>
        throw new NotSupportedException(
            "This storage does not implement schedule versioning. Use a built-in provider, or implement the " +
            $"compare-and-swap overload of {nameof(CompleteRecurringRun)}.");

    /// <summary>
    /// The occurrences of a schedule, optionally only those that can still lead to an execution.
    /// </summary>
    /// <remarks>
    /// Read-only, so the default is a correct (if unindexed) query over <see cref="Get"/> rather than a
    /// refusal: a custom storage keeps working, and the built-in providers override it with an indexed one.
    /// </remarks>
    async Task<QueuedTask[]> GetOccurrences(Guid parentId, bool nonTerminalOnly = false,
                                            CancellationToken ct = default)
    {
        var rows = await Get(t => t.ParentTaskId == parentId, ct).ConfigureAwait(false);
        return nonTerminalOnly
                   ? rows.Where(r => QueuedTask.IsNonTerminalStatus(r.Status)).ToArray()
                   : rows;
    }

    /// <summary>
    /// Number of occurrences of a schedule that are not terminal yet — the storage half of the
    /// concurrency budget (the in-process delivery and scheduler registries complete it).
    /// </summary>
    async Task<int> CountActiveOccurrences(Guid parentId, CancellationToken ct = default) =>
        (await GetOccurrences(parentId, nonTerminalOnly: true, ct).ConfigureAwait(false)).Length;

    /// <summary>
    /// Saves execution logs for a task. Called by WorkerExecutor after task execution.
    /// If <paramref name="logs"/> is empty, implementations should skip the database write.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="logs">The logs to save (ordered by SequenceNumber).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves all execution logs for a task, ordered by SequenceNumber ascending.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logs ordered by SequenceNumber (oldest first).</returns>
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves execution logs for a task with pagination, ordered by SequenceNumber ascending.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="skip">Number of logs to skip (for pagination).</param>
    /// <param name="take">Number of logs to take (for pagination).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logs ordered by SequenceNumber (oldest first).</returns>
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken);
}
