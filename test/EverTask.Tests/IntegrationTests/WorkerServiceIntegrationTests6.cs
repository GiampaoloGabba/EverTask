﻿using EverTask.Monitoring;
using EverTask.Storage;

namespace EverTask.Tests.IntegrationTests;

public class WorkerServiceIntegrationTests
{
    private ITaskDispatcher _dispatcher;
    private ITaskStorage _storage;
    private IHost _host;
    private IWorkerQueue _workerQueue;
    private readonly IWorkerBlacklist _workerBlacklist;
    private readonly IEverTaskWorkerExecutor _workerExecutor;
    private readonly ICancellationSourceProvider _cancSourceProvider;

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
                }).Build();

        _dispatcher         = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage            = _host.Services.GetRequiredService<ITaskStorage>();
        _workerQueue        = _host.Services.GetRequiredService<IWorkerQueue>();
        _workerBlacklist    = _host.Services.GetRequiredService<IWorkerBlacklist>();
        _workerExecutor     = _host.Services.GetRequiredService<IEverTaskWorkerExecutor>();
        _cancSourceProvider = _host.Services.GetRequiredService<ICancellationSourceProvider>();
    }

    [Fact]
    public async Task Should_execute_task_and_clear_cancellation_source()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task);
        await Task.Delay(100);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        await Task.Delay(600);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        _cancSourceProvider.TryGet(taskId).ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskConcurrent1.Counter.ShouldBe(1);
    }

    [Fact]
    public async Task Should_execute_cpu_bound_task_and_clear_cancellation_source()
    {
        await _host.StartAsync();

        var task = new TestTaskCpubound();
        TestTaskCpubound.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task);
        await Task.Delay(100);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        await Task.Delay(600);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        _cancSourceProvider.TryGet(taskId).ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskCpubound.Counter.ShouldBe(1);
    }

    [Fact]
    public async Task Should_cancel_non_started_task_and_not_creating_cancellation_source()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task, TimeSpan.FromMilliseconds(300));

        await Task.Delay(100);

        await _dispatcher.Cancel(taskId);

        await Task.Delay(200);

        _cancSourceProvider.TryGet(taskId).ShouldBeNull();

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskConcurrent1.Counter.ShouldBe(0);
    }

    [Fact]
    public async Task Should_cancel_started_task_and_relative_cancellation_source()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task);

        await Task.Delay(100);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        await _dispatcher.Cancel(taskId);

        await Task.Delay(200);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        _cancSourceProvider.TryGet(taskId).ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskConcurrent1.Counter.ShouldBe(0);
    }

    [Fact]
    public async Task Should_cancel_task_when_service_stopped()
    {
        await _host.StartAsync();

        var monitorCalled = false;

        _workerExecutor.TaskEventOccurredAsync += data =>
        {
            data.Severity.ShouldBe(SeverityLevel.Warning.ToString());
            monitorCalled = true;
            return Task.CompletedTask;
        };

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        await _dispatcher.Dispatch(task);

        var cts = new CancellationTokenSource();

        await Task.Delay(200);

        cts.CancelAfter(50);
        await _host.StopAsync(cts.Token);

        await Task.Delay(300);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(1);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull();

        monitorCalled.ShouldBeTrue();

        TestTaskConcurrent1.Counter.ShouldBe(0);
    }


    [Fact]
    public async Task Should_execute_task_with_standard_retry_policy()
    {
        await _host.StartAsync();

        var task = new TestTaskWithRetryPolicy();
        TestTaskConcurrent1.Counter = 0;
        await _dispatcher.Dispatch(task);

        await Task.Delay(1600);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskWithRetryPolicy.Counter.ShouldBe(3);
    }

    [Fact]
    public async Task Should_execute_task_with_standard_custom_policy()
    {
        await _host.StartAsync();

        var task = new TestTaskWithCustomRetryPolicy();
        TestTaskWithCustomRetryPolicy.Counter = 0;
        await _dispatcher.Dispatch(task);

        await Task.Delay(700);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskWithCustomRetryPolicy.Counter.ShouldBe(5);
    }

    [Fact]
    public async Task Should_execute_task_with_max_run_until_max_run_reached()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;
        await _dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().MaxRuns(3));

        await Task.Delay(4000);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].MaxRuns = tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskDelayed1.Counter.ShouldBe(3);
    }

    [Fact]
    public async Task Should_execute_task_with_run_at_until_expires()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;
        await _dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().RunUntil(DateTimeOffset.Now.AddSeconds(4)));

        await Task.Delay(4000);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed).ShouldBe(3);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskDelayed1.Counter.ShouldBe(3);
    }


    [Fact]
    public async Task Should_not_execute_task_with_custom_timeout_excedeed()
    {
        await _host.StartAsync();

        var task = new TestTaskWithCustomTimeout();
        TestTaskWithCustomTimeout.Counter = 0;
        await _dispatcher.Dispatch(task);

        await Task.Delay(900);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull().ShouldContain("TimeoutException");

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);

        TestTaskWithCustomTimeout.Counter.ShouldBe(0);
    }

    [Fact]
    public async Task Should_throw_for_non_executable_tasks()
    {
        await _host.StartAsync();

        var task = new TestTaskRequestError();
        await _dispatcher.Dispatch(task);

        await Task.Delay(1600);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull().ShouldContain("AggregateException");

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    //todo: Reqrite this test
    /*[Fact]
    public async Task Should_execute_pending_and_concurrent_task()
    {
        TestTaskConcurrent1.Counter = 0;
        TestTaskConcurrent2.Counter = 0;

        var task1 = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task1);

        var task2 = new TestTaskConcurrent2();
        await _dispatcher.Dispatch(task2);

        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task1);
        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task2);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(2);

        await _host.StartAsync();

        await Task.Delay(400, CancellationToken.None);

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(2);

        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[1].Status.ShouldBe(QueuedTaskStatus.Completed);

        //todo: gestire meglio i test task.. usando sempre questa proprietà statica non ha senso e porta a errori
        TestTaskConcurrent1.Counter.ShouldBe(1);
        TestTaskConcurrent2.Counter.ShouldBe(1);

        var parallelExecution = TestTaskConcurrent1.StartTime < TestTaskConcurrent2.EndTime &&
                                TestTaskConcurrent2.StartTime < TestTaskConcurrent1.EndTime;

        parallelExecution.ShouldBeTrue();


        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }*/

    [Fact]
    public async Task Should_skip_blacklisted_task()
    {
        var task1 = new TestTaskConcurrent1();
        var task2 = new TestTaskConcurrent2();

        TestTaskConcurrent1.Counter = 0;
        TestTaskConcurrent2.Counter = 0;

        var task1Id = await _dispatcher.Dispatch(task1);
        await _dispatcher.Cancel(task1Id);

        await _host.StartAsync();

        var task2Id = await _dispatcher.Dispatch(task2, TimeSpan.FromMinutes(2));
        await _dispatcher.Cancel(task2Id);

        await Task.Delay(300, CancellationToken.None);

        _workerBlacklist.IsBlacklisted(task2Id).ShouldBeTrue();

        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(2);

        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[1].Status.ShouldBe(QueuedTaskStatus.Cancelled);

        TestTaskConcurrent1.Counter.ShouldBe(0);
        TestTaskConcurrent2.Counter.ShouldBe(0);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_monitoring()
    {
        await _host.StartAsync();

        var monitorCalled = false;

        _workerExecutor.TaskEventOccurredAsync += data =>
        {
            data.Severity.ShouldBe(SeverityLevel.Information.ToString());
            monitorCalled = true;
            return Task.CompletedTask;
        };

        var task = new TestTaskConcurrent1();
        TestTaskConcurrent1.Counter = 0;
        await _dispatcher.Dispatch(task);

        await Task.Delay(600);

        monitorCalled.ShouldBeTrue();

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

        TestTaskConcurrent1.Counter = 0;
        TestTaskConcurrent2.Counter = 0;

        var task1 = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task1);

        await Task.Delay(600, CancellationToken.None);

        TestTaskConcurrent1.Counter.ShouldBe(1);

        var task2 = new TestTaskConcurrent2();
        await _dispatcher.Dispatch(task2);

        await Task.Delay(600, CancellationToken.None);

        TestTaskConcurrent2.Counter.ShouldBe(1);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
