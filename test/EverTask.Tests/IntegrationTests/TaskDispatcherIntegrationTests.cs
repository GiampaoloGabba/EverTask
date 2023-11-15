﻿using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class TaskDispatcherIntegrationTests
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly IWorkerQueue _workerQueue;


    public TaskDispatcherIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                       .SetChannelOptions(1));

        services.AddSingleton<ITaskStorage, TestTaskStorage>();

        var provider = services.BuildServiceProvider();
        _dispatcher  = provider.GetRequiredService<ITaskDispatcher>();
        _workerQueue = provider.GetRequiredService<IWorkerQueue>();
    }

    [Fact]
    public async Task Should_put_item_into_Queue()
    {
        var task = new TestTaskRequest("Test");
        await _dispatcher.Dispatch(task);

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task);
    }

    [Fact]
    public async Task Should_wait_for_Queue_when_is_full()
    {
        var task  = new TestTaskRequest("Test");
        var task2 = new TestTaskRequest2();
        var task3 = new TestTaskRequest3();

        await _dispatcher.Dispatch(task);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        Task.Run(async () =>
        {
            await _dispatcher.Dispatch(task2);
        });

        Task.Run(async () =>
        {
            await _dispatcher.Dispatch(task3);
        });

        Task.Delay(500);

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();

        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();

        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    [Fact]
    public async Task Should_throw_for_not_registered()
    {
        var task = new TestTaskRequestNoHandler();
        await Assert.ThrowsAsync<ArgumentNullException>(() => _dispatcher.Dispatch(task));
    }

    [Fact]
    public async Task Should_throw_when_ThrowIfUnableToPersist_is_true_and_no_storage_is_registered()
    {
        await Assert.ThrowsAsync<Exception>(() => _dispatcher.Dispatch(new ThrowStorageError()));
    }

    [Fact]
    public async Task Should_not_throw_when_ThrowIfUnableToPersist_is_true_and_no_storage_is_registered()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                       .SetChannelOptions(1));
        var provider = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ITaskDispatcher>();

        await dispatcher.Dispatch(new ThrowStorageError());
    }

    [Fact]
    public async Task Should_persist_Task()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                       .SetChannelOptions(1));

        services.AddSingleton<ITaskStorage, MemoryTaskStorage>();

        var provider   = services.BuildServiceProvider();
        var dispatcher = provider.GetRequiredService<ITaskDispatcher>();
        var storage    = provider.GetRequiredService<ITaskStorage>();

        await dispatcher.Dispatch(new TestTaskRequest("Test"));

        var pending = await storage.RetrievePendingTasks();
        pending.Length.ShouldBe(1);
        pending[0].Request.ShouldBe("{\"Name\":\"Test\"}");
        pending[0].Status.ShouldBe(QueuedTaskStatus.Queued);
        pending[0].Type.ShouldBe(typeof(TestTaskRequest).AssemblyQualifiedName);
        pending[0].Handler.ShouldBe(typeof(TestTaskHanlder).AssemblyQualifiedName);
        pending[0].Id.ShouldBeOfType<Guid>();
    }
}