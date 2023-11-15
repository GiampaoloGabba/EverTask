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
                                                   .SetChannelOptions(1)).AddMemoryStorage();
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
    public async Task Should_execute_pending_task()
    {
        if (File.Exists("test1.txt"))
            File.Delete("test1.txt");

        var task = new TestTaskRequest2();
        await _dispatcher.Dispatch(task);

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(1);

        await _host.StartAsync();
        await Task.Delay(500, CancellationToken.None);

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        File.Exists("test1.txt").ShouldBeTrue();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
