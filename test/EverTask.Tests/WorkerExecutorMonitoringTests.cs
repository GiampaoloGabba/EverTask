using EverTask.Handler;
using EverTask.Logger;
using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverTask.Tests;

/// <summary>
/// P-B hot-path monitoring gates:
/// - L30: RegisterEvent must skip string.Format + object[] boxing when the level is filtered AND
///   there are no monitoring subscribers (nobody consumes the message).
/// - F24: PublishEvent fan-out must be bounded — a slow/blocked subscriber under load must not spawn
///   an unbounded number of fire-and-forget callbacks.
/// Both are [UNIT-necessario]: the seam is WorkerExecutor.RegisterInfo (internal), driven directly so
/// the format/fan-out invariants are observed deterministically without timing.
/// </summary>
public class WorkerExecutorMonitoringTests
{
    private sealed record MonitoringProbeTask : IEverTask;

    // Custom arg whose ToString() bumps a counter, so "was the message formatted?" is observable.
    private sealed class FormatProbe
    {
        public static int ToStringCount;
        public static void Reset() => ToStringCount = 0;
        public override string ToString()
        {
            Interlocked.Increment(ref ToStringCount);
            return "probe";
        }
    }

    private static WorkerExecutor CreateExecutor(IEverTaskLogger<WorkerExecutor> logger) =>
        new(new Mock<IWorkerBlacklist>().Object,
            new EverTaskServiceConfiguration(),
            new Mock<IServiceScopeFactory>().Object,
            new Mock<IScheduler>().Object,
            new Mock<ICancellationSourceProvider>().Object,
            logger,
            NullLoggerFactory.Instance);

    private static TaskHandlerExecutor SampleExecutor() =>
        new(new MonitoringProbeTask(),
            new object(),
            null, null, null, null, null, null, null,
            Guid.NewGuid(),
            "default",
            null,
            AuditLevel.Full);

    // ---- L30 ----

    [Fact]
    public void Should_not_format_event_message_when_level_filtered_and_no_subscribers()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor = CreateExecutor(logger.Object); // no TaskEventOccurredAsync subscribers

        FormatProbe.Reset();
        executor.RegisterInfo(SampleExecutor(), "value = {0}", new FormatProbe());

        FormatProbe.ToStringCount.ShouldBe(0,
            "with the level filtered and zero subscribers the message must not be formatted (L30)");
    }

    [Fact]
    public void Should_format_event_message_when_level_enabled()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var executor = CreateExecutor(logger.Object);

        FormatProbe.Reset();
        executor.RegisterInfo(SampleExecutor(), "value = {0}", new FormatProbe());

        FormatProbe.ToStringCount.ShouldBe(1,
            "non-regression: when the level is enabled the message is formatted exactly once");
    }

    // ---- F24 ----

    [Fact]
    public void Should_bound_monitoring_fanout_under_load()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var executor = CreateExecutor(logger.Object);

        // Subscribers block until released: every admitted callback holds its permit, so the in-flight
        // count parks at the cap and everything beyond it is dropped.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int subscribers = 4;
        for (var s = 0; s < subscribers; s++)
            executor.TaskEventOccurredAsync += _ => release.Task;

        var cap = WorkerExecutor.MonitoringMaxConcurrency;

        // Fire far more invocations than the cap. Admission (Wait(0)) is synchronous, so after this
        // loop the accounting is deterministic regardless of thread-pool scheduling.
        var fires = cap + 5;
        var totalInvocations = fires * subscribers;
        for (var i = 0; i < fires; i++)
            executor.RegisterInfo(SampleExecutor(), "evt {0}", i);

        try
        {
            executor.MonitoringInFlightCount.ShouldBe(cap,
                "the fan-out must admit at most MonitoringMaxConcurrency concurrent callbacks (F24)");
            executor.MonitoringDroppedEvents.ShouldBe(totalInvocations - cap,
                "every over-cap monitoring callback must be dropped, never spawned unbounded (F24)");
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task Should_deliver_all_monitoring_events_under_moderate_load()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        var executor = CreateExecutor(logger.Object);

        const int events = 50;
        var delivered = 0;
        // Event-driven completion: the TCS fires the instant the last event is delivered, so the test waits on
        // the actual signal instead of polling on a fixed cadence. The timeout is only a safety net.
        var allDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Fast subscriber: completes synchronously, so every admitted callback frees its permit right away.
        executor.TaskEventOccurredAsync += _ =>
        {
            if (Interlocked.Increment(ref delivered) == events)
                allDelivered.TrySetResult();
            return Task.CompletedTask;
        };

        // "Moderate load" must mean the in-flight count never reaches the fan-out cap — otherwise the
        // non-blocking Wait(0) admission drops over-cap events BY DESIGN (that is the F24 contract).
        // A fixed-rate loop fires far faster than the Task.Run callbacks drain on a 2-core CI box
        // (cap == 4 there), so it dropped events and never reached `events` → the old flake.
        // We are the sole producer and callbacks only ever RELEASE permits, so once we observe headroom
        // our next fire is guaranteed a permit and cannot be dropped — deterministic, no timing assumptions.
        var cap = WorkerExecutor.MonitoringMaxConcurrency;
        for (var i = 0; i < events; i++)
        {
            while (executor.MonitoringInFlightCount >= cap)
                await Task.Delay(1);
            executor.RegisterInfo(SampleExecutor(), "evt {0}", i);
        }

        await allDelivered.Task.WaitAsync(TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));

        Volatile.Read(ref delivered).ShouldBe(events,
            "non-regression: under moderate load every event reaches the subscriber (no silent loss)");
        executor.MonitoringDroppedEvents.ShouldBe(0);
    }
}
