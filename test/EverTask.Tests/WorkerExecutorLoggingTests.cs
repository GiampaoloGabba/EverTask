using System.Globalization;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EverTask.Tests;

/// <summary>
/// #32: WorkerExecutor logs through [LoggerMessage] templates while the monitoring event keeps its own
/// severity and its rendered, culture-invariant sentence. These drive the REAL call sites through
/// <see cref="WorkerExecutor.DoWork"/> (no seam, no hand-built message) so the log level, the structured
/// properties and the published <see cref="EverTaskEventData"/> are observed together.
/// </summary>
public class WorkerExecutorLoggingTests
{
    private sealed record LoggingProbeTask : IEverTask;

    private sealed record LogEntry(LogLevel Level, string Message, Dictionary<string, object?> Properties);

    private sealed class RecordingLogger : IEverTaskLogger<WorkerExecutor>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            // The generated state exposes the template's named properties; a pre-rendered string would not.
            var properties = state as IReadOnlyList<KeyValuePair<string, object?>>;
            var entry = new LogEntry(logLevel, formatter(state, exception),
                properties?.ToDictionary(p => p.Key, p => p.Value) ?? []);

            lock (_entries) _entries.Add(entry);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private sealed class TestGuidGenerator : IGuidGenerator
    {
        public Guid NewDatabaseFriendly() => Guid.NewGuid();
    }

    private static WorkerExecutor CreateExecutor(RecordingLogger logger, IRateLimitGate? rateLimitGate = null)
    {
        // A real container: CreateLogCapture resolves IGuidGenerator per task, and no ITaskStorage is
        // registered so the run stays storage-free.
        var services = new ServiceCollection()
                       .AddSingleton<IGuidGenerator, TestGuidGenerator>()
                       .BuildServiceProvider();

        return new WorkerExecutor(
            new Mock<IWorkerBlacklist>().Object,
            new EverTaskServiceConfiguration(),
            services.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IScheduler>().Object,
            new Mock<ICancellationSourceProvider>().Object,
            logger,
            NullLoggerFactory.Instance,
            rateLimitGate);
    }

    // Eager executor whose handler callback completes immediately: DoWork runs start → execute → complete.
    private static TaskHandlerExecutor SampleExecutor(RateLimitPolicy? rateLimitPolicy = null,
                                                      string? rateLimitKey = null) =>
        new(new LoggingProbeTask(),
            new object(),
            null, null, null,
            static (_, _) => Task.CompletedTask,
            null, null, null,
            Guid.NewGuid(),
            "default",
            null,
            AuditLevel.Full,
            rateLimitPolicy,
            rateLimitKey);

    private static (WorkerExecutor Executor, List<EverTaskEventData> Events) CreateSubscribedExecutor(
        RecordingLogger logger, IRateLimitGate? rateLimitGate = null)
    {
        var executor = CreateExecutor(logger, rateLimitGate);
        var events   = new List<EverTaskEventData>();

        executor.TaskEventOccurredAsync += data =>
        {
            lock (events) events.Add(data);
            return Task.CompletedTask;
        };

        return (executor, events);
    }

    // Events are published fire-and-forget: poll until the one we are after has been delivered.
    private static async Task<EverTaskEventData> WaitForEventAsync(List<EverTaskEventData> events,
                                                                   Func<EverTaskEventData, bool> predicate)
    {
        await TaskWaitHelper.WaitForConditionAsync(
            () => { lock (events) return events.Any(predicate); },
            timeoutMs: TestEnvironment.GetTimeout(5000, 30000));

        lock (events) return events.First(predicate);
    }

    private static LogEntry SingleEntry(RecordingLogger logger, Func<LogEntry, bool> predicate) =>
        logger.Entries.Where(predicate).ShouldHaveSingleItem();

    [Fact]
    public async Task Should_log_task_start_at_debug_when_the_event_severity_stays_information()
    {
        var logger = new RecordingLogger();
        var (executor, events) = CreateSubscribedExecutor(logger);
        var task = SampleExecutor();

        await executor.DoWork(task, CancellationToken.None);

        var expected = $"Starting task with id {task.PersistenceId}";

        var logEntry = SingleEntry(logger, e => e.Message == expected);
        logEntry.Level.ShouldBe(LogLevel.Debug, "per-execution chatter belongs at Debug in the ILogger");
        logEntry.Properties["TaskId"].ShouldBe(task.PersistenceId);

        var published = await WaitForEventAsync(events, e => e.Message == expected);
        published.Severity.ShouldBe(nameof(SeverityLevel.Information),
            "the demotion applies to the log level only: the dashboard still receives an Information event");
    }

