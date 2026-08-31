using EverTask.Handler;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;

namespace EverTask.Tests.RateLimiting;

/// <summary>
/// P9, limiter leg: the rate limiter, the gate and the parking lot must run on the SAME clock the schedulers
/// sleep on, and they get it from the container — two of the three are built by hand-written factories in
/// <c>AddEverTask</c> that pass the clock explicitly. Every other rate-limiting test builds those components
/// directly and hands them a fake clock, so it proves the components read the clock they are GIVEN and says
/// nothing about which clock the container gives them. These build them the way production does.
/// </summary>
public class RateLimiterDeterministicClockTests
{
    private sealed record ClockGatedTask : IEverTask;

    // A decade behind the wall clock: every instant asserted below is unreachable by the real one.
    private static readonly DateTimeOffset FrozenNow = new(2016, 4, 1, 8, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Only the one required member matters here: the gate re-parks a deferred task by scheduling it, and the
    /// slot it schedules is the observable. The real scheduler would also start delivering it.
    /// </summary>
    private sealed class RecordingScheduler : IScheduler
    {
        public readonly List<DateTimeOffset?> ScheduledSlots = [];

        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null) =>
            ScheduledSlots.Add(nextRecurringRun ?? item.ExecutionTime);

        public bool TryUnschedule(Guid persistenceId) => false;
    }

    private static ServiceProvider BuildProvider(TimeProvider clock, IScheduler? scheduler = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Before AddEverTask, so its own TryAddSingleton finds the seat taken.
        if (scheduler != null)
            services.AddSingleton(scheduler);

        services.AddEverTask(opt =>
            opt.RegisterTasksFromAssembly(typeof(RateLimiterDeterministicClockTests).Assembly));

        services.AddSingleton(clock);

        return services.BuildServiceProvider();
    }

    private static RateLimitPolicy Policy(int permits = 15) =>
        new(permits, TimeSpan.FromMinutes(1))
        {
            Burst                 = permits,
            MaxReservationHorizon = TimeSpan.FromHours(1)
        };

    private static TaskHandlerExecutor Executor(RateLimitPolicy policy) =>
        new(new ClockGatedTask(), new object(), null, null, null, null, null, null, null, Guid.NewGuid(),
            "default", null, AuditLevel.Full, policy, "tenant-1");

    [Fact]
    public async Task The_limiter_resolved_from_the_container_measures_its_slots_on_the_injected_clock()
    {
        var clock = new FakeTimeProvider(FrozenNow);
        await using var provider = BuildProvider(clock);

        var limiter = provider.GetRequiredService<IKeyedRateLimiter>();
        var policy  = Policy();

        for (var i = 0; i < 15; i++)
            (await limiter.TryAcquireAsync(policy, typeof(ClockGatedTask), "tenant-1", Guid.NewGuid()))
                .Acquired.ShouldBeTrue($"acquire #{i + 1} is within the burst");

        var deferred = await limiter.TryAcquireAsync(policy, typeof(ClockGatedTask), "tenant-1", Guid.NewGuid());

        deferred.Acquired.ShouldBeFalse();
        deferred.RetryAt.ShouldBe(FrozenNow.AddSeconds(4),
            "one emission interval (60s / 15) past the injected now — a limiter left on the system clock "
            + "would answer a decade later");
    }

    [Fact]
    public async Task The_gate_resolved_from_the_container_parks_the_deferral_at_the_slot_that_clock_gives_it()
    {
        var clock     = new FakeTimeProvider(FrozenNow);
        var scheduler = new RecordingScheduler();
        await using var provider = BuildProvider(clock, scheduler);

        var gate    = provider.GetRequiredService<IRateLimitGate>();
        var limiter = provider.GetRequiredService<IKeyedRateLimiter>();
        var policy  = Policy(permits: 1);

        (await limiter.TryAcquireAsync(policy, typeof(ClockGatedTask), "tenant-1", Guid.NewGuid()))
            .Acquired.ShouldBeTrue("the single permit is spent up front so the gate can only defer");

        var result = await gate.TryPassAsync(Executor(policy), CancellationToken.None);

        result.Outcome.ShouldBe(RateLimitGateOutcome.Deferred);
        scheduler.ScheduledSlots.ShouldHaveSingleItem().ShouldBe(FrozenNow.AddMinutes(1),
            "the gate re-parks at the limiter's next slot untouched; on the system clock that slot would look "
            + "a decade old and the past-slot floor would replace it with now + 100 ms");
    }

    [Fact]
    public async Task The_parking_lot_resolved_from_the_container_bounds_its_pause_on_the_injected_clock()
    {
        var clock = new FakeTimeProvider(FrozenNow);
        await using var provider = BuildProvider(clock);

        var lot = provider.GetRequiredService<RateLimitParkingLot>();
        lot.MaxOverflowPause     = TimeSpan.FromMinutes(10);
        lot.OverflowPollInterval = TimeSpan.FromSeconds(30);

        for (var i = 0; i < lot.MaxParkedTasks; i++)
            lot.Park(Guid.NewGuid(), "default", "tenant-1", FrozenNow);

        lot.Count.ShouldBeGreaterThanOrEqualTo(lot.MaxParkedTasks, "the lot has to be over capacity to pause");

        var timersBefore = clock.PendingTimerCount;
        var pause        = lot.WaitForCapacityAsync("default", CancellationToken.None).AsTask();

        // Both halves of the pause are on the injected clock: the poll interval is a delay of THAT clock, so
        // the loop cannot come round again until it moves, and the deadline is ten minutes of it away. Wait
        // for the delay to be armed before jumping, or the jump would land before there is anything to elapse.
        (await clock.WaitForPendingTimersAsync(timersBefore + 1)).ShouldBeTrue(
            "the poll interval must be a delay of the injected clock, not a real half-second nap");

        // The lot never drains here, so the ONLY way out is the deadline. A lot left on the system clock
        // would keep polling for ten real minutes and this wait would give up first.
        clock.Advance(TimeSpan.FromMinutes(11));

        await pause.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
