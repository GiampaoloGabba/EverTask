using System.Collections.Concurrent;
using EverTask.Configuration;
using EverTask.Handler;
using EverTask.Logger;
using EverTask.RateLimiting;
using EverTask.Scheduler;
using EverTask.Storage;
using EverTask.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EverTask.Tests.IntegrationTests;

/// <summary>
/// The eager handler EverTask resolves at dispatch lives in an EverTask-OWNED scope carried on the
/// executor (L27), and the delivery that consumes it is that scope's last owner: everything a delivery
/// can continue into — the next occurrence, a rate-limit re-park, a deferral — is a <c>ToLazy()</c> copy,
/// which drops the scope. These tests walk the exits of <c>DoWorkGuarded</c> that never reach
/// <c>DoWorkCore</c> nor the terminal rejection, where the release used to be missing: each one leaves a
/// handler and every scoped dependency built with it (in a real application, a DbContext and its pooled
/// connection) alive forever, one per dropped delivery. The last one covers the terminal rejection's
/// recurring branch, which is not about whether the scope is released but about WHEN: it has to happen
/// before the series schedules its next occurrence.
/// </summary>
public class EagerHandlerScopeReleaseTests : IsolatedIntegrationTestBase
{
    private readonly EagerScopeTestState _state = new();

    /// <summary>
    /// A host that resolves EVERY handler eagerly — so each delivery carries an owned scope — and re-parks
    /// with 50 ms granularity. The scheduler is the real one, wrapped so the tests can see WHEN a task was
    /// handed to it relative to the scope releases.
    /// </summary>
    private async Task<IHost> CreateEagerScopeHostAsync(int channelCapacity = 5, int maxDegreeOfParallelism = 3) =>
        await CreateIsolatedHostWithBuilderAsync(b =>
            {
                b.AddMemoryStorage();
                b.Services.AddSingleton(_state);

                // Scoped, like the DbContext it stands for: one instance per scope that resolves a handler.
                b.Services.AddScoped<EagerScopeProbe>();

                b.Services.Replace(ServiceDescriptor.Singleton<IScheduler>(sp => new SchedulingOrderRecorder(
                    new PeriodicTimerScheduler(
                        sp.GetRequiredService<IWorkerQueueManager>(),
                        sp.GetRequiredService<IEverTaskLogger<PeriodicTimerScheduler>>(),
                        TimeSpan.FromMilliseconds(50)),
                    _state)));
            },
            configureEverTask: cfg => cfg.SetChannelOptions(channelCapacity)
                                         .SetMaxDegreeOfParallelism(maxDegreeOfParallelism)
                                         .DisableLazyHandlerResolution());

    /// <summary>
    /// The REAL scheduler, with a note taken of every task handed to it and of how many scopes had been
    /// released by then. It observes and forwards: a scheduler that did anything else would not be running
    /// the code whose ordering is under test.
    /// </summary>
    private sealed class SchedulingOrderRecorder(PeriodicTimerScheduler inner, EagerScopeTestState state)
        : IScheduler, IDisposable
    {
        public void Schedule(TaskHandlerExecutor item, DateTimeOffset? nextRecurringRun = null)
        {
            state.RecordScheduled(item.PersistenceId);
            inner.Schedule(item, nextRecurringRun);
        }

        public bool TryUnschedule(Guid persistenceId) => inner.TryUnschedule(persistenceId);

        public bool TryUnschedule(Guid persistenceId, TaskHandlerExecutor expected) =>
            inner.TryUnschedule(persistenceId, expected);

        public bool IsScheduled(Guid persistenceId) => inner.IsScheduled(persistenceId);

        public bool SupportsScheduleInspection => inner.SupportsScheduleInspection;

        public void Dispose() => inner.Dispose();
    }

    /// <summary>
    /// Builds an eager executor the way <c>TaskHandlerWrapperImp</c> does — handler and callbacks resolved
    /// inside an EverTask-owned scope carried on the executor — so a test can hand the worker two deliveries
    /// of the SAME persistence id (the shape a startup recovery racing a live dispatch produces) with
    /// deterministic interleaving.
    /// </summary>
    private TaskHandlerExecutor CreateEagerExecutor<TTask>(TTask task, Guid persistenceId,
                                                           RateLimitPolicy? policy = null,
                                                           string? rateLimitKey = null)
        where TTask : IEverTask
    {
        var handlerScope = Host!.Services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var handler      = handlerScope.ServiceProvider.GetRequiredService<IEverTaskHandler<TTask>>();

        return new TaskHandlerExecutor(
            task,
            handler,
            handler.GetType().AssemblyQualifiedName,
            ExecutionTime: null,
            RecurringTask: null,
            (theTask, theToken) => handler.Handle((TTask)theTask, theToken),
            (id, exception, message) => handler.OnError(id, exception, message),
            handler.OnStarted,
            handler.OnCompleted,
            persistenceId,
            QueueNames.Default,
            TaskKey: null,
            AuditLevel.Full,
            policy,
            rateLimitKey,
            handlerScope);
    }

