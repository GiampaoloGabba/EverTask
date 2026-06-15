using EverTask.Tests.TestHelpers;
﻿using EverTask.Handler;
using EverTask.Monitoring;
using EverTask.Scheduler.Recurring;
using EverTask.Storage;
using Newtonsoft.Json;

namespace EverTask.Tests;

public class HanlderExecutorTests
{
    private readonly IServiceProvider _provider;

    public HanlderExecutorTests()
    {
        // Real DI provider (not a Mock): the eager handler-resolution path now resolves handlers inside
        // an EverTask-owned scope (L27), which requires IServiceScopeFactory and a scope that can resolve
        // the handler — exactly what a real provider gives, and what production always uses.
        var services = new ServiceCollection();
        services.AddTransient<IEverTaskHandler<TestTaskRequest>, TestTaskHanlder>();
        services.AddTransient<TestTaskHanlder>();
        services.AddTransient<IEverTaskHandler<TestTaskRequestNoSerializable>, TestTaskHandlertNoSerializable>();
        services.AddTransient<TestTaskHandlertNoSerializable>();
        services.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));

        _provider = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Should_return_Executor()
    {
        var guid               = TestGuidGenerator.New();
        var task               = new TestTaskRequest("test");
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = await taskHandlerWrapper.Handle(task, null, null, _provider, AuditLevel.Full, guid);

        executor.PersistenceId.ShouldBe(guid);
        executor.Task.ShouldBe(task);
        executor.Handler.ShouldBeOfType<TestTaskHanlder>();
    }

    [Fact]
    public async Task Executor_Should_convert_offset_to_utc()
    {
        var task           = new TestTaskRequest("test");
        var inputOffset    = new DateTimeOffset(2023, 1, 1, 12, 0, 0, TimeSpan.FromHours(10));
        var expectedOffset = new DateTimeOffset(inputOffset.UtcDateTime);

        var handlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor       = await handlerWrapper.Handle(task, inputOffset, null, _provider, AuditLevel.Full, TestGuidGenerator.New());

        executor.ExecutionTime.ShouldBe(expectedOffset);
    }

    [Fact]
    public async Task Should_throw_for_not_registered()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequestNoHandler>();
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await taskHandlerWrapper.Handle(null!, null, null, _provider, AuditLevel.Full));
    }

    [Fact]
    public async Task Should_throw_for_not_serializable()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequestNoSerializable>();
        var executor =
            await taskHandlerWrapper.Handle(new TestTaskRequestNoSerializable(IPAddress.None), null, null, _provider, AuditLevel.Full);

        Should.Throw<JsonSerializationException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public async Task Should_return_queued_task()
    {
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = await taskHandlerWrapper.Handle(new TestTaskRequest("test"), null, null, _provider, AuditLevel.Full);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBeOfType<Guid>();
        queuedTask.Type.ShouldBe(executor.Task.GetType().AssemblyQualifiedName);
        queuedTask.Request.ShouldBe(JsonConvert.SerializeObject(executor.Task));
        queuedTask.Handler.ShouldBe(executor.Handler!.GetType().AssemblyQualifiedName);  // Handler is guaranteed to be present from TaskHandlerWrapper
        queuedTask.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
    }

    [Fact]
    public async Task Should_persis_existing_Guid()
    {
        var guid               = TestGuidGenerator.New();
        var task               = new TestTaskRequest("test");
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor           = await taskHandlerWrapper.Handle(task, null, null, _provider, AuditLevel.Full, guid);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBe(guid);
    }

    [Fact]
    public async Task Should_return_lazy_executor_without_handler_instance_when_lazy_requested()
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddTransient<IEverTaskHandler<TestTaskRequest>, TestTaskHanlder>();
        serviceCollection.AddSingleton<IGuidGenerator>(new DefaultGuidGenerator(UUIDNext.Database.Other));
        await using var provider = serviceCollection.BuildServiceProvider();

        var guid               = TestGuidGenerator.New();
        var task               = new TestTaskRequest("test");
        var taskHandlerWrapper = new TaskHandlerWrapperImp<TestTaskRequest>();
        var executor = await taskHandlerWrapper.Handle(task, null, null, provider, AuditLevel.Full, guid,
            useLazyExecutor: true);

        executor.IsLazy.ShouldBeTrue();
        executor.Handler.ShouldBeNull();
        executor.HandlerCallback.ShouldBeNull();
        executor.HandlerTypeName.ShouldBe(typeof(TestTaskHanlder).AssemblyQualifiedName);
        executor.PersistenceId.ShouldBe(guid);

        // The lazy executor must still serialize correctly (HandlerTypeName path)
        var queuedTask = executor.ToQueuedTask();
        queuedTask.Handler.ShouldBe(typeof(TestTaskHanlder).AssemblyQualifiedName);
    }

    [Fact]
    public async Task Should_resolve_lazy_handler_through_interface_binding_when_concrete_type_not_registered()
    {
        // Manual registration scenario: the app binds ONLY IEverTaskHandler<T> → implementation
        // (no assembly scanning, so the concrete type is not self-registered). Immediate
        // dispatches are lazy by default since v3.7: GetOrResolveHandler must fall back to the
        // interface binding instead of failing at execution time.
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddTransient<IEverTaskHandler<TestTaskRequest>, TestTaskHanlder>();
        await using var provider = serviceCollection.BuildServiceProvider();

        var executor = new TaskHandlerExecutor(
            new TestTaskRequest("test"), null, typeof(TestTaskHanlder).AssemblyQualifiedName,
            null, null, null, null, null, null,
            TestGuidGenerator.New(), null, null, AuditLevel.Full);

        var handler = executor.GetOrResolveHandler(provider);

        handler.ShouldBeOfType<TestTaskHanlder>();
    }

    [Fact]
    public void Should_throw_for_null_Request()
    {
        var executor =
            new TaskHandlerExecutor(null!, new TestTaskHanlder(), null, null, null, null!, null, null, null, TestGuidGenerator.New(), null, null, AuditLevel.Full);
        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void Should_throw_for_null_Handler_Handle()
    {
        var executor = new TaskHandlerExecutor(new TestTaskRequest("Test"), null!, null, null, null, null!, null, null, null,
            TestGuidGenerator.New(), null, null, AuditLevel.Full);
        Should.Throw<InvalidOperationException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void ToQueuedTask_Should_correctly_map_Properties()
    {
        var task          = new TestTaskRequest("test");
        var handler       = new object();
        var executionTime = DateTimeOffset.UtcNow;
        var persistenceId = TestGuidGenerator.New();
        var runUntil      = DateTimeOffset.UtcNow.AddMinutes(2);
        var recurringTask = new RecurringTask{ RunNow = true, RunUntil = runUntil, MaxRuns = 2 };

        var executor = new TaskHandlerExecutor(
            Task: task,
            Handler: handler,
            HandlerTypeName: null,  // null for eager mode
            ExecutionTime: executionTime,
            RecurringTask: recurringTask,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: persistenceId,
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.Full);

        var queuedTask = executor.ToQueuedTask();

        queuedTask.Id.ShouldBe(persistenceId);
        queuedTask.Request.ShouldBe(JsonConvert.SerializeObject(task));
        queuedTask.Type.ShouldBe(task.GetType().AssemblyQualifiedName);
        queuedTask.Handler.ShouldBe(handler.GetType().AssemblyQualifiedName);
        queuedTask.Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        queuedTask.ScheduledExecutionUtc.ShouldBe(executionTime);
        queuedTask.CreatedAtUtc.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
        queuedTask.RecurringTask.ShouldBeEquivalentTo(JsonConvert.SerializeObject(recurringTask));
        queuedTask.IsRecurring.ShouldBe(true);
        queuedTask.RecurringInfo.ShouldBe(recurringTask.ToString());
        queuedTask.RunUntil.ShouldBe(runUntil);
        queuedTask.MaxRuns.ShouldBe(2);
    }

    [Fact]
    public void ToQueuedTask_Should_throw_ArgumentNullException_when_Task_is_null()
    {
        var executor = new TaskHandlerExecutor(
            Task: null!,
            Handler: new object(),
            HandlerTypeName: null,  // null for eager mode
            ExecutionTime: DateTimeOffset.UtcNow,
            RecurringTask: null,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: TestGuidGenerator.New(),
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.Full);

        Should.Throw<ArgumentNullException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void ToQueuedTask_Should_throw_InvalidOperationException_when_Handler_is_null()
    {
        var executor = new TaskHandlerExecutor(
            Task: new TestTaskRequest("test"),
            Handler: null!,
            HandlerTypeName: null,
            ExecutionTime: null!,
            RecurringTask: null,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: TestGuidGenerator.New(),
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.Full);

        Should.Throw<InvalidOperationException>(() => executor.ToQueuedTask());
    }

    [Fact]
    public void EverTaskEventData_FromExecutor_correctly_map_Properties()
    {
        var task          = new TestTaskRequest("test");
        var handler       = new object();
        var executionTime = DateTimeOffset.UtcNow;
        var persistenceId = TestGuidGenerator.New();

        var executor = new TaskHandlerExecutor(
            Task: task,
            Handler: handler,
            HandlerTypeName: null,  // null for eager mode
            ExecutionTime: executionTime,
            RecurringTask: null,
            HandlerCallback: (everTask, token) => Task.CompletedTask,
            HandlerErrorCallback: null,
            HandlerStartedCallback: null,
            HandlerCompletedCallback: null,
            PersistenceId: persistenceId,
            QueueName: null,
            TaskKey: null,
            AuditLevel: AuditLevel.Full);

        var eventData = EverTaskEventData.FromExecutor(executor, SeverityLevel.Information, "test", null);

        eventData.TaskId.ShouldBe(persistenceId);
        eventData.TaskParameters.ShouldBe(JsonConvert.SerializeObject(task));
        eventData.TaskType.ShouldBe(task.GetType().ToString());
        eventData.TaskHandlerType.ShouldBe(handler.GetType().ToString());
        eventData.Severity.ShouldBe(SeverityLevel.Information.ToString());
    }
}
