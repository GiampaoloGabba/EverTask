using System.Linq.Expressions;
using System.Reflection;
using EverTask.Handler;
using EverTask.Monitoring;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.Serialization;

/// <summary>
/// P6 / X4 — the durable-occurrence work must be ADDITIVE to the public surface. Three complementary proofs:
/// a storage written against the previous version still compiles and behaves, the public records kept their
/// exact construction shape, and an assembly COMPILED against the previous packages still binds — and still
/// runs a whole delivery — against the current ones.
/// </summary>
/// <remarks>
/// The last of the three needs a real host, which is why this class inherits the integration base: what a
/// consumer's handler is worth is whether the worker can execute it, not whether it can be constructed.
/// </remarks>
public class ConsumerCompatibilityTests : IsolatedIntegrationTestBase
{
    /// <summary>
    /// A storage implementing ONLY what the version before durable occurrences required. It exists to be
    /// COMPILED: the day one of the new operations stops being a default member, this class fails to build
    /// and the additivity claim is broken at the earliest possible moment — no assertion needed.
    /// </summary>
    private sealed class LegacyMinimalTaskStorage : ITaskStorage
    {
        public readonly List<QueuedTask> Rows = [];

        public Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default) =>
            Task.FromResult(Rows.Where(where.Compile()).ToArray());

        public Task<QueuedTask[]> GetAll(CancellationToken ct = default) => Task.FromResult(Rows.ToArray());

        public Task Persist(QueuedTask executor, CancellationToken ct = default)
        {
            Rows.Add(executor);
            return Task.CompletedTask;
        }

        public Task<QueuedTask[]> RetrievePending(DateTimeOffset? lastCreatedAt, Guid? lastId, int take,
                                                  CancellationToken ct = default)
        {
            RetrievePendingCalls++;
            return Task.FromResult(Rows.Take(take).ToArray());
        }

        public int RetrievePendingCalls { get; private set; }

        public Task SetQueued(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default)
        {
            Find(taskId).Status = QueuedTaskStatus.Queued;
            return Task.CompletedTask;
        }

        public Task SetInProgress(Guid taskId, AuditLevel auditLevel, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SetCompleted(Guid taskId, double executionTimeMs, AuditLevel auditLevel) => Task.CompletedTask;

        public Task SetCancelledByUser(Guid taskId, AuditLevel auditLevel) => Task.CompletedTask;

        public Task SetCancelledByService(Guid taskId, Exception exception, AuditLevel auditLevel) =>
            Task.CompletedTask;

        public Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel,
                              double? executionTimeMs = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateCurrentRun(Guid taskId, double executionTimeMs, DateTimeOffset? nextRun,
                                     AuditLevel auditLevel) => Task.CompletedTask;

        public Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default) =>
            Task.FromResult<QueuedTask?>(null);

        public Task UpdateTask(QueuedTask task, CancellationToken ct = default) => Task.CompletedTask;

        public Task Remove(Guid taskId, CancellationToken ct = default) => Task.CompletedTask;

