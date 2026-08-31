using System.Globalization;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.Scheduler;
using EverTask.Scheduler.Recurring;
using EverTask.Scheduler.Recurring.Intervals;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverTask.Tests;

/// <summary>
/// P-B hot-path monitoring gates:
/// - L30: RegisterEvent must do no work at all — no structured log, no rendered event sentence — when
///   the level is filtered AND there are no monitoring subscribers (nobody consumes the message).
///   Since the log now carries a compile-time template, the flat sentence is rendered ONLY for
///   subscribers: an enabled level alone must not trigger it.
/// - F24: PublishEvent fan-out must be bounded — a slow/blocked subscriber under load must not spawn
///   an unbounded number of fire-and-forget callbacks.
/// Both are [UNIT-necessario]: the seam is WorkerExecutor.RegisterEvent (internal), driven directly so
/// the render/fan-out invariants are observed deterministically without timing.
/// </summary>
public class WorkerExecutorMonitoringTests
{
    private sealed record MonitoringProbeTask : IEverTask;

    // Custom arg whose ToString() bumps a counter, so "was the event sentence rendered?" is observable;
    // the log delegate bumps a second one, so "did anything reach the ILogger?" is observable too.
    private sealed class FormatProbe
    {
        public static int ToStringCount;
        public static int LogCount;

        public static void Reset()
        {
            ToStringCount = 0;
            LogCount      = 0;
        }

        public override string ToString()
        {
            Interlocked.Increment(ref ToStringCount);
            return "probe";
        }
    }

    // Stand-ins for the generated [LoggerMessage] method and the invariant renderer of a real call site.
    private static void LogProbe(ILogger logger, FormatProbe probe, Exception? exception) =>
        Interlocked.Increment(ref FormatProbe.LogCount);

    private static string RenderProbe(FormatProbe probe) => $"value = {probe}";

    private static void LogCounter(ILogger logger, int value, Exception? exception) { }

    private static string RenderCounter(int value) =>
        string.Create(CultureInfo.InvariantCulture, $"evt {value}");

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

