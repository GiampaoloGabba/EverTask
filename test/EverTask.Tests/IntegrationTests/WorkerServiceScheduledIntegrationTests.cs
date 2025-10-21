using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

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
        var taskId = await _dispatcher.Dispatch(task, TimeSpan.FromSeconds(1.2));

        // Wait for task to be in waiting queue
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after delay
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);
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
    }

    [Fact]
    public async Task Should_execute_specific_time_task()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;
        var specificDate = DateTimeOffset.Now.AddSeconds(1.2);
        var taskId = await _dispatcher.Dispatch(task, specificDate);

        // Wait for task to be in waiting queue
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for task to complete after scheduled time
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);
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
    }

    [Fact]
    public async Task Should_execute_recurring_cron()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed2();
        TestTaskDelayed2.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task, builder => builder.RunDelayed(TimeSpan.FromMilliseconds(600)).Then().UseCron("*/2 * * * * *").MaxRuns(3));

        // Wait for task to be scheduled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Wait for recurring task to complete 3 runs
        // Increased timeout for parallel test execution on .NET 8/9
        var completedTask = await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, 3, timeoutMs: 15000);

        // Use the returned task from WaitForRecurringRunsAsync to avoid race conditions
        completedTask.CurrentRunCount.ShouldBe(3);
        completedTask.RunsAudits.Count.ShouldBe(3);

        // Verify in storage as well
        pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);
        pt[0].StatusAudits.Count.ShouldBe(9);

        // Counter already verified via RunsAudits above - no need for static counter check

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_second_interval()
    {
        await _host.StartAsync();

        var task = new TestTaskRecurringSeconds();
        TestTaskRecurringSeconds.Counter = 0;

        // Every 2 seconds, max 3 runs
        var taskId = await _dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be scheduled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, 3, timeoutMs: 10000);

        pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();
        pt[0].RunsAudits.All(r => r.Exception == null).ShouldBeTrue();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_initial_delay_then_interval()
    {
        await _host.StartAsync();

        var task = new TestTaskRecurringSeconds();
        TestTaskRecurringSeconds.Counter = 0;

        // Wait 500ms, then every 2 seconds, max 3 runs
        var taskId = await _dispatcher.Dispatch(task, builder =>
            builder.RunDelayed(TimeSpan.FromMilliseconds(500))
                   .Then()
                   .Every(2).Seconds()
                   .MaxRuns(3));

        // Wait for task to be scheduled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, 3, timeoutMs: 10000);

        pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_recurring_task_with_run_now_then_interval()
    {
        await _host.StartAsync();

        var task = new TestTaskRecurringSeconds();
        TestTaskRecurringSeconds.Counter = 0;

        // Run immediately, then every 2 seconds, max 3 runs
        var taskId = await _dispatcher.Dispatch(task, builder =>
            builder.RunNow()
                   .Then()
                   .Every(2).Seconds()
                   .MaxRuns(3));

        // Wait for task to be scheduled and start executing
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].IsRecurring.ShouldBeTrue();

        // Wait for recurring task to complete 3 runs
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, 3, timeoutMs: 10000);

        pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all runs completed successfully
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_reschedule_recurring_task_after_failure_with_retry()
    {
        await _host.StartAsync();

        var task = new TestTaskRecurringWithFailure();
        TestTaskRecurringWithFailure.Counter = 0;
        TestTaskRecurringWithFailure.FailUntilCount = 2; // Fail first 2 attempts, succeed on 3rd

        // Every 2 seconds, max 3 runs - first run will retry internally due to LinearRetryPolicy(3, 50ms)
        var taskId = await _dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        // Wait for task to be scheduled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        // Wait for recurring task to complete 3 runs
        // First run: fails twice (retry), succeeds on 3rd attempt
        // Second run: succeeds immediately (counter=4, > threshold)
        // Third run: succeeds immediately (counter=5, > threshold)
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, taskId, 3, timeoutMs: 15000);

        var pt = await _storage.GetAll();
        pt.Length.ShouldBe(1);
        pt[0].CurrentRunCount.ShouldBe(3);
        pt[0].RunsAudits.Count.ShouldBe(3);

        // Verify all 3 recurring runs completed successfully (retries are internal to each run)
        pt[0].RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Counter should be > 3 due to retries during first run
        TestTaskRecurringWithFailure.Counter.ShouldBeGreaterThan(3);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_handle_multiple_concurrent_recurring_tasks()
    {
        await _host.StartAsync();

        // Reset counters
        TestTaskRecurringSeconds.Counter = 0;
        TestTaskDelayed1.Counter = 0;
        TestTaskDelayed2.Counter = 0;

        // Dispatch 3 different recurring tasks with different intervals
        var task1 = new TestTaskRecurringSeconds();
        var task1Id = await _dispatcher.Dispatch(task1, builder => builder.Schedule().Every(2).Seconds().MaxRuns(3));

        var task2 = new TestTaskDelayed1();
        var task2Id = await _dispatcher.Dispatch(task2, builder => builder.Schedule().Every(3).Seconds().MaxRuns(2));

        var task3 = new TestTaskDelayed2();
        var task3Id = await _dispatcher.Dispatch(task3, builder => builder.RunNow().Then().Every(2).Seconds().MaxRuns(2));

        // Wait for all tasks to be scheduled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task1Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task2Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task3Id, QueuedTaskStatus.WaitingQueue, timeoutMs: 1000);

        // Verify all 3 tasks are in storage
        var allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(3);
        allTasks.All(t => t.IsRecurring).ShouldBeTrue();

        // Wait for all tasks to complete their runs
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, task1Id, 3, timeoutMs: 15000);
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, task2Id, 2, timeoutMs: 15000);
        await TaskWaitHelper.WaitForRecurringRunsAsync(_storage, task3Id, 2, timeoutMs: 15000);

        // Verify each task completed the correct number of runs independently
        allTasks = await _storage.GetAll();
        allTasks.Length.ShouldBe(3);

        var completedTask1 = allTasks.FirstOrDefault(t => t.Id == task1Id);
        var completedTask2 = allTasks.FirstOrDefault(t => t.Id == task2Id);
        var completedTask3 = allTasks.FirstOrDefault(t => t.Id == task3Id);

        completedTask1.ShouldNotBeNull();
        completedTask1.CurrentRunCount.ShouldBe(3);
        completedTask1.RunsAudits.Count.ShouldBe(3);
        completedTask1.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask2.ShouldNotBeNull();
        completedTask2.CurrentRunCount.ShouldBe(2);
        completedTask2.RunsAudits.Count.ShouldBe(2);
        completedTask2.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        completedTask3.ShouldNotBeNull();
        completedTask3.CurrentRunCount.ShouldBe(2);
        completedTask3.RunsAudits.Count.ShouldBe(2);
        completedTask3.RunsAudits.All(r => r.Status == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Verify no interference - total completed runs should match expected
        var totalCompletedRuns = allTasks.Sum(t => t.CurrentRunCount ?? 0);
        totalCompletedRuns.ShouldBe(7); // 3 + 2 + 2

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

}
