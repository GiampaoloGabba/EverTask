using EverTask.Storage;

namespace EverTask.Tests;

public class WrokerServiceIntegrationTests
{
    private readonly ITaskDispatcher _dispatcher;
    private readonly ITaskStorage _storage;
    private readonly IHost _host;

    public WrokerServiceIntegrationTests()
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

        _dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage    = _host.Services.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public async Task Should_execute_task()
    {
        await _host.StartAsync();

        var task = new TestTaskRequest("Test");
        await _dispatcher.Dispatch(task);

        await Task.Delay(500);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_pending_task()
    {
        var task = new TestTaskRequest("Test");
        await _dispatcher.Dispatch(task);

        await _host.StartAsync();
        await Task.Delay(500, CancellationToken.None);

        var pt = await _storage.RetrievePendingTasks();
        pt.Length.ShouldBe(1);

        await Task.Delay(500, CancellationToken.None);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status = QueuedTaskStatus.Completed;

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
