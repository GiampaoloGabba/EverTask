using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests;

/// <summary>
/// P9 for the schedulers: due-time decisions must read the injected clock, not the wall clock.
/// </summary>
/// <remarks>
/// Both schedulers used to sleep on <c>SemaphoreSlim.WaitAsync(timeout)</c>, whose timeout is hard-wired to
/// the real clock; the wait is now a race between the wake-up signal and a <c>TimeProvider</c>-driven delay.
/// These tests hold the clock still while real time passes — nothing may fire — and then move the clock,
/// which is the only thing that makes an occurrence due. The delay itself is virtual: <see cref="FakeTimeProvider"/>
/// hands out timers that only <c>Advance</c> can elapse, so neither a wake-up signal nor wall time can stand
/// in for the clock here.
/// </remarks>
[Collection("TimingSensitiveTests")]
public class SchedulerDeterministicClockTests
{
    private readonly Mock<IWorkerQueueManager> _queueManager = new();
    private readonly List<TaskHandlerExecutor> _dispatched   = [];

    public SchedulerDeterministicClockTests()
    {
        _queueManager
            .Setup(x => x.TryEnqueueImmediate(It.IsAny<string?>(), It.IsAny<TaskHandlerExecutor>(),
                It.IsAny<CancellationToken>()))
            .Returns<string?, TaskHandlerExecutor, CancellationToken>((_, executor, _) =>
            {
                lock (_dispatched) _dispatched.Add(executor);
                return Task.FromResult(EnqueueResult.Enqueued);
            });
    }

    private int DispatchedCount
    {
        get { lock (_dispatched) return _dispatched.Count; }
    }

    private static TaskHandlerExecutor ExecutorDueAt(DateTimeOffset executionTime) =>
        new(new TestTaskRequest("clock"), new TestTaskHanlder(), null, executionTime, null,
            (_, _) => Task.CompletedTask, (_, _, _) => ValueTask.CompletedTask, _ => ValueTask.CompletedTask,
            _ => ValueTask.CompletedTask, Guid.NewGuid(), null, null, AuditLevel.Full);

    private async Task<bool> WaitForDispatchAsync(int expected, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (DispatchedCount >= expected)
                return true;

            await Task.Delay(25);
        }

        return DispatchedCount >= expected;
    }

    [Fact]
    public async Task PeriodicTimerScheduler_should_hold_an_occurrence_until_the_injected_clock_reaches_it()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        using var scheduler = new PeriodicTimerScheduler(
            _queueManager.Object,
            Mock.Of<IEverTaskLogger<PeriodicTimerScheduler>>(),
            checkInterval: TimeSpan.FromMinutes(30),
            taskStorage: null,
            timeProvider: clock);

        scheduler.Schedule(ExecutorDueAt(clock.GetUtcNow().AddHours(1)));

        // The loop is now parked on the whole remaining delay of the injected clock, and nothing else.
        (await clock.WaitForPendingTimersAsync(1)).ShouldBeTrue("the wait must be armed on the injected clock");

        (await WaitForDispatchAsync(1, 400)).ShouldBeFalse(
            "real time passing must not make an occurrence due when the scheduling clock stands still");

        clock.Advance(TimeSpan.FromHours(2));

        (await WaitForDispatchAsync(1, 4000)).ShouldBeTrue(
            "moving the scheduling clock past the slot is what makes the occurrence due");
    }

    [Fact]
    public async Task ShardedScheduler_should_hold_an_occurrence_until_the_injected_clock_reaches_it()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        using var scheduler = new ShardedScheduler(
            _queueManager.Object,
            Mock.Of<IEverTaskLogger<ShardedScheduler>>(),
            taskStorage: null,
            shardCount: 2,
            timeProvider: clock);

        scheduler.Schedule(ExecutorDueAt(clock.GetUtcNow().AddHours(1)));

        // A shard has no periodic re-check: it sleeps for the WHOLE remaining delay, so this occurrence can
        // only come back if that delay is measured on the injected clock.
        (await clock.WaitForPendingTimersAsync(1)).ShouldBeTrue("the shard must be parked on the injected clock");

        (await WaitForDispatchAsync(1, 400)).ShouldBeFalse();

        clock.Advance(TimeSpan.FromHours(2));

        (await WaitForDispatchAsync(1, 4000)).ShouldBeTrue(
            "elapsing the shard's own delay on the injected clock is what delivers the occurrence");
        DispatchedCount.ShouldBe(1, "and it is delivered exactly once");
    }

    [Fact]
    public async Task PeriodicTimerScheduler_should_still_wake_on_a_signal_while_the_clock_stands_still()
    {
        // The wait is a race, so a brand-new due occurrence must be picked up immediately even though the
        // clock never moves — the old semaphore timeout gave this for free and it must not be lost.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        using var scheduler = new PeriodicTimerScheduler(
            _queueManager.Object,
            Mock.Of<IEverTaskLogger<PeriodicTimerScheduler>>(),
            checkInterval: TimeSpan.FromMinutes(30),
            taskStorage: null,
            timeProvider: clock);

        scheduler.Schedule(ExecutorDueAt(clock.GetUtcNow().AddSeconds(-1)));

        (await WaitForDispatchAsync(1, 4000)).ShouldBeTrue();
    }

    [Fact]
    public async Task PeriodicTimerScheduler_should_sleep_past_the_old_check_grid_and_wake_for_an_earlier_registration()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));

        using var scheduler = new PeriodicTimerScheduler(
            _queueManager.Object,
            Mock.Of<IEverTaskLogger<PeriodicTimerScheduler>>(),
            checkInterval: TimeSpan.FromSeconds(1),
            taskStorage: null,
            timeProvider: clock);

        scheduler.Schedule(ExecutorDueAt(clock.GetUtcNow().AddHours(1)));

        (await clock.WaitForPendingTimersAsync(1)).ShouldBeTrue("the long wait must be armed before moving the clock");
        var timersBeforeOldGrid = clock.CreatedTimerCount;

        clock.Advance(TimeSpan.FromSeconds(1));
        await Task.Delay(200);

        clock.CreatedTimerCount.ShouldBe(timersBeforeOldGrid,
            "the removed one-second clamp must not wake and re-arm the scheduler");
        DispatchedCount.ShouldBe(0);

        scheduler.Schedule(ExecutorDueAt(clock.GetUtcNow()));

        (await WaitForDispatchAsync(1, 4000)).ShouldBeTrue(
            "an earlier registration must interrupt the existing long wait without advancing the clock again");
        DispatchedCount.ShouldBe(1);
    }
}
