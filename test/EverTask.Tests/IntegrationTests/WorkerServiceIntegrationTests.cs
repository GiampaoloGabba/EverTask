using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class WorkerServiceIntegrationTests
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly ITaskStorage _storage;
    private readonly IHost _host;
    private readonly IWorkerQueue _workerQueue;

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
        if (File.Exists("test1.txt"))
            File.Delete("test1.txt");

        await _host.StartAsync();

        var task = new TestTaskRequest2();
        await _dispatcher.Dispatch(task);

        await Task.Delay(500);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        File.Exists("test1.txt").ShouldBeTrue();

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_pending_and_concurrent_task()
    {
        if (File.Exists("test1.txt"))
            File.Delete("test1.txt");

        if (File.Exists("test2.txt"))
            File.Delete("test2.txt");


        var task1 = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task1);

        var task2 = new TestTaskConcurrent2();
        await _dispatcher.Dispatch(task2);

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task1);
        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task2);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(2);

        await _host.StartAsync();
        await Task.Delay(500, CancellationToken.None);

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(2);
        tasks[0].Status = QueuedTaskStatus.Completed;

        File.Exists("test1.txt").ShouldBeTrue();
        File.Exists("test2.txt").ShouldBeTrue();

        var parallelExecution = TestTaskConcurrent1.StartTime < TestTaskConcurrent2.EndTime &&
                                TestTaskConcurrent2.StartTime < TestTaskConcurrent1.EndTime;

        parallelExecution.ShouldBeTrue();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
