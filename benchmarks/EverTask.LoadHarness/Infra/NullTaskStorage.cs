using System.Linq.Expressions;
using EverTask.Abstractions;
using EverTask.Storage;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// A storage that persists NOTHING — every read returns empty, every write is a no-op. Used by the
/// "worker-only" anchor (A4): running the real EverTask engine over this isolates the engine cost
/// (wrappers, executor, scheduler routing, blacklist, delivery registry) from persistence, so the gap
/// between A4-worker and L8 is the pure storage cost (BENCHMARK_PLAN §4).
///
/// Not for correctness use: recovery finds nothing, dedup never matches. That's intentional.
/// </summary>
public sealed class NullTaskStorage : ITaskStorage
{
    private static readonly QueuedTask[] Empty = [];

    public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) => Task.FromResult(Empty);
    public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => Task.FromResult(Empty);
    public Task Persist(QueuedTask executor, CancellationToken ct = default) => Task.CompletedTask;
    public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default) => Task.FromResult(Empty);
    public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) => Task.CompletedTask;
    public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) => Task.CompletedTask;
    public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) => Task.CompletedTask;
    public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel, double? executionTimeMs = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetCurrentRunCount(Guid taskId) => Task.FromResult(0);
    public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel) => Task.CompletedTask;
    public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) => Task.FromResult<QueuedTask?>(null);
    public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => Task.CompletedTask;
    public Task Remove(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;
    public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);
    public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);
}
