using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Tests for the startup-recovery filter of <see cref="MemoryTaskStorage.RetrievePending"/>:
/// WaitingQueue rows (persisted but never delivered) and recurring tasks between two runs
/// must be recoverable; terminal one-shot statuses must not.
/// </summary>
public class MemoryStorageRecoveryFilterTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private static QueuedTask CreateTask(
        QueuedTaskStatus status,
        bool isRecurring = false,
        DateTimeOffset? nextRunUtc = null,
        DateTimeOffset? createdAt = null) =>
        new()
        {
            Id           = Guid.NewGuid(),
            Type         = "TestType",
            Request      = "{}",
            Handler      = "TestHandler",
            Status       = status,
            IsRecurring  = isRecurring,
            NextRunUtc   = nextRunUtc,
            CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow.AddMinutes(-1)
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
}
