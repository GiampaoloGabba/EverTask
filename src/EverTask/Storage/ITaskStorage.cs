using System.Linq.Expressions;

namespace EverTask.Storage;

/// <summary>
/// Represents a storage interface for tasks.
/// </summary>
public interface ITaskStorage
{
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
