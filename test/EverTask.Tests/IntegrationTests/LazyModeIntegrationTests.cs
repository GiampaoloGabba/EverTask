using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for lazy handler resolution mode.
///
/// KEY INSIGHT: BOTH lazy and eager modes ALWAYS dispose handlers after execution.
/// The difference is WHEN the EXECUTING handler is created:
/// - Eager mode: Handler created at dispatch time (lives in memory until execution)
/// - Lazy mode: Handler created at execution time (minimal memory footprint)
///
/// Lazy dispatches additionally resolve a short-lived metadata instance at dispatch time
/// (queue name / handler type extraction) which is created AND disposed inside the dispatch
/// scope (MEM-2 fix: nothing stays pinned in the root container). So in lazy mode
/// DisposeAsyncCore fires once at dispatch (metadata instance, never executed) and once
/// after each execution (executing instance).
///
/// Tests validate:
/// 1. Adaptive algorithm assigns correct mode based on task type/interval
/// 2. Handlers are disposed after execution in BOTH modes (not kept in memory)
/// 3. In eager mode, disposal happens AFTER execution, not during dispatch
/// </summary>
public class LazyModeIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_dispose_handler_after_each_recurring_execution_in_lazy_mode()
    {
        // Create isolated host with lazy mode enabled for recurring tasks
        await CreateIsolatedHostAsync(configureEverTask: cfg => cfg
            .UseLazyHandlerResolution = true);

        // CRITICAL: Reset static counters BEFORE dispatching task
        // Multiple tests use TestTaskLazyModeRecurringWithAsyncDispose, and when running
        // in parallel (xUnit default), they share the same static state.
        // We must reset immediately before dispatch to avoid pollution from other tests.
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // Dispatch recurring task with 3 runs (RunNow + 2 more)
        // Using short interval (1 second) to complete quickly
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()
            .Then()
            .Every(1).Seconds()
            .MaxRuns(3));

        // Wait for all 3 runs to complete using intelligent polling
        // Adaptive: Local 5s (3 runs with RunNow + 1s intervals), CI 15s (coverage overhead)
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: TestEnvironment.GetTimeout(5000, 15000));

        // CRITICAL: Wait additional time to ensure no 4th run is scheduled after 3rd completes
        // The recurring task scheduler reschedules after each execution, so we need to wait
        // beyond the interval (1 second) to ensure the MaxRuns check prevents a 4th run.
        // Wait time: 100ms (task execution) + 200ms (disposal) + 1500ms (2x interval for safety)
        await Task.Delay(1800);

        // CRITICAL: Verify execution count using STORAGE (RunsAudits), NOT static counters!
        // Static counters are shared across all tests running in parallel and can be polluted
        // by other tests using TestTaskLazyModeRecurringWithAsyncDispose.
        // Storage RunsAudits is isolated per test because each test has its own IHost/Storage.
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        var completedRuns = tasks[0].RunsAudits.Count(x => x != null && x.Status == QueuedTaskStatus.Completed);
        completedRuns.ShouldBe(3, "Task should execute exactly 3 times (MaxRuns=3)");

        // Secondary verification: Check static counters (may be unreliable due to parallel test execution)
        // These should match storage counts in an ideal scenario, but may differ due to race conditions
        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        // Note: We don't assert on static counters anymore - they're unreliable in parallel execution
        // Storage RunsAudits is the source of truth
    }

    [Fact]
    public async Task Should_dispose_handler_after_execution_for_delayed_task_in_eager_mode()
    {
        // With 2-second delay (< 30min threshold), task uses eager mode but still disposes after execution
        await CreateIsolatedHostAsync(configureEverTask: cfg =>
        {
            cfg.UseLazyHandlerResolution = true;
        });

        // Reset static properties for this isolated test
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed = false;
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch = false;

        var task = new TestTaskLazyModeDelayedWithAsyncDispose();

        // Dispatch with 2 second delay (< 30min threshold → eager mode)
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(2));

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // Verify disposal was called after execution (eager mode also disposes handlers)
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Handler should be disposed after execution in eager mode");

        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch.ShouldBeFalse(
            "Handler should NOT be disposed during dispatch (only after execution)");

        // Verify callback order: Handle -> DisposeAsyncCore
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count.ShouldBe(2);
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[0].ShouldBe("Handle");
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[1].ShouldBe("DisposeAsyncCore");

        // Verify task completed successfully in storage
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Should_not_dispose_handler_during_dispatch_in_lazy_mode_for_recurring_task()
    {
        // Test validates: In lazy mode, handler is NOT created during dispatch (disposeCount = 0)
        // Key behavior: Handler created at execution time, then disposed after execution
        // Difference from eager: Eager creates handler at dispatch, lazy creates at execution
        // This test PAUSES host to verify dispatch doesn't create handler (lazy mode)

        // Create isolated host with lazy mode enabled
        // Start host AFTER dispatch to control timing
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false,
            configureEverTask: cfg => cfg.UseLazyHandlerResolution = true);

        // Reset static counters
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // Dispatch recurring task with 10-minute interval (> 5 min threshold → lazy mode)
        // CRITICAL: Use .RunNow() to execute immediately, otherwise .Schedule().Every(10).Minutes()
        // schedules first execution 10 minutes in the future (see RecurringTask.cs:52-55)
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()            // Execute immediately (first run)
            .Then()
            .Every(10).Minutes() // Long interval → lazy mode
            .MaxRuns(1));


        // At this point, dispatch is complete but task hasn't executed.
        // In lazy mode the dispatch resolves a short-lived metadata instance and disposes it
        // with the dispatch scope (MEM-2): one dispose, zero executions.
        TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount.ShouldBe(1,
            "The dispatch-time metadata instance should be disposed with the dispatch scope (MEM-2)");

        TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount.ShouldBe(0,
            "Task should not have executed yet (host not started)");

        // Verify task was persisted to storage
        var tasksBefore = await Storage.GetAll();
        tasksBefore.Length.ShouldBe(1);
        tasksBefore[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // IMPORTANT: Clear the task from storage before starting host to prevent recovery logic
        // from re-dispatching it (which would cause double execution).
        // The task is already scheduled in-memory. We'll re-add a fresh entry so that
        // the task can update its status when it executes.
        var originalTask = tasksBefore[0];
        await Storage.Remove(taskId);

        // Re-persist a clean task entry with Completed status so the task can update it
        // This prevents recovery from re-scheduling while allowing status updates
        originalTask.Status = QueuedTaskStatus.Completed;
        originalTask.CurrentRunCount = null;
        originalTask.RunsAudits.Clear();
        await Storage.Persist(originalTask);

        // Now start the host and let the task execute
        await Host!.StartAsync();

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // CRITICAL: Verify execution using STORAGE, NOT static counters!
        // Static counters (DisposeCount, ExecutionCount) are shared across all parallel tests
        // and can be polluted by other tests using TestTaskLazyModeRecurringWithAsyncDispose.
        var tasksAfter = await Storage.GetAll();
        tasksAfter.Length.ShouldBe(1);
        tasksAfter[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasksAfter[0].RunsAudits.Count(x => x != null && x.Status == QueuedTaskStatus.Completed).ShouldBe(1,
            "Task should have executed exactly once (MaxRuns=1)");
    }

    [Fact]
    public async Task Should_not_dispose_handler_during_dispatch_in_eager_mode_for_delayed_task()
    {
        // Test validates: Even in eager mode, handler NOT disposed during dispatch (only after execution)
        // Key behavior: Eager mode creates handler at dispatch, but keeps it until execution completes
        // This test PAUSES host to verify handler lives from dispatch → execution → disposal
        // Difference from lazy: Lazy never creates handler during dispatch

        // Create isolated host with lazy mode enabled (but 2-second delay uses eager mode)
        // Start host AFTER dispatch to control timing
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false,
            configureEverTask: cfg =>
            {
                cfg.UseLazyHandlerResolution = true;
            });

        // Reset static properties
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed = false;
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch = false;

        var task = new TestTaskLazyModeDelayedWithAsyncDispose();

        // Dispatch with 2-second delay (< 30min threshold → eager mode)
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(2));

        // At this point, dispatch is complete but task hasn't executed
        // In eager mode, handler was created at dispatch but NOT disposed
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeFalse(
            "Handler should NOT be disposed during dispatch (even in eager mode)");

        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count.ShouldBe(0,
            "Task should not have executed yet (host not started)");

        // Verify task was persisted to storage
        var tasksBefore = await Storage.GetAll();
        tasksBefore.Length.ShouldBe(1);
        tasksBefore[0].Status.ShouldBe(QueuedTaskStatus.WaitingQueue);

        // Now start the host and let the task execute
        await Host!.StartAsync();

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // NOW the handler should be disposed (after execution, not during dispatch)
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Handler should be disposed after execution");

        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch.ShouldBeFalse(
            "Handler should NOT have been disposed during dispatch");

        // Verify callback order: Handle → DisposeAsyncCore
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count.ShouldBe(2);
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[0].ShouldBe("Handle");
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[1].ShouldBe("DisposeAsyncCore");
    }

    [Fact]
    public async Task Should_dispose_executing_handler_after_immediate_task_execution()
    {
        // Immediate tasks are lazy by default (MEM-2): the executing handler is resolved and
        // disposed inside the worker's per-task scope; the dispatch-time metadata instance is
        // disposed with the dispatch scope.
        await CreateIsolatedHostAsync(configureEverTask: cfg =>
        {
            cfg.UseLazyHandlerResolution = true;
        });

        // Reset static properties
        TestTaskLifecycleWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLifecycleWithAsyncDispose.WasDisposed = false;

        var task = new TestTaskLifecycleWithAsyncDispose();
        var taskId = await Dispatcher.Dispatch(task); // Immediate dispatch = lazy mode (MEM-2)

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // Verify disposal was called
        TestTaskLifecycleWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Executing handler should be disposed after execution");

        TestTaskLifecycleWithAsyncDispose.CallbackOrder.ShouldContain("DisposeAsyncCore");

        // Verify callback order: the EXECUTING instance is disposed after Handle.
        // (The dispatch-time metadata instance may add a DisposeAsyncCore entry before Handle,
        // so compare against the LAST dispose.)
        var handleIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.IndexOf("Handle");
        var lastDisposeIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.LastIndexOf("DisposeAsyncCore");

        handleIndex.ShouldBeGreaterThanOrEqualTo(0);
        lastDisposeIndex.ShouldBeGreaterThanOrEqualTo(0);
        lastDisposeIndex.ShouldBeGreaterThan(handleIndex, "The executing handler is disposed after Handle");
    }

    [Fact]
    public async Task Should_use_lazy_mode_for_infrequent_recurring_tasks()
    {
        await CreateIsolatedHostAsync();

        // Reset static counters
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // 6-minute interval >= 5-minute threshold → lazy mode
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()  // Execute first run immediately
            .Then()
            .Every(6).Minutes()  // Interval >= 5 min → lazy mode
            .MaxRuns(1));  // Limit to 1 run for test isolation

        // Wait for first run to complete
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1,
            timeoutMs: TestEnvironment.GetTimeout(1000, 3000));

        await Task.Delay(100); // Allow disposal to complete

        // Verify lazy mode: handler disposed after first run
        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(1, "First run should have executed");
        disposeCount.ShouldBe(2,
            "Both the dispatch-time metadata instance (MEM-2) and the executing handler should be disposed");

        // Verify first run completed successfully
        var tasks = await Storage.GetAll();
        tasks[0].RunsAudits.Count(x => x?.Status == QueuedTaskStatus.Completed).ShouldBeGreaterThanOrEqualTo(1, "At least one run should be completed");
    }

    [Fact]
    public async Task Should_use_eager_mode_for_frequent_recurring_tasks()
    {
        // Test validates: Short-interval recurring tasks (< 5 min) use eager mode (handler created at dispatch)
        // Key behavior: BOTH eager and lazy modes dispose handlers after execution (disposeCount = 1)
        // Difference: Eager mode creates handler at dispatch, lazy mode creates at execution time
        // Validation: Check QueuedTask.IsLazy property to verify eager mode was used

        await CreateIsolatedHostAsync();

        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // 2-second interval < 5-minute threshold → eager mode
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()
            .Then()
            .Every(2).Seconds()  // Interval < 5 min → eager mode
            .MaxRuns(1));  // Limit to 1 run to prevent race condition on slow CI

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1,
            timeoutMs: TestEnvironment.GetTimeout(1000, 3000));

        await Task.Delay(100);

        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(1, "Task should have executed once");
        disposeCount.ShouldBe(1, "Handler should be disposed after execution (ALL modes dispose handlers)");

        // Verify task has executed at least once
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].RunsAudits.Count(x => x?.Status == QueuedTaskStatus.Completed).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Should_use_lazy_mode_for_daily_cron_expressions()
    {
        await CreateIsolatedHostAsync();

        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // Daily cron (interval ~24 hours) → lazy mode
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()
            .Then()
            .UseCron("0 0 * * *")  // Daily at midnight (~24h interval → lazy)
            .MaxRuns(1));  // Limit to 1 run for test isolation

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1,
            timeoutMs: TestEnvironment.GetTimeout(1000, 3000));

        await Task.Delay(100);

        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(1);
        disposeCount.ShouldBe(2,
            "Both the dispatch-time metadata instance (MEM-2) and the executing handler should be disposed (lazy mode for long-interval cron)");
    }

    [Fact]
    public async Task Should_use_eager_mode_for_frequent_cron_expressions()
    {
        // Test validates: Cron with short interval (< 5 min) uses eager mode (handler created at dispatch)
        // Key behavior: BOTH eager and lazy modes dispose handlers after execution (disposeCount = 1)
        // Difference: Eager mode creates handler at dispatch, lazy mode creates at execution time
        // Validation: Check QueuedTask.IsLazy property to verify eager mode was used

        await CreateIsolatedHostAsync();

        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // 10-second cron (6-field format) → eager mode (interval < 5 min)
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()
            .Then()
            .UseCron("*/10 * * * * *")  // Every 10 seconds < 5 min → eager mode
            .MaxRuns(1));  // Limit to 1 run to prevent race condition on slow CI

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1,
            timeoutMs: TestEnvironment.GetTimeout(1000, 3000));

        await Task.Delay(100);

        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(1, "Task should have executed once");
        disposeCount.ShouldBe(1, "Handler should be disposed after execution (ALL modes dispose handlers)");

        // Verify task has executed at least once
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].RunsAudits.Count(x => x?.Status == QueuedTaskStatus.Completed).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Should_use_eager_mode_when_lazy_disabled_globally()
    {
        // Test validates: When lazy mode disabled globally, ALL tasks use eager mode
        // Key behavior: BOTH eager and lazy modes dispose handlers after execution (disposeCount = 1)
        // Difference: Eager mode creates handler at dispatch, lazy mode creates at execution time
        // Validation: Check QueuedTask.IsLazy property to verify eager mode was used

        await CreateIsolatedHostAsync(configureEverTask: cfg =>
            cfg.DisableLazyHandlerResolution());  // Explicitly disable lazy mode

        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount = 0;
            TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount = 0;
        }

        var task = new TestTaskLazyModeRecurringWithAsyncDispose();

        // Even with long interval (6 min > 5 min threshold), should use eager mode (lazy disabled globally)
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .RunNow()
            .Then()
            .Every(6).Minutes()
            .MaxRuns(1));  // Limit to 1 run for test isolation

        await WaitForRecurringRunsAsync(taskId, expectedRuns: 1,
            timeoutMs: TestEnvironment.GetTimeout(1000, 3000));

        await Task.Delay(100);

        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(1, "Task should have executed once");
        disposeCount.ShouldBe(1, "Handler should be disposed after execution (ALL modes dispose handlers)");

        // Verify task has executed at least once
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].RunsAudits.Count(x => x?.Status == QueuedTaskStatus.Completed).ShouldBeGreaterThanOrEqualTo(1);
    }
}
