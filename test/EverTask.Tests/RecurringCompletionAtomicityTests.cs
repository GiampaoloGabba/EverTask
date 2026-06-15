using System.Linq.Expressions;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// M5 (batch B) — CU14/L29: completing a recurring occurrence and advancing its run counter / next
/// run used to be TWO separate storage writes (<c>SetCompleted</c> then <c>UpdateCurrentRun</c>). A
/// crash between them left the row Completed but not advanced, so recovery re-dispatched the
/// already-finished occurrence and a MaxRuns-bounded series ran one extra time.
///
/// <para>
/// [UNIT-necessario: la finestra crash-tra-i-due-write non è raggiungibile end-to-end; si inietta il
/// crash con un decorator <see cref="ITaskStorage"/> che lancia sulla scrittura di avanzamento.]
/// </para>
/// </summary>
public class RecurringCompletionAtomicityTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_not_leave_recurring_completed_but_not_advanced_when_advance_crashes()
    {
        var inner = new MemoryTaskStorage(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>());

        await CreateIsolatedHostWithBuilderAsync(
            b => b.Services.AddSingleton<ITaskStorage>(_ => new CrashOnAdvanceStorage(inner)),
            startHost: false);

        var id        = Guid.NewGuid();
        var recurring = new RecurringTask { SecondInterval = new SecondInterval(30) };
        var executor  = new TaskHandlerExecutor(
            new TestTaskRecurringSeconds(),
            Handler: null,
            HandlerTypeName: typeof(TestTaskRecurringSecondsHandler).AssemblyQualifiedName,
            ExecutionTime: DateTimeOffset.UtcNow,
            RecurringTask: recurring,
            HandlerCallback: null,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: id,
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.None);

        await inner.Persist(executor.ToQueuedTask()); // WaitingQueue, CurrentRunCount 0, NextRunUtc ~ now

        // The injected crash (the advance write throws) must surface out of DoWork.
        await Should.ThrowAsync<Exception>(async () =>
            await WorkerExecutor.DoWork(executor, CancellationToken.None));

        var row = (await inner.GetAll()).Single(t => t.Id == id);

        // The atomic CompleteRecurringRun crashed, so NEITHER half may have landed: the row must still be
        // in its pre-completion InProgress state with the counter un-advanced (not Completed, not advanced)
        // — a split write that persisted Completed-without-advance would let recovery resurrect the
        // finished occurrence (CU14/L29). Asserting the exact durable state keeps this from passing
        // vacuously (e.g. if the row were simply never written).
        row.Status.ShouldBe(QueuedTaskStatus.InProgress,
            "the crashed atomic completion must leave the row in its pre-completion InProgress state, not Completed");
        (row.CurrentRunCount ?? 0).ShouldBe(0, "the run counter must not advance when the completion crashed");

        var completedButNotAdvanced = row.Status == QueuedTaskStatus.Completed && (row.CurrentRunCount ?? 0) == 0;
        completedButNotAdvanced.ShouldBeFalse(
            "completion and run-counter advance must be atomic: a crash must not persist Completed without the advance");
    }

    /// <summary>
    /// <see cref="ITaskStorage"/> decorator that simulates a process crash by throwing on the run-counter
    /// advance writes (<see cref="ITaskStorage.UpdateCurrentRun(Guid,double,DateTimeOffset?,AuditLevel)"/>
    /// and <see cref="ITaskStorage.CompleteRecurringRun"/>). Everything else delegates to the inner store.
    /// </summary>
    private sealed class CrashOnAdvanceStorage(MemoryTaskStorage inner) : ITaskStorage
    {
        public Task CompleteRecurringRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel)
            => throw new InvalidOperationException("simulated crash during recurring completion");

        public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel)
            => throw new InvalidOperationException("simulated crash during run-counter advance");

        public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) => inner.Get(where, ct);
        public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => inner.GetAll(ct);
        public Task Persist(QueuedTask executor, CancellationToken ct = default) => inner.Persist(executor, ct);
        public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default) => inner.RetrievePending(lastCreatedAt, lastId, take, ct);
        public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.SetQueued(taskId, auditLevel, ct);
        public Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.TrySetQueuedIfRecoverable(taskId, auditLevel, ct);
        public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.SetInProgress(taskId, auditLevel, ct);
        public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) => inner.SetCompleted(taskId, executionTimeMs, auditLevel);
        public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) => inner.SetCancelledByUser(taskId, auditLevel);
        public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) => inner.SetCancelledByService(taskId, exception, auditLevel);
        public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel, double? executionTimeMs = null, CancellationToken ct = default) => inner.SetStatus(taskId, status, exception, auditLevel, executionTimeMs, ct);
        public Task<int> GetCurrentRunCount(Guid taskId) => inner.GetCurrentRunCount(taskId);
        public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) => inner.GetByTaskKey(taskKey, ct);
        public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => inner.UpdateTask(task, ct);
        public Task Remove(Guid taskId, CancellationToken ct = default) => inner.Remove(taskId, ct);
        public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken) => inner.SaveExecutionLogsAsync(taskId, logs, cancellationToken);
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken) => inner.GetExecutionLogsAsync(taskId, cancellationToken);
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken) => inner.GetExecutionLogsAsync(taskId, skip, take, cancellationToken);
    }
}
