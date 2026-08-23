using System.Linq.Expressions;

namespace EverTask.Storage;

/// <inheritdoc />
public class MemoryTaskStorage(IEverTaskLogger<MemoryTaskStorage> logger) : ITaskStorage, ITaskStorageStatistics
{
    private readonly List<QueuedTask> _pendingTasks = new();
    private readonly List<TaskExecutionLog> _executionLogs = new();
    private readonly object _pendingTasksLock = new();
    private readonly object _executionLogsLock = new();

    /// <inheritdoc />
    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            return Task.FromResult(_pendingTasks.Where(where.Compile()).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            return Task.FromResult(_pendingTasks.ToArray());
        }
    }

    /// <inheritdoc />
    public bool SupportsDurableOccurrences => true;

    /// <inheritdoc />
    public bool SupportsScheduleVersioning => true;

    /// <inheritdoc />
    public Task Persist(QueuedTask task, CancellationToken ct = default)
    {
        logger.TaskPersisted(task.Type);

        lock (_pendingTasksLock)
        {
            // Mirror the relational unique index on TaskKey: reject a duplicate so two rows can never
            // share a key — each would execute, since the delivery registry dedups only by PersistenceId,
            // which are distinct (G13). Whitespace keys are treated as "no key" to match the dispatcher's
            // dedup semantics (IsNullOrWhiteSpace).
            if (!string.IsNullOrWhiteSpace(task.TaskKey) &&
                _pendingTasks.Any(t => t.TaskKey == task.TaskKey))
            {
                throw new InvalidOperationException(
                    $"A task with TaskKey '{task.TaskKey}' already exists (unique constraint violation).");
            }

            ValidateOccurrenceConstraints(task);

            _pendingTasks.Add(task);
        }
        return Task.FromResult(task.Id);
    }

    /// <summary>
    /// Enforces, by hand, the three relational guarantees an occurrence row relies on: the check constraint
    /// (an occurrence always names its slot), the self-referencing foreign key (no orphans) and the unique
    /// index on (parent, slot) (a slot is materialized at most once). Without them the in-memory store would
    /// silently accept states the relational providers reject, and the same test would pass here and fail there.
    /// </summary>
    private void ValidateOccurrenceConstraints(QueuedTask task)
    {
        if (task.ParentTaskId is not { } parentId)
            return;

        if (task.ScheduledExecutionUtc == null)
        {
            throw new InvalidOperationException(
                "An occurrence must carry its nominal slot in ScheduledExecutionUtc " +
                "(check constraint CK_QueuedTasks_OccurrenceSlot).");
        }

        if (_pendingTasks.All(t => t.Id != parentId))
        {
            throw new InvalidOperationException(
                $"No schedule row '{parentId}' exists for this occurrence (foreign key FK_QueuedTasks_Parent).");
        }

        if (_pendingTasks.Any(t => t.ParentTaskId == parentId
                                   && t.ScheduledExecutionUtc == task.ScheduledExecutionUtc))
        {
            throw new InvalidOperationException(
                $"Slot {task.ScheduledExecutionUtc:O} of schedule '{parentId}' is already materialized " +
                "(unique constraint UX_QueuedTasks_Occurrence).");
        }
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default) =>
        RetrievePending(DateTimeOffset.UtcNow, lastCreatedAt, lastId, take, ct);

    /// <inheritdoc />
    public Task<QueuedTask[]> RetrievePending(DateTimeOffset nowUtc, DateTimeOffset? lastCreatedAt, Guid? lastId,
                                              int take, CancellationToken ct = default)
    {
        logger.RetrievingPendingTasks(lastCreatedAt, lastId, take);

        lock (_pendingTasksLock)
        {
            var now = nowUtc;

            // X3: the union of the two recovery categories — rows with work left to EXECUTE, and recurring
            // series that only need FINALIZING. Canonical predicates on QueuedTask, shared with every provider.
            var pending = _pendingTasks
                .Where(t => t.IsRecoverableForExecution(now) || t.IsRecurringSeriesToFinalize());

            if (lastCreatedAt.HasValue)
            {
                pending = pending.Where(t =>
                    t.CreatedAtUtc > lastCreatedAt.Value ||
                    (t.CreatedAtUtc == lastCreatedAt.Value && lastId.HasValue && t.Id.CompareTo(lastId.Value) > 0));
            }

            return Task.FromResult(
                pending
                    .OrderBy(t => t.CreatedAtUtc)
                    .ThenBy(t => t.Id)
                    .Take(take)
                    .ToArray());
        }
    }

    /// <inheritdoc />
    public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.Queued, null, auditLevel, null, ct);

    /// <inheritdoc />
    public Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, taskId, auditLevel, ct);

    /// <inheritdoc />
    public Task<bool> TrySetQueuedIfRecoverable(DateTimeOffset nowUtc, Guid taskId, AuditLevel auditLevel,
                                                CancellationToken ct = default)
    {
        // Atomic check-and-set under the store lock: the startup recovery must never resurrect a
        // task that terminally finished after its page was read. Uses the canonical execution predicate
        // (QueuedTask.IsRecoverableForExecution) so the MaxRuns/RunUntil guards can never drift from
        // RetrievePending. A series that only needs finalizing is deliberately NOT requeueable.
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);

            var recoverable = task != null && task.IsRecoverableForExecution(nowUtc);

            if (!recoverable)
            {
                logger.TaskNoLongerRecoverable(taskId);
                return Task.FromResult(false);
            }

            task!.Status = QueuedTaskStatus.Queued;

            // Audit the recovery Queued transition like the relational providers (and like Memory's own
            // live SetQueued), so the audit trail does not diverge by backend (L43).
            if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Queued, null))
            {
                task.StatusAudits.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    NewStatus    = QueuedTaskStatus.Queued,
                    Exception    = null
                });
            }
        }

        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.InProgress, null, auditLevel, null, ct);

    /// <inheritdoc />
    public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Completed, null, auditLevel, executionTimeMs);

    public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Cancelled, null, auditLevel);

    public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, auditLevel);

    /// <inheritdoc />
    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                          double? executionTimeMs = null, CancellationToken ct = default)
    {
        logger.StatusSet(taskId, status);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task != null)
            {
                task.Status    = status;
                task.Exception = exception.ToDetailedString();

                // LastExecutionUtc only on terminal transitions, same rule as EfCoreTaskStorage.SetStatus:
                // intermediate statuses (WaitingQueue, Queued, InProgress, Cancelled, Pending) preserve
                // the previous value (no fake execution time, no wipe of the last real run).
                if (status is not (QueuedTaskStatus.WaitingQueue or QueuedTaskStatus.Queued
                    or QueuedTaskStatus.InProgress or QueuedTaskStatus.Cancelled or QueuedTaskStatus.Pending))
                {
                    task.LastExecutionUtc = DateTimeOffset.UtcNow;
                }

                // Set execution time if provided
                if (executionTimeMs.HasValue)
                {
                    task.ExecutionTimeMs = executionTimeMs.Value;
                }

                // Respect audit level
                if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, exception))
                {
                    task.StatusAudits.Add(new StatusAudit
                    {
                        QueuedTaskId = taskId,
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                        NewStatus    = status,
                        Exception    = exception.ToDetailedString()
                    });
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<int> GetCurrentRunCount(Guid taskId)
    {
        logger.GettingCurrentRunCount(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

            // Return 0 if task not found or CurrentRunCount is null (before first run)
            // The count represents completed runs, so 0 = no runs completed yet
            return Task.FromResult(task?.CurrentRunCount ?? 0);
        }
    }

    /// <inheritdoc />
    public Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null)
                return Task.FromResult(0);

            task.RecoveryDispatchFailureCount = (task.RecoveryDispatchFailureCount ?? 0) + 1;
            return Task.FromResult(task.RecoveryDispatchFailureCount.Value);
        }
    }

    /// <inheritdoc />
    public Task ClearRecoveryFailure(Guid taskId, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task is { RecoveryDispatchFailureCount: > 0 })
                task.RecoveryDispatchFailureCount = null;
        }

        return Task.CompletedTask;
    }

    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel)
    {
        logger.UpdatingCurrentRunCount(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

            if (task != null)
                UpdateCurrentRunLocked(task, executionTimeMs, nextRun, auditLevel);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                     AuditLevel auditLevel)
    {
        logger.CompletingRecurringRun(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task != null)
                CompleteRecurringRunLocked(task, executionTimeMs, nextRun, auditLevel);
        }

        return Task.CompletedTask;
    }

    public Task SetRecurringSeriesCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
    {
        logger.FinalizingRecurringSeries(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null)
                return Task.CompletedTask;

            var now = DateTimeOffset.UtcNow;

            // Status -> Completed (+ status audit) AND NextRunUtc cleared together under the store lock.
            // NO run-counter advance and NO runs audit: the skipped occurrence never executed (Option B).
            // Clearing NextRunUtc is what keeps the terminal row out of IsRecoverable (a Completed recurring
            // row with NextRunUtc != null is resurrected by recovery).
            if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null))
            {
                task.StatusAudits.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Completed,
                    Exception    = null
                });
            }

            task.Status           = QueuedTaskStatus.Completed;
            task.Exception        = null;
            task.LastExecutionUtc = now;
            task.ExecutionTimeMs  = executionTimeMs;
            task.NextRunUtc       = null;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetRecurringTaskPoisoned(Guid taskId, Exception exception, AuditLevel auditLevel,
                                         CancellationToken ct = default)
    {
        logger.PoisoningRecurringTask(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null)
                return Task.CompletedTask;

            var now = DateTimeOffset.UtcNow;
            var ex  = exception.ToDetailedString();

            // Status -> Failed (+ status audit, LastExecutionUtc) AND NextRunUtc cleared together under the
            // store lock. Clearing NextRunUtc is what keeps the poisoned recurring row out of IsRecoverable
            // (a Failed recurring row with NextRunUtc != null is resurrected and re-poisoned at every restart).
            if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Failed, exception))
            {
                task.StatusAudits.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Failed,
                    Exception    = ex
                });
            }

            task.Status           = QueuedTaskStatus.Failed;
            task.Exception        = ex;
            task.LastExecutionUtc = now;
            task.NextRunUtc       = null;
        }

        return Task.CompletedTask;
    }

    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.TaskKey == taskKey);
            return Task.FromResult(task);
        }
    }

    public Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        logger.UpdatingTask(task.Id, task.TaskKey);

        lock (_pendingTasksLock)
        {
            var existingTask = _pendingTasks.FirstOrDefault(t => t.Id == task.Id);
            if (existingTask != null)
            {
                // Update all relevant properties
                existingTask.Type                  = task.Type;
                existingTask.Request               = task.Request;
                existingTask.Handler               = task.Handler;
                existingTask.ScheduledExecutionUtc = task.ScheduledExecutionUtc;
                existingTask.IsRecurring           = task.IsRecurring;
                existingTask.RecurringTask         = task.RecurringTask;
                existingTask.RecurringInfo         = task.RecurringInfo;
                existingTask.MaxRuns               = task.MaxRuns;
                existingTask.RunUntil              = task.RunUntil;
                existingTask.NextRunUtc            = task.NextRunUtc;
                existingTask.QueueName             = task.QueueName;
                existingTask.TaskKey               = task.TaskKey;
            }
            else
            {
                logger.TaskNotFoundForUpdate(task.Id);
            }
        }

        return Task.CompletedTask;
    }

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        logger.RemovingTask(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                // Occurrences go with their schedule, in the same critical section. The relational
                // providers use a restrict foreign key, which would otherwise refuse the delete outright;
                // deleting the children here is what keeps the two behaviours identical.
                _pendingTasks.RemoveAll(t => t.ParentTaskId == taskId);
                _pendingTasks.Remove(task);
            }
        }

        return Task.CompletedTask;
    }

    // ---- Durable occurrences and schedule versioning ----------------------------------------------
    // Every operation below runs entirely under _pendingTasksLock, which IS this store's transaction: an
    // observer either sees the whole change or none of it, exactly like the single-statement / single-commit
    // implementations of the relational providers.

    /// <inheritdoc />
    public Task<OccurrenceMaterializationOutcome> MaterializeOccurrence(
        Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc, QueuedTask occurrence,
        DateTimeOffset? newCursorUtc, AuditLevel auditLevel, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(occurrence);

        lock (_pendingTasksLock)
        {
            var parent = _pendingTasks.FirstOrDefault(t => t.Id == parentId);

            // Inactive means "must not grow new occurrences": gone, cancelled, or already finalized (a
            // finalized series has no cursor left to advance).
            if (parent == null || parent.Status == QueuedTaskStatus.Cancelled || parent.NextRunUtc == null)
                return Task.FromResult(OccurrenceMaterializationOutcome.ParentInactive);

            if (parent.ScheduleVersion != expectedScheduleVersion)
                return Task.FromResult(OccurrenceMaterializationOutcome.VersionMismatch);

            if (parent.NextRunUtc != expectedCursorUtc)
                return Task.FromResult(OccurrenceMaterializationOutcome.CursorMoved);

            if (_pendingTasks.Any(t => t.ParentTaskId == parentId
                                       && t.ScheduledExecutionUtc == occurrence.ScheduledExecutionUtc))
                return Task.FromResult(OccurrenceMaterializationOutcome.AlreadyExists);

            // Same row shape the relational providers write: this store keeps the caller's entity, so the
            // contract is stamped on it rather than spelled out in an INSERT column list.
            occurrence.ApplyOccurrenceContract(parentId, expectedScheduleVersion);
            ValidateOccurrenceConstraints(occurrence);
            _pendingTasks.Add(occurrence);

            parent.NextRunUtc = newCursorUtc;
            // A materialization IS the run of a durable series (M14): the child may later fail or be
            // cancelled, and the budget is still spent — the schedule did produce that occurrence.
            parent.CurrentRunCount = parent.CurrentRunCount >= int.MaxValue ? int.MaxValue : (parent.CurrentRunCount ?? 0) + 1;

            // Last slot: the series ends in the SAME critical section that created its final occurrence, so
            // no crash can leave a finished series with a cursor that recovery would resurrect.
            if (newCursorUtc == null)
                FinalizeParentLocked(parent, auditLevel);

            return Task.FromResult(OccurrenceMaterializationOutcome.Created);
        }
    }

    /// <inheritdoc />
    public Task<bool> TrySetRecurringSeriesCompleted(Guid taskId, DateTimeOffset? expectedCursorUtc,
                                                     QueuedTaskStatus expectedStatus, int expectedScheduleVersion,
                                                     double executionTimeMs, AuditLevel auditLevel,
                                                     CancellationToken ct = default)
    {
        logger.FinalizingRecurringSeries(taskId);

        // A schedule with no cursor is already over: a null expectation would match exactly the rows that are
        // finalized or poisoned. The relational stores refuse it for the same reason.
        if (expectedCursorUtc == null)
            return Task.FromResult(false);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);

            if (task == null
                || task.Status != expectedStatus
                || task.NextRunUtc != expectedCursorUtc
                || task.ScheduleVersion != expectedScheduleVersion)
            {
                return Task.FromResult(false);
            }

            task.ExecutionTimeMs = executionTimeMs;
            FinalizeParentLocked(task, auditLevel);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task CancelSchedule(Guid parentId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var parent = _pendingTasks.FirstOrDefault(t => t.Id == parentId);
            if (parent != null)
                TransitionLocked(parent, QueuedTaskStatus.Cancelled, auditLevel);

            // Occurrences already executing are left alone: they own a live delivery and end on their own.
            foreach (var child in _pendingTasks.Where(t => t.ParentTaskId == parentId
                                                           && t.Status is QueuedTaskStatus.WaitingQueue
                                                               or QueuedTaskStatus.Queued or QueuedTaskStatus.Pending))
            {
                TransitionLocked(child, QueuedTaskStatus.Cancelled, auditLevel);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> RequeueTerminal(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null || task.Status is not (QueuedTaskStatus.Failed or QueuedTaskStatus.Cancelled))
                return Task.FromResult(false);

            task.Exception = null;
            TransitionLocked(task, QueuedTaskStatus.Queued, auditLevel);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryRequeueStaleOccurrence(Guid childId, QueuedTaskStatus expectedStatus, AuditLevel auditLevel,
                                                CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == childId);
            if (task == null || task.Status != expectedStatus)
                return Task.FromResult(false);

            TransitionLocked(task, QueuedTaskStatus.Queued, auditLevel);
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> UpdateSchedule(Guid taskId, int expectedScheduleVersion, string recurringTaskJson,
                                     string? recurringInfo, DateTimeOffset? nextRunUtc, int? maxRuns,
                                     DateTimeOffset? runUntil, string? runtimeInfo, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);
            if (task == null || task.ScheduleVersion != expectedScheduleVersion)
                return Task.FromResult(false);

            task.RecurringTask   = recurringTaskJson;
            task.RecurringInfo   = recurringInfo;
            task.NextRunUtc      = nextRunUtc;
            task.MaxRuns         = maxRuns;
            task.RunUntil        = runUntil;
            task.RuntimeInfo     = runtimeInfo;
            task.ScheduleVersion = expectedScheduleVersion + 1;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<bool> TryHaltSchedule(Guid parentId, int expectedScheduleVersion, DateTimeOffset? expectedCursorUtc,
                                      QueuedTaskStatus expectedStatus, string runtimeInfo,
                                      CancellationToken ct = default)
    {
        // Same refusal as the finalization above: halting an already-ended series is not a compare-and-swap win.
        if (expectedCursorUtc == null)
            return Task.FromResult(false);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == parentId);

            if (task == null
                || task.ScheduleVersion != expectedScheduleVersion
                || task.NextRunUtc != expectedCursorUtc
                || task.Status != expectedStatus)
            {
                return Task.FromResult(false);
            }

            task.RuntimeInfo = runtimeInfo;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<ScheduleCasResult> UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                    AuditLevel auditLevel, int expectedScheduleVersion)
    {
        logger.UpdatingCurrentRunCount(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null || task.ScheduleVersion != expectedScheduleVersion)
                return Task.FromResult(ScheduleCasResult.VersionMismatch);

            UpdateCurrentRunLocked(task, executionTimeMs, nextRun, auditLevel);
            return Task.FromResult(ScheduleCasResult.Applied);
        }
    }

    /// <inheritdoc />
    public Task<ScheduleCasResult> CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                                        AuditLevel auditLevel, int expectedScheduleVersion)
    {
        logger.CompletingRecurringRun(taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null || task.ScheduleVersion != expectedScheduleVersion)
                return Task.FromResult(ScheduleCasResult.VersionMismatch);

            CompleteRecurringRunLocked(task, executionTimeMs, nextRun, auditLevel);
            return Task.FromResult(ScheduleCasResult.Applied);
        }
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> GetOccurrences(Guid parentId, bool nonTerminalOnly = false,
                                             CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            return Task.FromResult(_pendingTasks
                                   .Where(t => t.ParentTaskId == parentId
                                               && (!nonTerminalOnly || QueuedTask.IsNonTerminalStatus(t.Status)))
                                   .ToArray());
        }
    }

    /// <inheritdoc />
    public Task<int> CountActiveOccurrences(Guid parentId, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            return Task.FromResult(_pendingTasks.Count(t => t.ParentTaskId == parentId
                                                            && QueuedTask.IsNonTerminalStatus(t.Status)));
        }
    }

    /// <summary>
    /// Run-counter advance (+ runs audit) of one real execution, under the caller's lock. Shared by the plain
    /// and the compare-and-swap overload so the version check and the write it guards sit in the SAME critical
    /// section: a check that released the lock before writing would let a reschedule slip in between and the
    /// stale run would still write its old-definition cursor, which is exactly what the CAS exists to refuse.
    /// </summary>
    private static void UpdateCurrentRunLocked(QueuedTask task, double executionTimeMs, DateTimeOffset? nextRun,
                                               AuditLevel auditLevel)
    {
        // Respect audit level. ExecutedAt is stamped at the current time, like the relational
        // providers — not the task's older LastExecutionUtc (L28).
        if (AuditPolicy.ShouldCreateRunsAudit(auditLevel, task.Status, task.Exception))
        {
            task.RunsAudits.Add(new RunsAudit
            {
                QueuedTaskId    = task.Id,
                ExecutedAt      = DateTimeOffset.UtcNow,
                ExecutionTimeMs = executionTimeMs,
                Status          = task.Status,
                Exception       = task.Exception
            });
        }

        task.ExecutionTimeMs = executionTimeMs;
        task.NextRunUtc      = nextRun;

        // Advance by exactly one real execution (Option B): skipped occurrences never count.
        // Saturating at int.MaxValue (see EfCoreTaskStorage.UpdateCurrentRun for the rationale).
        task.CurrentRunCount = task.CurrentRunCount >= int.MaxValue ? int.MaxValue : (task.CurrentRunCount ?? 0) + 1;
    }

    /// <summary>
    /// Completed transition (+ status audit, LastExecutionUtc) AND the run-counter / next-run advance
    /// (+ runs audit) applied together, under the caller's lock, so a crash cannot leave the row Completed
    /// but not advanced (CU14/L29). Shared by the plain and the compare-and-swap overload — see
    /// <see cref="UpdateCurrentRunLocked"/> for why the CAS cannot check the version outside this lock.
    /// </summary>
    private static void CompleteRecurringRunLocked(QueuedTask task, double executionTimeMs, DateTimeOffset? nextRun,
                                                   AuditLevel auditLevel)
    {
        var now = DateTimeOffset.UtcNow;

        if (AuditPolicy.ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null))
        {
            task.StatusAudits.Add(new StatusAudit
            {
                QueuedTaskId = task.Id,
                UpdatedAtUtc = now,
                NewStatus    = QueuedTaskStatus.Completed,
                Exception    = null
            });
        }

        if (AuditPolicy.ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null))
        {
            task.RunsAudits.Add(new RunsAudit
            {
                QueuedTaskId    = task.Id,
                ExecutedAt      = now,
                ExecutionTimeMs = executionTimeMs,
                Status          = QueuedTaskStatus.Completed,
                Exception       = null
            });
        }

        task.Status           = QueuedTaskStatus.Completed;
        task.Exception        = null;
        task.LastExecutionUtc = now;
        task.ExecutionTimeMs  = executionTimeMs;
        task.NextRunUtc       = nextRun;
        task.CurrentRunCount  = task.CurrentRunCount >= int.MaxValue ? int.MaxValue : (task.CurrentRunCount ?? 0) + 1; // one real execution (Option B); saturating
    }

    /// <summary>Terminal Completed transition of a schedule row with its cursor cleared. Caller holds the lock.</summary>
    private static void FinalizeParentLocked(QueuedTask parent, AuditLevel auditLevel)
    {
        parent.NextRunUtc = null;
        TransitionLocked(parent, QueuedTaskStatus.Completed, auditLevel);
        parent.Exception        = null;
        parent.LastExecutionUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>Status transition plus its audit row, under the caller's lock.</summary>
    private static void TransitionLocked(QueuedTask task, QueuedTaskStatus status, AuditLevel auditLevel)
    {
        task.Status = status;

        if (!AuditPolicy.ShouldCreateStatusAudit(auditLevel, status, null))
            return;

        task.StatusAudits.Add(new StatusAudit
        {
            QueuedTaskId = task.Id,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            NewStatus    = status,
            Exception    = null
        });
    }

    /// <inheritdoc />
    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken)
    {
        // Performance optimization: skip if no logs
        if (logs.Count == 0)
            return Task.CompletedTask;

        logger.SavingExecutionLogs(logs.Count, taskId);

        lock (_executionLogsLock)
        {
            _executionLogs.AddRange(logs);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var logs = GetExecutionLogsQuery(taskId);
        return Task.FromResult<IReadOnlyList<TaskExecutionLog>>(logs);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken)
    {
        var logs = GetExecutionLogsQuery(taskId)
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult<IReadOnlyList<TaskExecutionLog>>(logs);
    }

    private List<TaskExecutionLog> GetExecutionLogsQuery(Guid taskId)
    {
        lock (_executionLogsLock)
        {
            return _executionLogs
                .Where(log => log.TaskId == taskId)
                .OrderBy(log => log.Id)            // Primary: UUIDv7 chronological order
                .ThenBy(log => log.SequenceNumber) // Secondary: preserve sequence within same timestamp
                .ToList();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<QueuedTaskStatus, int>> CountByStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            IReadOnlyDictionary<QueuedTaskStatus, int> counts = _pendingTasks
                .Where(t => createdAtOrAfterUtc == null || t.CreatedAtUtc >= createdAtOrAfterUtc.Value)
                .GroupBy(t => t.Status)
                .ToDictionary(g => g.Key, g => g.Count());

            return Task.FromResult(counts);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, IReadOnlyDictionary<QueuedTaskStatus, int>>> CountByQueueAndStatusAsync(
        DateTimeOffset? createdAtOrAfterUtc = null, CancellationToken ct = default)
    {
        lock (_pendingTasksLock)
        {
            IReadOnlyDictionary<string, IReadOnlyDictionary<QueuedTaskStatus, int>> counts = _pendingTasks
                .Where(t => createdAtOrAfterUtc == null || t.CreatedAtUtc >= createdAtOrAfterUtc.Value)
                .GroupBy(t => t.QueueName ?? string.Empty)
                .ToDictionary(
                    g => g.Key,
                    g => (IReadOnlyDictionary<QueuedTaskStatus, int>)g
                         .GroupBy(t => t.Status)
                         .ToDictionary(sg => sg.Key, sg => sg.Count()));

            return Task.FromResult(counts);
        }
    }
}
