using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Tests.TestHelpers;

namespace EverTask.Tests.RateLimiting;

/// <summary>
/// Unit tests for <see cref="RateLimiterIntrospection"/>: the single-node monitoring view over
/// the parking lot and the in-memory limiter.
/// </summary>
public class RateLimiterIntrospectionTests
{
    private sealed record IntrospectionTask : IEverTask;

    private readonly RateLimitParkingLot _parkingLot;
    private readonly InMemoryKeyedRateLimiter _limiter;
    private readonly RateLimiterIntrospection _introspection;

    public RateLimiterIntrospectionTests()
    {
        var options = new RateLimiterOptions();
        options.ResolveDefaults(1000);

        _parkingLot = new RateLimitParkingLot(options);
        _limiter = new InMemoryKeyedRateLimiter(options,
            new Mock<IEverTaskLogger<InMemoryKeyedRateLimiter>>().Object,
            new FakeTimeProvider(),
            sweepInterval: TimeSpan.Zero);
        _introspection = new RateLimiterIntrospection(_parkingLot, _limiter);
    }

    [Fact]
    public void Should_expose_parked_counts_and_snapshot_grouped_by_queue_and_key()
    {
        var slotA1 = DateTimeOffset.UtcNow.AddSeconds(5);
        var slotA2 = DateTimeOffset.UtcNow.AddSeconds(10);
        var slotB  = DateTimeOffset.UtcNow.AddSeconds(7);

        _parkingLot.Park(Guid.NewGuid(), "default", "tenant-A", slotA2);
        _parkingLot.Park(Guid.NewGuid(), "default", "tenant-A", slotA1);
        _parkingLot.Park(Guid.NewGuid(), "exports", "tenant-B", slotB);

        _introspection.ParkedTaskCount.ShouldBe(3);
        _introspection.MaxParkedTasks.ShouldBeGreaterThan(0);

        var snapshot = _introspection.GetParkedSnapshot();
        snapshot.Count.ShouldBe(2);

        var bucketA = snapshot.Single(s => s.Key == "tenant-A");
        bucketA.QueueName.ShouldBe("default");
        bucketA.ParkedCount.ShouldBe(2);
        bucketA.NextSlotUtc.ShouldBe(slotA1, "the snapshot exposes the EARLIEST slot of the bucket");

        var bucketB = snapshot.Single(s => s.Key == "tenant-B");
        bucketB.QueueName.ShouldBe("exports");
        bucketB.ParkedCount.ShouldBe(1);
    }

    [Fact]
    public void Should_expose_throttled_until_for_parked_tasks_only()
    {
        var taskId = Guid.NewGuid();
        var slot   = DateTimeOffset.UtcNow.AddSeconds(5);

        _introspection.GetThrottledUntil(taskId).ShouldBeNull();

        _parkingLot.Park(taskId, "default", "tenant-A", slot);
        _introspection.GetThrottledUntil(taskId).ShouldBe(slot);

        _parkingLot.Remove(taskId);
        _introspection.GetThrottledUntil(taskId).ShouldBeNull();
    }

    [Fact]
    public async Task Should_expose_limiter_counters()
    {
        var policy = new RateLimitPolicy(1, TimeSpan.FromMinutes(1)) { Burst = 1 };

        await _limiter.TryAcquireAsync(policy, typeof(IntrospectionTask), "key-1", Guid.NewGuid());
        await _limiter.TryAcquireAsync(policy, typeof(IntrospectionTask), "key-2", Guid.NewGuid());

        _introspection.TrackedKeyCount.ShouldBe(2);
        _introspection.FailOpenCount.ShouldBe(0);
    }
}
