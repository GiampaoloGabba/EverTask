using EverTask.Storage;

namespace EverTask.Tests;

public class WrokerServiceIntegrationTests
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly IHostedService _workerService;
    private readonly ITaskStorage _storage;

    public WrokerServiceIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(cfg => cfg.RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                                       .SetChannelOptions(1)).AddMemoryStorage();
        services.AddSingleton<ITaskStorage, MemoryTaskStorage>();

        var provider = services.BuildServiceProvider();

        _workerService = provider.GetRequiredService<IHostedService>();
        _dispatcher    = provider.GetRequiredService<ITaskDispatcher>();
        _storage       = provider.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public async Task Should_execute_task()
    {
        await _workerService.StartAsync(CancellationToken.None);

        var task = new TestTaskRequest("Test");
        await _dispatcher.Dispatch(task);

        await Task.Delay(500);

        (await _storage.RetrievePendingTasks()).Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        await _workerService.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Should_execute_pending_task()
    {
        var task = new TestTaskRequest("Test");
        await _dispatcher.Dispatch(task);

        (await _storage.RetrievePendingTasks()).Length.ShouldBe(1);

        await _workerService.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await _workerService.StopAsync(CancellationToken.None);

        await _workerService.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        (await _storage.RetrievePendingTasks()).Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        //await _workerService.StopAsync(CancellationToken.None);
    }

    
}
