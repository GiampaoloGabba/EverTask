namespace EverTask.Tests;

/// <summary>
/// Unit tests for <see cref="TaskDeliveryRegistry"/>: the write-boundary defense that makes
/// in-process double delivery impossible (recovery racing a live dispatch, scheduler slot
/// fires, taskKey re-dispatch).
/// </summary>
public class TaskDeliveryRegistryTests
{
    [Fact]
    public void Should_reject_second_begin_while_delivery_is_in_flight()
    {
        var registry = new TaskDeliveryRegistry();
        var id       = Guid.NewGuid();

        registry.TryBegin(id).ShouldBeTrue();
        registry.TryBegin(id).ShouldBeFalse();
        registry.IsDelivering(id).ShouldBeTrue();
    }

    [Fact]
    public void Should_allow_new_delivery_after_end()
    {
        var registry = new TaskDeliveryRegistry();
        var id       = Guid.NewGuid();

        registry.TryBegin(id).ShouldBeTrue();
        registry.End(id);

        registry.IsDelivering(id).ShouldBeFalse();
        registry.TryBegin(id).ShouldBeTrue();
    }

    [Fact]
    public void Should_treat_end_of_unknown_id_as_noop()
    {
        var registry = new TaskDeliveryRegistry();

        registry.End(Guid.NewGuid()); // must not throw

        var id = Guid.NewGuid();
        registry.TryBegin(id).ShouldBeTrue();
        registry.End(Guid.NewGuid()); // unrelated id: no effect on the in-flight delivery
        registry.IsDelivering(id).ShouldBeTrue();
    }

    [Fact]
    public async Task Should_admit_exactly_one_winner_under_concurrent_begins()
    {
        var registry = new TaskDeliveryRegistry();
        var id       = Guid.NewGuid();
        var winners  = 0;

        var contenders = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            if (registry.TryBegin(id))
                Interlocked.Increment(ref winners);
        }));

        await Task.WhenAll(contenders);

        winners.ShouldBe(1);
        registry.Count.ShouldBe(1);
    }
}
