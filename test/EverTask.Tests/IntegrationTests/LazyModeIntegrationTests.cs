using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// Integration tests for lazy handler resolution mode.
/// Tests verify that handlers are properly disposed after execution in lazy mode,
/// and NOT disposed during dispatch (only GC-collected).
/// </summary>
public class LazyModeIntegrationTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_dispose_handler_after_each_recurring_execution_in_lazy_mode()
    {
        // Create isolated host with lazy mode enabled for recurring tasks
        await CreateIsolatedHostAsync(configureEverTask: cfg => cfg
            .UseLazyHandlerResolution = true);

        // Reset static counters for this isolated test
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
        await WaitForRecurringRunsAsync(taskId, expectedRuns: 3, timeoutMs: 5000);

        // Give disposal a moment to complete after final run
        await Task.Delay(300);

        // Verify: Each execution should have been followed by disposal
        int executionCount, disposeCount;
        lock (TestTaskLazyModeRecurringWithAsyncDispose.LockObject)
        {
            executionCount = TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount;
            disposeCount = TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount;
        }

        executionCount.ShouldBe(3,
            "Task should execute 3 times");

        disposeCount.ShouldBe(3,
            "Handler should be disposed after EACH execution (lazy mode creates new handler per run)");

        // Verify task completed successfully in storage
        var tasks = await Storage.GetAll();
        tasks.Length.ShouldBe(1);
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);
        tasks[0].RunsAudits.Count(x => x.Status == QueuedTaskStatus.Completed).ShouldBe(3);
    }

    [Fact]
    public async Task Should_dispose_handler_after_execution_for_delayed_task_in_lazy_mode()
    {
        // Create isolated host with lazy mode enabled and threshold set low to trigger lazy mode
        await CreateIsolatedHostAsync(configureEverTask: cfg =>
        {
            cfg.UseLazyHandlerResolution = true;
            cfg.LazyHandlerResolutionThreshold = TimeSpan.FromSeconds(1); // Tasks delayed > 1s use lazy mode
        });

        // Reset static properties for this isolated test
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed = false;
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch = false;

        var task = new TestTaskLazyModeDelayedWithAsyncDispose();

        // Dispatch with 2 second delay (exceeds threshold, triggers lazy mode)
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(2));

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // Verify disposal was called after execution
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Handler should be disposed after execution in lazy mode");

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

        // Dispatch recurring task (host not started yet)
        var taskId = await Dispatcher.Dispatch(task, builder => builder
            .Schedule()
            .Every(1).Seconds()
            .MaxRuns(1));

        // At this point, dispatch is complete but task hasn't executed
        // In lazy mode, the handler created for validation should be GC'd, NOT disposed
        TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount.ShouldBe(0,
            "Handler should NOT be disposed during dispatch (lazy mode sets handler to null for GC)");

        TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount.ShouldBe(0,
            "Task should not have executed yet (host not started)");

        // Now start the host and let the task execute
        await Host!.StartAsync();

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // NOW the handler should be disposed (after execution, not during dispatch)
        TestTaskLazyModeRecurringWithAsyncDispose.DisposeCount.ShouldBe(1,
            "Handler should be disposed after execution, not during dispatch");

        TestTaskLazyModeRecurringWithAsyncDispose.ExecutionCount.ShouldBe(1,
            "Task should have executed once");
    }

    [Fact]
    public async Task Should_not_dispose_handler_during_dispatch_in_lazy_mode_for_delayed_task()
    {
        // Create isolated host with lazy mode enabled
        // Start host AFTER dispatch to control timing
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false,
            configureEverTask: cfg =>
            {
                cfg.UseLazyHandlerResolution = true;
                cfg.LazyHandlerResolutionThreshold = TimeSpan.FromSeconds(1);
            });

        // Reset static properties
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed = false;
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch = false;

        var task = new TestTaskLazyModeDelayedWithAsyncDispose();

        // Dispatch with delay exceeding threshold (triggers lazy mode)
        var taskId = await Dispatcher.Dispatch(task, TimeSpan.FromSeconds(2));

        // At this point, dispatch is complete but task hasn't executed
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeFalse(
            "Handler should NOT be disposed during dispatch");

        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count.ShouldBe(0,
            "Task should not have executed yet (host not started)");

        // Now start the host and let the task execute
        await Host!.StartAsync();

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // NOW the handler should be disposed (after execution)
        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Handler should be disposed after execution");

        TestTaskLazyModeDelayedWithAsyncDispose.WasDisposedDuringDispatch.ShouldBeFalse(
            "Handler should NOT have been disposed during dispatch");

        // Verify callback order
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder.Count.ShouldBe(2);
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[0].ShouldBe("Handle");
        TestTaskLazyModeDelayedWithAsyncDispose.CallbackOrder[1].ShouldBe("DisposeAsyncCore");
    }

    [Fact]
    public async Task Should_dispose_handler_in_eager_mode_for_immediate_task()
    {
        // This test verifies the original fix: eager mode (immediate tasks) should also dispose handlers
        await CreateIsolatedHostAsync(configureEverTask: cfg =>
        {
            cfg.UseLazyHandlerResolution = true; // Enabled, but immediate tasks use eager mode
        });

        // Reset static properties
        TestTaskLifecycleWithAsyncDispose.CallbackOrder = new List<string>();
        TestTaskLifecycleWithAsyncDispose.WasDisposed = false;

        var task = new TestTaskLifecycleWithAsyncDispose();
        var taskId = await Dispatcher.Dispatch(task); // Immediate dispatch = eager mode

        // Wait for task to complete
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Give disposal a moment to complete
        await Task.Delay(200);

        // Verify disposal was called (this is the bug that was fixed)
        TestTaskLifecycleWithAsyncDispose.WasDisposed.ShouldBeTrue(
            "Handler should be disposed in eager mode too (bug fix verification)");

        TestTaskLifecycleWithAsyncDispose.CallbackOrder.ShouldContain("DisposeAsyncCore");

        // Verify callback order: Handle should come before DisposeAsyncCore
        var handleIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.IndexOf("Handle");
        var disposeIndex = TestTaskLifecycleWithAsyncDispose.CallbackOrder.IndexOf("DisposeAsyncCore");

        handleIndex.ShouldBeGreaterThanOrEqualTo(0);
        disposeIndex.ShouldBeGreaterThanOrEqualTo(0);
        disposeIndex.ShouldBeGreaterThan(handleIndex, "DisposeAsyncCore should be called after Handle");
    }
}
