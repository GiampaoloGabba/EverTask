using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Storage;

namespace EverTask.Tests;

public class QueueTests
{
    private readonly MemoryTaskStorage _memoryStorage;
    private readonly WorkerQueue _workerQueue;
    private readonly TaskHandlerExecutor _executor;

    public QueueTests()
    {
        var loggerMock        = new Mock<IEverTaskLogger<WorkerQueue>>();
        var loggerMock2       = new Mock<IEverTaskLogger<MemoryTaskStorage>>();
        var mockBlacklist     = new Mock<IWorkerBlacklist>();

        // Create a QueueConfiguration instead of EverTaskServiceConfiguration
        var queueConfig = new QueueConfiguration
        {
            Name = "default",
            MaxDegreeOfParallelism = 5,
            ChannelOptions = new System.Threading.Channels.BoundedChannelOptions(100)
        };

        _memoryStorage = new MemoryTaskStorage(loggerMock2.Object);
        _workerQueue   = new WorkerQueue(queueConfig, loggerMock.Object, mockBlacklist.Object, _memoryStorage);
        _executor = new TaskHandlerExecutor(
            new TestTaskRequest2(),
            new TestTaskHanlder2(),
            null,  // HandlerTypeName - null for eager mode
            null,
            null,
            null!,
            null,
            null,
            null,
            Guid.NewGuid(),
            null,
            null);
    }

    [Fact]
    public async Task Should_queue_and_dequeue_task()
    {
        await _workerQueue.Queue(_executor);
        var task = await _workerQueue.Dequeue(CancellationToken.None);
        task.ShouldBe(_executor);
    }

    [Fact]
    public async Task Should_update_persisted_task_status()
    {
        await _memoryStorage.Persist(_executor.ToQueuedTask());

        await _workerQueue.Queue(_executor);
        var persist = await _memoryStorage.RetrievePending();
        persist.Length.ShouldBe(1);
        persist[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public async Task Should_update_Audit_correctly_when_updating_status()
    {
        await _memoryStorage.Persist(_executor.ToQueuedTask());

        await _workerQueue.Queue(_executor);
        var persist = await _memoryStorage.RetrievePending();
        persist.Length.ShouldBe(1);
        persist[0].Status.ShouldBe(QueuedTaskStatus.Queued);

        persist[0].StatusAudits.Count.ShouldBe(1);
        persist[0].StatusAudits.FirstOrDefault()?.QueuedTaskId.ShouldBe(persist[0].Id);
        persist[0].StatusAudits.FirstOrDefault()?.NewStatus.ShouldBe(QueuedTaskStatus.Queued);
    }
}