    private static void RegisterProbe(WorkerExecutor executor, FormatProbe probe) =>
        executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, SampleExecutor(), null, null,
            probe, LogProbe, RenderProbe);

    // ---- L30 ----

    [Fact]
    public void Should_not_render_or_log_event_when_level_filtered_and_no_subscribers()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor = CreateExecutor(logger.Object); // no TaskEventOccurredAsync subscribers

        FormatProbe.Reset();
        RegisterProbe(executor, new FormatProbe());

        FormatProbe.ToStringCount.ShouldBe(0,
            "with the level filtered and zero subscribers the event sentence must not be rendered (L30)");
        FormatProbe.LogCount.ShouldBe(0,
            "with the level filtered and zero subscribers nothing must reach the ILogger either (L30)");
    }

    [Fact]
    public void Should_log_without_rendering_when_level_enabled_and_no_subscribers()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var executor = CreateExecutor(logger.Object);

        FormatProbe.Reset();
        RegisterProbe(executor, new FormatProbe());

        FormatProbe.LogCount.ShouldBe(1, "the structured log must be written exactly once");
        FormatProbe.ToStringCount.ShouldBe(0,
            "the flat sentence exists only for EverTaskEventData.Message: with no subscriber the " +
            "structured log needs no rendering");
    }

    [Fact]
    public async Task Should_render_event_once_for_subscriber_when_level_filtered()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor  = CreateExecutor(logger.Object);
        var published = new List<EverTaskEventData>();
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.TaskEventOccurredAsync += data =>
        {
            lock (published) published.Add(data);
            delivered.TrySetResult();
            return Task.CompletedTask;
        };

        FormatProbe.Reset();
        RegisterProbe(executor, new FormatProbe());

        FormatProbe.ToStringCount.ShouldBe(1,
            "a filtered level does not suppress the monitoring event: it is rendered exactly once for the subscriber");
        FormatProbe.LogCount.ShouldBe(0, "the ILogger must stay untouched while the level is filtered");

        await delivered.Task.WaitAsync(TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));
        lock (published)
        {
            published.ShouldHaveSingleItem().Message.ShouldBe("value = probe");
        }
    }

    [Fact]
    public async Task Should_publish_the_occurrence_context_of_the_executor_it_reports_on()
    {
        // The three occurrence fields used to be mapped twice: once in EverTaskEventData.FromExecutor, which
        // the tests call, and once in the worker's caching copy, which is the only one that ever publishes.
        // Pin them on the PRODUCTION path — RegisterEvent is the single gate every monitoring event passes.
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor  = CreateExecutor(logger.Object);
        var delivered = new TaskCompletionSource<EverTaskEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.TaskEventOccurredAsync += data =>
        {
            delivered.TrySetResult(data);
            return Task.CompletedTask;
        };

        var parent      = Guid.NewGuid();
        var nominalSlot = new DateTimeOffset(2026, 8, 22, 9, 0, 0, TimeSpan.Zero);
        var occurrence = SampleExecutor() with
        {
            // The canonical shape of an occurrence: NO schedule definition. ApplyOccurrenceContract strips it
            // from the child row, so an executor built from that row carries only the parent id — which is
            // why deriving the version from the definition published null exactly here.
            // The rate-limit gate replaces ExecutionTime with its reserved slot, so the nominal slot is the
            // one a dashboard must show.
            ExecutionTime   = nominalSlot.AddMinutes(3),
            ParentTaskId    = parent,
            NominalSlotUtc  = nominalSlot,
            ScheduleVersion = 7
        };

        executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, occurrence, null, null,
            new FormatProbe(), LogProbe, RenderProbe);

        var data = await delivered.Task.WaitAsync(
            TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));

        data.ParentTaskId.ShouldBe(parent);
        data.ScheduledAtUtc.ShouldBe(nominalSlot, "the nominal slot wins over the gate's reserved one");
        data.ScheduleVersion.ShouldBe(7,
            "an occurrence reports the version of the definition it was materialized against, and the parent " +
            "id is the only thing that says it belongs to one");
    }

    [Fact]
    public async Task Should_publish_the_schedule_version_of_the_schedule_row_itself()
    {
        // The other half of the pair: a schedule row owns the definition and has no parent.
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor  = CreateExecutor(logger.Object);
        var delivered = new TaskCompletionSource<EverTaskEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.TaskEventOccurredAsync += data =>
        {
            delivered.TrySetResult(data);
            return Task.CompletedTask;
        };

        var schedule = SampleExecutor() with
        {
            RecurringTask   = new RecurringTask { SecondInterval = new SecondInterval(30) },
            ScheduleVersion = 3
        };

        executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, schedule, null, null,
            new FormatProbe(), LogProbe, RenderProbe);

        var data = await delivered.Task.WaitAsync(
            TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));

        data.ParentTaskId.ShouldBeNull();
        data.ScheduleVersion.ShouldBe(3);
    }

    [Fact]
    public async Task Should_publish_no_schedule_version_for_a_plain_one_shot()
    {
        var logger = new Mock<IEverTaskLogger<WorkerExecutor>>();
        logger.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(false);

        var executor  = CreateExecutor(logger.Object);
        var delivered = new TaskCompletionSource<EverTaskEventData>(TaskCreationOptions.RunContinuationsAsynchronously);
        executor.TaskEventOccurredAsync += data =>
        {
            delivered.TrySetResult(data);
            return Task.CompletedTask;
        };

        executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, SampleExecutor(), null, null,
            new FormatProbe(), LogProbe, RenderProbe);

        var data = await delivered.Task.WaitAsync(
            TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));

        data.ParentTaskId.ShouldBeNull();
        data.ScheduleVersion.ShouldBeNull("a one-shot belongs to no schedule definition");
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
            executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, SampleExecutor(), null, null,
                i, LogCounter, RenderCounter);

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
            executor.RegisterEvent(LogLevel.Information, SeverityLevel.Information, SampleExecutor(), null, null,
                i, LogCounter, RenderCounter);
        }

        await allDelivered.Task.WaitAsync(TimeSpan.FromMilliseconds(TestEnvironment.GetTimeout(5000, 30000)));

        Volatile.Read(ref delivered).ShouldBe(events,
            "non-regression: under moderate load every event reaches the subscriber (no silent loss)");
        executor.MonitoringDroppedEvents.ShouldBe(0);
    }
}
