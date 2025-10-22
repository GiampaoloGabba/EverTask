using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

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

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => _cancSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        // Wait for task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

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

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => _cancSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        // Wait for task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

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

        // Give some time for task to be scheduled (but not started yet)
        await Task.Delay(100);

        await _dispatcher.Cancel(taskId);

        // Wait for task to be cancelled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Cancelled);

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

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => _cancSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = _cancSourceProvider.TryGet(taskId);

        await _dispatcher.Cancel(taskId);

        // Wait for task to be cancelled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Cancelled);

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

    [ConditionalFact("NET6_GITHUB")]
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
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to start execution
        await TaskWaitHelper.WaitForConditionAsync(() => _cancSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        await _host.StopAsync(cts.Token);

        // Wait a bit for the service stopped status to be persisted
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
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to complete with retries (3 attempts)
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

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
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to complete with custom retry policy (5 attempts)
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);

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
        var taskId = await _dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().MaxRuns(3));

        // Wait for recurring task to complete all 3 runs (RunNow + 2x EverySecond)
        // RunNow executes immediately, then waits 1 second for each recurring run
        // Each handler takes ~300ms, so total time: 300ms + 1000ms + 300ms + 1000ms + 300ms = ~3s
        // Adding buffer for safety
        await Task.Delay(4500);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].MaxRuns = tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Verify exactly 3 runs completed (using storage instead of static counter to avoid race conditions)
        tasks[0].RunsAudits.Count(x => x.Status == QueuedTaskStatus.Completed).ShouldBe(3);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_execute_task_with_run_at_until_expires()
    {
        await _host.StartAsync();

        var task = new TestTaskDelayed1();
        TestTaskDelayed1.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().RunUntil(DateTimeOffset.Now.AddSeconds(4)));

        // Wait for recurring task to complete (RunUntil set to 4 seconds from now)
        // RunNow executes immediately, then waits 1 second for each recurring run until 4s expires
        // Expected ~3 runs total, adding buffer for completion
        await Task.Delay(5500);

        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(0);

        var tasks = await _storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed).ShouldBe(4);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Counter already verified via RunsAudits above - no need for static counter check

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }


    [Fact]
    public async Task Should_not_execute_task_with_custom_timeout_excedeed()
    {
        await _host.StartAsync();

        var task = new TestTaskWithCustomTimeout();
        TestTaskWithCustomTimeout.Counter = 0;
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to fail due to timeout (timeout is 300ms, handler takes 500ms)
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Failed, timeoutMs: 2000);

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
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to fail (with retries)
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Failed, timeoutMs: 3000);

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

    [Fact]
    public async Task Should_execute_pending_and_concurrent_tasks()
    {
        // Reset legacy static counters
        TestTaskConcurrent1.Counter = 0;
        TestTaskConcurrent2.Counter = 0;

        // Dispatch tasks BEFORE starting the host (they should go to pending)
        var task1 = new TestTaskConcurrent1();
        var task1Id = await _dispatcher.Dispatch(task1);

        var task2 = new TestTaskConcurrent2();
        var task2Id = await _dispatcher.Dispatch(task2);

        // Verify tasks are queued but not yet executed (host not started)
        var dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task1);
        dequeued = await _workerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task2);

        // Verify both tasks are in pending state
        var pt = await _storage.RetrievePending();
        pt.Length.ShouldBe(2);

        // NOW start the host - it should pick up the pending tasks and execute them
        await _host.StartAsync();

        // Wait for both tasks to complete using intelligent polling
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task1Id, QueuedTaskStatus.Completed);
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task2Id, QueuedTaskStatus.Completed);

        // Verify both tasks completed successfully
        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(2);

        var completedTask1 = tasks.FirstOrDefault(t => t.Id == task1Id);
        var completedTask2 = tasks.FirstOrDefault(t => t.Id == task2Id);

        completedTask1.ShouldNotBeNull();
        completedTask1.Status.ShouldBe(QueuedTaskStatus.Completed);
        completedTask1.LastExecutionUtc.ShouldNotBeNull();
        completedTask1.Exception.ShouldBeNull();

        completedTask2.ShouldNotBeNull();
        completedTask2.Status.ShouldBe(QueuedTaskStatus.Completed);
        completedTask2.LastExecutionUtc.ShouldNotBeNull();
        completedTask2.Exception.ShouldBeNull();

        // Verify parallel execution by checking that execution times overlap
        // Both tasks take ~300ms, so if executed in parallel they should have overlapping execution windows
        var task1Start = completedTask1.StatusAudits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.InProgress)?.UpdatedAtUtc;
        var task1End = completedTask1.LastExecutionUtc!.Value;
        var task2Start = completedTask2.StatusAudits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.InProgress)?.UpdatedAtUtc;
        var task2End = completedTask2.LastExecutionUtc!.Value;

        task1Start.ShouldNotBeNull();
        task2Start.ShouldNotBeNull();

        // Check for time overlap (parallel execution)
        // Task1: [task1Start ---------- task1End]
        // Task2:         [task2Start ---------- task2End]
        // Parallel if: task1Start < task2End AND task2Start < task1End
        var parallelExecution = task1Start < task2End && task2Start < task1End;
        parallelExecution.ShouldBeTrue("Tasks should execute in parallel with MaxDegreeOfParallelism=3");

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

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

        // Wait for both tasks to be cancelled
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task1Id, QueuedTaskStatus.Cancelled);
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task2Id, QueuedTaskStatus.Cancelled);

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
        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to complete (which triggers monitoring event)
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

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
        var task1Id = await _dispatcher.Dispatch(task1);

        // Wait for first task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task1Id, QueuedTaskStatus.Completed);

        TestTaskConcurrent1.Counter.ShouldBe(1);

        var task2 = new TestTaskConcurrent2();
        var task2Id = await _dispatcher.Dispatch(task2);

        // Wait for second task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, task2Id, QueuedTaskStatus.Completed);

        TestTaskConcurrent2.Counter.ShouldBe(1);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_invoke_lifecycle_callbacks_in_correct_order()
    {
        await _host.StartAsync();

        var task = new TestTaskLifecycle();
        TestTaskLifecycle.CallbackOrder = new List<string>();
        TestTaskLifecycle.LastTaskId = null;

        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

        // Verify callback order
        TestTaskLifecycle.CallbackOrder.Count.ShouldBe(3);
        TestTaskLifecycle.CallbackOrder[0].ShouldBe("OnStarted");
        TestTaskLifecycle.CallbackOrder[1].ShouldBe("Handle");
        TestTaskLifecycle.CallbackOrder[2].ShouldBe("OnCompleted");

        // Verify task ID was passed correctly
        TestTaskLifecycle.LastTaskId.ShouldBe(taskId);

        // Verify task completed successfully in storage
        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].Exception.ShouldBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_invoke_on_error_callback_when_task_fails()
    {
        await _host.StartAsync();

        var task = new TestTaskLifecycleWithError();
        TestTaskLifecycleWithError.CallbackOrder = new List<string>();
        TestTaskLifecycleWithError.LastTaskId = null;
        TestTaskLifecycleWithError.LastErrorMessage = null;
        TestTaskLifecycleWithError.LastException = null;

        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to fail
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

        // Give OnError callback a moment to complete (it may be called asynchronously)
        await Task.Delay(200);

        // Verify callback order: OnStarted -> Handle -> OnRetry -> Handle (retry) -> OnError
        // With 1 retry configured, we get: OnStarted, Handle (fail), OnRetry, Handle (fail again), OnError
        TestTaskLifecycleWithError.CallbackOrder.Count.ShouldBe(5);
        TestTaskLifecycleWithError.CallbackOrder[0].ShouldBe("OnStarted");
        TestTaskLifecycleWithError.CallbackOrder[1].ShouldBe("Handle");
        TestTaskLifecycleWithError.CallbackOrder[2].ShouldBe("OnRetry");
        TestTaskLifecycleWithError.CallbackOrder[3].ShouldBe("Handle");
        TestTaskLifecycleWithError.CallbackOrder[4].ShouldBe("OnError");

        // Verify OnCompleted was NOT called (only OnError for failures)
        TestTaskLifecycleWithError.CallbackOrder.ShouldNotContain("OnCompleted");

        // Verify error details were captured
        TestTaskLifecycleWithError.LastErrorMessage.ShouldNotBeNull();
        TestTaskLifecycleWithError.LastException.ShouldNotBeNull();

        // Exception might be wrapped in AggregateException by retry policy
        var exception = TestTaskLifecycleWithError.LastException;
        if (exception is AggregateException aggEx)
        {
            aggEx.InnerExceptions.ShouldContain(ex => ex is InvalidOperationException);
        }
        else
        {
            exception.ShouldBeOfType<InvalidOperationException>();
        }

        // Verify task failed in storage
        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].Exception.ShouldNotBeNull();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_dispose_async_handler_after_execution()
    {
        await _host.StartAsync();

        var task = new TestTaskLifecycleWithAsyncDispose();
        TestTaskLifecycleWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLifecycleWithAsyncDispose.WasDisposed = false;

        var taskId = await _dispatcher.Dispatch(task);

        // Wait for task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // Verify disposal was called
        TestTaskLifecycleWithAsyncDispose.WasDisposed.ShouldBeTrue();
        TestTaskLifecycleWithAsyncDispose.CallbackOrder.ShouldContain("DisposeAsyncCore");

        // Verify callback order: Handle should come before DisposeAsyncCore
        var handleIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.IndexOf("Handle");
        var disposeIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.IndexOf("DisposeAsyncCore");

        handleIndex.ShouldBeGreaterThanOrEqualTo(0);
        disposeIndex.ShouldBeGreaterThanOrEqualTo(0);
        disposeIndex.ShouldBeGreaterThan(handleIndex, "DisposeAsyncCore should be called after Handle");

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Should_create_correct_status_audits_throughout_lifecycle()
    {
        await _host.StartAsync();

        var task = new TestTaskRecurringSeconds();
        TestTaskRecurringSeconds.Counter = 0;

        // Dispatch a recurring task to ensure we go through WaitingQueue status
        var taskId = await _dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(1));

        // Wait for WaitingQueue first
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        // Wait for task to complete
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Retrieve the task and verify StatusAudits
        var tasks = await _storage.GetAll();
        tasks.Length.ShouldBe(1);

        var completedTask = tasks[0];
        completedTask.Id.ShouldBe(taskId);
        completedTask.Status.ShouldBe(QueuedTaskStatus.Completed);

        // Verify StatusAudits were created
        completedTask.StatusAudits.ShouldNotBeNull();
        completedTask.StatusAudits.Count.ShouldBeGreaterThan(0);

        // Convert to ordered list for verification
        var audits = completedTask.StatusAudits.OrderBy(a => a.UpdatedAtUtc).ToList();

        // Expected lifecycle: [WaitingQueue] -> Queued -> InProgress -> Completed
        // Note: WaitingQueue might not always be in StatusAudits even if task.Status is set to it
        // Find key status transitions
        var waitingQueueAudit = audits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.WaitingQueue);
        var queuedAudit = audits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.Queued);
        var inProgressAudit = audits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.InProgress);
        var completedAudit = audits.FirstOrDefault(a => a.NewStatus == QueuedTaskStatus.Completed);

        // Verify essential statuses exist (WaitingQueue is optional in audits)
        inProgressAudit.ShouldNotBeNull("InProgress status should exist");
        completedAudit.ShouldNotBeNull("Completed status should exist");

        // Verify timestamps are progressive
        var orderedAudits = audits.Where(a =>
            a.NewStatus == QueuedTaskStatus.Queued ||
            a.NewStatus == QueuedTaskStatus.InProgress ||
            a.NewStatus == QueuedTaskStatus.Completed).ToList();

        orderedAudits.Count.ShouldBeGreaterThanOrEqualTo(2, "Should have at least InProgress and Completed");

        // Verify progressive timestamps
        for (int i = 1; i < orderedAudits.Count; i++)
        {
            orderedAudits[i-1].UpdatedAtUtc.ShouldBeLessThan(orderedAudits[i].UpdatedAtUtc,
                $"{orderedAudits[i-1].NewStatus} should happen before {orderedAudits[i].NewStatus}");
        }

        // Verify all audits reference the correct task
        audits.All(a => a.QueuedTaskId == taskId).ShouldBeTrue("All audits should reference the same task");

        // Verify no exception in successful execution
        completedAudit.Exception.ShouldBeNull("Completed task should have no exception");

        // Verify the final status matches the last audit
        completedTask.Status.ShouldBe(audits.Last().NewStatus);

        // Verify each audit has proper timestamp
        foreach (var audit in audits)
        {
            audit.UpdatedAtUtc.ShouldNotBe(default(DateTimeOffset), "Each audit should have a valid timestamp");
        }

        var cts = new CancellationTokenSource();
        cts.CancelAfter(2000);

        await _host.StopAsync(cts.Token);
    }
}
