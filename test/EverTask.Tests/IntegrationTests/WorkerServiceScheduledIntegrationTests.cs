using EverTask.Monitoring;
using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class WorkerServiceScheduledIntegrationTests
{
    private ITaskDispatcher _dispatcher;
    private ITaskStorage _storage;
    private IHost _host;
    private IWorkerQueue _workerQueue;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly IEverTaskWorkerExecutor _workerExecutor;
    private readonly ICancellationSourceProvider _cancSourceProvider;

    public WorkerServiceScheduledIntegrationTests()
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
                }).Build();

        _dispatcher         = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage            = _host.Services.GetRequiredService<ITaskStorage>();
        _workerQueue        = _host.Services.GetRequiredService<IWorkerQueue>();
        _workerBlacklist    = _host.Services.GetRequiredService<IWorkerBlacklist>();
        _workerExecutor     = _host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        _cancSourceProvider = _host.Services.GetRequiredService<ICancellationSourceProvider>();
    }

    [Fact]
    public async Task Should_execute_delayed_task()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        await _dispatcher.Dispatch(task, TimeSpan.FromSeconds(1.2));

        await Task.Delay(1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        await Task.Delay(800);
        pt = await _storage.RetrievePending();
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
    public async Task Should_execute_specific_time_task()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;
        var specificDate = DateTimeOffset.Now.AddSeconds(1.2);
        await _dispatcher.Dispatch(task, specificDate);

        await Task.Delay(1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        await Task.Delay(800);
        pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskDelayed1.Counter.ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_recurring_cron()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed2();
        TestTaskDelayed2.Counter = 0;
        await _dispatcher.Dispatch(task, builder => builder.RunDelayed(TimeSpan.FromSeconds(0.5)).Then().UseCron("* * * * * */2").MaxRuns(3));

        await Task.Delay(300);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        await Task.Delay(600);
        pt = await _storage.GetAll();
        pt[0].CurrentRunCount.ShouldBe(1);

        await Task.Delay(6000);
        pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);
        pt[0].StatusAudits.Count.ShouldBe(9);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskDelayed2.Counter.ShouldBe(3);
    }

}
