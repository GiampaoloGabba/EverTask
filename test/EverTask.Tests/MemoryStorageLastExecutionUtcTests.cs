using EverTask.Logger;
using EverTask.Storage;

namespace EverTask.Tests;

/// <summary>
/// Tests for the LastExecutionUtc rule of <see cref="MemoryTaskStorage.SetStatus"/>:
/// only terminal transitions (a run actually finished) write LastExecutionUtc; intermediate
/// statuses (WaitingQueue, Queued, InProgress, Cancelled, Pending) preserve the previous value.
/// Same rule as EfCoreTaskStorage.SetStatus and the usp_SetTaskStatus stored procedure.
/// </summary>
public class MemoryStorageLastExecutionUtcTests
{
    private readonly MemoryTaskStorage _storage = new(new Mock<IEverTaskLogger<MemoryTaskStorage>>().Object);

    private async Task<QueuedTask> PersistTask(QueuedTaskStatus status, DateTimeOffset? lastExecutionUtc)
    {
        var task = new QueuedTask
        {
            Id               = Guid.NewGuid(),
            Type             = "TestType",
            Request          = "{}",
            Handler          = "TestHandler",
            Status           = status,
            CreatedAtUtc     = DateTimeOffset.UtcNow,
            LastExecutionUtc = lastExecutionUtc
        };
        await _storage.Persist(task);
        return task;
    }

    [Theory]
    [InlineData(QueuedTaskStatus.Completed)]
    [InlineData(QueuedTaskStatus.Failed)]
    [InlineData(QueuedTaskStatus.ServiceStopped)]
    public async Task Should_set_last_execution_utc_for_terminal_statuses(QueuedTaskStatus status)
    {
        var task = await PersistTask(QueuedTaskStatus.InProgress, lastExecutionUtc: null);

        await _storage.SetStatus(task.Id, status, null, AuditLevel.None);

        var stored = (await _storage.Get(t => t.Id == task.Id)).Single();
        stored.LastExecutionUtc.ShouldNotBeNull("terminal transitions must set LastExecutionUtc");
    }

    [Theory]
    [InlineData(QueuedTaskStatus.WaitingQueue)]
    [InlineData(QueuedTaskStatus.Queued)]
    [InlineData(QueuedTaskStatus.InProgress)]
    [InlineData(QueuedTaskStatus.Cancelled)]
    [InlineData(QueuedTaskStatus.Pending)]
    public async Task Should_not_set_last_execution_utc_for_intermediate_statuses(QueuedTaskStatus status)
    {
        // WaitingQueue is the important case: a full-queue revert must not stamp a fake
        // execution time on a task that never ran
        var task = await PersistTask(QueuedTaskStatus.WaitingQueue, lastExecutionUtc: null);

        await _storage.SetStatus(task.Id, status, null, AuditLevel.None);

        var stored = (await _storage.Get(t => t.Id == task.Id)).Single();
        stored.LastExecutionUtc.ShouldBeNull("intermediate transitions must not set LastExecutionUtc");
    }

    [Fact]
    public async Task Should_preserve_last_execution_utc_on_intermediate_transitions()
    {
        // Re-queueing a recurring task between runs must keep the timestamp of its last real run
        var lastRun = DateTimeOffset.UtcNow.AddMinutes(-10);
        var task    = await PersistTask(QueuedTaskStatus.Completed, lastExecutionUtc: lastRun);

        await _storage.SetStatus(task.Id, QueuedTaskStatus.Queued, null, AuditLevel.None);

        var stored = (await _storage.Get(t => t.Id == task.Id)).Single();
        stored.LastExecutionUtc.ShouldBe(lastRun, "intermediate transitions must PRESERVE the previous value");
    }
}
