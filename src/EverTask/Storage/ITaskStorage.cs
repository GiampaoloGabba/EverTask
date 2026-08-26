using System.Collections.ObjectModel;
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
    /// <remarks>
    /// An implementation MUST store the row's timestamps at offset zero
    /// (<see cref="QueuedTask.NormalizeTimestampsToUtc"/>): a store that compares them as text, as SQLite
    /// does, otherwise reads the same instant written at a different offset as a different value, and every
    /// cursor compare-and-swap against that row loses (F1).
    /// </remarks>
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
    /// <para>
    /// The relational override is BEST EFFORT, like <see cref="SetStatus"/>: it logs its own failed write and
    /// returns, so a failed poison never breaks the recovery of sibling rows. Returning normally therefore
    /// says nothing about the row, and the recovery confirms the outcome by re-reading it before it reports a
    /// terminalization.
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
    /// <remarks>
    /// Carries the same UTC-normalization obligation as <see cref="Persist"/>, and for a sharper reason:
    /// this is the entry point that rewrites the schedule cursor itself.
    /// </remarks>
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
    /// Moves a durable schedule's cursor forward WITHOUT creating an occurrence, guarded by a compare-and-swap
    /// on the version and the current cursor.
    /// </summary>
    /// <remarks>
    /// This is how slots are SKIPPED. Every kept slot advances the cursor inside
    /// <see cref="MaterializeOccurrence"/>, which is what makes a skip free: the occurrence is written at the
    /// slot that survives while the cursor jumps from the one that did not. When nothing survives at all — a
    /// whole backlog older than the age window, or a stale slot under the skip policy — there is no
    /// materialization to carry the jump, and this is that jump on its own.
    /// <para>
    /// It counts no run and writes no audit: nothing executed. A cursor that would move to <c>null</c> is the
    /// end of the series and goes through <see cref="TrySetRecurringSeriesCompleted"/> instead, which is why
    /// the new cursor here is not nullable.
    /// </para>
    /// </remarks>
    /// <returns>True when the cursor was moved; false when the compare-and-swap lost.</returns>
    Task<bool> TryAdvanceScheduleCursor(Guid parentId, int expectedScheduleVersion, DateTimeOffset expectedCursorUtc,
                                        DateTimeOffset newCursorUtc, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(TryAdvanceScheduleCursor)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Cancels a durable schedule AND every occurrence of it still pending, in one transaction, so a
    /// materializer racing the cancel can only observe the schedule as already inactive. Occurrences already
    /// executing are left alone and run to their own end.
    /// </summary>
    /// <remarks>
    /// The set it cancels is the exact complement of the set startup recovery puts back in a queue —
    /// <c>WaitingQueue</c>, <c>Queued</c>, <c>Pending</c> and <c>ServiceStopped</c>. Leaving any of them out
    /// means an occurrence of a cancelled schedule comes back at the next restart and runs (R7).
    /// <para>
    /// Audits only the rows it really changed. A schedule a concurrent <c>Remove</c> already deleted is a
    /// silent no-op — never an error, and never a status audit for a task that no longer exists.
    /// </para>
    /// <para>
    /// CONTRACT for an implementation whose backend admits CONCURRENT WRITERS (R6b): the audited set must come
    /// from the cancelling statement itself — SQL Server's <c>OUTPUT</c>, PostgreSQL's <c>RETURNING</c>, or the
    /// equivalent — never from a second read. Under READ COMMITTED a re-read can attribute to this call an
    /// occurrence another writer cancelled, so the audit trail would claim a transition this transaction never
    /// made. The three optimized providers derive it from the statement; the base implementation here re-reads
    /// inside the transaction, which is exact only while writers are serialized (as SQLite serializes them).
    /// </para>
    /// </remarks>
    Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default) =>
        throw new NotSupportedException(
            "This storage does not implement durable occurrences. Use a built-in provider, or implement " +
            $"{nameof(CancelSchedule)} atomically and set {nameof(SupportsDurableOccurrences)} to true.");

    /// <summary>
    /// Puts a terminal row (<c>Failed</c> or <c>Cancelled</c>) back into <c>Queued</c>, clearing its error and
    /// its <see cref="QueuedTask.RecoveryDispatchFailureCount"/> while keeping its identity, history and audit
    /// trail. Refuses anything that is not terminal.
    /// </summary>
    /// <remarks>
    /// The failure counter is cleared with the error because this call is the way back from a poison: a row
    /// requeued still carrying the attempts that ended it would be poisoned again by its first failure,
    /// without one of the retries the attempt ceiling exists to grant.
    /// </remarks>
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
    /// <paramref name="expectedScheduleVersion"/> AND stands at <paramref name="expectedCursorUtc"/> AND has
    /// not been cancelled. Two concurrent reschedules cannot both win.
    /// </summary>
    /// <remarks>
    /// A <see cref="QueuedTaskStatus.Cancelled"/> row is never updated. The version and the cursor do not
    /// answer for it — a cancel writes the status and leaves both untouched — so a reschedule that read the
    /// row before the cancellation committed would match on both and write a live definition over a series an
    /// operator has ended, then report success to its caller. Every other status is a legitimate target,
    /// <see cref="QueuedTaskStatus.InProgress"/> included (S3): a schedule that happens to be running is
    /// rescheduled, never refused.
    /// </remarks>
    /// <param name="expectedCursorUtc">
    /// The <see cref="QueuedTask.NextRunUtc"/> the caller computed its new definition against — including
    /// <c>null</c>, which expects a series that has already ended. Part of the compare-and-swap because a
    /// successful advance moves the cursor and the run counter WITHOUT touching the version: keying on the
    /// version alone lets a reschedule decided on a run count and a cursor that a completion has since
    /// superseded commit over it, and a rebase computed from that stale reading runs one occurrence past the
    /// budget it was given.
    /// </param>
    /// <returns>True when the new definition was written.</returns>
    Task<bool> UpdateSchedule(Guid taskId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc,
                              string recurringTaskJson, string? recurringInfo, DateTimeOffset? nextRunUtc,
                              int? maxRuns, DateTimeOffset? runUntil, string? runtimeInfo,
                              CancellationToken ct = default) =>
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
    /// One page of the occurrences of a schedule, newest slot first, with the total that matches the request.
    /// </summary>
    /// <remarks>
    /// The paging belongs to the storage because the reader that needs it — a dashboard listing a schedule
    /// with a year of retention behind it — must never pull the whole series into memory to show a hundred
    /// rows. The default composes the unpaged read so a custom storage keeps working; the built-in providers
    /// override it and let the database order, count and slice over the
    /// <c>(ParentTaskId, ScheduledExecutionUtc)</c> index the occurrence contract already needs.
    /// </remarks>
    /// <param name="parentId">The schedule row.</param>
    /// <param name="nonTerminalOnly">Keep only the occurrences that can still lead to an execution.</param>
    /// <param name="skip">How many occurrences to skip, from the newest slot. Never negative.</param>
    /// <param name="take">How many occurrences to return. Never negative; 0 asks for the count alone.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<OccurrencePage> GetOccurrencesPage(Guid parentId, bool nonTerminalOnly, int skip, int take,
                                                  CancellationToken ct = default)
    {
        var rows = await GetOccurrences(parentId, nonTerminalOnly, ct).ConfigureAwait(false);

        return new OccurrencePage(
            rows.OrderByDescending(r => r.ScheduledExecutionUtc).Skip(skip).Take(take).ToArray(),
            rows.Length);
    }

    /// <summary>
    /// Number of occurrences of a schedule that are not terminal yet — the storage half of the
    /// concurrency budget (the in-process delivery and scheduler registries complete it).
    /// </summary>
    async Task<int> CountActiveOccurrences(Guid parentId, CancellationToken ct = default) =>
        (await GetOccurrences(parentId, nonTerminalOnly: true, ct).ConfigureAwait(false)).Length;

    /// <summary>
    /// When the last run of each of the given rows STARTED, as the status audit trail recorded it. Rows with
    /// no recorded start are simply absent from the result.
    /// </summary>
    /// <remarks>
    /// No column holds this: <see cref="QueuedTask.LastExecutionUtc"/> is written on TERMINAL transitions, so
    /// it says when a run ENDED, and the only trace of the moment a run began is the
    /// <see cref="QueuedTaskStatus.InProgress"/> transition in <see cref="QueuedTask.StatusAudits"/> — which
    /// therefore has to be READ, and is the one source that can speak for a run still in flight. It is asked
    /// for a whole page of rows at once because its reader is a dashboard listing them.
    /// <para>
    /// A row whose <see cref="AuditLevel"/> does not record that transition (anything below
    /// <see cref="EverTask.Abstractions.AuditLevel.Full"/>) has no recorded start at all, and answering
    /// nothing for it is the correct answer.
    /// </para>
    /// <para>
    /// Read-only, so the default is a correct query over <see cref="Get"/> rather than a refusal: a custom
    /// storage that materializes the audit navigation keeps working, and the built-in providers override it
    /// with one indexed query over the audit table.
    /// </para>
    /// </remarks>
    /// <param name="taskIds">The rows to answer for.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<IReadOnlyDictionary<Guid, DateTimeOffset>> GetLastRunStarts(IReadOnlyCollection<Guid> taskIds,
                                                                          CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return ReadOnlyDictionary<Guid, DateTimeOffset>.Empty;

        var rows   = await Get(t => taskIds.Contains(t.Id), ct).ConfigureAwait(false);
        var starts = new Dictionary<Guid, DateTimeOffset>(rows.Length);

        foreach (var row in rows)
        {
            foreach (var audit in row.StatusAudits)
            {
                if (audit.NewStatus != QueuedTaskStatus.InProgress)
                    continue;

                if (!starts.TryGetValue(row.Id, out var known) || audit.UpdatedAtUtc >= known)
                    starts[row.Id] = audit.UpdatedAtUtc;
            }
        }

        return starts;
    }

    /// <summary>
    /// The status transitions recorded for one row, newest first.
    /// </summary>
    /// <remarks>
    /// Same reason as <see cref="GetLastRunStarts"/>: the audit trail has to be READ. No read populates
    /// <see cref="QueuedTask.StatusAudits"/>, so a reader walking that navigation answers only over a store
    /// that keeps the audits on the row object itself and hands back an empty history on every relational
    /// one — for a row whose audit table holds the whole transition history.
    /// <para>
    /// Read-only, so the default is a correct query over <see cref="Get"/> rather than a refusal: a custom
    /// storage that materializes the navigation keeps working, and the built-in providers override it with
    /// one indexed query over the audit table.
    /// </para>
    /// </remarks>
    /// <param name="taskId">The row to answer for.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<StatusAudit[]> GetStatusAudits(Guid taskId, CancellationToken ct = default)
    {
        var rows = await Get(t => t.Id == taskId, ct).ConfigureAwait(false);

        // Reversed first, so that audits sharing an instant — a coarse clock makes that ordinary — come back
        // in the order they were recorded rather than upside down: a stable sort keeps whatever order it was
        // handed for equal keys.
        return rows.Length == 0
                   ? []
                   : rows[0].StatusAudits.Reverse().OrderByDescending(a => a.UpdatedAtUtc).ToArray();
    }

    /// <summary>
    /// The runs recorded for one row, newest first.
    /// </summary>
    /// <remarks>
    /// The <see cref="RunsAudit"/> half of <see cref="GetStatusAudits"/>, and dead in exactly the same way
    /// when read off <see cref="QueuedTask.RunsAudits"/>.
    /// </remarks>
    /// <param name="taskId">The row to answer for.</param>
    /// <param name="ct">Cancellation token.</param>
    async Task<RunsAudit[]> GetRunsAudits(Guid taskId, CancellationToken ct = default)
    {
        var rows = await Get(t => t.Id == taskId, ct).ConfigureAwait(false);

        return rows.Length == 0
                   ? []
                   : rows[0].RunsAudits.Reverse().OrderByDescending(a => a.ExecutedAt).ToArray();
    }

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