    [Fact]
    public async Task Should_log_task_completion_at_debug_when_the_event_severity_stays_information()
    {
        var logger = new RecordingLogger();
        var (executor, events) = CreateSubscribedExecutor(logger);
        var task = SampleExecutor();

        await executor.DoWork(task, CancellationToken.None);

        var prefix = $"Task with id {task.PersistenceId} was completed in ";

        var logEntry = SingleEntry(logger, e => e.Message.StartsWith(prefix, StringComparison.Ordinal));
        logEntry.Level.ShouldBe(LogLevel.Debug);
        logEntry.Properties["TaskId"].ShouldBe(task.PersistenceId);
        logEntry.Properties.ContainsKey("ElapsedMs").ShouldBeTrue(
            "the elapsed time must reach sinks as a structured property, not only inside the rendered text");

        var published = await WaitForEventAsync(events, e => e.Message.StartsWith(prefix, StringComparison.Ordinal));
        published.Severity.ShouldBe(nameof(SeverityLevel.Information));
        published.Message.ShouldEndWith(" ms");
    }

    [Fact]
    public async Task Should_render_completion_event_invariantly_when_current_culture_uses_a_comma_separator()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("it-IT");
        try
        {
            var logger = new RecordingLogger();
            var (executor, events) = CreateSubscribedExecutor(logger);
            var task = SampleExecutor();

            await executor.DoWork(task, CancellationToken.None);

            var prefix = $"Task with id {task.PersistenceId} was completed in ";
            var published = await WaitForEventAsync(events,
                e => e.Message.StartsWith(prefix, StringComparison.Ordinal));

            const string suffix = " ms";
            var elapsed = published.Message[prefix.Length..^suffix.Length];

            elapsed.Contains(',', StringComparison.Ordinal).ShouldBeFalse(
                "the event sentence is rendered with InvariantCulture, never the ambient decimal separator");
            double.TryParse(elapsed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                  .ShouldBeTrue($"'{elapsed}' must parse as an invariant double");
            published.Message.ShouldNotEndWith(".", "event messages are fragments, with no trailing period");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Should_keep_the_machine_parseable_shape_when_a_task_is_deferred_by_the_rate_limiter()
    {
        var slot = new DateTimeOffset(2026, 8, 22, 10, 30, 0, TimeSpan.Zero);
        var gate = new Mock<IRateLimitGate>();
        gate.Setup(g => g.TryPassAsync(It.IsAny<TaskHandlerExecutor>(), It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<RateLimitGateResult>(
                new RateLimitGateResult(RateLimitGateOutcome.Deferred, slot,
                    EmitDeferralEvent: true, AggregatedDeferrals: 3)));

        var logger = new RecordingLogger();
        var (executor, events) = CreateSubscribedExecutor(logger, gate.Object);
        var task = SampleExecutor(new RateLimitPolicy(1, TimeSpan.FromSeconds(1)), "tenant-7");

        await executor.DoWork(task, CancellationToken.None);

        var published = await WaitForEventAsync(events,
            e => e.Message.StartsWith("Rate limit deferred task ", StringComparison.Ordinal));

        // Documented machine-parseable contract (docs/monitoring-events.md, SignalR CLAUDE.md).
        var expected = string.Create(CultureInfo.InvariantCulture,
            $"Rate limit deferred task {task.PersistenceId}: key=tenant-7 slotUtc={slot:O} policy={typeof(LoggingProbeTask)} deferredCount=3");
        published.Message.ShouldBe(expected);

        var logEntry = SingleEntry(logger,
            e => e.Message.StartsWith("Rate limit deferred task ", StringComparison.Ordinal));
        logEntry.Level.ShouldBe(LogLevel.Information);
        logEntry.Properties["TaskId"].ShouldBe(task.PersistenceId);
        logEntry.Properties["Key"].ShouldBe("tenant-7");
        logEntry.Properties["SlotUtc"].ShouldBe(slot);
        logEntry.Properties["DeferredCount"].ShouldBe(3);
    }
}
