using System.Runtime.CompilerServices;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// MEM-2 regression suite: dispatching immediate tasks must not pin handler instances in the
/// root container. Before the fix, the singleton dispatcher resolved eager transient handlers
/// from the root provider; IAsyncDisposable ones were tracked in the root disposables list
/// until shutdown (and ToLazy() did not free them).
/// With the fix, immediate dispatches are lazy: the dispatch-time metadata instance is resolved
/// and disposed inside a short-lived scope, and the executing instance lives in the worker's
/// per-task scope.
/// </summary>
public class MemoryLeakRegressionTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_not_retain_handler_instances_after_immediate_tasks_complete()
    {
        await CreateIsolatedHostAsync(channelCapacity: 50, maxDegreeOfParallelism: 4);

        TestTaskMem2TrackedHandler.Instances.Clear();

        const int taskCount = 10;
        await DispatchAndAwaitCompletion(taskCount);

        TestTaskMem2TrackedHandler.Instances.ShouldNotBeEmpty();

        // While the host is still running, every handler instance ever created for these
        // dispatches must be collectable: nothing may be pinned in the root container.
        var allCollected = false;
        for (var attempt = 0; attempt < 10 && !allCollected; attempt++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);

            allCollected = TestTaskMem2TrackedHandler.Instances.All(wr => !wr.IsAlive);

            if (!allCollected)
                await Task.Delay(100);
        }

        allCollected.ShouldBeTrue(
            "all handler instances must be collectable while the host is running: " +
            "a live reference means the instance is pinned in the root container (MEM-2)");
    }

    // Keep dispatch + wait in a separate non-inlined method so no local in the test frame
    // accidentally roots an executor/handler reference during the GC assertions.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task DispatchAndAwaitCompletion(int taskCount)
    {
        var taskIds = new List<Guid>(taskCount);
        for (var i = 0; i < taskCount; i++)
            taskIds.Add(await Dispatcher.Dispatch(new TestTaskMem2Tracked()));

        foreach (var id in taskIds)
            await WaitForTaskStatusAsync(id, QueuedTaskStatus.Completed, timeoutMs: 15000);
    }

    [Fact]
    public async Task Should_dispose_dispatch_time_metadata_handler_when_dispatching_immediate_task()
    {
        // Host NOT started: only the dispatch runs, no execution.
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false);

        TestTaskMem2DisposeProbeHandler.Reset();

        await Dispatcher.Dispatch(new TestTaskMem2DisposeProbe());

        // The dispatch-time metadata instance is created and promptly disposed with the
        // dispatch scope; the task itself has not executed.
        TestTaskMem2DisposeProbeHandler.Created.ShouldBe(1);
        TestTaskMem2DisposeProbeHandler.Disposed.ShouldBe(1,
            "the dispatch-time metadata instance must be disposed with the dispatch scope (MEM-2)");
        TestTaskMem2DisposeProbeHandler.Executed.ShouldBe(0);
    }

    [Fact]
    public async Task Should_resolve_and_dispose_fresh_handler_per_execution_for_immediate_tasks()
    {
        await CreateIsolatedHostAsync();

        TestTaskMem2DisposeProbeHandler.Reset();

        var taskId = await Dispatcher.Dispatch(new TestTaskMem2DisposeProbe());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed, timeoutMs: 15000);

        // Disposal of the executing scope can lag the Completed status by a moment
        await TaskWaitHelper.WaitForConditionAsync(
            () => TestTaskMem2DisposeProbeHandler.Disposed == 2, timeoutMs: 5000);

        // One metadata instance (dispatch scope) + one executing instance (worker scope),
        // both disposed; exactly one execution.
        TestTaskMem2DisposeProbeHandler.Created.ShouldBe(2);
        TestTaskMem2DisposeProbeHandler.Disposed.ShouldBe(2);
        TestTaskMem2DisposeProbeHandler.Executed.ShouldBe(1);
    }
}
