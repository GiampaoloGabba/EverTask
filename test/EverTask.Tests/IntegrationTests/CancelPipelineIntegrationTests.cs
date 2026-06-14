using System.Linq.Expressions;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// M4 — cancel pipeline: F17 (user cancel misclassified as ServiceStopped when shutdown races),
/// CU9/L46 (Completed written over Cancelled when the handler ignores a late cancel), CU13 (blacklist
/// must precede the Cancelled persist so a racing enqueue is discarded), L23/CU10 (a user-cancelled
/// recurring series must not schedule the next occurrence).
/// </summary>
public class CancelPipelineIntegrationTests : IsolatedIntegrationTestBase
{
    private readonly CancelTestState _state = new();

    [Fact]
    public async Task Should_classify_user_cancel_as_cancelled_even_when_shutdown_races()
    {
        // F17: a user cancel (blacklisted id) whose OCE is raised by the service token during shutdown
        // must classify as terminal Cancelled, not recoverable ServiceStopped (which re-executes at
        // the next restart).
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var id = await Dispatcher.Dispatch(new CancelBlockingTask());
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        WorkerBlacklist.Add(id);   // the user cancel's blacklist
        await StopHostAsync();      // shutdown cancels the service token → the handler's OCE

        await Task.Delay(500);
        var status = (await Storage.GetAll()).Single(t => t.Id == id).Status;
        status.ShouldBe(QueuedTaskStatus.Cancelled);
    }

    [Fact]
    public async Task Should_not_complete_over_cancelled_when_cancel_lands_before_token()
    {
        // CU9/L46: a cancel that lands before the per-task token is created (blacklist set + Cancelled
        // persisted, but the fresh token is NOT cancelled) lets the handler run to completion. The
        // general path must re-check the blacklist before writing the outcome, or it writes Completed
        // over the user's Cancelled. Modeled here by setting the blacklist + Cancelled directly (so the
        // running handler's token stays uncancelled) and then letting the handler complete.
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var id = await Dispatcher.Dispatch(new CancelBlockingTask());
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        WorkerBlacklist.Add(id);                                              // user cancel's blacklist
        await Storage.SetCancelledByUser(id, AuditLevel.ErrorsOnly);          // user cancel's persisted status
        _state.Gate.Release(10);                                              // handler completes (token uncancelled)

        await Task.Delay(800);

        var status = (await Storage.GetAll()).Single(t => t.Id == id).Status;
        status.ShouldBe(QueuedTaskStatus.Cancelled,
            "Completed must not clobber the user's Cancelled status when the cancel landed before the token");
    }

    [Fact]
    public async Task Should_keep_cancelled_when_enqueue_races_cancel()
    {
        // CU13: Dispatcher.Cancel must set the blacklist BEFORE persisting Cancelled, so a concurrent
        // enqueue (scheduler slot / gate) is discarded by the blacklist instead of writing SetQueued
        // over the Cancelled status.
        await CreateIsolatedHostWithBuilderAsync(b =>
        {
            b.Services.AddSingleton<ITaskStorage>(sp =>
                new BlacklistObservingStorage(
                    new MemoryTaskStorage(Mock.Of<IEverTaskLogger<MemoryTaskStorage>>()),
                    sp.GetRequiredService<IWorkerBlacklist>()));
            b.Services.AddSingleton(_state);
        },
        startHost: false);

        var id = await Dispatcher.Dispatch(new CancelBlockingTask());
        await Dispatcher.Cancel(id);

        ((BlacklistObservingStorage)Storage).WasBlacklistedAtCancelPersist.ShouldBeTrue(
            "the blacklist must be set before the Cancelled status is persisted, so a racing enqueue is discarded");
    }

    [Fact]
    public async Task Should_not_reschedule_recurring_after_user_cancel()
    {
        // L23/CU10: cancelling a recurring task mid-run must stop the series — no next occurrence is
        // scheduled (durable, independent of the in-memory blacklist's ~1h TTL).
        await CreateIsolatedHostAsync(configureServices: s => s.AddSingleton(_state));

        var id = await Dispatcher.Dispatch(new CancelRecurringBlockingTask(),
            r => r.RunNow().Then().Every(30).Seconds());
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        await Dispatcher.Cancel(id);
        _state.Gate.Release(10);

        await WaitForTaskStatusAsync(id, QueuedTaskStatus.Cancelled, timeoutMs: 5000);
        await Task.Delay(500); // let the worker's finally (QueueNextOccourrence) run

        // The next occurrence must NOT be scheduled: QueueNextOccourrence must not advance the run
        // counter for a cancelled series (deterministic, unlike the racy scheduler registration).
        var row = (await Storage.GetAll()).Single(t => t.Id == id);
        row.CurrentRunCount.ShouldBe(0,
            "a user-cancelled recurring series must not advance to / schedule its next occurrence");
    }

    /// <summary>
    /// <see cref="ITaskStorage"/> decorator that records whether the id was already blacklisted when its
    /// Cancelled status was persisted (CU13 ordering probe). All other operations delegate to the inner
    /// memory storage.
    /// </summary>
    private sealed class BlacklistObservingStorage(MemoryTaskStorage inner, IWorkerBlacklist blacklist) : ITaskStorage
    {
        public bool WasBlacklistedAtCancelPersist { get; private set; }

        public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel)
        {
            WasBlacklistedAtCancelPersist = blacklist.IsBlacklisted(taskId);
            return inner.SetCancelledByUser(taskId, auditLevel);
        }

        public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) => inner.Get(where, ct);
        public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => inner.GetAll(ct);
        public Task Persist(QueuedTask executor, CancellationToken ct = default) => inner.Persist(executor, ct);
        public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default) => inner.RetrievePending(lastCreatedAt, lastId, take, ct);
        public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.SetQueued(taskId, auditLevel, ct);
        public Task<bool> TrySetQueuedIfRecoverable(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.TrySetQueuedIfRecoverable(taskId, auditLevel, ct);
        public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) => inner.SetInProgress(taskId, auditLevel, ct);
        public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) => inner.SetCompleted(taskId, executionTimeMs, auditLevel);
        public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) => inner.SetCancelledByService(taskId, exception, auditLevel);
        public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel, double? executionTimeMs = null, CancellationToken ct = default) => inner.SetStatus(taskId, status, exception, auditLevel, executionTimeMs, ct);
        public Task<int> GetCurrentRunCount(Guid taskId) => inner.GetCurrentRunCount(taskId);
        public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun, AuditLevel auditLevel) => inner.UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel);
        public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) => inner.GetByTaskKey(taskKey, ct);
        public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => inner.UpdateTask(task, ct);
        public Task Remove(Guid taskId, CancellationToken ct = default) => inner.Remove(taskId, ct);
        public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken) => inner.SaveExecutionLogsAsync(taskId, logs, cancellationToken);
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken) => inner.GetExecutionLogsAsync(taskId, cancellationToken);
        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken) => inner.GetExecutionLogsAsync(taskId, skip, take, cancellationToken);
    }
}
