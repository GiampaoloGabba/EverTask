using System.Linq.Expressions;
using EverTask.Logger;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// X3, failure side: a recovery FINALIZATION that throws is accounted exactly like a re-dispatch that
/// throws — counted durably (L18), retried while the count is below the limit, poisoned terminally once it
/// reaches it, and the counter cleared the moment a later attempt succeeds. Without that accounting a
/// series nobody can finalize would be retried at every restart forever while the summary logged success.
/// </summary>
/// <remarks>
/// The store is a REAL <see cref="MemoryTaskStorage"/> behind <see cref="FaultInjectingTaskStorage"/>: the
/// counter, the poison write and the finalization all execute for real, and the only thing the test decides
/// is when <c>TrySetRecurringSeriesCompleted</c> is allowed to reach it. A mocked storage would have made
/// both the failure and the recovery from it fictional.
/// </remarks>
public class RecoveryFinalizationFailureTests
{
    private readonly MemoryTaskStorage _real = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    /// <summary>A series whose pending slot sits at or past RunUntil: category (ii), finalize, never run.</summary>
    private static QueuedTask SeriesToFinalize() => new()
    {
        Id            = Guid.NewGuid(),
        Type          = "EverTask.Tests.SeriesToFinalizeProbe",
        Request       = "{}",
        Handler       = "probe",
        Status        = QueuedTaskStatus.Queued,
        IsRecurring   = true,
        CreatedAtUtc  = DateTimeOffset.UtcNow.AddMinutes(-10),
        RunUntil      = DateTimeOffset.UtcNow.AddMinutes(-5),
        NextRunUtc    = DateTimeOffset.UtcNow.AddMinutes(-4)
    };

    private static WorkerService CreateRecovery(ITaskStorage storage, int maxAttempts) =>
        RecoveryHarness.CreateRecoveryService(storage, maxAttempts);

    private async Task<QueuedTask> ReadBack(Guid id) => (await _real.Get(t => t.Id == id)).Single();

    [Fact]
    public async Task Should_count_a_failing_finalization_and_poison_the_series_after_the_limit()
    {
        var row = SeriesToFinalize();
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.FailAlways(nameof(ITaskStorage.TrySetRecurringSeriesCompleted));

        var service = CreateRecovery(storage, maxAttempts: 2);

        await service.ProcessPendingAsync();

        var afterFirst = await ReadBack(row.Id);
        afterFirst.RecoveryDispatchFailureCount.ShouldBe(1, "a failing finalization must be counted durably (L18)");
        afterFirst.Status.ShouldBe(QueuedTaskStatus.Queued, "one failure is transient: the row stays recoverable");
        afterFirst.NextRunUtc.ShouldNotBeNull();

        await service.ProcessPendingAsync();

        var afterSecond = await ReadBack(row.Id);
        afterSecond.Status.ShouldBe(QueuedTaskStatus.Failed,
            "a finalization that keeps failing must be poisoned at the attempt limit, not retried forever");
        afterSecond.NextRunUtc.ShouldBeNull(
            "the poison must be TERMINAL for a recurring row: a Failed row with a cursor is revived at every restart");

        // Terminal means terminal: the poisoned row is no longer part of a recovery page.
        var attemptsSoFar = storage.Calls[nameof(ITaskStorage.TrySetRecurringSeriesCompleted)];
        await service.ProcessPendingAsync();
        storage.Calls[nameof(ITaskStorage.TrySetRecurringSeriesCompleted)].ShouldBe(attemptsSoFar);
    }

    [Fact]
    public async Task Should_clear_the_failure_counter_when_a_later_finalization_succeeds()
    {
        var row = SeriesToFinalize();
        await _real.Persist(row);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.FailNext(nameof(ITaskStorage.TrySetRecurringSeriesCompleted), times: 1);

        var service = CreateRecovery(storage, maxAttempts: 3);

        await service.ProcessPendingAsync();
        (await ReadBack(row.Id)).RecoveryDispatchFailureCount.ShouldBe(1);

        await service.ProcessPendingAsync();

        var finalized = await ReadBack(row.Id);
        finalized.Status.ShouldBe(QueuedTaskStatus.Completed);
        finalized.NextRunUtc.ShouldBeNull("finalizing clears the cursor in the same write");
        // Cleared, whichever way the provider spells "no failures" (null in memory, 0 on a relational row).
        (finalized.RecoveryDispatchFailureCount ?? 0).ShouldBe(0,
            "a transient failure must not accumulate toward the poison limit once the row reaches its terminal state");
        storage.Calls.ContainsKey(nameof(ITaskStorage.ClearRecoveryFailure)).ShouldBeTrue(
            "the success path must reset the counter explicitly, not rely on the row being terminal");
    }

    [Fact]
    public async Task Should_not_poison_a_finalized_series_when_only_the_counter_reset_fails()
    {
        // The terminal write is already committed when the counter reset runs. Sharing the finalization's
        // catch with it let a housekeeping failure be counted as a finalization failure — and on a row one
        // attempt short of the limit that means overwriting a just-committed Completed with Failed.
        var row = SeriesToFinalize();
        await _real.Persist(row);

        for (var i = 0; i < 4; i++)
            await _real.IncrementRecoveryFailure(row.Id);

        var storage = new FaultInjectingTaskStorage(_real);
        storage.FailAlways(nameof(ITaskStorage.ClearRecoveryFailure));

        var service = CreateRecovery(storage, maxAttempts: 5);

        await service.ProcessPendingAsync();

        var finalized = await ReadBack(row.Id);
        finalized.Status.ShouldBe(QueuedTaskStatus.Completed,
            "the series was finalized; a failed counter reset must not rewrite it as Failed");
        finalized.NextRunUtc.ShouldBeNull();
        storage.Calls.ContainsKey(nameof(ITaskStorage.SetRecurringTaskPoisoned)).ShouldBeFalse(
            "the poison path belongs to a failed finalization, not to failed bookkeeping after a committed one");
    }

