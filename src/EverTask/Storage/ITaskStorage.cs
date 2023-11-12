using System.Linq.Expressions;

namespace EverTask.Storage;

public interface ITaskStorage
{
    Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default);
    Task<QueuedTask[]> GetAll(CancellationToken ct = default);
    Task PersistTask(QueuedTask executor, CancellationToken ct = default);
    Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default);
    Task SetTaskQueued(Guid taskId, CancellationToken ct = default);
    Task SetTaskInProgress(Guid taskId, CancellationToken ct = default);
    Task SetTaskCompleted(Guid taskId, CancellationToken ct = default);
    Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null, CancellationToken ct = default);
}
