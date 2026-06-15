using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests;

/// <summary>
/// P-C / CU19: latest-wins replacement used to update _scheduledItems but leave the old node in the
/// priority queue until its (possibly far-future) due time, so repeated re-registrations of the same
/// id accumulated orphan entries that retained executor/payload/policy. The fix evicts the stale node
/// at replacement. These gates inspect the queue directly via the internal seams.
/// [UNIT-necessario: direct inspection of the scheduler priority queue.]
/// </summary>
public class SchedulerOrphanHeapTests
{
    private const int Replacements = 50;

    private static TaskHandlerExecutor CreateExecutor(DateTimeOffset executionTime, Guid id) =>
        new(new ResilienceCounterTask(0),
            new object(),
            null,
            executionTime,
            null, null, null, null, null,
            id,
            null,
            null,
            AuditLevel.Full);

    private static Mock<IWorkerQueueManager> NeverEnqueueManager()
    {
        // Far-future items are never due during the assert, so the manager is never actually called;
        // a default mock is enough.
        return new Mock<IWorkerQueueManager>();
    }

    [Fact]
    public void Should_not_retain_orphan_entries_on_latest_wins_replacement_periodic()
    {
        using var scheduler = new PeriodicTimerScheduler(
            NeverEnqueueManager().Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromSeconds(30)); // long check interval: the loop never dequeues far-future items

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        // Same id re-registered N times far-future (distinct executor instances, latest wins).
        var first = CreateExecutor(future, id);
        scheduler.Schedule(first);
        for (var i = 1; i < Replacements; i++)
            scheduler.Schedule(first with { });

        scheduler.IsScheduled(id).ShouldBeTrue();
        scheduler.GetQueue().Count.ShouldBe(1,
            $"latest-wins must keep a single heap entry per id; pre-fix it grew to ~{Replacements} orphans (CU19)");
    }

    [Fact]
    public void Should_not_retain_orphan_entries_on_latest_wins_replacement_sharded()
    {
        using var scheduler = new ShardedScheduler(
            NeverEnqueueManager().Object,
            new Mock<IEverTaskLogger<ShardedScheduler>>().Object,
            null,
            shardCount: 4);

        var id     = Guid.NewGuid();
        var future = DateTimeOffset.UtcNow.AddHours(1);

        var first = CreateExecutor(future, id);
        scheduler.Schedule(first);
        for (var i = 1; i < Replacements; i++)
            scheduler.Schedule(first with { });

        scheduler.IsScheduled(id).ShouldBeTrue();
        scheduler.GetQueueCount(id).ShouldBe(1,
            $"latest-wins must keep a single heap entry per id on the owning shard; pre-fix it grew to ~{Replacements} orphans (CU19)");
    }

    [Fact]
    public async Task Should_still_dispatch_single_id_scheduled_once_periodic()
    {
        // Non-regression: a normally-scheduled id still fires exactly once.
        var calls = 0;
        var mock = new Mock<IWorkerQueueManager>();
        mock.Setup(x => x.TryEnqueueImmediate(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, TaskHandlerExecutor, CancellationToken>((_, _, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(EnqueueResult.Enqueued);
            });

        using var scheduler = new PeriodicTimerScheduler(
            mock.Object,
            new Mock<IEverTaskLogger<PeriodicTimerScheduler>>().Object,
            TimeSpan.FromMilliseconds(50));

        scheduler.Schedule(CreateExecutor(DateTimeOffset.UtcNow.AddMilliseconds(100), Guid.NewGuid()));

        await TaskWaitHelper.WaitForConditionAsync(() => Volatile.Read(ref calls) >= 1, timeoutMs: 5000);
        await Task.Delay(300);

        Volatile.Read(ref calls).ShouldBe(1);
    }
}
