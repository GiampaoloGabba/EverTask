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
}