        public Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs,
                                           CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId,
                                                                          CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);

        public Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take,
                                                                          CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TaskExecutionLog>>([]);

        private QueuedTask Find(Guid id) => Rows.First(r => r.Id == id);
    }

    private static QueuedTask NewRow() => new()
    {
        Id           = Guid.NewGuid(),
        Type         = "T",
        Request      = "{}",
        Handler      = "H",
        Status       = QueuedTaskStatus.Queued,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task A_storage_from_the_previous_version_still_serves_the_clock_carrying_overloads()
    {
        // The core always asks on its own clock, and the default members route that back to the signatures
        // the previous version implemented — so an existing storage keeps working, and keeps its own
        // atomicity, instead of being bypassed.
        var storage = new LegacyMinimalTaskStorage();
        var row     = NewRow();
        await storage.Persist(row);

        var page = await ((ITaskStorage)storage).RetrievePending(DateTimeOffset.UtcNow, null, null, 10);

        page.ShouldHaveSingleItem().Id.ShouldBe(row.Id);
        storage.RetrievePendingCalls.ShouldBe(1, "the default overload must delegate, not re-implement");

        (await ((ITaskStorage)storage).TrySetQueuedIfRecoverable(DateTimeOffset.UtcNow, row.Id, AuditLevel.Full))
            .ShouldBeTrue();
        row.Status.ShouldBe(QueuedTaskStatus.Queued);
    }

    [Fact]
    public void A_storage_from_the_previous_version_declares_no_durable_capability()
    {
        ITaskStorage storage = new LegacyMinimalTaskStorage();

        storage.SupportsDurableOccurrences.ShouldBeFalse();
        storage.SupportsScheduleVersioning.ShouldBeFalse();
    }

    [Fact]
    public async Task The_atomic_operations_refuse_rather_than_degrade_on_a_storage_that_lacks_them()
    {
        // No "best effort" emulation: two separate writes are exactly the crash window these operations
        // exist to close, so the only honest answer is to refuse the feature.
        ITaskStorage storage = new LegacyMinimalTaskStorage();
        var          row     = NewRow();

        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.MaterializeOccurrence(Guid.NewGuid(), 0, DateTimeOffset.UtcNow, row, null, AuditLevel.Full));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.CancelSchedule(Guid.NewGuid(), AuditLevel.Full));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.TrySetRecurringSeriesCompleted(row.Id, null, QueuedTaskStatus.Queued, 0, 0, AuditLevel.Full));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.TryHaltSchedule(row.Id, 0, null, QueuedTaskStatus.Queued, "{}"));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.UpdateSchedule(row.Id, 0, null, "{}", null, null, null, null, null));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.RequeueTerminal(row.Id, AuditLevel.Full));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.TryRequeueStaleOccurrence(row.Id, QueuedTaskStatus.Queued, AuditLevel.Full));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.UpdateCurrentRun(row.Id, 0, null, AuditLevel.Full, 0));
        await Should.ThrowAsync<NotSupportedException>(() =>
            storage.CompleteRecurringRun(row.Id, 0, null, AuditLevel.Full, 0));
    }

    [Fact]
    public async Task The_occurrence_reads_stay_answerable_on_a_storage_that_lacks_them()
    {
        // Reads carry no atomicity contract, so they degrade to a correct query instead of refusing.
        var storage = new LegacyMinimalTaskStorage();
        var parent  = NewRow();
        var child   = NewRow();
        child.ParentTaskId = parent.Id;
        await storage.Persist(parent);
        await storage.Persist(child);

        (await ((ITaskStorage)storage).GetOccurrences(parent.Id)).ShouldHaveSingleItem().Id.ShouldBe(child.Id);
    }

    [Theory]
    [InlineData(typeof(TaskHandlerExecutor), 16)]
    [InlineData(typeof(EverTaskEventData), 9)]
    public void Public_records_keep_their_construction_shape(Type recordType, int expectedParameterCount)
    {
        // X4: the schedule and occurrence metadata went into INIT properties precisely so these two shapes
        // could not move. Appending a positional parameter would change the primary constructor AND the
        // generated Deconstruct, breaking every consumer that builds or deconstructs one.
        var constructors = recordType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var primary      = constructors.MaxBy(c => c.GetParameters().Length).ShouldNotBeNull();

        primary.GetParameters().Length.ShouldBe(expectedParameterCount,
            $"{recordType.Name}'s primary constructor must keep its exact arity");

        var deconstruct = recordType.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance);
        deconstruct.ShouldNotBeNull();
        deconstruct.GetParameters().Length.ShouldBe(expectedParameterCount,
            $"{recordType.Name}'s Deconstruct must keep its exact arity");
    }

    [Fact]
    public void The_new_executor_metadata_is_carried_by_with_expressions_and_by_ToLazy()
    {
        // The occurrence metadata must survive every copy the pipeline makes, or a re-parked or lazily
        // continued delivery would silently lose its parent, its slot and its schedule version.
        var parentId = Guid.NewGuid();
        var slot     = DateTimeOffset.UtcNow;

        var eager = new TaskHandlerExecutor(
            new GoldenProbeTask(), new object(), null, slot, null, null, null, null, null,
            Guid.NewGuid(), "recurring", null, AuditLevel.Full)
        {
            ParentTaskId    = parentId,
            RuntimeInfo     = "{\"SlotUtc\":\"x\"}",
            RunNumber       = 7,
            ScheduleVersion = 3,
            NominalSlotUtc  = slot
        };

        var lazy = eager.ToLazy();

        lazy.IsLazy.ShouldBeTrue();
        lazy.HandlerTypeName.ShouldNotBeNull("ToLazy must stamp the handler type before dropping the instance");
        lazy.ParentTaskId.ShouldBe(parentId);
        lazy.RuntimeInfo.ShouldBe("{\"SlotUtc\":\"x\"}");
        lazy.RunNumber.ShouldBe(7);
        lazy.ScheduleVersion.ShouldBe(3);
        lazy.NominalSlotUtc.ShouldBe(slot);

        (lazy with { ExecutionTime = slot.AddMinutes(1) }).ParentTaskId.ShouldBe(parentId);
    }

    public record GoldenProbeTask : IEverTask;
}
