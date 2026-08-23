using System.Linq.Expressions;
using EverTask.Abstractions;
using EverTask.Storage;

namespace EverTask.ConsumerCompatibility.Baseline;

/// <summary>
/// A storage that implements exactly what <see cref="ITaskStorage"/> required in the previous release.
/// </summary>
/// <remarks>
/// The in-tree twin of this class proves the same thing at COMPILE time. This one proves it at LOAD time:
/// the CLR builds the interface map against today's <see cref="ITaskStorage"/>, so if any of the members
/// added since had arrived abstract instead of as a default one, merely touching this type would throw a
/// <see cref="TypeLoadException"/> — recompiling the twin would not.
/// </remarks>
public sealed class BaselineTaskStorage : ITaskStorage
{
    private readonly List<QueuedTask> _rows = [];

    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) =>
        Task.FromResult(_rows.Where(where.Compile()).ToArray());

    public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => Task.FromResult(_rows.ToArray());

    public Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        _rows.Add(executor);
        return Task.CompletedTask;
    }

    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                              CancellationToken ct = default) =>
        Task.FromResult(_rows.Take(take).ToArray());

    public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.Queued, null, auditLevel, null, ct);

    public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
        SetStatus(taskId, QueuedTaskStatus.InProgress, null, auditLevel, null, ct);

    public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Completed, null, auditLevel, executionTimeMs);

    public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Cancelled, null, auditLevel);

    public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
        SetStatus(taskId, QueuedTaskStatus.Cancelled, exception, auditLevel);

    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                          double? executionTimeMs = null, CancellationToken ct = default)
    {
        var row = _rows.FirstOrDefault(r => r.Id == taskId);
        if (row != null)
            row.Status = status;

        return Task.CompletedTask;
    }

    public Task<int> GetCurrentRunCount(Guid taskId) => Task.FromResult(0);

    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                 AuditLevel auditLevel) => Task.CompletedTask;

    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) =>
        Task.FromResult(_rows.FirstOrDefault(r => r.TaskKey == taskKey));

    public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => Task.CompletedTask;

    public Task Remove(Guid taskId, CancellationToken ct = default)
    {
        _rows.RemoveAll(r => r.Id == taskId);
        return Task.CompletedTask;
    }

    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                       CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId,
                                                                      CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);

    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take,
                                                                      CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);
}
