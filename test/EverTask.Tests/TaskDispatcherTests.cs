using EverTask.Logger;

namespace EverTask.Tests;

public class TaskDispatcherTests
{
    private readonly Mock<IServiceProvider> _serviceProviderMock;

    private readonly TaskDispatcher _taskDispatcher;

    public TaskDispatcherTests()
    {
        _serviceProviderMock = new Mock<IServiceProvider>();

        var workerQueueMock          = new Mock<IWorkerQueue>();
        var serviceConfigurationMock = new Mock<EverTaskServiceConfiguration>();
        var loggerMock               = new Mock<IEverTaskLogger<TaskDispatcher>>();

        _taskDispatcher = new TaskDispatcher(
            _serviceProviderMock.Object,
            workerQueueMock.Object,
            serviceConfigurationMock.Object,
            loggerMock.Object);
    }

    [Fact]
    public async Task Should_throw_ArgumentNullException_when_task_IsNull()
    {
        IEverTask task = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => _taskDispatcher.Dispatch(task));
    }

    [Fact]
    public async Task Should_throw_ArgumentNullException_when_task_isNot_IEverTask()
    {
        object task = new object();
        await Assert.ThrowsAsync<ArgumentException>(() => _taskDispatcher.Dispatch(task));
    }
}
