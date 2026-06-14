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
    public Task Persist(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Persist Task: {type}", task.Type);

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

            _pendingTasks.Add(task);
        }
        return Task.FromResult(task.Id);
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default)
    {
        logger.LogInformation("Retrieve Pending Tasks (keyset: lastCreatedAt={LastCreatedAt}, lastId={LastId}, take={Take})",
            lastCreatedAt, lastId, take);

        lock (_pendingTasksLock)
        {
            var now = DateTimeOffset.UtcNow;

            // Recoverable statuses: canonical predicate shared with every provider (QueuedTask.IsRecoverable)
            var pending = _pendingTasks
                .Where(t => t.IsRecoverable(now));

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
    public Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
    {
        // Atomic check-and-set under the store lock: the startup recovery must never resurrect a
        // task that terminally finished after its page was read. Uses the canonical recoverable
        // predicate (QueuedTask.IsRecoverable) so the MaxRuns/RunUntil guards can never drift from
        // RetrievePending.
        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);

            var recoverable = task != null && task.IsRecoverable(DateTimeOffset.UtcNow);

            if (!recoverable)
            {
                logger.LogDebug("Task {taskId} is no longer recoverable, skipping SetQueued", taskId);
                return Task.FromResult(false);
            }

            task!.Status = QueuedTaskStatus.Queued;
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
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

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
                if (ShouldCreateStatusAudit(auditLevel, status, exception))
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

    private static bool ShouldCreateStatusAudit(AuditLevel auditLevel, QueuedTaskStatus status, Exception? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly => exception != null || status is QueuedTaskStatus.Failed or QueuedTaskStatus.ServiceStopped,
            AuditLevel.Minimal => exception != null || status is QueuedTaskStatus.Failed or QueuedTaskStatus.ServiceStopped,
            AuditLevel.Full => true,
            _ => true
        };

    public Task<int> GetCurrentRunCount(Guid taskId)
    {
        logger.LogInformation("Get the current run counter for Task {taskId}", taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

            // Return 0 if task not found or CurrentRunCount is null (before first run)
            // The count represents completed runs, so 0 = no runs completed yet
            return Task.FromResult(task?.CurrentRunCount ?? 0);
        }
    }

    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel) =>
        UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel, 1);

    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel,
                                 int runsToAdvance)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

        // Skipped occurrences must count toward the run counter (F7/F8): advance by 1 + skipped,
        // never below 1.
        if (runsToAdvance < 1)
            runsToAdvance = 1;

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

            if (task != null)
            {
                // Respect audit level
                if (ShouldCreateRunsAudit(auditLevel, task.Status, task.Exception))
                {
                    task.RunsAudits.Add(new RunsAudit
                    {
                        QueuedTaskId    = taskId,
                        ExecutedAt      = task.LastExecutionUtc ?? DateTimeOffset.UtcNow,
                        ExecutionTimeMs = executionTimeMs,
                        Status          = task.Status,
                        Exception       = task.Exception
                    });
                }

                task.ExecutionTimeMs = executionTimeMs;
                task.NextRunUtc      = nextRun;
                var currentRun       = task.CurrentRunCount ?? 0;

                task.CurrentRunCount = currentRun + runsToAdvance;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                     int runsToAdvance, AuditLevel auditLevel)
    {
        logger.LogInformation("Complete recurring run for Task {taskId}", taskId);

        if (runsToAdvance < 1)
            runsToAdvance = 1;

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task == null)
                return Task.CompletedTask;

            // Status -> Completed (+ status audit, LastExecutionUtc) AND the run-counter / next-run
            // advance (+ runs audit) committed TOGETHER under the single store lock, so a crash cannot
            // leave the row Completed but not advanced (CU14/L29).
            var now = DateTimeOffset.UtcNow;

            if (ShouldCreateStatusAudit(auditLevel, QueuedTaskStatus.Completed, null))
            {
                task.StatusAudits.Add(new StatusAudit
                {
                    QueuedTaskId = taskId,
                    UpdatedAtUtc = now,
                    NewStatus    = QueuedTaskStatus.Completed,
                    Exception    = null
                });
            }

            if (ShouldCreateRunsAudit(auditLevel, QueuedTaskStatus.Completed, null))
            {
                task.RunsAudits.Add(new RunsAudit
                {
                    QueuedTaskId    = taskId,
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
            task.CurrentRunCount  = (task.CurrentRunCount ?? 0) + runsToAdvance;
        }

        return Task.CompletedTask;
    }

    private static bool ShouldCreateRunsAudit(AuditLevel auditLevel, QueuedTaskStatus status, string? exception) =>
        auditLevel switch
        {
            AuditLevel.None => false,
            AuditLevel.ErrorsOnly => !string.IsNullOrEmpty(exception) || status == QueuedTaskStatus.Failed,
            AuditLevel.Minimal => true,
            AuditLevel.Full => true,
            _ => true
        };

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
        logger.LogInformation("Updating task {taskId} with key {taskKey}", task.Id, task.TaskKey);

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
                logger.LogWarning("Task {taskId} not found for update", task.Id);
            }
        }

        return Task.CompletedTask;
    }

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        logger.LogInformation("Removing task {taskId}", taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);
            if (task != null)
            {
                _pendingTasks.Remove(task);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken)
    {
        // Performance optimization: skip if no logs
        if (logs.Count == 0)
            return Task.CompletedTask;

        logger.LogInformation("Saving {Count} execution logs for task {TaskId}", logs.Count, taskId);

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
