using EverTask.Storage;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// P-B.1 / F23: the lifecycle MethodInfo (OnStarted/OnCompleted/OnError) must be resolved by
/// reflection exactly once per handler type and cached, like OnRetry — not re-resolved per task on
/// the lazy (default-for-immediate) hot path.
///
/// This gate is a post-fix invariant (the LifecycleReflectionResolutions seam is introduced by the
/// fix); the magnitude RED lives in the BenchmarkDotNet A/B (benchmarks/RESULTS.md).
/// </summary>
public class WorkerExecutorHotPathTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_resolve_lifecycle_methodinfo_once_per_handler_type()
    {
        await CreateIsolatedHostAsync(channelCapacity: 50, maxDegreeOfParallelism: 4);

        // Per-type seam: immune to other handler types resolved by concurrent tests in this process.
        var handlerType = typeof(PerfLifecycleProbeHandler);

        // First execution resolves this (process-unique) handler type once; await it so the per-type
        // cache is populated before the rest, avoiding a GetOrAdd factory race on the first touch.
        var firstId = await Dispatcher.Dispatch(new PerfLifecycleProbeTask());
        await WaitForTaskStatusAsync(firstId, QueuedTaskStatus.Completed, timeoutMs: 15000);

        const int more = 25;
        var ids = new List<Guid>(more);
        for (var i = 0; i < more; i++)
            ids.Add(await Dispatcher.Dispatch(new PerfLifecycleProbeTask()));
        foreach (var id in ids)
            await WaitForTaskStatusAsync(id, QueuedTaskStatus.Completed, timeoutMs: 15000);

        EverTask.Worker.WorkerExecutor.GetLifecycleResolutionCount(handlerType).ShouldBe(1,
            "the lifecycle MethodInfo of a handler type must be resolved exactly once across many " +
            "lazy executions of that type (F23 per-type cache)");
    }
}
