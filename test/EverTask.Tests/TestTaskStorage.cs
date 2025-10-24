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

    public Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        if (executor.Type.Contains("ThrowStorageError"))
            throw new Exception();

        return Task.CompletedTask;
    }

    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<QueuedTask>());
    }

    public Task<QueuedTask[]> RetrievePendingPaged(int skip, int take, CancellationToken ct = default)
    {
        return Task.FromResult(Array.Empty<QueuedTask>());
    }

    public Task SetQueued(Guid taskId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SetInProgress(Guid taskId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SetCompleted(Guid taskId)
    {
        return Task.CompletedTask;
    }

    public Task SetCancelledByUser(Guid taskId)
    {
        return Task.CompletedTask;
    }

    public Task SetCancelledByService(Guid taskId, Exception exception)
    {
        return Task.CompletedTask;
    }

    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<int> GetCurrentRunCount(Guid taskId)
    {
        return Task.FromResult(0);
    }

    public Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun)
    {
        return Task.CompletedTask;
    }

    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default)
    {
        return Task.FromResult<QueuedTask?>(null);
    }

    public Task UpdateTask(QueuedTask task, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<TaskExecutionLog>>(Array.Empty<TaskExecutionLog>());
    }

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int pageNumber, int pageSize, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<TaskExecutionLog>>(Array.Empty<TaskExecutionLog>());
    }
}
