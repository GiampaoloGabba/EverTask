using System.Linq.Expressions;

namespace EverTask.Storage;

public class MemoryTaskStorage(IEverTaskLogger<MemoryTaskStorage> logger) : ITaskStorage
{
    private readonly List<QueuedTask> _pendingTasks = new();

    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        return Task.FromResult(_pendingTasks.Where(where.Compile()).ToArray());
    }

    public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        return Task.FromResult(_pendingTasks.ToArray());
    }

    public Task PersistTask(QueuedTask task, CancellationToken ct = default)
    {
        logger.LogInformation("Persist Task: {type}", task.Type);

        _pendingTasks.Add(task);
        return Task.FromResult(task.Id);
    }

    public Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default)
    {
        logger.LogInformation("Retrieve Pending Tasks");

        var pending = _pendingTasks.Where(x => x.Status
                                                   is QueuedTaskStatus.Queued
                                                   or QueuedTaskStatus.Pending
                                                   or QueuedTaskStatus.InProgress);
        return Task.FromResult(pending.ToArray());
    }

    public Task SetTaskQueued(Guid taskId, CancellationToken ct = default) =>
        SetTaskStatus(taskId, QueuedTaskStatus.Queued, null, ct);

    public Task SetTaskInProgress(Guid taskId, CancellationToken ct = default) =>
        SetTaskStatus(taskId, QueuedTaskStatus.InProgress, null, ct);

    public Task SetTaskCompleted(Guid taskId, CancellationToken ct = default)
    {
        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
        if (task != null)
            task.Status = QueuedTaskStatus.Completed;

        return SetTaskStatus(taskId, QueuedTaskStatus.Completed, null, ct);
    }

    public Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                              CancellationToken ct = default)
    {
        logger.LogInformation("Set Task {taskId} with Status {status}", taskId, status);

        var task = _pendingTasks.FirstOrDefault(x => x.Id == taskId);
        if (task != null)
            task.Status = status;

        return Task.CompletedTask;
    }
}
