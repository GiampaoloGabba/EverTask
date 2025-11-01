using EverTask.Scheduler;
using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class TaskDispatcherIntegrationTests : IAsyncDisposable
{
    private IHost? _host;

    [Fact]
    public async Task Should_put_item_into_Queue()
    {
        // Create isolated host with TestTaskStorage (no-op storage)
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var workerQueue = _host.Services.GetRequiredService<IWorkerQueue>();

        var task = new TestTaskRequest("Test");
        await dispatcher.Dispatch(task);

        var dequeued = await workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task);
    }

    [Fact]
    public async Task Should_wait_for_Queue_when_is_full()
    {
        // Create isolated host with TestTaskStorage
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var workerQueue = _host.Services.GetRequiredService<IWorkerQueue>();

        var task  = new TestTaskRequest("Test");
        var task2 = new TestTaskRequest2();
        var task3 = new TestTaskRequest3();

        await dispatcher.Dispatch(task);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
        Task.Run(async () =>
        {
            await dispatcher.Dispatch(task2);
        });

        Task.Run(async () =>
        {
            await dispatcher.Dispatch(task3);
        });

        // Give time for background tasks to start dispatching
        await Task.Delay(500);

        var dequeued = await workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();

        dequeued = await workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();

        dequeued = await workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBeAssignableTo<IEverTask>();
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
    }

    [Fact]
    public async Task Should_throw_for_not_registered()
    {
        // Create isolated host with TestTaskStorage
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();

        var task = new TestTaskRequestNoHandler();
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.Dispatch(task));
    }

    [Fact]
    public async Task Should_throw_when_ThrowIfUnableToPersist_is_true_and_no_storage_is_registered()
    {
        // Create isolated host with TestTaskStorage that throws on ThrowStorageError
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();

        await Assert.ThrowsAsync<Exception>(() => dispatcher.Dispatch(new ThrowStorageError()));
    }

    [Fact]
    public async Task Should_not_throw_when_ThrowIfUnableToPersist_is_true_and_no_storage_is_registered()
    {
        // Create isolated host WITHOUT storage registration
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                // NO storage registered at all
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();

        await dispatcher.Dispatch(new ThrowStorageError());
    }

    [Fact]
    public async Task Should_persist_Task()
    {
        // Create isolated host with MemoryTaskStorage for persistence test
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1))
                    .AddMemoryStorage();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var storage = _host.Services.GetRequiredService<ITaskStorage>();

        await dispatcher.Dispatch(new TestTaskRequest("Test"), builder => builder.Schedule().UseCron("5 * * * *"));

        var pending = await storage.GetAll();
        pending.Length.ShouldBe(1);
        pending[0].Request.ShouldBe("{\"Name\":\"Test\"}");
        pending[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        pending[0].Type.ShouldBe(typeof(TestTaskRequest).AssemblyQualifiedName);
        pending[0].Handler.ShouldBe(typeof(TestTaskHanlder).AssemblyQualifiedName);
        pending[0].Id.ShouldBeOfType<Guid>();
        pending[0].IsRecurring.ShouldBe(true);
        pending[0].RecurringInfo.ShouldBe("Use Cron expression: 5 * * * *");
        pending[0].RecurringTask.ShouldBe("{\"RunNow\":false,\"InitialDelay\":null,\"SpecificRunTime\":null,\"CronInterval\":{\"CronExpression\":\"5 * * * *\"},\"SecondInterval\":null,\"MinuteInterval\":null,\"HourInterval\":null,\"DayInterval\":null,\"WeekInterval\":null,\"MonthInterval\":null,\"MaxRuns\":null,\"RunUntil\":null}");
    }

    [Fact]
    public async Task Should_put_Timespan_Item_into_Timed_Scheduler()
    {
        // Create isolated host with TestTaskStorage
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var scheduler = _host.Services.GetRequiredService<IScheduler>();

        var task       = new TestTaskRequest("Test");
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(1);
        await dispatcher.Dispatch(task, TimeSpan.FromMinutes(1));

        var dequeued = ((PeriodicTimerScheduler)scheduler).GetQueue().Peek();
        dequeued.Task.ShouldBe(task);

        Assert.NotNull(dequeued.ExecutionTime);

        dequeued.ExecutionTime!.Value.ShouldBeGreaterThan(futureDate);
    }

    [Fact]
    public async Task Should_put_DateOffset_Item_into_Timed_Scheduler()
    {
        // Create isolated host with TestTaskStorage
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var scheduler = _host.Services.GetRequiredService<IScheduler>();

        var task       = new TestTaskRequest("Test");
        var futureDate = DateTimeOffset.UtcNow.AddMinutes(1);
        await dispatcher.Dispatch(task, futureDate);

        var dequeued = ((PeriodicTimerScheduler)scheduler).GetQueue().Peek();
        dequeued.Task.ShouldBe(task);

        Assert.NotNull(dequeued.ExecutionTime);

        dequeued.ExecutionTime!.Value.ShouldBe(futureDate);
    }

    [Fact]
    public async Task Should_put_Recurring_Item_into_Timed_Scheduler()
    {
        // Create isolated host with TestTaskStorage
        _host = new HostBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(1));
                services.AddSingleton<ITaskStorage, TestTaskStorage>();
            })
            .Build();

        var dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        var scheduler = _host.Services.GetRequiredService<IScheduler>();

        var task       = new TestTaskRequest("Test");
        await dispatcher.Dispatch(task, recurring=>recurring.Schedule().UseCron("5 * * * *"));

        var dequeued = ((PeriodicTimerScheduler)scheduler).GetQueue().Peek();
        dequeued.Task.ShouldBe(task);

        Assert.NotNull(dequeued.ExecutionTime);

        dequeued.ExecutionTime!.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            _host.Dispose();
            _host = null;
        }

        await Task.CompletedTask;
    }
}
