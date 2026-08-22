using System.Globalization;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.Monitoring;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Storage;
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
            get { lock (_entries) return [.. _entries]; }
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

    /// <summary>
    /// Real handler for the cancellation split: it writes one captured log (so the published event has
    /// <c>ExecutionLogs</c> to carry), lets the test trigger whatever is cancelling it, then throws the
    /// <see cref="OperationCanceledException"/> that <c>WorkerExecutor.HandleExceptionAsync</c> must classify.
    /// </summary>
    private sealed class CancellingProbeHandler(Action onExecuting) : EverTaskHandler<LoggingProbeTask>
    {
        public const string LoggedLine = "probe handler is about to be cancelled";

        public override Task Handle(LoggingProbeTask backgroundTask, CancellationToken cancellationToken)
        {
            Logger.LogInformation(LoggedLine);
            onExecuting();
            throw new OperationCanceledException();
        }
    }

    private static WorkerExecutor CreateExecutor(RecordingLogger logger, IRateLimitGate? rateLimitGate = null,
                                                 IWorkerBlacklist? workerBlacklist = null,
                                                 ITaskStorage? taskStorage = null,
                                                 EverTaskServiceConfiguration? configuration = null)
    {
        // A real container: CreateLogCapture resolves IGuidGenerator per task. ITaskStorage is registered
        // only when a test asserts on the persisted outcome; otherwise the run stays storage-free.
        var services = new ServiceCollection().AddSingleton<IGuidGenerator, TestGuidGenerator>();

        if (taskStorage != null)
            services.AddSingleton(taskStorage);

        var provider = services.BuildServiceProvider();

        return new WorkerExecutor(
            workerBlacklist ?? new Mock<IWorkerBlacklist>().Object,
            configuration ?? new EverTaskServiceConfiguration(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new Mock<IScheduler>().Object,
            new Mock<ICancellationSourceProvider>().Object,
            logger,
            NullLoggerFactory.Instance,
            rateLimitGate);
    }

    private static EverTaskServiceConfiguration CapturingConfiguration()
    {
        var configuration = new EverTaskServiceConfiguration();
        configuration.PersistentLogger.Enable();
        return configuration;
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

    // Eager executor over a REAL handler: the worker injects the log capture through
    // IEverTaskHandler<T>.SetLogCapture, so what the handler logs reaches the published event.
    private static TaskHandlerExecutor HandlerExecutor(EverTaskHandler<LoggingProbeTask> handler,
                                                       Guid persistenceId) =>
        new(new LoggingProbeTask(),
            handler,
            null, null, null,
            (task, token) => handler.Handle((LoggingProbeTask)task, token),
            null, null, null,
            persistenceId,
            "default",
            null,
            AuditLevel.Full);

    private static (WorkerExecutor Executor, List<EverTaskEventData> Events) CreateSubscribedExecutor(
        RecordingLogger logger, IRateLimitGate? rateLimitGate = null, IWorkerBlacklist? workerBlacklist = null,
        ITaskStorage? taskStorage = null, EverTaskServiceConfiguration? configuration = null)
    {
        var executor = CreateExecutor(logger, rateLimitGate, workerBlacklist, taskStorage, configuration);
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

            // A measured elapsed time can land on a whole number of milliseconds, and a whole number
            // renders identically in every culture — the assertion would then pass vacuously. Drive the
            // seam with a FIXED fractional value, using the completion site's own delegates. The id is
            // its own so this event can never be confused with the real completion below.
            var seamTaskId = Guid.NewGuid();
            executor.RegisterEvent(LogLevel.Debug, SeverityLevel.Information, task, null, null,
                (TaskId: seamTaskId, ElapsedMs: 12.5),
                static (l, a, _) => l.TaskCompleted(a.TaskId, a.ElapsedMs),
                static a => string.Create(CultureInfo.InvariantCulture,
                    $"Task with id {a.TaskId} was completed in {a.ElapsedMs} ms"));

            var expected = $"Task with id {seamTaskId} was completed in 12.5 ms";
            var fractional = await WaitForEventAsync(events,
                e => e.Message.StartsWith($"Task with id {seamTaskId} was completed in ", StringComparison.Ordinal));
            fractional.Message.ShouldBe(expected,
                "the event sentence is rendered with InvariantCulture, never the ambient decimal separator");

            // The real call site must produce that same shape: an invariant decimal and no trailing period.
            await executor.DoWork(task, CancellationToken.None);

            var prefix = $"Task with id {task.PersistenceId} was completed in ";
            var published = await WaitForEventAsync(events,
                e => e.Message.StartsWith(prefix, StringComparison.Ordinal));

            published.Message.ShouldMatch("^Task with id [0-9a-f-]{36} was completed in [0-9]+(\\.[0-9]+)? ms$");
            published.Message.ShouldNotEndWith(".", "event messages are fragments, with no trailing period");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Should_report_a_user_cancel_as_a_warning_when_the_task_id_is_blacklisted()
    {
        var logger    = new RecordingLogger();
        var blacklist = new WorkerBlacklist();
        var storage   = new Mock<ITaskStorage>();
        var taskId    = Guid.NewGuid();

        // The user cancels WHILE the handler runs: blacklisting up front would drop the delivery at the
        // entry check instead of reaching the classification under test.
        var task = HandlerExecutor(new CancellingProbeHandler(() => blacklist.Add(taskId)), taskId);
        var (executor, events) = CreateSubscribedExecutor(logger, workerBlacklist: blacklist,
            taskStorage: storage.Object, configuration: CapturingConfiguration());

        await executor.DoWork(task, CancellationToken.None);

        storage.Verify(s => s.SetCancelledByUser(taskId, AuditLevel.Full), Times.Once);
        storage.Verify(s => s.SetCancelledByService(It.IsAny<Guid>(), It.IsAny<Exception>(), It.IsAny<AuditLevel>()),
            Times.Never);

        var expected = $"Task with id {taskId} was cancelled by the user";

        var logEntry = SingleEntry(logger, e => e.Message == expected);
        logEntry.Level.ShouldBe(LogLevel.Warning);
        logEntry.Properties["TaskId"].ShouldBe(taskId);

        var published = await WaitForEventAsync(events, e => e.Message == expected);
        published.Severity.ShouldBe(nameof(SeverityLevel.Warning));
        published.ExecutionLogs.ShouldNotBeNull();
        published.ExecutionLogs.ShouldContain(l => l.Message == CancellingProbeHandler.LoggedLine,
            "with log capture on, the cancellation event must carry the handler's own logs");
    }

    [Fact]
    public async Task Should_report_a_service_stop_as_a_warning_when_the_task_id_is_not_blacklisted()
    {
        var logger    = new RecordingLogger();
        var blacklist = new WorkerBlacklist();
        var storage   = new Mock<ITaskStorage>();
        var taskId    = Guid.NewGuid();

        using var serviceCts = new CancellationTokenSource();

        // The service stops WHILE the handler runs, and nobody cancelled this task: the same
        // OperationCanceledException must classify as ServiceStopped, not as a user cancel.
        var task = HandlerExecutor(new CancellingProbeHandler(serviceCts.Cancel), taskId);
        var (executor, events) = CreateSubscribedExecutor(logger, workerBlacklist: blacklist,
            taskStorage: storage.Object, configuration: CapturingConfiguration());

        await executor.DoWork(task, serviceCts.Token);

        storage.Verify(s => s.SetCancelledByService(taskId, It.IsAny<OperationCanceledException>(), AuditLevel.Full),
            Times.Once);
        storage.Verify(s => s.SetCancelledByUser(It.IsAny<Guid>(), It.IsAny<AuditLevel>()), Times.Never);

        var expected = $"Task with id {taskId} was cancelled by service while stopping";

        var logEntry = SingleEntry(logger, e => e.Message == expected);
        logEntry.Level.ShouldBe(LogLevel.Warning);
        logEntry.Properties["TaskId"].ShouldBe(taskId);

        var published = await WaitForEventAsync(events, e => e.Message == expected);
        published.Severity.ShouldBe(nameof(SeverityLevel.Warning));
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
