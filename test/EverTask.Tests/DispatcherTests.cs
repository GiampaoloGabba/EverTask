using EverTask.Dispatcher;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;

namespace EverTask.Tests;

public class DispatcherTests
{
    private readonly Dispatcher.Dispatcher _dispatcher;

    private readonly Mock<IWorkerQueue> _workerQueueMock;
    private readonly Mock<IWorkerBlacklist> _blackListMock;
    private readonly Mock<IScheduler> _delayedQueue;
    private readonly Mock<ICancellationSourceProvider> _cancSourceProviderMock;

    public DispatcherTests()
    {
        _workerQueueMock        = new Mock<IWorkerQueue>();
        _blackListMock          = new Mock<IWorkerBlacklist>();
        _delayedQueue           = new Mock<IScheduler>();
        _cancSourceProviderMock = new Mock<ICancellationSourceProvider>();

        var serviceProviderMock      = new Mock<IServiceProvider>();
        var serviceConfigurationMock = new Mock<EverTaskServiceConfiguration>();
        var loggerMock               = new Mock<IEverTaskLogger<Dispatcher.Dispatcher>>();

        serviceProviderMock.Setup(s => s.GetService(typeof(IEverTaskHandler<TestTaskRequest2>)))
                           .Returns(new TestTaskHanlder2());

        serviceProviderMock.Setup(s => s.GetService(typeof(IEverTaskHandler<TestTaskRequest3>)))
                           .Returns(new TestTaskHanlder3());


        serviceProviderMock.Setup(s => s.GetService(typeof(IWorkerBlacklist)))
                           .Returns(new WorkerBlacklist());

        _dispatcher = new Dispatcher.Dispatcher(
            serviceProviderMock.Object,
            _workerQueueMock.Object,
            _delayedQueue.Object,
            serviceConfigurationMock.Object,
            loggerMock.Object,
            _blackListMock.Object,
            _cancSourceProviderMock.Object);
    }

    [Fact]
    public async Task Should_throw_ArgumentNullException_when_task_IsNull()
    {
        IEverTask task = null!;
        await Assert.ThrowsAsync<ArgumentNullException>(() => _dispatcher.Dispatch(task));
    }

    [Fact]
    public async Task Should_throw_ArgumentNullException_when_handler_IsNull()
    {
        var task = new TestTaskRequest("Test request");
        await Assert.ThrowsAsync<ArgumentNullException>(() => _dispatcher.Dispatch(task));
    }

    [Fact]
    public async Task Should_assign_a_task_id()
    {
        var taskId = await _dispatcher.Dispatch(new TestTaskRequest2());
        taskId.ShouldBeOfType<Guid>();
    }

    [Fact]
    public async Task Should_Queue_Task()
    {
        var task = new TestTaskRequest2();

        var taskId = await _dispatcher.Dispatch(task);
        _workerQueueMock.Verify(q => q.Queue(It.Is<TaskHandlerExecutor>(executor => executor.PersistenceId == taskId)), Times.Once);
    }

    [Fact]
    public async Task Should_cancel_a_task()
    {
        var taskId = await _dispatcher.Dispatch(new TestTaskRequest2());
        taskId.ShouldBeOfType<Guid>();

        await _dispatcher.Cancel(taskId);

        _blackListMock.Verify(q => q.Add(taskId), Times.Once);
        _cancSourceProviderMock.Verify(q => q.CancelTokenForTask(taskId), Times.Once);
    }

    [Fact]
    public async Task Should_put_delayed_tasks_in_scheduler()
    {
        var taskId = await _dispatcher.Dispatch(new TestTaskRequest2(), TimeSpan.FromSeconds(5));
        taskId.ShouldBeOfType<Guid>();

        var taskId2 = await _dispatcher.Dispatch(new TestTaskRequest3(), TimeSpan.FromSeconds(5));
        taskId2.ShouldBeOfType<Guid>();

        _delayedQueue.Verify(q => q.Schedule(It.Is<TaskHandlerExecutor>(executor => executor.PersistenceId == taskId)), Times.Once);
        _delayedQueue.Verify(q => q.Schedule(It.Is<TaskHandlerExecutor>(executor => executor.PersistenceId == taskId2)), Times.Once);
    }

    [Fact]
    public async Task Should_put_recurring_cron_task_in_scheduler()
    {
        var taskId2 = await _dispatcher.Dispatch(new TestTaskRequest3(), recurring=>recurring.Schedule().UseCron("*/5 * * * *"));
        taskId2.ShouldBeOfType<Guid>();

        _delayedQueue.Verify(q => q.Schedule(It.Is<TaskHandlerExecutor>(executor => executor.PersistenceId == taskId2)), Times.Once);
    }
}