    /// <summary>
    /// Asserts that every scope the test built was released, and that the scenario really produced at
    /// least the deliveries it is about. The wait is only there to let the last release land; the
    /// assertion is what reports the real numbers when it never did.
    /// </summary>
    private async Task AssertEveryScopeReleasedAsync(int minimumScopes, string because)
    {
        try
        {
            await TaskWaitHelper.WaitForConditionAsync(
                () => _state.Disposed >= minimumScopes && _state.Disposed == _state.Created, timeoutMs: 5000);
        }
        catch (TimeoutException)
        {
            // Fall through: Shouldly reports created vs disposed, which says far more than the timeout.
        }

        _state.Created.ShouldBeGreaterThanOrEqualTo(minimumScopes);
        _state.Disposed.ShouldBe(_state.Created, because);
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_a_cancelled_delivery_is_dropped()
    {
        // One consumer: the blocker holds it while the second task waits IN THE CHANNEL, where the Cancel's
        // TryUnschedule cannot reach it (nothing is parked in the scheduler). The cancellation is therefore
        // applied by the worker's own blacklist check — the FIRST exit of DoWorkGuarded, before the gate.
        await CreateEagerScopeHostAsync(maxDegreeOfParallelism: 1);

        var cancellationsSignaled = new ConcurrentBag<Guid>();
        WorkerExecutor.TaskEventOccurredAsync += data =>
        {
            if (data.Message.Contains("signaled to be cancelled", StringComparison.Ordinal))
                cancellationsSignaled.Add(data.TaskId);

            return Task.CompletedTask;
        };

        await Dispatcher.Dispatch(new EagerScopeBlockingTask(0));
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        var cancelledId = await Dispatcher.Dispatch(new EagerScopeBlockingTask(1));
        await WaitForTaskStatusAsync(cancelledId, QueuedTaskStatus.Queued, timeoutMs: 5000);

        await Dispatcher.Cancel(cancelledId);

        _state.Created.ShouldBe(2, "one owned scope per eager dispatch");
        _state.Disposed.ShouldBe(0, "the blocker still executes inside its scope, the other never ran");

        // Free the consumer: it dequeues the cancelled delivery and drops it at the blacklist check.
        _state.Gate.Release();

        await TaskWaitHelper.WaitForConditionAsync(
            () => cancellationsSignaled.Contains(cancelledId), timeoutMs: 10000);

        _state.Executed.ShouldNotContain(1, "a cancelled task must not execute");

        await AssertEveryScopeReleasedAsync(2,
            "the dropped delivery owned the only reference to its scope: nothing else can ever release it");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_the_rate_limit_gate_defers_the_delivery()
    {
        await CreateEagerScopeHostAsync();

        using var deferrals = TaskWaitHelper.CreateDeferralCollector(WorkerExecutor);

        // The warm-up takes the only permit of the 2 s window and completes the ordinary way, so its own
        // scope is released by DoWorkCore's finally.
        var warmupId = await Dispatcher.Dispatch(new EagerScopeGatedTask("defer-scope-key", 0));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed, timeoutMs: 10000);

        // No budget left: the gate parks a ToLazy() COPY at the reserved slot and this delivery returns
        // without entering DoWorkCore — the eager scope it carries is left with no owner at all.
        var deferredId = await Dispatcher.Dispatch(new EagerScopeGatedTask("defer-scope-key", 1));
        await deferrals.WaitForTaskAsync(deferredId, timeoutMs: 10000);

        // The redelivery of the parked copy is LAZY: its handler is resolved in the worker's per-task scope.
        await WaitForTaskStatusAsync(deferredId, QueuedTaskStatus.Completed, timeoutMs: 20000);

        _state.Executed.Count(index => index == 1).ShouldBe(1, "the deferred task runs exactly once");

        await AssertEveryScopeReleasedAsync(3,
            "at least three scopes served this test — the warm-up, the deferred eager delivery and the lazy " +
            "redelivery that ran it — and the deferred one is the one that used to strand its own");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_a_gated_redelivery_is_reparked_in_flight()
    {
        await CreateEagerScopeHostAsync();

        // A re-park slot far enough away that the redelivery cannot land inside this test.
        var gate = (RateLimitGate)Host!.Services.GetRequiredService<IRateLimitGate>();
        gate.InFlightRedeliveryDelay = TimeSpan.FromSeconds(30);

        var taskId = GuidGenerator.NewDatabaseFriendly();
        var policy = new RateLimitPolicy(1, TimeSpan.FromMilliseconds(700)) { Burst = 1 };

        var original   = CreateEagerExecutor(new EagerScopeGatedBlockingTask("inflight-scope-key", 0), taskId,
            policy, "inflight-scope-key");
        var redelivery = CreateEagerExecutor(new EagerScopeGatedBlockingTask("inflight-scope-key", 1), taskId,
            policy, "inflight-scope-key");

        var originalDelivery = WorkerExecutor.DoWork(original, CancellationToken.None).AsTask();
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        _state.Created.ShouldBe(2, "one owned scope per executor built above");
        _state.Disposed.ShouldBe(0, "the original delivery is still executing inside its scope");

        // The gate re-parks a ToLazy() copy of the redelivery BEFORE touching any budget, and returns:
        // the executor handed in here is dead, together with the scope it carries.
        await WorkerExecutor.DoWork(redelivery, CancellationToken.None);

        _state.Disposed.ShouldBe(1, "the re-parked redelivery must release its own scope on the way out");
        _state.Executed.ShouldNotContain(1, "a redelivery must never execute while the original is in flight");

        _state.Gate.Release();
        await originalDelivery;

        await AssertEveryScopeReleasedAsync(2, "the original releases its scope through DoWorkCore's finally");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_before_a_rejected_occurrence_schedules_the_next()
    {
        await CreateEagerScopeHostAsync();

        // The warm-up takes the only permit of the 30 s window and completes the ordinary way.
        var warmupId = await Dispatcher.Dispatch(new EagerScopeRejectedTask("reject-scope-key", 0));
        await WaitForTaskStatusAsync(warmupId, QueuedTaskStatus.Completed, timeoutMs: 10000);
        await AssertEveryScopeReleasedAsync(1, "the warm-up releases its scope through DoWorkCore's finally");

        // The series' first occurrence finds no budget, and Discard rejects it terminally: the delivery
        // ends in the rejection path, which never enters DoWorkCore, and the series advances from there.
        var seriesId = await Dispatcher.Dispatch(new EagerScopeRejectedTask("reject-scope-key", 1),
            recurring => recurring.Schedule().Every(1).Seconds());

        // Two hand-offs to the scheduler: the dispatch parking the first occurrence, then the rejected
        // delivery scheduling the next one.
        await TaskWaitHelper.WaitForConditionAsync(
            () => _state.ScheduledFor(seriesId).Length >= 2, timeoutMs: 15000);

        _state.Executed.ShouldNotContain(1, "a terminally rejected occurrence never executes");

        var reschedule = _state.ScheduledFor(seriesId)[1];
        reschedule.DisposedSoFar.ShouldBe(2,
            "the rejected delivery's scope must be released BEFORE the next occurrence is scheduled: the " +
            "executor is dead the moment the occurrence is skipped, and DoWork's finally would only catch " +
            "it after the series has already moved on");

        await AssertEveryScopeReleasedAsync(2,
            "the rejected delivery owned the only reference to its scope; the next occurrence is a " +
            "ToLazy() copy, which drops it");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_a_duplicate_delivery_is_skipped()
    {
        await CreateEagerScopeHostAsync();

        // Same duplicate, without a rate-limit policy: the second delivery is skipped at the in-flight
        // guard itself (no gate to re-park it), the exit that only logs and returns.
        var taskId = GuidGenerator.NewDatabaseFriendly();

        var original  = CreateEagerExecutor(new EagerScopeBlockingTask(0), taskId);
        var duplicate = CreateEagerExecutor(new EagerScopeBlockingTask(1), taskId);

        var originalDelivery = WorkerExecutor.DoWork(original, CancellationToken.None).AsTask();
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        _state.Created.ShouldBe(2, "one owned scope per executor built above");
        _state.Disposed.ShouldBe(0, "the original delivery is still executing inside its scope");

        await WorkerExecutor.DoWork(duplicate, CancellationToken.None);

        _state.Disposed.ShouldBe(1, "the skipped duplicate must release its own scope on the way out");
        _state.Executed.ShouldNotContain(1, "the in-flight execution IS the task: the duplicate is skipped");

        _state.Gate.Release();
        await originalDelivery;

        await AssertEveryScopeReleasedAsync(2, "the original releases its scope through DoWorkCore's finally");
    }
}
