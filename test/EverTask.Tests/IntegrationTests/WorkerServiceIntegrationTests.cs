using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class WorkerServiceIntegrationTests
{
    private ITaskDispatcher _dispatcher;
    private ITaskStorage _storage;
    private IHost _host;
    private IWorkerQueue _workerQueue;

    //TODO: Delayed dispatch tests

    public WorkerServiceIntegrationTests()
    {
        _host = new HostBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddLogging();
                    services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                                   .SetChannelOptions(3)
                                                   .SetMaxDegreeOfParallelism(3))
                            .AddMemoryStorage();
                    services.AddSingleton<ITaskStorage, MemoryTaskStorage>();
                })
                .Build();

        _dispatcher  = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage     = _host.Services.GetRequiredService<ITaskStorage>();
        _workerQueue = _host.Services.GetRequiredService<IWorkerQueue>();
    }

    [Fact]
    public async Task Should_execute_task()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        await _dispatcher.Dispatch(task);

        await Task.Delay(1100);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskConcurrent1.Counter.ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_pending_and_concurrent_task()
    {
        var task1 = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task1);

        var task2 = new TestTaskConcurrent2();
        await _dispatcher.Dispatch(task2);

        TestTaskConcurrent1.Counter = 0;
        TestTaskConcurrent2.Counter = 0;

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task1);
        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task2);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(2);

        await _host.StartAsync();

        await Task.Delay(1100, CancellationToken.None);

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(2);
        tasks[0].Status = QueuedTaskStatus.Completed;

        TestTaskConcurrent1.Counter.ShouldBe(1);
        TestTaskConcurrent2.Counter.ShouldBe(1);

        var parallelExecution = TestTaskConcurrent1.StartTime < TestTaskConcurrent2.EndTime &&
                                TestTaskConcurrent2.StartTime < TestTaskConcurrent1.EndTime;

        parallelExecution.ShouldBeTrue();


        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_Tasks_sequentially()
    {
        _host = new HostBuilder()
                .ConfigureServices((hostContext, services) =>
                {
                    services.AddLogging();
                    services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                                   .SetChannelOptions(3)
                                                   .SetMaxDegreeOfParallelism(1))
                            .AddMemoryStorage();
                    services.AddSingleton<ITaskStorage, MemoryTaskStorage>();
                })
                .Build();

        _dispatcher  = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage     = _host.Services.GetRequiredService<ITaskStorage>();
        _workerQueue = _host.Services.GetRequiredService<IWorkerQueue>();

        await _host.StartAsync();

        var task1 = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task1);

        await Task.Delay(1100, CancellationToken.None);

        TestTaskConcurrent1.Counter.ShouldBe(1);

        var task2 = new TestTaskConcurrent2();
        await _dispatcher.Dispatch(task2);

        await Task.Delay(1100, CancellationToken.None);

        TestTaskConcurrent2.Counter.ShouldBe(1);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
