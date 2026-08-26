using System.Collections.Concurrent;
using System.Threading.Channels;
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
/// connection) alive forever, one per dropped delivery. One of them covers the terminal rejection's
/// recurring branch, which is not about whether the scope is released but about WHEN: it has to happen
/// before the series schedules its next occurrence.
/// <para>
/// The last four walk the exits BEFORE the worker: a delivery the ENQUEUE boundary drops never becomes a
/// delivery at all, so <c>DoWork</c>'s finally is never entered and the same scope is stranded. Those drops
/// are ordinary operation, not an edge case — startup recovery racing a live dispatch is the very race the
/// delivery registry exists to stop, and it produces one refused eager executor every time it happens.
/// </para>
/// </summary>
public class EagerHandlerScopeReleaseTests : IsolatedIntegrationTestBase
{
    private readonly EagerScopeTestState _state = new();

    /// <summary>
    /// A host that resolves EVERY handler eagerly — so each delivery carries an owned scope — and re-parks
    /// with 50 ms granularity. The scheduler is the real one, wrapped so the tests can see WHEN a task was
    /// handed to it relative to the scope releases.
    /// </summary>
    private async Task<IHost> CreateEagerScopeHostAsync(int channelCapacity = 5, int maxDegreeOfParallelism = 3,
                                                        Action<EverTaskServiceBuilder>? configureBuilder = null) =>
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

