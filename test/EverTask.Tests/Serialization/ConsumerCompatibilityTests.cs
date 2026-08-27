using System.Linq.Expressions;
using System.Reflection;
using EverTask.ConsumerCompatibility.Baseline;
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

    /// <summary>
    /// The handler the baseline fixture's probe task needs to be dispatchable. It lives here, in the test
    /// assembly, because the fixture must not reference anything but the baseline packages.
    /// </summary>
    public class BaselineProbeTaskHandler : EverTaskHandler<BaselineConsumer.ProbeTask>
    {
        public override Task Handle(BaselineConsumer.ProbeTask backgroundTask, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// X6: what makes the fixture a cross-version proof instead of a tautology is that its call sites were
    /// compiled against 3.11 while the assembly answering them is 4.0. Unasserted, a tree that forgot the
    /// version bump — or a baseline repacked from the current sources — would run the fixture against itself
    /// and still pass, so every test that uses it starts here.
    /// </summary>
    private static void AssertTheFixtureIsAMajorBehind()
    {
        var compiledAgainst = typeof(BaselineConsumer).Assembly
                                                      .GetReferencedAssemblies()
                                                      .Single(a => a.Name == "EverTask")
                                                      .Version.ShouldNotBeNull();
        var runningAgainst = typeof(ITaskStorage).Assembly.GetName().Version.ShouldNotBeNull();

        compiledAgainst.Major.ShouldBeLessThan(runningAgainst.Major,
            $"the fixture must be compiled against an older MAJOR than the assembly under test: it asks for "
            + $"{compiledAgainst} and got {runningAgainst}");
    }

    /// <summary>
    /// P6 / X6, the binary half: an assembly COMPILED against the packages master shipped before durable
    /// occurrences, executed here against the current ones.
    /// </summary>
    /// <remarks>
    /// The tests above recompile against the new sources, so they can only ever prove SOURCE compatibility —
    /// a member that merely grew an optional parameter keeps compiling and quietly changes its IL signature.
    /// The fixture's call sites were fixed at compile time against the old signatures, so this is the only
    /// place where such a change shows up, as the <see cref="MissingMethodException"/> a real consumer would
    /// get. Assertions are on the results; the proof is that none of the calls throws.
    /// </remarks>
    [Fact]
    public async Task An_assembly_compiled_against_the_baseline_still_binds_to_the_current_ones()
    {
        AssertTheFixtureIsAMajorBehind();

        BaselineConsumer.BuildEverySchedule();
        BaselineConsumer.ConstructEveryBuilderDirectly();
        BaselineConsumer.ImplementTheBuilderInterfaces();
        BaselineConsumer.ConstructRuntimeComponents();

        var (nextRun, interval, skipped) = BaselineConsumer.ComputeOccurrences();
        nextRun.ShouldBe(new DateTimeOffset(2026, 5, 1, 12, 10, 0, TimeSpan.Zero));
        interval.ShouldBe(TimeSpan.FromMinutes(10));
        skipped.ShouldBeGreaterThan(0, "the two-hour gap really was skipped forward");

        var (rowId, rowType) = BaselineConsumer.UsePublicRecords();
        rowId.ShouldNotBe(Guid.Empty);
        rowType.ShouldContain(nameof(BaselineConsumer.ProbeTask));

        (await BaselineConsumer.DriveTaskStorage()).ShouldBe(1);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(ConsumerCompatibilityTests).Assembly))
                .AddMemoryStorage();

        await using var provider = services.BuildServiceProvider();
        BaselineConsumer.ConstructHostedComponents(provider);
        await BaselineConsumer.UseDispatcher(provider.GetRequiredService<ITaskDispatcher>());
    }

    /// <summary>
    /// T3 / P6, the binary half for the schedule builders: an implementation of the builder interfaces
    /// compiled against the baseline still loads, and reaches the default bodies of the members added since.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>InTimeZone</c> was added to eight of these interfaces as a default interface member precisely so an
    /// application that implements them — wrapping the fluent API, or replacing it — would keep compiling, and
    /// every member added since (<c>OnMisfire</c>, <c>WithDurableOccurrences</c>, <c>BackfillFrom</c>,
    /// <c>UseOccurrenceProvider</c>) arrived the same way. The probes above build schedules with EverTask's own
    /// builders, which say nothing about that: it is the OUTSIDE implementation that a new abstract member
    /// would break, and only one compiled against the old metadata can show it. The CLR builds that type's
    /// interface map against today's interfaces, so constructing it is the assertion; the throws after it are
    /// the second half, that each declaration really has a reachable default body rather than a hole.
    /// </para>
    /// <para>
    /// <see cref="NotSupportedException"/> and not silence: a builder that accepted a zone and dropped it
    /// would run the schedule at the wrong hour instead of failing.
    /// </para>
    /// </remarks>
    [Fact]
    public void Builder_interfaces_implemented_against_the_baseline_still_load_and_reach_the_new_defaults()
    {
        AssertTheFixtureIsAMajorBehind();

        var builder = BaselineConsumer.ImplementTheBuilderInterfaces();
        var rome    = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");

        builder.Calls.ShouldContain("Schedule");
        builder.Calls.ShouldContain("OnDays(int[])", "the monthly day-list overload is part of the map too");

        Should.Throw<NotSupportedException>(() => ((IIntervalSchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IIntervalSchedulerBuilder)builder).InTimeZone("Europe/Rome"));
        Should.Throw<NotSupportedException>(() => ((IHourSchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IMinuteSchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IDailyTimeSchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IWeeklySchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IMonthlySchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IBuildableSchedulerBuilder)builder).InTimeZone(rome));
        Should.Throw<NotSupportedException>(() => ((IBuildableSchedulerBuilder)builder).InTimeZone("Europe/Rome"));

        // Every member added after the baseline, not only the zone ones: each is its own declaration, so each
        // is its own chance to have arrived abstract and broken the type load of an outside implementation.
        Should.Throw<NotSupportedException>(() => ((IIntervalSchedulerBuilder)builder).OnMisfire(_ => { }));
        Should.Throw<NotSupportedException>(() => ((IIntervalSchedulerBuilder)builder).WithDurableOccurrences());
        Should.Throw<NotSupportedException>(() => ((IIntervalSchedulerBuilder)builder).BackfillFrom(DateTimeOffset.UtcNow));
        Should.Throw<NotSupportedException>(
            () => ((IIntervalSchedulerBuilder)builder).UseOccurrenceProvider("business-days", "{\"v\":1}"));
    }

    /// <summary>
    /// C2 / P6, the binary half of the execution-context contract: handlers that an application compiled
    /// BEFORE the context existed are still executed, end to end, by the current worker.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetExecutionContext</c> arrived as a default interface member so a handler written against the bare
    /// <see cref="IEverTaskHandler{TTask}"/> would neither have to declare it nor be recompiled. The in-tree
    /// <c>RawInterfaceTaskHandler</c> covers the first half by compiling; only an assembly built against the
    /// old metadata covers the second. The CLR builds its interface map against TODAY's interface, so a member
    /// that had arrived abstract instead would fail the type load the moment the container resolves the
    /// handler, and the worker's injector — which calls through the interface — reaches a default body inside
    /// an assembly nobody rebuilt. That the injector lands on that slot at all is pinned separately, by
    /// <c>RawInterfaceContextTaskHandler</c>, which implements the member and records what it receives.
    /// </para>
    /// <para>
    /// Both shapes an application uses are here, because they break differently: the direct implementor
    /// through the interface map, the <see cref="EverTaskHandler{TTask}"/> subclass through the base class's
    /// own new members and the virtual slots it overrides.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Handlers_compiled_against_the_baseline_are_still_executed_end_to_end()
    {
        AssertTheFixtureIsAMajorBehind();

        var probe = new BaselineHandlerProbe();

        await CreateIsolatedHostAsync(
            configureEverTask: cfg => cfg.RegisterTasksFromAssembly(typeof(BaselineConsumer).Assembly),
            configureServices: services => services.AddSingleton(probe));

        var rawId  = await Dispatcher.Dispatch(new BaselineRawInterfaceTask());
        var baseId = await Dispatcher.Dispatch(new BaselineBaseClassTask());

        await TaskWaitHelper.WaitForConditionAsync(
            () => probe.Phases.Count(phase => phase.EndsWith("-OnCompleted", StringComparison.Ordinal)) == 2,
            TestEnvironment.GetTimeout(8000, 30000));

        // The context injection happens inside the execution core, before OnStarted, so an injection that
        // threw on a handler unaware of the context would end the delivery Failed with the exception on the
        // row — which is why both handlers record OnError too, instead of leaving a missing phase to explain.
        foreach (var taskId in new[] { rawId, baseId })
            (await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed)).Exception.ShouldBeNull();

        // The two deliveries run concurrently, so only each handler's own sequence is deterministic.
        probe.Phases.Where(phase => phase.StartsWith("raw-", StringComparison.Ordinal))
             .ShouldBe(["raw-OnStarted", "raw-Handle", "raw-OnCompleted"],
                 "the whole lifecycle of a handler compiled against the bare interface still runs");

        probe.Phases.Where(phase => phase.StartsWith("base-", StringComparison.Ordinal))
             .ShouldBe(["base-OnStarted", "base-Handle", "base-OnCompleted"],
                 "so does the lifecycle of one compiled against the previous base class");
    }
}
