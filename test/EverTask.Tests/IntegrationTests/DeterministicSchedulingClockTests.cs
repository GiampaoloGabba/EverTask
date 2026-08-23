using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Newtonsoft.Json;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// P9 end-to-end: one <see cref="TimeProvider"/> governs every scheduling decision of the pipeline —
/// dispatch, the recovery cutoff and the recovery filter alike. Registering a clock before the host is built
/// is the whole seam, and these tests drive it to an instant the wall clock will never agree with, so a
/// single forgotten <c>UtcNow</c> shows up immediately.
/// </summary>
public class DeterministicSchedulingClockTests : IsolatedIntegrationTestBase
{
    private readonly ResilienceTestState _state = new();

    // Ten years behind the wall clock: every assertion below would flip if any decision used the real one.
    private static readonly DateTimeOffset FrozenNow = new(2016, 4, 1, 8, 0, 0, TimeSpan.Zero);

    private Task<IHost> StartHostOnFrozenClockAsync(bool startHost) =>
        CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost,
            clock: new FakeTimeProvider(FrozenNow));

    [Fact]
    public async Task A_delayed_dispatch_is_scheduled_from_the_injected_clock()
    {
        await StartHostOnFrozenClockAsync(startHost: true);

        var id = await Dispatcher.Dispatch(new ResilienceCounterTask(1), TimeSpan.FromMinutes(10));

        var row = (await Storage.Get(t => t.Id == id))[0];

        row.ScheduledExecutionUtc.ShouldBe(FrozenNow.AddMinutes(10),
            "the delay is applied to the scheduling clock, not to the wall clock");
        _state.ExecutedIndexes.ShouldBeEmpty("and the occurrence is not due while that clock stands still");
    }

    [Fact]
    public async Task The_recurring_builder_validates_RunUntil_on_the_injected_clock()
    {
        await StartHostOnFrozenClockAsync(startHost: true);

        // This boundary is in the wall clock's PAST, so the builder's "RunUntil cannot be in the past" guard
        // would reject it — unless the guard reads the injected clock, for which it is an hour away.
        var boundary = FrozenNow.AddHours(1);

        var id = await Dispatcher.Dispatch(new ResilienceCounterTask(2),
            b => b.RunNow().Then().EveryMinute().RunUntil(boundary));

        var row = (await Storage.Get(t => t.Id == id))[0];

        row.RunUntil.ShouldBe(boundary);
        row.NextRunUtc.ShouldNotBeNull();
        row.NextRunUtc!.Value.ShouldBeInRange(FrozenNow, boundary,
            "RunNow resolves on the injected clock too, so the first occurrence lands inside the series");
    }

    [Fact]
    public async Task The_recurring_builder_on_the_real_clock_still_rejects_a_past_RunUntil()
    {
        // The control for the test above: nothing was loosened, the guard simply follows the clock it is given.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: true);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Dispatcher.Dispatch(new ResilienceCounterTask(3),
                b => b.Schedule().EveryMinute().RunUntil(FrozenNow.AddHours(1))));
    }

    [Fact]
    public async Task The_worker_computes_the_next_occurrence_on_the_injected_clock()
    {
        // The leg that only a real run reaches: after the handler returns, the worker asks the evaluator for
        // the next occurrence and hands it BOTH the slot just executed and its own "now". The second one is
        // what decides whether the answer is taken as-is or realigned as a missed occurrence — so on the
        // system clock the freshly computed 2016 slot looks a decade stale and the series jumps to today.
        await StartHostOnFrozenClockAsync(startHost: true);

        var id = await Dispatcher.Dispatch(new ResilienceCounterTask(11), b => b.RunNow().Then().EveryMinute());

        // The run counter and the new cursor are written after the handler returns, so waiting on the
        // handler's own signal alone would race that write.
        var row = await TaskWaitHelper.WaitUntilAsync(
            async () => (await Storage.Get(t => t.Id == id)).FirstOrDefault(),
            task => task?.CurrentRunCount >= 1,
            timeoutMs: 8000);

        _state.ExecutedIndexes.ShouldContain(11, "RunNow is due the moment the injected clock reads it");

        row.ShouldNotBeNull();
        row.NextRunUtc.ShouldBe(FrozenNow.AddMinutes(1),
            "one grid step past the slot that just ran, judged against the injected now");
        row.CurrentRunCount.ShouldBe(1, "and exactly one real execution was counted");
    }

    [Fact]
    public async Task Recovery_judges_RunUntil_on_the_injected_clock()
    {
        await StartHostOnFrozenClockAsync(startHost: false);

        // RunUntil sits in the wall clock's PAST and in the frozen clock's FUTURE. On the real clock this row
        // is past its boundary and must never run; on the injected one it is simply still pending.
        var seeded = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(ResilienceCounterTask).AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new ResilienceCounterTask(7)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            RunUntil     = FrozenNow.AddHours(1),
            CreatedAtUtc = FrozenNow.AddHours(-1)
        };
        await Storage.Persist(seeded);

        await Host!.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && _state.ExecutedIndexes.IsEmpty)
            await Task.Delay(50);

        _state.ExecutedIndexes.ShouldContain(7,
            "the recovery filter and the conditional requeue must both read the clock the core hands them");
    }

    [Fact]
    public async Task Recovery_on_the_real_clock_leaves_the_same_row_alone()
    {
        // The control for the test above: with no injected clock the very same row is past its boundary and
        // must stay untouched — proving the previous assertion really came from the injected clock.
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);
            },
            startHost: false);

        var seeded = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(ResilienceCounterTask).AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new ResilienceCounterTask(7)),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            RunUntil     = FrozenNow.AddHours(1),
            CreatedAtUtc = FrozenNow.AddHours(-1)
        };
        await Storage.Persist(seeded);

        await Host!.StartAsync();
        await Task.Delay(500);

        _state.ExecutedIndexes.ShouldBeEmpty();
        (await Storage.Get(t => t.Id == seeded.Id))[0].Status.ShouldBe(QueuedTaskStatus.Queued);
    }
}