                configureBuilder?.Invoke(b);
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
                                                           string? rateLimitKey = null,
                                                           string queueName = QueueNames.Default)
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
            queueName,
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

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_a_duplicate_enqueue_is_skipped()
    {
        // One consumer, held by the blocking handler for the whole test: the id under test stays registered
        // in the delivery registry, which is what makes every later enqueue of it a duplicate.
        await CreateEagerScopeHostAsync(maxDegreeOfParallelism: 1);

        var taskId = GuidGenerator.NewDatabaseFriendly();
        var queue  = (EverTask.Worker.WorkerQueue)WorkerQueue;

        var live = CreateEagerExecutor(new EagerScopeBlockingTask(0), taskId);
        await queue.Queue(live);
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        // Startup recovery whose page read raced this live dispatch: a SECOND eager executor of the same
        // row, refused at the write boundary. Ordinary operation — the recovery cutoff is a best-effort
        // first pass and the registry is the actual defense — so its executor is dropped on every restart
        // that overlaps a live delivery.
        var recovered = CreateEagerExecutor(new EagerScopeBlockingTask(1), taskId);

        _state.Created.ShouldBe(2, "one owned scope per executor built above");
        _state.Disposed.ShouldBe(0, "the live delivery is still executing inside its scope");

        await queue.QueueForRecovery(recovered);

        _state.Disposed.ShouldBe(1, "the refused enqueue is the last hand that ever holds that scope");

        // The same refusal through the other door: a live dispatch of an id already in flight, which the
        // queue manager reports as idempotent success and then drops.
        var redispatched = CreateEagerExecutor(new EagerScopeBlockingTask(2), taskId);

        (await Host!.Services.GetRequiredService<IWorkerQueueManager>().TryEnqueue(null, redispatched))
            .ShouldBeTrue("a delivery already in flight is idempotent success, not a failure");

        _state.Disposed.ShouldBe(2, "the manager is the last hand there: nothing retries that instance");

        _state.Gate.Release();

        _state.Executed.ShouldNotContain(1, "a duplicate enqueue must never produce a second execution");
        _state.Executed.ShouldNotContain(2, "nor must a duplicate live dispatch");

        await AssertEveryScopeReleasedAsync(3, "the live delivery releases its own through DoWorkCore");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_a_cancelled_delivery_is_never_enqueued()
    {
        await CreateEagerScopeHostAsync();

        var queue = (EverTask.Worker.WorkerQueue)WorkerQueue;

        // Cancelled between the dispatch that built the executor and the enqueue: the queue drops it at its
        // own blacklist check, before the registry and before storage.
        var blockingId  = GuidGenerator.NewDatabaseFriendly();
        var scheduledId = GuidGenerator.NewDatabaseFriendly();

        WorkerBlacklist.Add(blockingId);
        WorkerBlacklist.Add(scheduledId);

        var blocking  = CreateEagerExecutor(new EagerScopeBlockingTask(0), blockingId);
        var scheduled = CreateEagerExecutor(new EagerScopeBlockingTask(1), scheduledId);

        _state.Created.ShouldBe(2, "one owned scope per executor built above");

        // Both doors: the blocking enqueue a dispatch takes, and the non-blocking one a fired slot takes.
        await queue.Queue(blocking);
        (await queue.TryQueue(scheduled)).ShouldBe(EnqueueResult.Discarded);

        _state.Disposed.ShouldBe(2, "a cancelled delivery is dropped for good, at either door");
        _state.Executed.ShouldBeEmpty("a cancelled task must not execute");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_the_row_finished_before_the_enqueue()
    {
        await CreateEagerScopeHostAsync();

        var queue = (EverTask.Worker.WorkerQueue)WorkerQueue;

        // The row terminally finished after the recovery page read it, or after the scheduler registered its
        // slot: both enqueues transition CONDITIONALLY, and a refused transition drops the executor.
        var recovered = CreateEagerExecutor(new EagerScopeBlockingTask(0), GuidGenerator.NewDatabaseFriendly());
        var fired     = CreateEagerExecutor(new EagerScopeBlockingTask(1), GuidGenerator.NewDatabaseFriendly());

        foreach (var executor in new[] { recovered, fired })
        {
            var row = executor.ToQueuedTask(Clock.GetUtcNow());
            row.Status = QueuedTaskStatus.Completed;
            await Storage.Persist(row);
        }

        _state.Created.ShouldBe(2, "one owned scope per executor built above");

        await queue.QueueForRecovery(recovered);
        (await queue.TryQueueForRecovery(fired)).ShouldBe(EnqueueResult.Discarded);

        _state.Disposed.ShouldBe(2, "nothing is left to deliver a row that already finished");
        _state.Executed.ShouldBeEmpty("a task that already completed must not run again");
    }

    [Fact]
    public async Task Should_release_the_eager_handler_scope_when_the_channel_evicts_the_delivery()
    {
        // A Drop* full mode never rejects a write: it silently evicts another queued task, which is the one
        // shape where an executor already accepted by the queue is still never delivered.
        await CreateEagerScopeHostAsync(configureBuilder: b => b.AddQueue("evicting", q =>
        {
            q.SetMaxDegreeOfParallelism(1);
            q.SetChannelOptions(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        }));

        var queue = Host!.Services.GetRequiredService<IWorkerQueueManager>().GetQueue("evicting");

        // The consumer of that queue is held by the first task, so the channel really holds the second.
        await queue.Queue(CreateEagerExecutor(new EagerScopeBlockingTask(0),
            GuidGenerator.NewDatabaseFriendly(), queueName: "evicting"));
        (await _state.Entered.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeTrue();

        var evicted = CreateEagerExecutor(new EagerScopeBlockingTask(1), GuidGenerator.NewDatabaseFriendly(),
            queueName: "evicting");
        await queue.Queue(evicted);

        // Capacity 1: this write evicts the one above.
        await queue.Queue(CreateEagerExecutor(new EagerScopeBlockingTask(2),
            GuidGenerator.NewDatabaseFriendly(), queueName: "evicting"));

        // The eviction callback is synchronous, so the release it starts is not: wait for it rather than
        // reading a count that may be one instruction early.
        await TaskWaitHelper.WaitForConditionAsync(() => _state.Disposed >= 1, timeoutMs: 5000);

        _state.Disposed.ShouldBe(1, "only the evicted copy is gone; the other two are alive");
        _state.Executed.ShouldNotContain(1, "the evicted delivery never runs in this process");

        _state.Gate.Release();
        _state.Gate.Release();
    }
}
