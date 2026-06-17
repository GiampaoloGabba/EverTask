using EverTask.Dispatcher;
using EverTask.Logger;
using EverTask.Scheduler.Recurring;
using EverTask.Storage;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace EverTask.Tests;

// Top-level so Type.GetType(AssemblyQualifiedName) resolves it cleanly during recovery deserialization.
public record RecoveryFailProbeTask : IEverTask;

/// <summary>
/// P-E.1 / L18: a persistent re-dispatch failure during startup recovery used to be logged and the row
/// left untouched — so it was retried at every restart with no poison, while the summary logged
/// "Completed processing N pending tasks", masking a constant error as success. The fix counts the
/// failures durably and poisons the task (marks Failed) after a limit, and the summary reports failures.
/// [UNIT-necessario: drives ProcessPendingAsync directly with a stub storage + a re-dispatch that always throws.]
/// </summary>
public class WorkerServiceRecoveryPoisonTests
{
    private sealed class RecordingLogger<T> : IEverTaskLogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception)));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public async Task Should_poison_persistently_failing_recovered_task_and_not_log_false_success()
    {
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(RecoveryFailProbeTask).AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new RecoveryFailProbeTask()),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var storage = new Mock<ITaskStorage>();
        // First page returns the row until it is poisoned; subsequent pages (cursor set) are empty.
        storage.Setup(s => s.RetrievePending(It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<int>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync((DateTimeOffset? last, Guid? id, int take, CancellationToken ct) =>
                   last == null && row.Status != QueuedTaskStatus.Failed ? new[] { row } : Array.Empty<QueuedTask>());
        storage.Setup(s => s.IncrementRecoveryFailure(row.Id, It.IsAny<CancellationToken>()))
               .ReturnsAsync(() =>
               {
                   row.RecoveryDispatchFailureCount = (row.RecoveryDispatchFailureCount ?? 0) + 1;
                   return row.RecoveryDispatchFailureCount.Value;
               });
        storage.Setup(s => s.SetStatus(row.Id, QueuedTaskStatus.Failed, It.IsAny<Exception?>(), It.IsAny<AuditLevel>(),
                   It.IsAny<double?>(), It.IsAny<CancellationToken>()))
               .Callback(() => row.Status = QueuedTaskStatus.Failed)
               .Returns(Task.CompletedTask);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var dispatchCount = 0;
        var dispatcher    = new Mock<ITaskDispatcherInternal>();
        dispatcher.Setup(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
                      It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
                      It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>()))
                  .Callback(() => Interlocked.Increment(ref dispatchCount))
                  .ThrowsAsync(new InvalidOperationException("re-dispatch boom"));

        var logger = new RecordingLogger<WorkerService>();

        var service = new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            dispatcher.Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskWorkerExecutor>().Object,
            logger)
        {
            MaxRecoveryDispatchAttempts = 2
        };

        // Restart #1: re-dispatch fails, count 1 < 2 → not poisoned, stays recoverable.
        await service.ProcessPendingAsync();
        row.Status.ShouldBe(QueuedTaskStatus.Queued, "after one failure the task must stay recoverable (transient)");

        // Restart #2: fails again, count 2 >= 2 → poisoned.
        await service.ProcessPendingAsync();
        row.Status.ShouldBe(QueuedTaskStatus.Failed,
            "a persistently failing re-dispatch must be poisoned after the attempt limit (L18)");

        // Restart #3: the poisoned row is no longer recoverable → never re-dispatched again.
        await service.ProcessPendingAsync();
        dispatchCount.ShouldBe(2, "the poisoned task must not be retried at every restart (L18)");

        // The failing runs must NOT be reported as success: no "Completed processing 1 pending tasks".
        logger.Entries.ShouldNotContain(
            e => e.Level == LogLevel.Information && e.Message.Contains("Completed processing 1 pending tasks"),
            "a run with a failed re-dispatch must not be logged as a success (L18 masking)");
        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("marked Failed"),
            "the recovery summary must surface the poisoned failure");
    }

    /// <summary>
    /// CU3/L44: a recurring row (IsRecurring=true, valid Request, NextRunUtc set) whose RecurringTask
    /// metadata JSON no longer deserializes used to be silently demoted to a one-shot at every restart:
    /// ExecuteDispatch(recurring=null, isRecovery:true) ran it once, recovery skipped UpdateTask so the
    /// row stayed recurring/recoverable, and it was re-executed once per restart forever, never poisoned
    /// (the L18 poison counter never fires because the one-shot dispatch SUCCEEDS). Corrupt metadata is
    /// not transient, so the fix poisons the row (marks Failed) immediately and never re-dispatches it.
    /// [UNIT-necessario: drives ProcessPendingAsync directly with a stub storage + a seeded corrupt row.]
    /// </summary>
    [Fact]
    public async Task Should_poison_recurring_task_with_corrupt_recurring_metadata_instead_of_reexecuting_forever()
    {
        var row = new QueuedTask
        {
            Id              = Guid.NewGuid(),
            Type            = typeof(RecoveryFailProbeTask).AssemblyQualifiedName!,
            Request         = JsonConvert.SerializeObject(new RecoveryFailProbeTask()),
            Handler         = "seeded-by-test",
            Status          = QueuedTaskStatus.Completed,      // recurring task between two runs
            IsRecurring     = true,
            RecurringTask   = "{ this-is-corrupt-recurring-metadata-json",  // fails RecurringTask deserialization
            NextRunUtc      = DateTimeOffset.UtcNow.AddMinutes(-1),
            CurrentRunCount = 1,
            CreatedAtUtc    = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var storage = new Mock<ITaskStorage>();
        // The row keeps coming back on the first page until it is poisoned (Failed).
        storage.Setup(s => s.RetrievePending(It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<int>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync((DateTimeOffset? last, Guid? id, int take, CancellationToken ct) =>
                   last == null && row.Status != QueuedTaskStatus.Failed ? new[] { row } : Array.Empty<QueuedTask>());
        storage.Setup(s => s.SetStatus(row.Id, QueuedTaskStatus.Failed, It.IsAny<Exception?>(), It.IsAny<AuditLevel>(),
                   It.IsAny<double?>(), It.IsAny<CancellationToken>()))
               .Callback(() => row.Status = QueuedTaskStatus.Failed)
               .Returns(Task.CompletedTask);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var dispatchCount = 0;
        var dispatcher    = new Mock<ITaskDispatcherInternal>();
        dispatcher.Setup(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
                      It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
                      It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>()))
                  .Callback(() => Interlocked.Increment(ref dispatchCount))
                  .ReturnsAsync(Guid.NewGuid());

        var logger = new RecordingLogger<WorkerService>();

        var service = new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            dispatcher.Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskWorkerExecutor>().Object,
            logger);

        // Restart #1: the corrupt recurring row must be poisoned, NOT dispatched as a one-shot.
        await service.ProcessPendingAsync();
        dispatchCount.ShouldBe(0,
            "a recurring row with corrupt metadata must NOT be silently re-dispatched as a one-shot (CU3/L44)");
        row.Status.ShouldBe(QueuedTaskStatus.Failed,
            "corrupt recurring metadata is not transient -> the row must be poisoned (marked Failed) immediately");

        // Restart #2: the poisoned row is no longer recoverable -> never re-executed again.
        await service.ProcessPendingAsync();
        dispatchCount.ShouldBe(0, "the poisoned recurring row must not be re-executed at the next restart (CU3/L44)");

        logger.Entries.ShouldContain(
            e => e.Level == LogLevel.Error && e.Message.Contains("corrupt recurring metadata"),
            "the recovery log must surface the corrupt recurring metadata poison");
    }

    /// <summary>
    /// B3 / F7: a recovered NON-recurring row whose Type is LOADABLE but whose persisted Request cannot be
    /// deserialized in this build (e.g. an unrecognized legacy format) must NOT be terminalized on the first
    /// restart. Today the deserialization throw leaves task=null and the row is marked Failed immediately —
    /// permanent loss of a task that is otherwise runnable and might heal (a serializer fix, a code change).
    /// The fix routes this case through the same BOUNDED recovery-failure counting as a transient
    /// re-dispatch failure: the row stays recoverable until the attempt limit, then is poisoned.
    /// [UNIT-necessario: a single deterministic poison row, same seam as the L18 poison test above.]
    /// </summary>
    [Fact]
    public async Task Should_not_terminalize_recovered_task_whose_loadable_type_has_unreadable_payload()
    {
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(RecoveryFailProbeTask).AssemblyQualifiedName!, // LOADABLE IEverTask type
            Request      = "{ this-is-not-valid-json-for-the-type",              // unreadable payload format
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.RetrievePending(It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<int>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync((DateTimeOffset? last, Guid? id, int take, CancellationToken ct) =>
                   last == null && row.Status != QueuedTaskStatus.Failed ? new[] { row } : Array.Empty<QueuedTask>());
        storage.Setup(s => s.IncrementRecoveryFailure(row.Id, It.IsAny<CancellationToken>()))
               .ReturnsAsync(() =>
               {
                   row.RecoveryDispatchFailureCount = (row.RecoveryDispatchFailureCount ?? 0) + 1;
                   return row.RecoveryDispatchFailureCount.Value;
               });
        storage.Setup(s => s.SetStatus(row.Id, QueuedTaskStatus.Failed, It.IsAny<Exception?>(), It.IsAny<AuditLevel>(),
                   It.IsAny<double?>(), It.IsAny<CancellationToken>()))
               .Callback(() => row.Status = QueuedTaskStatus.Failed)
               .Returns(Task.CompletedTask);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var dispatcher = new Mock<ITaskDispatcherInternal>();

        var logger = new RecordingLogger<WorkerService>();

        var service = new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            dispatcher.Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskWorkerExecutor>().Object,
            logger)
        {
            MaxRecoveryDispatchAttempts = 2
        };

        // Restart #1: a loadable-but-unreadable payload must NOT be terminalized — stays recoverable.
        await service.ProcessPendingAsync();
        row.Status.ShouldBe(QueuedTaskStatus.Queued,
            "a loadable type whose payload cannot be read must stay recoverable, not be marked Failed (B3/F7)");

        // Restart #2: still unreadable, count 2 >= 2 → bounded poison so it does not retry forever.
        await service.ProcessPendingAsync();
        row.Status.ShouldBe(QueuedTaskStatus.Failed,
            "an unreadable payload must be poisoned only after the attempt limit, not on the first restart (B3/F7)");

        // The task handler was never invoked (the payload never deserialized) — no false dispatch.
        dispatcher.Verify(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
            It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
            It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>()), Times.Never);
    }

    /// <summary>
    /// B3 counterpart: a row whose Type is NOT loadable (assembly/type gone, or genuine corruption) can
    /// NEVER run, so it must still be poisoned immediately — the B3 leniency applies only to a LOADABLE type
    /// with an unreadable payload, not to an unresolvable type.
    /// </summary>
    [Fact]
    public async Task Should_poison_immediately_when_recovered_task_type_is_not_loadable()
    {
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = "Totally.Bogus.Type, NoSuchAssembly",   // Type.GetType -> null (unloadable)
            Request      = "{}",
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.RetrievePending(It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<int>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync((DateTimeOffset? last, Guid? id, int take, CancellationToken ct) =>
                   last == null && row.Status != QueuedTaskStatus.Failed ? new[] { row } : Array.Empty<QueuedTask>());
        storage.Setup(s => s.SetStatus(row.Id, QueuedTaskStatus.Failed, It.IsAny<Exception?>(), It.IsAny<AuditLevel>(),
                   It.IsAny<double?>(), It.IsAny<CancellationToken>()))
               .Callback(() => row.Status = QueuedTaskStatus.Failed)
               .Returns(Task.CompletedTask);

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        var service = new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            new Mock<ITaskDispatcherInternal>().Object,
            new EverTaskServiceConfiguration(),
            new Mock<IEverTaskWorkerExecutor>().Object,
            new RecordingLogger<WorkerService>())
        {
            MaxRecoveryDispatchAttempts = 2
        };

        await service.ProcessPendingAsync();
        row.Status.ShouldBe(QueuedTaskStatus.Failed,
            "an unloadable type can never run, so it must be poisoned immediately (not granted B3 leniency)");
    }

    /// <summary>
    /// Recovery must re-dispatch a recovered task with its PERSISTED per-task audit level, not the
    /// global default. The re-dispatch used to omit the auditLevel argument, so ExecuteDispatch fell
    /// back to EverTaskServiceConfiguration.DefaultAuditLevel — a recovered task silently reverted to
    /// the global default after a restart (e.g. a per-dispatch Minimal task became Full-audited). The
    /// same loss surfaced as a flaky integration test whenever a coarse-clock cutoff tie let recovery
    /// win the delivery race against the live dispatch.
    /// [UNIT-necessario: drives ProcessPendingAsync directly and captures the audit level handed to ExecuteDispatch.]
    /// </summary>
    [Fact]
    public async Task Should_carry_persisted_audit_level_into_recovery_redispatch()
    {
        var row = new QueuedTask
        {
            Id           = Guid.NewGuid(),
            Type         = typeof(RecoveryFailProbeTask).AssemblyQualifiedName!,
            Request      = JsonConvert.SerializeObject(new RecoveryFailProbeTask()),
            Handler      = "seeded-by-test",
            Status       = QueuedTaskStatus.Queued,
            AuditLevel   = (int)AuditLevel.Minimal,                 // per-dispatch override on the persisted row
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)     // well before the dynamic recovery cutoff
        };

        var storage = new Mock<ITaskStorage>();
        storage.Setup(s => s.RetrievePending(It.IsAny<DateTimeOffset?>(), It.IsAny<Guid?>(), It.IsAny<int>(),
                   It.IsAny<CancellationToken>()))
               .ReturnsAsync((DateTimeOffset? last, Guid? id, int take, CancellationToken ct) =>
                   last == null ? new[] { row } : Array.Empty<QueuedTask>());

        var provider = new Mock<IServiceProvider>();
        provider.Setup(p => p.GetService(typeof(ITaskStorage))).Returns(storage.Object);
        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
        var scopeFactory = new Mock<IServiceScopeFactory>();
        scopeFactory.Setup(f => f.CreateScope()).Returns(scope.Object);

        AuditLevel? capturedAuditLevel = null;
        var dispatcher = new Mock<ITaskDispatcherInternal>();
        dispatcher.Setup(d => d.ExecuteDispatch(It.IsAny<IEverTask>(), It.IsAny<DateTimeOffset?>(),
                      It.IsAny<RecurringTask?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>(),
                      It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<AuditLevel?>(), It.IsAny<bool>()))
                  .Callback((IEverTask _, DateTimeOffset? _, RecurringTask? _, int? _, CancellationToken _,
                             Guid? _, string? _, AuditLevel? auditLevel, bool _) => capturedAuditLevel = auditLevel)
                  .ReturnsAsync(Guid.NewGuid());

        var service = new WorkerService(
            new Mock<IWorkerQueueManager>().Object,
            scopeFactory.Object,
            dispatcher.Object,
            new EverTaskServiceConfiguration(),   // global default is Full — the override must win
            new Mock<IEverTaskWorkerExecutor>().Object,
            new RecordingLogger<WorkerService>());

        await service.ProcessPendingAsync();

        capturedAuditLevel.ShouldBe(AuditLevel.Minimal,
            "recovery must re-dispatch with the persisted per-task audit level, not the global default");
    }
}
