using System.Linq.Expressions;
using EverTask.Logger;

namespace EverTask.Storage;

/// <inheritdoc />
public class MemoryTaskStorage(IEverTaskLogger<MemoryTaskStorage> logger) : ITaskStorage
{
    private readonly List<QueuedTask> _pendingTasks = new();

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
    public Task PersistTask(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Persist Task: {type}", task.Type);

        _pendingTasks.Add(task);
        return Task.FromResult(task.Id);
    }

    /// <inheritdoc />
    public Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default)
    {
        logger.LogInformation("Retrieve Pending Tasks");

        var pending = _pendingTasks.Where(t => t.Status == QueuedTaskStatus.Queued ||
                                                t.Status == QueuedTaskStatus.Pending ||
                                                t.Status == QueuedTaskStatus.InProgress);
        return Task.FromResult(pending.ToArray());
    }

    /// <inheritdoc />
    public Task SetTaskQueued(Guid taskId, CancellationToken ct = default) =>
        SetTaskStatus(taskId, QueuedTaskStatus.Queued, null, ct);

    /// <inheritdoc />
    public Task SetTaskInProgress(Guid taskId, CancellationToken ct = default) =>
        SetTaskStatus(taskId, QueuedTaskStatus.InProgress, null, ct);

    /// <inheritdoc />
    public Task SetTaskCompleted(Guid taskId, CancellationToken ct = default) =>
        SetTaskStatus(taskId, QueuedTaskStatus.Completed, null, ct);

    /// <inheritdoc />
    public Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                              CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
        if (task != null)
        {
            task.Status           = status;
            task.LastExecutionUtc = DateTimeOffset.UtcNow;

            task.StatusAudits.Add(new StatusAudit
            {
                QueuedTaskId = taskId,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                NewStatus    = status,
                Exception    = exception?.ToString()
            });

        }

        return Task.CompletedTask;
    }
}
