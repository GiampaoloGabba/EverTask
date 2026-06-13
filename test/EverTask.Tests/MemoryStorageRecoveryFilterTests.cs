using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Tests for the startup-recovery predicate of <see cref="MemoryTaskStorage"/>: both
/// <see cref="MemoryTaskStorage.RetrievePending"/> and the atomic
/// <see cref="MemoryTaskStorage.TrySetQueuedIfRecoverable"/> must apply the SAME canonical
/// <see cref="QueuedTask.IsRecoverable"/> rules — WaitingQueue rows (persisted but never delivered)
/// and recurring tasks between two runs are recoverable; terminal one-shot statuses, tasks past
/// their RunUntil and tasks that exhausted MaxRuns are not.
/// </summary>
public class MemoryStorageRecoveryFilterTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private static QueuedTask CreateTask(
        QueuedTaskStatus status,
        bool isRecurring = false,
        DateTimeOffset? nextRunUtc = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? runUntil = null,
        int? maxRuns = null,
        int? currentRunCount = null) =>
        new()
        {
            Id              = Guid.NewGuid(),
            Type            = "TestType",
            Request         = "{}",
            Handler         = "TestHandler",
            Status          = status,
            IsRecurring     = isRecurring,
            NextRunUtc      = nextRunUtc,
            CreatedAtUtc    = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1),
            RunUntil        = runUntil,
            MaxRuns         = maxRuns,
            CurrentRunCount = currentRunCount
        };

    [Theory]
    [InlineData(QueuedTaskStatus.WaitingQueue)]
    [InlineData(QueuedTaskStatus.Queued)]
    [InlineData(QueuedTaskStatus.Pending)]
    [InlineData(QueuedTaskStatus.ServiceStopped)]
    [InlineData(QueuedTaskStatus.InProgress)]
    public async Task Should_retrieve_recoverable_statuses(QueuedTaskStatus status)
    {
        var task = CreateTask(status);
        await _storage.Persist(task);

        var pending = await _storage.RetrievePending(null, null, 10);

        pending.ShouldContain(t => t.Id == task.Id);
    }

    [Theory]
    [InlineData(QueuedTaskStatus.Completed)]
    [InlineData(QueuedTaskStatus.Failed)]
    [InlineData(QueuedTaskStatus.Cancelled)]
    public async Task Should_not_retrieve_terminal_one_shot_statuses(QueuedTaskStatus status)
    {
        var task = CreateTask(status);
        await _storage.Persist(task);

        var pending = await _storage.RetrievePending(null, null, 10);

        pending.ShouldNotContain(t => t.Id == task.Id);
    }

    [Theory]
    [InlineData(QueuedTaskStatus.Completed)]
    [InlineData(QueuedTaskStatus.Failed)]
    public async Task Should_retrieve_recurring_task_between_runs(QueuedTaskStatus status)
    {
        // A recurring task between two runs sits in storage as Completed/Failed with a
        // future NextRunUtc: it must be revived at startup or it dies after a restart.
        var task = CreateTask(status, isRecurring: true, nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(5));
        await _storage.Persist(task);

        var pending = await _storage.RetrievePending(null, null, 10);

        pending.ShouldContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Should_not_retrieve_recurring_task_without_next_run()
    {
        // Recurring task that exhausted its schedule (MaxRuns/RunUntil): no NextRunUtc, not revived.
        var task = CreateTask(QueuedTaskStatus.Completed, isRecurring: true, nextRunUtc: null);
        await _storage.Persist(task);

        var pending = await _storage.RetrievePending(null, null, 10);

        pending.ShouldNotContain(t => t.Id == task.Id);
    }

    [Fact]
    public async Task Should_not_retrieve_cancelled_recurring_task()
    {
        // A user-cancelled recurring task must never be revived, even with a future NextRunUtc.
        var task = CreateTask(QueuedTaskStatus.Cancelled, isRecurring: true,
            nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(5));
        await _storage.Persist(task);

        var pending = await _storage.RetrievePending(null, null, 10);

        pending.ShouldNotContain(t => t.Id == task.Id);
    }

    [Theory]
    [InlineData(QueuedTaskStatus.WaitingQueue)]
    [InlineData(QueuedTaskStatus.Queued)]
    [InlineData(QueuedTaskStatus.InProgress)]
    public async Task Should_TrySetQueuedIfRecoverable_transition_recoverable_status(QueuedTaskStatus status)
    {
        var task = CreateTask(status);
        await _storage.Persist(task);

        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(t => t.Id == task.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Theory]
    [InlineData(QueuedTaskStatus.Completed)]
    [InlineData(QueuedTaskStatus.Failed)]
    [InlineData(QueuedTaskStatus.Cancelled)]
    public async Task Should_TrySetQueuedIfRecoverable_refuse_terminal_one_shot(QueuedTaskStatus status)
    {
        var task = CreateTask(status);
        await _storage.Persist(task);

        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(t => t.Id == task.Id))[0].Status.ShouldBe(status);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_refuse_recurring_task_past_RunUntil()
    {
        // Regression (F1): TrySetQueuedIfRecoverable must apply the SAME MaxRuns/RunUntil guards as
        // RetrievePending. A recurring task past its RunUntil must never be resurrected even though
        // its status (Completed + future NextRunUtc) looks recoverable on status alone.
        var task = CreateTask(QueuedTaskStatus.Completed, isRecurring: true,
            nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(5), runUntil: DateTimeOffset.UtcNow.AddMinutes(-5));
        await _storage.Persist(task);

        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(t => t.Id == task.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_refuse_recurring_task_with_exhausted_MaxRuns()
    {
        // Regression (F1): a recurring task that exhausted MaxRuns must not be resurrected.
        var task = CreateTask(QueuedTaskStatus.Completed, isRecurring: true,
            nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(5), maxRuns: 3, currentRunCount: 4);
        await _storage.Persist(task);

        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full)).ShouldBeFalse();
        (await _storage.Get(t => t.Id == task.Id))[0].Status.ShouldBe(QueuedTaskStatus.Completed);
    }

    [Fact]
    public async Task Should_TrySetQueuedIfRecoverable_transition_recurring_task_within_bounds()
    {
        var task = CreateTask(QueuedTaskStatus.Completed, isRecurring: true,
            nextRunUtc: DateTimeOffset.UtcNow.AddMinutes(5), runUntil: DateTimeOffset.UtcNow.AddMinutes(10),
            maxRuns: 10, currentRunCount: 1);
        await _storage.Persist(task);

        (await _storage.TrySetQueuedIfRecoverable(task.Id, AuditLevel.Full)).ShouldBeTrue();
        (await _storage.Get(t => t.Id == task.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }
}
