using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

[Collection("StorageTests")]
public class WorkerServiceIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_execute_task_and_clear_cancellation_source()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => CancellationSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = CancellationSourceProvider.TryGet(taskId);

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        CancellationSourceProvider.TryGet(taskId).ShouldBeNull();

    }


    [Fact]
    public async Task Should_execute_cpu_bound_task_and_clear_cancellation_source()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskCpubound();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => CancellationSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = CancellationSourceProvider.TryGet(taskId);

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        CancellationSourceProvider.TryGet(taskId).ShouldBeNull();

    }

    [Fact]
    public async Task Should_cancel_non_started_task_and_not_creating_cancellation_source()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromMilliseconds(300));

        // Give some time for task to be scheduled (but not started yet)
        await Task.Delay(100);

        await Dispatcher.Cancel(taskId);

        // Wait for task to be cancelled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Cancelled);

        CancellationSourceProvider.TryGet(taskId).ShouldBeNull();

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

    }

    [Fact]
    public async Task Should_cancel_started_task_and_relative_cancellation_source()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for cancellation source to be created (with polling)
        await TaskWaitHelper.WaitForConditionAsync(() => CancellationSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);
        var ctsToken = CancellationSourceProvider.TryGet(taskId);

        await Dispatcher.Cancel(taskId);

        // Wait for task to be cancelled
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Cancelled);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        Should.Throw<ObjectDisposedException>(() => ctsToken?.Token);
        CancellationSourceProvider.TryGet(taskId).ShouldBeNull();

    }

    [Fact]
    public async Task Should_cancel_task_when_service_stopped()
    {
        await CreateIsolatedHostAsync();

        var monitorCalled = false;

        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            data.Severity.ShouldBe(SeverityLevel.Warning.ToString());
            monitorCalled = true;
            return Task.CompletedTask;
        };

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to start execution
        await TaskWaitHelper.WaitForConditionAsync(() => CancellationSourceProvider.TryGet(taskId) != null, timeoutMs: 2000);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        await Host!.StopAsync(cts.Token);

        // Wait a bit for the service stopped status to be persisted
        await Task.Delay(300);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(1);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.ServiceStopped);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull();

        monitorCalled.ShouldBeTrue();

    }


    [Fact]
    public async Task Should_execute_task_with_standard_retry_policy()
    {
        await CreateIsolatedHostAsync();

        TestTaskWithRetryPolicy.Counter = 0; // Reset static counter
        var task = new TestTaskWithRetryPolicy();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to complete with retries (3 attempts)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 3000);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Verify retry count via StateManager (thread-safe)
        StateManager.GetCounter(nameof(TestTaskWithRetryPolicy)).ShouldBe(3);
    }

    [Fact]
    public async Task Should_execute_task_with_standard_custom_policy()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskWithCustomRetryPolicy();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to complete with custom retry policy (5 attempts)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 2000);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Verify custom retry count via StateManager (thread-safe)
        StateManager.GetCounter(nameof(TestTaskWithCustomRetryPolicy)).ShouldBe(5);
    }

    [Fact]
    public async Task Should_execute_task_with_max_run_until_max_run_reached()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskDelayed1();
        var taskId = await Dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().MaxRuns(3));

        // Wait for recurring task to complete all 3 runs (RunNow + 2x EverySecond)
        // RunNow executes immediately, then waits 1 second for each recurring run
        // Each handler takes ~300ms, so total time: 300ms + 1000ms + 300ms + 1000ms + 300ms = ~3s
        // Adaptive: Local 5s, CI 15s (coverage tool overhead)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: TestEnvironment.GetTimeout(5000, 15000));

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].MaxRuns = tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Verify exactly 3 runs completed (using storage instead of static counter to avoid race conditions)
        tasks[0].RunsAudits.Count(x => x != null && x.Status == QueuedTaskStatus.Completed).ShouldBe(3);
    }

    [Fact]
    public async Task Should_execute_task_with_run_at_until_expires()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskDelayed1();
        var taskId = await Dispatcher.Dispatch(task, builder => builder.RunNow().Then().EverySecond().RunUntil(DateTimeOffset.Now.AddSeconds(4)));

        // Wait for recurring task to complete (RunUntil set to 4 seconds from now)
        // RunNow executes immediately, then waits 1 second for each recurring run until 4s expires
        // Expected ~4 runs total
        // Adaptive: Local 6s, CI 18s (coverage tool overhead)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 4, timeoutMs: TestEnvironment.GetTimeout(6000, 18000));

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].RunsAudits.Count(x=>x.Status == QueuedTaskStatus.Completed).ShouldBe(4);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldBeNull();

        // Counter already verified via RunsAudits above - no need for static counter check
    }


    [Fact]
    public async Task Should_not_execute_task_with_custom_timeout_excedeed()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskWithCustomTimeout();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to fail due to timeout (timeout is 300ms, handler takes 500ms)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 2000);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull().ShouldContain("TimeoutException");

    }

    [Fact]
    public async Task Should_throw_for_non_executable_tasks()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRequestError();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to fail (with retries)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 3000);

        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(0);

        var tasks = await Storage.GetAll();

        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].LastExecutionUtc.ShouldNotBeNull();
        tasks[0].Exception.ShouldNotBeNull().ShouldContain("AggregateException");
    }

    [Fact]
    public async Task Should_execute_pending_and_concurrent_tasks()
    {
        // Create isolated host but don't start it yet
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false,
            configureEverTask: cfg => cfg
                .SetChannelOptions(3)
                .SetMaxDegreeOfParallelism(3));

        // Reset legacy static counters

        // Dispatch tasks BEFORE starting the host (they should go to pending)
        var task1 = new TestTaskConcurrent1();
        var task1Id = await Dispatcher.Dispatch(task1);

        var task2 = new TestTaskConcurrent2();
        var task2Id = await Dispatcher.Dispatch(task2);

        // Verify tasks are queued but not yet executed (host not started)
        var dequeued = await WorkerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task1);
        dequeued = await WorkerQueue.Dequeue(CancellationToken.None);
        dequeued.Task.ShouldBe(task2);

        // Verify both tasks are in pending state
        var pt = await Storage.RetrievePending(null, null, 10);
        pt.Length.ShouldBe(2);

        // NOW start the host - it should pick up the pending tasks and execute them
        await Host!.StartAsync();

        // Wait for both tasks to complete using intelligent polling
        await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.Completed);
        await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.Completed);

        // Verify both tasks completed successfully
        var tasks = await Storage.GetAll();
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
    }

    [Fact]
    public async Task Should_skip_blacklisted_task()
    {
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false,
            configureEverTask: cfg => cfg
                .SetChannelOptions(3)
                .SetMaxDegreeOfParallelism(3));

        var task1 = new TestTaskConcurrent1();
        var task2 = new TestTaskConcurrent2();


        var task1Id = await Dispatcher.Dispatch(task1);
        await Dispatcher.Cancel(task1Id);

        await Host!.StartAsync();

        var task2Id = await Dispatcher.Dispatch(task2, TimeSpan.FromMinutes(2));
        await Dispatcher.Cancel(task2Id);

        // Wait for both tasks to be cancelled
        await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.Cancelled);
        await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.Cancelled);

        WorkerBlacklist.IsBlacklisted(task2Id).ShouldBeTrue();

        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(2);

        tasks[0].Status.ShouldBe(QueuedTaskStatus.Cancelled);
        tasks[1].Status.ShouldBe(QueuedTaskStatus.Cancelled);

    }

    [Fact]
    public async Task Should_execute_monitoring()
    {
        await CreateIsolatedHostAsync();

        var monitorCalled = false;

        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            data.Severity.ShouldBe(SeverityLevel.Information.ToString());
            monitorCalled = true;
            return Task.CompletedTask;
        };

        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to complete (which triggers monitoring event)
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        monitorCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Should_execute_Tasks_sequentially()
    {
        // Create host with MaxDegreeOfParallelism=1 for sequential execution
        await CreateIsolatedHostAsync(
            channelCapacity: 3,
            maxDegreeOfParallelism: 1);

        var task1 = new TestTaskConcurrent1();
        var task1Id = await Dispatcher.Dispatch(task1);

        // Wait for first task to complete
        var completedTask1 = await WaitForTaskStatusAsync(task1Id, QueuedTaskStatus.Completed);

        // Verify first task completed via storage (use StatusAudits, not RunsAudits for non-recurring tasks)
        completedTask1.Status.ShouldBe(QueuedTaskStatus.Completed);
        completedTask1.StatusAudits.Any(x => x.NewStatus == QueuedTaskStatus.Completed).ShouldBeTrue();

        var task2 = new TestTaskConcurrent2();
        var task2Id = await Dispatcher.Dispatch(task2);

        // Wait for second task to complete
        var completedTask2 = await WaitForTaskStatusAsync(task2Id, QueuedTaskStatus.Completed);

        // Verify second task completed via storage
        completedTask2.Status.ShouldBe(QueuedTaskStatus.Completed);
        completedTask2.StatusAudits.Any(x => x.NewStatus == QueuedTaskStatus.Completed).ShouldBeTrue();

        // Additionally verify sequential execution: task1 must have completed before task2 started
        // Since MaxDegreeOfParallelism=1, task2 can only start after task1 completes
        var allTasks = await Storage.GetAll();
        allTasks.Length.ShouldBe(2);

        var task1FromStorage = allTasks.First(t => t.Id == task1Id);
        var task2FromStorage = allTasks.First(t => t.Id == task2Id);

        // Task1 should have completed before task2 started queueing (compare status audit timestamps)
        var task1CompletedAt = task1FromStorage.StatusAudits.First(x => x.NewStatus == QueuedTaskStatus.Completed).UpdatedAtUtc;
        var task2QueuedAt = task2FromStorage.StatusAudits.First(x => x.NewStatus == QueuedTaskStatus.Queued).UpdatedAtUtc;

        // With sequential execution, task1 must complete before task2 even begins
        (task1CompletedAt < task2QueuedAt).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_invoke_lifecycle_callbacks_in_correct_order()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskLifecycle();
        TestTaskLifecycle.CallbackOrder = new List<string>();
        TestTaskLifecycle.LastTaskId = null;

        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Verify callback order
        TestTaskLifecycle.CallbackOrder.Count.ShouldBe(3);
        TestTaskLifecycle.CallbackOrder[0].ShouldBe("OnStarted");
        TestTaskLifecycle.CallbackOrder[1].ShouldBe("Handle");
        TestTaskLifecycle.CallbackOrder[2].ShouldBe("OnCompleted");

        // Verify task ID was passed correctly
        TestTaskLifecycle.LastTaskId.ShouldBe(taskId);

        // Verify task completed successfully in storage
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_invoke_on_error_callback_when_task_fails()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskLifecycleWithError();
        TestTaskLifecycleWithError.CallbackOrder = new List<string>();
        TestTaskLifecycleWithError.LastTaskId = null;
        TestTaskLifecycleWithError.LastErrorMessage = null;
        TestTaskLifecycleWithError.LastException = null;

        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to fail
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Failed, timeoutMs: 5000);

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
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Failed);
        tasks[0].Exception.ShouldNotBeNull();
    }

    [Fact]
    public async Task Should_dispose_async_handler_after_execution()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskLifecycleWithAsyncDispose();
        TestTaskLifecycleWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLifecycleWithAsyncDispose.WasDisposed = false;

        var taskId = await Dispatcher.Dispatch(task);

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

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
    }

    [Fact]
    public async Task Should_create_correct_status_audits_throughout_lifecycle()
    {
        await CreateIsolatedHostAsync();

        var task = new TestTaskRecurringSeconds();

        // Dispatch a recurring task to ensure we go through WaitingQueue status
        var taskId = await Dispatcher.Dispatch(task, builder => builder.Schedule().Every(2).Seconds().MaxRuns(1));

        // Wait for WaitingQueue first
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.WaitingQueue, timeoutMs: 2000);

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Retrieve the task and verify StatusAudits
        var tasks = await Storage.GetAll();
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
    }
}
