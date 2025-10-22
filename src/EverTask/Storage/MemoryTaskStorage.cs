using System.Linq.Expressions;

namespace EverTask.Storage;

/// <inheritdoc />
public class MemoryTaskStorage(IEverTaskLogger<MemoryTaskStorage> logger) : ITaskStorage
{
    private readonly List<QueuedTask> _pendingTasks = new();
    private readonly List<TaskExecutionLog> _executionLogs = new();

    /// <inheritdoc />
    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        return Task.FromResult(_pendingTasks.Where(where.Compile()).ToArray());
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        return Task.FromResult(_pendingTasks.ToArray());
    }

    /// <inheritdoc />
    public Task Persist(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Persist Task: {type}", task.Type);

        _pendingTasks.Add(task);
        return Task.FromResult(task.Id);
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> RetrievePending(CancellationToken ct = default)
    {
        logger.LogInformation("Retrieve Pending Tasks");

        var pending = _pendingTasks.Where(t => (t.MaxRuns == null || t.CurrentRunCount <= t.MaxRuns)
                                               && (t.RunUntil == null || t.RunUntil >= DateTimeOffset.UtcNow)
                                               && (t.Status is QueuedTaskStatus.Queued or QueuedTaskStatus.Pending ||
                                                   t.Status == QueuedTaskStatus.ServiceStopped ||
                                                   t.Status == QueuedTaskStatus.InProgress));
        return Task.FromResult(pending.ToArray());
    }

    /// <inheritdoc />
    public Task SetQueued(Guid taskId, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.Queued, null, ct);

    /// <inheritdoc />
    public Task SetInProgress(Guid taskId, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.InProgress, null, ct);

    /// <inheritdoc />
    public Task SetCompleted(Guid taskId) =>
        SetStatus(taskId, QueuedTaskStatus.Completed);

    public Task SetCancelledByUser(Guid taskId) =>
        SetStatus(taskId, QueuedTaskStatus.Cancelled);

    public Task SetCancelledByService(Guid taskId, Exception exception) =>
        SetStatus(taskId, QueuedTaskStatus.ServiceStopped, exception);

    /// <inheritdoc />
    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                          CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
        if (task != null)
        {
            task.Status           = status;
            task.LastExecutionUtc = DateTimeOffset.UtcNow;
            task.Exception        = exception.ToDetailedString();

            task.StatusAudits.Add(new StatusAudit
            {
                QueuedTaskId = taskId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NewStatus    = status,
                Exception    = exception.ToDetailedString()
            });
        }

        return Task.CompletedTask;
    }

    public Task<int> GetCurrentRunCount(Guid taskId)
    {
        logger.LogInformation("Get the current run counter for Task {taskId}", taskId);
        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

        // Return 0 if task not found or CurrentRunCount is null (before first run)
        // The count represents completed runs, so 0 = no runs completed yet
        return Task.FromResult(task?.CurrentRunCount ?? 0);
    }

    public Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun)
    {
        logger.LogInformation("Update the current run counter for Task {taskId}", taskId);
        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);

        if (task != null)
        {
            task.RunsAudits.Add(new RunsAudit
            {
                QueuedTaskId = taskId,
                ExecutedAt   = task.LastExecutionUtc ?? DateTimeOffset.UtcNow,
                Status       = task.Status,
                Exception    = task.Exception
            });

            task.NextRunUtc = nextRun;
            var currentRun = task.CurrentRunCount ?? 0;

            task.CurrentRunCount = currentRun + 1;
        }

        return Task.CompletedTask;
    }

    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        var task = _pendingTasks.FirstOrDefault(t => t.TaskKey == taskKey);
        return Task.FromResult(task);
    }

    public Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Updating task {taskId} with key {taskKey}", task.Id, task.TaskKey);

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

        return Task.CompletedTask;
    }

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        logger.LogInformation("Removing task {taskId}", taskId);

        var task = _pendingTasks.FirstOrDefault(t => t.Id == taskId);
        if (task != null)
        {
            _pendingTasks.Remove(task);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences, CancellationToken ct = default)
    {
        if (skippedOccurrences.Count == 0)
            return Task.CompletedTask;

        logger.LogInformation("Recording {Count} skipped occurrences for task {TaskId}", skippedOccurrences.Count, taskId);

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

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken)
    {
        // Performance optimization: skip if no logs
        if (logs.Count == 0)
            return Task.CompletedTask;

        logger.LogInformation("Saving {Count} execution logs for task {TaskId}", logs.Count, taskId);

        _executionLogs.AddRange(logs);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken)
    {
        var logs = GetExecutionLogsQuery(taskId).ToList();
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

    private IOrderedEnumerable<TaskExecutionLog> GetExecutionLogsQuery(Guid taskId)
    {
        return _executionLogs
            .Where(log => log.TaskId == taskId)
            .OrderBy(log => log.SequenceNumber);
    }
}
