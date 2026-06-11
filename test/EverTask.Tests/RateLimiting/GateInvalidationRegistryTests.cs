using EverTask.RateLimiting;

namespace EverTask.Tests.RateLimiting;

/// <summary>
/// Unit tests for <see cref="GateInvalidationRegistry"/> (WS0 skeleton): the epoch registry
/// closing the dequeue→re-park limbo window that TryUnschedule cannot see.
/// </summary>
public class GateInvalidationRegistryTests
{
    [Fact]
    public void Should_return_epoch_zero_when_task_was_never_invalidated()
    {
        var registry = new GateInvalidationRegistry();

        registry.GetEpoch(Guid.NewGuid()).ShouldBe(0);
    }

    [Fact]
    public void Should_bump_epoch_when_invalidated()
    {
        var registry = new GateInvalidationRegistry();
        var id = Guid.NewGuid();

        registry.Invalidate(id);
        registry.GetEpoch(id).ShouldBe(1);

        registry.Invalidate(id);
        registry.GetEpoch(id).ShouldBe(2);
    }

    [Fact]
    public void Should_detect_change_when_invalidated_after_epoch_capture()
    {
        var registry = new GateInvalidationRegistry();
        var id = Guid.NewGuid();

        var epoch = registry.GetEpoch(id);

        registry.Invalidate(id);

        registry.HasChangedSince(id, epoch).ShouldBeTrue();
    }

    [Fact]
    public void Should_not_detect_change_when_no_invalidation_occurred()
    {
        var registry = new GateInvalidationRegistry();
        var id = Guid.NewGuid();

        // Never invalidated: absent entry means no change
        registry.HasChangedSince(id, registry.GetEpoch(id)).ShouldBeFalse();

        // Invalidated before capture, stable afterwards
        registry.Invalidate(id);
        var epoch = registry.GetEpoch(id);
        registry.HasChangedSince(id, epoch).ShouldBeFalse();
    }

    [Fact]
    public async Task Should_sweep_lapsed_entries()
    {
        var registry = new GateInvalidationRegistry
        {
            EntryTtl      = TimeSpan.FromMilliseconds(50),
            SweepInterval = TimeSpan.Zero
        };

        var old = Guid.NewGuid();
        registry.Invalidate(old);
        registry.GetEpoch(old).ShouldBe(1);

        await Task.Delay(150);

        // Sweep runs opportunistically on Invalidate
        registry.Invalidate(Guid.NewGuid());

        registry.GetEpoch(old).ShouldBe(0);

        // After the lapse, an absent entry must read as "no recent change"
        registry.HasChangedSince(old, 0).ShouldBeFalse();
    }

    [Fact]
    public void Should_count_invalidations_exactly_under_concurrency()
    {
        var registry = new GateInvalidationRegistry();
        var id = Guid.NewGuid();

        Parallel.For(0, 1000, _ => registry.Invalidate(id));

        registry.GetEpoch(id).ShouldBe(1000);
    }
}
