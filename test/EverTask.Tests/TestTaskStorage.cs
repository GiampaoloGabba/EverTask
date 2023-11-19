using System.Linq.Expressions;
using EverTask.Storage;

namespace EverTask.Tests;

public class TestTaskStorage : ITaskStorage
{
    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<QueuedTask>());
    }

    public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<QueuedTask>());
    }

    public Task PersistTask(QueuedTask executor, CancellationToken ct = default)
    {
        if (executor.Type.Contains("ThrowStorageError"))
            throw new Exception();

        return Task.CompletedTask;
    }

    public Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<QueuedTask>());
    }

    public Task SetTaskQueued(Guid taskId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SetTaskInProgress(Guid taskId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SetTaskCompleted(Guid taskId)
    {
        return Task.CompletedTask;
    }

    public Task SetTaskCancelledByUser(Guid taskId)
    {
        return Task.CompletedTask;
    }

    public Task SetTaskCancelledByService(Guid taskId, Exception exception)
    {
        return Task.CompletedTask;
    }

    public Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
