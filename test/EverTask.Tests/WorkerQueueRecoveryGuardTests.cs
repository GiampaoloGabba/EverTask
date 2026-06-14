using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Threading.Channels;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Storage;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

/// <summary>
/// M1 — recovery-resurrection / double-delivery guards (CU1/L21 + F25).
///
/// These are the <c>[UNIT-necessario]</c> cases of the milestone: the "channel-full after the
/// capacity check" rollback window is not reachable deterministically end-to-end, and the
/// <c>ITaskStorage</c> DIM contract for <c>TrySetQueuedIfRecoverable</c> is a pure unit concern.
/// </summary>
public class WorkerQueueRecoveryGuardTests
{
    private static TaskHandlerExecutor CreateExecutor(Guid? id = null, string? queueName = null) =>
        new(new ResilienceCounterTask(0),
            new object(),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            id ?? Guid.NewGuid(),
            queueName,
            null,
            AuditLevel.Full);

    private static WorkerQueue CreateQueue(
        int capacity,
        ITaskStorage? storage = null,
        TaskDeliveryRegistry? registry = null) =>
        new(new QueueConfiguration
            {
                Name                   = "test",
                MaxDegreeOfParallelism = 1,
                ChannelOptions         = new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait }
            },
            Mock.Of<ILogger>(),
            Mock.Of<IWorkerBlacklist>(),
            storage,
            registry);

    // ---- CU1 / L21 ---------------------------------------------------------------------------

    [Fact]
    public async Task Should_hold_delivery_registration_until_rollback_revert_completes()
    {
        // CU1/L21: on the full-queue rollback (TryWrite fails after the capacity check passed) the
        // delivery registration must survive UNTIL the revert to WaitingQueue has completed. Today
        // End() runs BEFORE the revert, so a successor delivery slips into the window, writes the
        // SAME task to the channel and executes it, then the predecessor's revert clobbers it.
        var registry = new TaskDeliveryRegistry();
        var storage  = new FakeTaskStorage();
        WorkerQueue queue = null!;

        var outer  = CreateExecutor();
        var filler = CreateExecutor();
        var filled = false;

        storage.Seed(new QueuedTask { Id = outer.PersistenceId,  Status = QueuedTaskStatus.WaitingQueue });
        storage.Seed(new QueuedTask { Id = filler.PersistenceId, Status = QueuedTaskStatus.WaitingQueue });

        // Fill the capacity-1 channel during the outer's SetQueued so its later TryWrite fails.
        storage.OnSetQueued = async id =>
        {
            if (id == outer.PersistenceId && !filled)
            {
                filled = true;
                (await queue.TryQueue(filler)).ShouldBe(EnqueueResult.Enqueued);
            }
        };

        bool          registrationHeldDuringRevert = false;
        EnqueueResult concurrentInWindow           = EnqueueResult.Enqueued;

        // Probe the rollback window from inside the revert (SetStatus -> WaitingQueue).
        storage.OnSetStatus = async (id, status) =>
        {
            if (id != outer.PersistenceId || status != QueuedTaskStatus.WaitingQueue)
                return;

            registrationHeldDuringRevert = registry.IsDelivering(outer.PersistenceId);

            // Drain the channel so a successor can reach the registry check (past the capacity
            // fast-path), then attempt a concurrent delivery of the SAME id: it must be rejected
            // as a duplicate while the predecessor still owns the registration.
            await queue.Dequeue(CancellationToken.None);
            concurrentInWindow = await queue.TryQueue(outer);
        };

        queue = CreateQueue(capacity: 1, storage: storage, registry: registry);

        (await queue.TryQueue(outer)).ShouldBe(EnqueueResult.QueueFull);

        registrationHeldDuringRevert.ShouldBeTrue(
            "the delivery registration must still be held while the revert to WaitingQueue runs");
        concurrentInWindow.ShouldBe(EnqueueResult.DuplicateInProcess,
            "a concurrent delivery in the rollback window must be rejected as a duplicate, not enqueued");
    }

    [Fact]
    public async Task Should_not_clobber_successor_status_on_full_queue_rollback()
    {
        // CU1/L21: the revert must be CONDITIONAL on the row still being Queued. If a successor
        // delivery already advanced it to InProgress/Completed, the predecessor's rollback must
        // NOT downgrade it back to WaitingQueue (a lost update that re-executes it at restart).
        var registry = new TaskDeliveryRegistry();
        var storage  = new FakeTaskStorage();
        WorkerQueue queue = null!;

        var outer  = CreateExecutor();
        var filler = CreateExecutor();
        var filled = false;

        storage.Seed(new QueuedTask { Id = outer.PersistenceId,  Status = QueuedTaskStatus.WaitingQueue });
        storage.Seed(new QueuedTask { Id = filler.PersistenceId, Status = QueuedTaskStatus.WaitingQueue });

        storage.OnSetQueued = async id =>
        {
            if (id != outer.PersistenceId || filled)
                return;

            filled = true;
            (await queue.TryQueue(filler)).ShouldBe(EnqueueResult.Enqueued);

            // A successor delivery of the SAME id wins the channel and advances it to InProgress
            // (it is now executing) BEFORE the predecessor gets to revert.
            storage.Set(outer.PersistenceId, QueuedTaskStatus.InProgress);
        };

        queue = CreateQueue(capacity: 1, storage: storage, registry: registry);

        (await queue.TryQueue(outer)).ShouldBe(EnqueueResult.QueueFull);

        storage.StatusOf(outer.PersistenceId).ShouldBe(QueuedTaskStatus.InProgress,
            "the rollback must not clobber a successor that already moved the task to InProgress");
    }

    // ---- F25 ---------------------------------------------------------------------------------

    [Fact]
    public async Task Should_refuse_unconditional_setqueued_for_custom_storage_without_override()
    {
        // F25: a third-party ITaskStorage that implements the interface but does NOT override
        // TrySetQueuedIfRecoverable must not be able to resurrect a terminally finished row via the
        // default interface member. The DIM fallback must be conditional, never an unconditional
        // SetQueued.
        ITaskStorage storage = new FakeTaskStorage(); // uses the DIM (no override)

        var terminalId = Guid.NewGuid();
        ((FakeTaskStorage)storage).Seed(new QueuedTask
        {
            Id          = terminalId,
            Status      = QueuedTaskStatus.Completed, // terminal, non-recurring => NOT recoverable
            IsRecurring = false
        });

        var transitioned = await storage.TrySetQueuedIfRecoverable(terminalId, AuditLevel.Full);

        transitioned.ShouldBeFalse("a terminal row must never be transitioned back to Queued");
        ((FakeTaskStorage)storage).SetQueuedCalls.ShouldNotContain(terminalId,
            "the DIM fallback must not issue an unconditional SetQueued for a terminal row");
        ((FakeTaskStorage)storage).StatusOf(terminalId).ShouldBe(QueuedTaskStatus.Completed);
    }

    /// <summary>
    /// Minimal stateful <see cref="ITaskStorage"/> double. Deliberately does NOT override
    /// <see cref="ITaskStorage.TrySetQueuedIfRecoverable"/> so the F25 test exercises the default
    /// interface member.
    /// </summary>
    private sealed class FakeTaskStorage : ITaskStorage
    {
        private readonly ConcurrentDictionary<Guid, QueuedTask> _rows = new();

        public List<Guid>           SetQueuedCalls { get; }      = new();
        public Func<Guid, Task>?    OnSetQueued    { get; set; }
        public Func<Guid, QueuedTaskStatus, Task>? OnSetStatus { get; set; }

        public void Seed(QueuedTask row) => _rows[row.Id] = row;
        public void Set(Guid id, QueuedTaskStatus status) { if (_rows.TryGetValue(id, out var r)) r.Status = status; }
        public QueuedTaskStatus StatusOf(Guid id) => _rows[id].Status;

        public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default)
            => Task.FromResult(_rows.Values.Where(where.Compile()).ToArray());

        public Task<QueuedTask[]> GetAll(CancellationToken ct = default)
            => Task.FromResult(_rows.Values.ToArray());

        public Task Persist(QueuedTask executor, CancellationToken ct = default)
        {
            _rows[executor.Id] = executor;
            return Task.CompletedTask;
        }

        public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default)
            => Task.FromResult(Array.Empty<QueuedTask>());

        public async Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
        {
            lock (SetQueuedCalls) SetQueuedCalls.Add(taskId);
            Set(taskId, QueuedTaskStatus.Queued);
            if (OnSetQueued != null)
                await OnSetQueued(taskId).ConfigureAwait(false);
        }

        public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
        {
            Set(taskId, QueuedTaskStatus.InProgress);
            return Task.CompletedTask;
        }

        public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel)
        {
            Set(taskId, QueuedTaskStatus.Completed);
            return Task.CompletedTask;
        }

        public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel)
        {
            Set(taskId, QueuedTaskStatus.Cancelled);
            return Task.CompletedTask;
        }

        public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel)
        {
            Set(taskId, QueuedTaskStatus.ServiceStopped);
            return Task.CompletedTask;
        }

        public async Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                                    double? executionTimeMs = null, CancellationToken ct = default)
        {
            Set(taskId, status);
            if (OnSetStatus != null)
                await OnSetStatus(taskId, status).ConfigureAwait(false);
        }

        public Task<int> GetCurrentRunCount(Guid taskId) => Task.FromResult(0);
        public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel) => Task.CompletedTask;
        public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) => Task.FromResult<QueuedTask?>(null);
        public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => Task.CompletedTask;
        public Task Remove(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TaskExecutionLog>>(Array.Empty<TaskExecutionLog>());
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TaskExecutionLog>>(Array.Empty<TaskExecutionLog>());
    }
}
