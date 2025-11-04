using System.Linq.Expressions;

namespace EverTask.Storage;

/// <inheritdoc />
public class MemoryTaskStorage(IEverTaskLogger<MemoryTaskStorage> logger) : ITaskStorage
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

            var pending = _pendingTasks
                .Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
                            && (t.RunUntil == null || t.RunUntil >= now)
                            && (t.Status is QueuedTaskStatus.Queued or QueuedTaskStatus.Pending
                                or QueuedTaskStatus.ServiceStopped or QueuedTaskStatus.InProgress));

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
        SetStatus(taskId, QueuedTaskStatus.Queued, null, auditLevel, ct);

    /// <inheritdoc />
    public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.InProgress, null, auditLevel, ct);

    /// <inheritdoc />
    public Task SetCompleted(Guid taskId, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Completed, null, auditLevel);

    public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Cancelled, null, auditLevel);

    public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception, auditLevel);

    /// <inheritdoc />
    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                          CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
            if (task != null)
            {
                task.Status           = status;
                task.LastExecutionUtc = DateTimeOffset.UtcNow;
                task.Exception        = exception.ToDetailedString();

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

    public Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun, AuditLevel auditLevel)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);

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
                        QueuedTaskId = taskId,
                        ExecutedAt   = task.LastExecutionUtc ?? DateTimeOffset.UtcNow,
                        Status       = task.Status,
                        Exception    = task.Exception
                    });
                }

                task.NextRunUtc = nextRun;
                var currentRun = task.CurrentRunCount ?? 0;

                task.CurrentRunCount = currentRun + 1;
            }
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
    public Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences, CancellationToken ct = default)
    {
        if (skippedOccurrences.Count == 0)
            return Task.CompletedTask;

        logger.LogInformation("Recording {Count} skipped occurrences for task {TaskId}", skippedOccurrences.Count, taskId);

        lock (_pendingTasksLock)
        {
            var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

            if (task != null)
            {
                // Create a summary of skipped times
                var skippedTimes = string.Join(", ", skippedOccurrences.Select(d => d.ToString("yyyy-MM-dd HH:mm:ss")));
                var skipMessage = $"Skipped {skippedOccurrences.Count} missed occurrence(s) to maintain schedule: {skippedTimes}";

                // Add a RunsAudit entry documenting the skips
                task.RunsAudits.Add(new RunsAudit
                {
                    QueuedTaskId = taskId,
                    ExecutedAt   = DateTimeOffset.UtcNow,
                    Status       = QueuedTaskStatus.Completed, // Using Completed as the base status
                    Exception    = skipMessage // Store skip info in Exception field for audit trail
                });
            }
            else
            {
                logger.LogWarning("Task {TaskId} not found when trying to record skipped occurrences", taskId);
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
                .OrderBy(log => log.SequenceNumber)
                .ToList(); // Materialize inside lock
        }
    }
}
