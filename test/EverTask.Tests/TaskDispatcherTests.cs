using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler;

namespace EverTask.Tests;

public class TaskDispatcherTests
{
    private readonly TaskDispatcher _taskDispatcher;

    public TaskDispatcherTests()
    {
        var serviceProviderMock      = new Mock<IServiceProvider>();
        var workerQueueMock          = new Mock<IWorkerQueue>();
        var delayedQueue             = new Mock<IScheduler>();
        var serviceConfigurationMock = new Mock<EverTaskServiceConfiguration>();
        var loggerMock               = new Mock<IEverTaskLogger<TaskDispatcher>>();

        _taskDispatcher = new TaskDispatcher(
            serviceProviderMock.Object,
            workerQueueMock.Object,
            delayedQueue.Object,
            serviceConfigurationMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task Should_throw_ArgumentNullException_when_task_IsNull()
    {
        IEverTask task = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => _taskDispatcher.Dispatch(task));
    }
}