    [Fact]
    public async Task Should_finalize_a_series_unconditionally_on_a_storage_without_schedule_versioning()
    {
        // X3: the compare-and-swap is used wherever the storage CAN do it. A custom storage from before
        // schedule versioning answers the CAS member with NotSupportedException, and treating a normal end of
        // series as an L18 failure poisons the row after N restarts (or retries it forever) instead of ending
        // it. Both finalization sites must degrade to the historical unconditional write.
        var row = SeriesToFinalize();
        var storage = new CasFreeTaskStorage(_real);
        await storage.Persist(row);

        var service = CreateRecovery(storage, maxAttempts: 2);

        await service.ProcessPendingAsync();
        await service.ProcessPendingAsync();

        var finalized = await ReadBack(row.Id);
        finalized.Status.ShouldBe(QueuedTaskStatus.Completed,
            "a storage without the compare-and-swap keeps the historical unconditional write");
        finalized.NextRunUtc.ShouldBeNull("the cursor is what kept the zombie alive across restarts");
        (finalized.RecoveryDispatchFailureCount ?? 0).ShouldBe(0,
            "the end of a series is not a recovery failure");
    }

    [Fact]
    public async Task Should_not_finalize_a_durable_series_unconditionally_without_schedule_versioning()
    {
        var storage = new CapabilityBlindStorage(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object,
            scheduleVersioning: false, durableOccurrences: true);
        var row = SeriesToFinalize();
        await storage.Persist(row);

        var service = CreateRecovery(storage, maxAttempts: 2);

        await service.ProcessPendingAsync();

        var unchanged = (await storage.Get(t => t.Id == row.Id)).Single();
        unchanged.Status.ShouldBe(QueuedTaskStatus.Queued,
            "a durable store cannot safely finalize a series without a conditional write");
        unchanged.NextRunUtc.ShouldBe(row.NextRunUtc,
            "recovery must leave the cursor intact when it cannot prove that the snapshot still owns the row");
    }

    /// <summary>
    /// A custom <see cref="ITaskStorage"/> as <c>docs/storage/custom-storage.md</c> describes one: it
    /// implements the members that existed before durable occurrences and inherits every new one from the
    /// interface's defaults — so <see cref="ITaskStorage.SupportsScheduleVersioning"/> stays false and
    /// <see cref="ITaskStorage.TrySetRecurringSeriesCompleted"/> throws
    /// <see cref="NotSupportedException"/>, exactly as a real one would.
    /// </summary>
    /// <remarks>
    /// Every legacy member forwards to a REAL <see cref="MemoryTaskStorage"/>, so the fallback write, the
    /// audit it produces and the recovery-failure counter all run for real; the only thing this class decides
    /// is which members exist. Notably <c>SetRecurringSeriesCompleted</c> is NOT forwarded — the interface's
    /// own two-write default is what must end the series here.
    /// </remarks>
    private sealed class CasFreeTaskStorage(MemoryTaskStorage inner) : ITaskStorage
    {
        public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) =>
            inner.Get(where, ct);

        public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => inner.GetAll(ct);

        public Task Persist(QueuedTask executor, CancellationToken ct = default) => inner.Persist(executor, ct);

        public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                  CancellationToken ct = default) =>
            inner.RetrievePending(lastCreatedAt, lastId, take, ct);

        public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
            inner.SetQueued(taskId, auditLevel, ct);

        public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
            inner.SetInProgress(taskId, auditLevel, ct);

        public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) =>
            inner.SetCompleted(taskId, executionTimeMs, auditLevel);

        public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) =>
            inner.SetCancelledByUser(taskId, auditLevel);

        public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
            inner.SetCancelledByService(taskId, exception, auditLevel);

        public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                              double? executionTimeMs = null, CancellationToken ct = default) =>
            inner.SetStatus(taskId, status, exception, auditLevel, executionTimeMs, ct);

        public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                     AuditLevel auditLevel) =>
            inner.UpdateCurrentRun(taskId, executionTimeMs, nextRun, auditLevel);

        public Task<int> IncrementRecoveryFailure(Guid taskId, CancellationToken ct = default) =>
            inner.IncrementRecoveryFailure(taskId, ct);

        public Task ClearRecoveryFailure(Guid taskId, CancellationToken ct = default) =>
            inner.ClearRecoveryFailure(taskId, ct);

        public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) =>
            inner.GetByTaskKey(taskKey, ct);

        public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => inner.UpdateTask(task, ct);

        public Task Remove(Guid taskId, CancellationToken ct = default) => inner.Remove(taskId, ct);

        public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                           CancellationToken cancellationToken) =>
            inner.SaveExecutionLogsAsync(taskId, logs, cancellationToken);

        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken ct) =>
            inner.GetExecutionLogsAsync(taskId, ct);

        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take,
                                                                          CancellationToken ct) =>
            inner.GetExecutionLogsAsync(taskId, skip, take, ct);
    }
}
