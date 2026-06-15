using System.Reflection;
using System.Runtime.CompilerServices;
using EverTask.Scheduler.Recurring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

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

    // ---- P-A / F22: the recurring ToString cache leak ----
    //
    // ToQueuedTask used to memoise RecurringTask.ToString() in a process-wide static
    // ConcurrentDictionary keyed by RecurringTask *reference identity*. Every persisted recurring
    // dispatch builds a fresh RecurringTask, so GetOrAdd never hit and every distinct dispatch added
    // a permanent entry (no TTL/sweep/WeakReference) — an unbounded leak in long-running processes
    // that re-dispatch recurring tasks dynamically. The fix computes ToString() inline, so nothing
    // is retained; this gate reads the cache count by reflection so it stays green once the field is
    // gone (no field -> nothing retained).

    private const int RecurringDispatchCount = 500;

    [Fact]
    public async Task Should_not_grow_recurring_tostring_cache_unbounded_across_distinct_dispatches()
    {
        // Storage is required: ToQueuedTask (and the cache GetOrAdd) only runs on a persisted dispatch.
        // Host NOT started: we only need the dispatch path to populate the cache, not execution.
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false);

        // Delta, never absolute: the cache is static and shared across the parallel test process.
        var before = GetRecurringToStringCacheCount();

        await DispatchDistinctRecurringTasks(RecurringDispatchCount);

        // Strong references in a dictionary survive GC, so a real leak does NOT shrink here.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true);

        var retained = GetRecurringToStringCacheCount() - before;

        // Pre-fix: ~RecurringDispatchCount entries retained (one per distinct dispatch) -> fails.
        // Post-fix: the cache is gone (or never grows) -> 0 retained.
        retained.ShouldBeLessThan(RecurringDispatchCount / 5,
            $"the recurring ToString cache must not retain ~one entry per distinct dispatch (F22 leak); " +
            $"retained {retained} of {RecurringDispatchCount}");
    }

    [Fact]
    public async Task Should_produce_identical_recurring_info_after_inlining()
    {
        // Non-regression: the RecurringInfo string persisted on the QueuedTask must be exactly the
        // RecurringTask.ToString() value the cache used to produce — i.e. the ToString() of the very
        // RecurringTask that was serialised onto the row.
        await CreateIsolatedHostWithBuilderAsync(
            builder => builder.AddMemoryStorage(),
            startHost: false);

        var runAt = DateTimeOffset.UtcNow.AddHours(1);
        var taskId = await Dispatcher.Dispatch(
            new TestTaskRecurringSeconds(),
            r => r.RunAt(runAt).Then().Every(7).Seconds().MaxRuns(3));

        var stored = (await Storage.GetAll()).Single(t => t.Id == taskId);

        stored.RecurringInfo.ShouldNotBeNullOrWhiteSpace();
        stored.RecurringTask.ShouldNotBeNull();

        var expected = JsonConvert.DeserializeObject<RecurringTask>(stored.RecurringTask!)!.ToString();
        stored.RecurringInfo.ShouldBe(expected);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task DispatchDistinctRecurringTasks(int count)
    {
        var future = DateTimeOffset.UtcNow.AddHours(1);
        for (var i = 0; i < count; i++)
        {
            // Distinct config per dispatch -> distinct RecurringTask instances (each a cache miss).
            await Dispatcher.Dispatch(
                new TestTaskRecurringSeconds(),
                r => r.RunAt(future).Then().Every(i + 1).Seconds());
        }
    }

    private static int GetRecurringToStringCacheCount()
    {
        var field = typeof(EverTask.Handler.TaskHandlerExecutorExtensions)
            .GetField("RecurringTaskToStringCache", BindingFlags.NonPublic | BindingFlags.Static);

        if (field?.GetValue(null) is not System.Collections.ICollection cache)
            return 0; // cache removed by the F22 fix -> nothing retained

        return cache.Count;
    }
}
