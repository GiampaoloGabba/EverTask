using EverTask.Handler;
using EverTask.Storage;
using Newtonsoft.Json;

namespace EverTask.Tests;

public class TaskHanlderExecutorTests
{
    private readonly IServiceProvider _provider;

    public TaskHanlderExecutorTests()
    {
        var serviceProviderMock = new Mock<IServiceProvider>();

        serviceProviderMock.Setup(s => s.GetService(typeof(IEverTaskHandler<TestTaskRequest>)))
                           .Returns(new TestTaskHanlder());

        serviceProviderMock.Setup(s => s.GetService(typeof(IEverTaskHandler<TestTaskRequestNoSerializable>)))
                           .Returns(new TestTaskHandlertNoSerializable());

        _provider = serviceProviderMock.Object;
    }

    [Fact]
    public void Should_return_Executor()
    {
        var guid               = Guid.NewGuid();
        var task               = new TestTaskRequest("test");
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = taskHandlerWrapper.Handle(task, null, _provider, guid);

        executor.PersistenceId.ShouldBe(guid);
        executor.Task.ShouldBe(task);
        executor.Handler.ShouldBeOfType<TestTaskHanlder>();
    }

    [Fact]
    public void Should_throw_for_not_registered()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequestNoHandler>();
        Should.Throw<ArgumentNullException>(() => taskHandlerWrapper.Handle(null!, null, _provider));
    }

    [Fact]
    public void Should_throw_for_not_serializable()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequestNoSerializable>();
        var executor = taskHandlerWrapper.Handle(new TestTaskRequestNoSerializable(IPAddress.None), null, _provider);

        Should.Throw<JsonSerializationException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void Should_return_queued_task()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = taskHandlerWrapper.Handle(new TestTaskRequest("test"), null, _provider);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBeOfType<Guid>();
        queuedTask.Type.ShouldBe(executor.Task.GetType().AssemblyQualifiedName);
        queuedTask.Request.ShouldBe(JsonConvert.SerializeObject(executor.Task));
        queuedTask.Handler.ShouldBe(executor.Handler.GetType().AssemblyQualifiedName);
        queuedTask.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
    }

    [Fact]
    public void Should_persis_existing_Guid()
    {
        var guid               = Guid.NewGuid();
        var task               = new TestTaskRequest("test");
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = taskHandlerWrapper.Handle(task, null, _provider, guid);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBe(guid);
    }

    [Fact]
    public void Should_throw_for_null_Request()
    {
        var executor = new TaskHandlerExecutor(null!, new TestTaskHanlder(), null,null!, null, null, null, Guid.NewGuid());
        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void Should_throw_for_null_Handler_Handle()
    {
        var executor = new TaskHandlerExecutor(new TestTaskRequest("Test"), null!, null, null!, null, null, null, Guid.NewGuid());
        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void ToQueuedTask_Should_correctly_map_Properties()
    {
        var task          = new TestTaskRequest("test");
        var handler       = new object();
        var executionTime = DateTimeOffset.UtcNow;
        var persistenceId = Guid.NewGuid();

        var executor = new TaskHandlerExecutor(
            Task: task,
            Handler: handler,
            ExecutionTime: executionTime,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: persistenceId);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBe(persistenceId);
        queuedTask.Request.ShouldBe(JsonConvert.SerializeObject(task));
        queuedTask.Type.ShouldBe(task.GetType().AssemblyQualifiedName);
        queuedTask.Handler.ShouldBe(handler.GetType().AssemblyQualifiedName);
        queuedTask.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        queuedTask.ScheduledExecutionUtc.ShouldBe(executionTime);
        queuedTask.CreatedAtUtc.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void ToQueuedTask_Should_throw_ArgumentNullException_when_Task_is_null()
    {
        var executor = new TaskHandlerExecutor(
            Task: null!,
            Handler: new object(),
            ExecutionTime: DateTimeOffset.UtcNow,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: Guid.NewGuid());

        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void ToQueuedTask_Should_throw_ArgumentNullException_when_Handler_is_null()
    {
        var executor = new TaskHandlerExecutor(
            Task: new TestTaskRequest("test"),
            Handler: null!,
            ExecutionTime: null!,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: Guid.NewGuid());

        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }
}
