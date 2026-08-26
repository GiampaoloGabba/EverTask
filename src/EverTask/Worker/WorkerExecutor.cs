using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using EverTask.Configuration;
using EverTask.Logging;
using EverTask.RateLimiting;
using EverTask.Scheduler.Occurrences;

namespace EverTask.Worker;

public interface IEverTaskWorkerExecutor
{
    event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;
    internal ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken);

    /// <summary>
    /// True while at least one monitoring subscriber is attached. A caller outside a delivery renders its
    /// message itself, so it asks first instead of paying for a sentence nobody reads.
    /// </summary>
    internal bool HasEventSubscribers => false;

    /// <summary>
    /// Publishes a monitoring event for work that happens OUTSIDE a delivery — the occurrence materializer,
    /// which has a schedule row and a decision but no execution.
    /// </summary>
    /// <remarks>
    /// It does NOT log: the caller has already logged through its own <c>[LoggerMessage]</c> template, in its
    /// own category. That split is deliberate — the one thing that must never happen is a rendered sentence
    /// reaching a logger as if it were a template (#32), and a publish-only entry point cannot do it.
    /// </remarks>
    internal void PublishExternalEvent(TaskHandlerExecutor executor, SeverityLevel severity, string message,
                                       Exception? exception = null) { }
}

public class WorkerExecutor(
    IWorkerBlacklist workerBlacklist,
    EverTaskServiceConfiguration options,
    IServiceScopeFactory serviceScopeFactory,
    IScheduler scheduler,
    ICancellationSourceProvider cancellationSourceProvider,
    IEverTaskLogger<WorkerExecutor> logger,
    ILoggerFactory loggerFactory,
    IRateLimitGate? rateLimitGate,
    TaskDeliveryRegistry? deliveryRegistry,
    TimeProvider? timeProvider) : IEverTaskWorkerExecutor
{
    /// <summary>
    /// The pre-P9 constructor, kept as a real overload so an assembly compiled against the previous release
    /// still binds (P6/X6). The container picks the longer one, which is the only one that carries the clock.
    /// </summary>
    public WorkerExecutor(
        IWorkerBlacklist workerBlacklist,
        EverTaskServiceConfiguration options,
        IServiceScopeFactory serviceScopeFactory,
        IScheduler scheduler,
        ICancellationSourceProvider cancellationSourceProvider,
        IEverTaskLogger<WorkerExecutor> logger,
        ILoggerFactory loggerFactory,
        IRateLimitGate? rateLimitGate = null,
        TaskDeliveryRegistry? deliveryRegistry = null)
        : this(workerBlacklist, options, serviceScopeFactory, scheduler, cancellationSourceProvider, logger,
            loggerFactory, rateLimitGate, deliveryRegistry, null) { }

    // The scheduling clock (P9): every next-occurrence decision below reads it, so a test clock drives the
    // whole series. Retry delays deliberately stay on the real clock (IRetryPolicy owns its own waits).
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    // Resolved once, lazily: the evaluator is a singleton, and the worker only reaches the container
    // through the scope factory. Racing initializations are harmless — the value is the same instance.
    private IScheduleEvaluator? _evaluator;

    // Same lazy resolution, and for one more reason: the materializer asks the worker executor to publish
    // its monitoring events, so injecting it here would close a constructor cycle.
    private OccurrenceMaterializer? _materializer;
    private bool _materializerResolved;

    // Same lazy resolution again: the registry is a singleton, absent from a hand-wired provider, and it is
    // read on every delivery of a recurring task — so it is resolved once and kept.
    private ScheduleVersionRegistry? _scheduleVersions;
    private bool _scheduleVersionsResolved;

    private OccurrenceProviderRetryRegistry? _providerRetries;
    private bool _providerRetriesResolved;

    private OccurrenceMaterializer? Materializer
    {
        get
        {
            if (_materializerResolved)
                return _materializer;

            using var scope = serviceScopeFactory.CreateScope();
            _materializer         = scope.ServiceProvider.GetService<OccurrenceMaterializer>();
            _materializerResolved = true;

            return _materializer;
        }
    }

    private ScheduleVersionRegistry? ScheduleVersions
    {
        get
        {
            if (_scheduleVersionsResolved)
                return _scheduleVersions;

            using var scope = serviceScopeFactory.CreateScope();
            _scheduleVersions         = scope.ServiceProvider.GetService<ScheduleVersionRegistry>();
            _scheduleVersionsResolved = true;

            return _scheduleVersions;
        }
    }

    /// <summary>
    /// The consecutive provider failures this host holds per schedule, resolved only to FORGET a series that
    /// has ended — the same lifetime as its published version.
    /// </summary>
    private OccurrenceProviderRetryRegistry? ProviderRetries
    {
        get
        {
            if (_providerRetriesResolved)
                return _providerRetries;

            using var scope = serviceScopeFactory.CreateScope();
            _providerRetries         = scope.ServiceProvider.GetService<OccurrenceProviderRetryRegistry>();
            _providerRetriesResolved = true;

            return _providerRetries;
        }
    }

    private IScheduleEvaluator Evaluator
    {
        get
        {
            if (_evaluator != null)
                return _evaluator;

            using var scope = serviceScopeFactory.CreateScope();
            return _evaluator = scope.ServiceProvider.GetService<IScheduleEvaluator>() ?? ScheduleEvaluator.Default;
        }
    }

    // Performance optimization: Cache for event data to avoid repeated serialization
    private static readonly ConditionalWeakTable<IEverTask, string> TaskJsonCache = new();
    private static readonly ConcurrentDictionary<Type, string> TypeStringCache = new();

    // Performance optimization: Cache handler options to avoid runtime casts per execution.
    // Stores the RAW handler overrides (null = no override): the fallback chain
    // handler → queue → global is resolved per execution, NOT baked into the cache.
    private static readonly ConcurrentDictionary<Type, HandlerOptionsCache> HandlerOptionsInternalCache = new();

    // F23: the lifecycle MethodInfo (OnStarted/OnCompleted/OnError) are cached per handler type just
    // like OnRetry, so lazy-mode executions no longer pay a GetMethod lookup per task on the hot path.
    // The two injectors are compiled delegates rather than MethodInfo: both members are explicitly
    // implemented on IEverTaskHandler<T>, so every execution used to pay an interface scan plus a
    // reflective Invoke to hand the handler its log capture.
    private record HandlerOptionsCache(
        IRetryPolicy? RetryPolicy,
        TimeSpan? Timeout,
        MethodInfo? OnRetryMethod,
        MethodInfo? OnStartedMethod,
        MethodInfo? OnCompletedMethod,
        MethodInfo? OnErrorMethod,
        Action<object, ITaskLogCapture>? SetLogCapture,
        Action<object, ITaskExecutionContext>? SetExecutionContext);

    // Test seam (F23): counts per-type reflection resolutions. The factory runs once per handler type
    // (GetOrAdd), so a single resolution across many lazy executions of the same type proves the cache
    // hits. Keyed per type so the deterministic gate is immune to other handler types resolved
    // concurrently elsewhere in the process. Not part of any production code path.
    internal static readonly ConcurrentDictionary<Type, int> LifecycleResolutionsByType = new();

    internal static int GetLifecycleResolutionCount(Type handlerType) =>
        LifecycleResolutionsByType.GetValueOrDefault(handlerType);

    private static HandlerOptionsCache ResolveHandlerOptions(Type _, object handlerInstance)
    {
        var type = handlerInstance.GetType();
        LifecycleResolutionsByType.AddOrUpdate(type, 1, static (_, count) => count + 1);

        // Resolve every per-type MethodInfo once and cache it together.
        var onRetryMethod = type.GetMethod(
            nameof(IEverTaskHandler<IEverTask>.OnRetry),
            BindingFlags.Public | BindingFlags.Instance);
        var onStartedMethod   = type.GetMethod("OnStarted");
        var onCompletedMethod = type.GetMethod("OnCompleted");
        var onErrorMethod     = type.GetMethod("OnError");

        // A handler closing IEverTaskHandler<> over two task types has two slots for these members; the
        // first one has always been the one the worker injects through, and it is the same instance state
        // either way (EverTaskHandler<T> closes the interface once).
        var handlerInterface = Array.Find(type.GetInterfaces(),
            i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEverTaskHandler<>));

        var setLogCapture = BuildInjector<ITaskLogCapture>(
            handlerInterface, nameof(IEverTaskHandler<IEverTask>.SetLogCapture));
        var setExecutionContext = BuildInjector<ITaskExecutionContext>(
            handlerInterface, nameof(IEverTaskHandler<IEverTask>.SetExecutionContext));

        // Cast only once per handler type (first time): cache the RAW overrides
        return handlerInstance is IEverTaskHandlerOptions handlerOpts
                   ? new HandlerOptionsCache(handlerOpts.RetryPolicy, handlerOpts.Timeout,
                       onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod,
                       setLogCapture, setExecutionContext)
                   : new HandlerOptionsCache(null, null,
                       onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod,
                       setLogCapture, setExecutionContext);
    }

    /// <summary>
    /// Compiles <c>(handler, value) =&gt; ((IEverTaskHandler&lt;T&gt;)handler).Method(value)</c> once per handler
    /// type. The call goes through the interface, so an explicit implementation and the interface's own default
    /// body are both reached — which a lookup on the concrete type would miss.
    /// </summary>
    private static Action<object, TValue>? BuildInjector<TValue>(Type? handlerInterface, string methodName)
    {
        var method = handlerInterface?.GetMethod(methodName, [typeof(TValue)]);
        if (method == null)
            return null;

        var handlerParam = Expression.Parameter(typeof(object), "handler");
        var valueParam   = Expression.Parameter(typeof(TValue), "value");

        var call = Expression.Call(Expression.Convert(handlerParam, handlerInterface!), method, valueParam);

        return Expression.Lambda<Action<object, TValue>>(call, handlerParam, valueParam).Compile();
    }

    // GetOrAdd is idempotent, so callers may populate the cache in any order (ExecuteTask or the
    // Get*Callback methods, whichever runs first for a given delivery).
    private static HandlerOptionsCache GetHandlerOptions(object handler) =>
        HandlerOptionsInternalCache.GetOrAdd(handler.GetType(), ResolveHandlerOptions, handler);

    /// <summary>
    /// Outcome of a task execution: elapsed time plus the gate result of a retry attempt that
    /// was deferred by the rate limiter (null when the execution ran to completion/failure).
    /// </summary>
    private readonly record struct TaskExecutionResult(double ExecutionTimeMs, RateLimitGateResult? RetryDeferral)
    {
        public bool Deferred => RetryDeferral is { Outcome: RateLimitGateOutcome.Deferred };
    }

    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;

    // PersistenceIds currently executing in this process. Last line of defense against
    // double execution when the same persisted task is delivered twice (e.g. startup
    // recovery racing a live dispatch): the second delivery is skipped while the first runs.
    private readonly ConcurrentDictionary<Guid, byte> _inFlightTasks = new();

    // In-memory run counter for recurring series when NO storage is registered: storage is the normal
    // source of CurrentRunCount, but without it a recurring series must still advance and honor
    // MaxRuns/RunUntil instead of dying after one run (F18). Entries are dropped when the series ends.
    private readonly ConcurrentDictionary<Guid, int> _inMemoryRunCounts = new();


    /// <summary>
    /// One delivery's claim on the EAGER handler EverTask resolved for it (L27): the executor carries an
    /// EverTask-OWNED scope holding that handler and every scoped dependency built with it, and this
    /// delivery is its last owner — whatever the delivery continues into (a re-park, a deferral, the next
    /// occurrence) is a <c>ToLazy()</c> copy, which drops the scope.
    /// </summary>
    /// <remarks>
    /// Release happens exactly ONCE, whichever exit the delivery takes: the ordered call sites (before the
    /// next occurrence is scheduled) keep their position, and <c>DoWork</c>'s finally covers every other
    /// exit as a no-op for them. Not thread-safe, and it does not need to be: the call sites are the
    /// sequential steps of one delivery.
    /// </remarks>
    private sealed class EagerHandlerOwnership(WorkerExecutor executor, TaskHandlerExecutor task)
    {
        private bool _released;

        public async ValueTask ReleaseAsync()
        {
            // Lazy-mode handlers belong to the worker's per-task scope and are released with it.
            if (_released || task.IsLazy)
                return;

            _released = true;

            // Disposing only the handler instance would strand the scope, its scoped dependencies and
            // whatever they hold (a DbContext and its pooled connection). An executor built without a
            // scope — never by this library, only by a caller constructing the public record itself —
            // still gets its handler disposed.
            if (task.HandlerScope != null)
                await executor.ExecuteDisposeHandlerScope(task.HandlerScope).ConfigureAwait(false);
            else
                await executor.ExecuteDisposeHandler(task.Handler!).ConfigureAwait(false);
        }
    }

    public async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        var eagerHandler = new EagerHandlerOwnership(this, task);

        try
        {
            await DoWorkGuarded(task, serviceToken, eagerHandler).ConfigureAwait(false);
        }
        finally
        {
            // Everything this finally does before the End is fallible — releasing a scope runs user
            // DisposeAsync code, and reaching the materializer resolves a service — so it all sits inside its
            // own try. The End below is not one of the things that may be skipped: a delivery whose id stays
            // registered can never be delivered again, and for an occurrence under the default budget of one
            // that stalls its whole schedule until a restart.
            try
            {
                // DoWorkCore clears the ambient context in its own finally; this covers the path where the
                // whole delivery completed synchronously (nothing ever suspended, so the value is still on
                // this flow) and a post-execution step threw before that line. Writing the value it already
                // has costs nothing.
                AmbientTaskExecutionContextAccessor.Set(null);

                // THE single release of this delivery's eager handler, on the same principle as the End
                // below: it covers every exit path of DoWorkGuarded with no per-path enumeration — the two
                // blacklist drops, the rate-limit deferral, the in-flight re-park, the duplicate-delivery
                // skip and a gate wait cancelled by shutdown all end the delivery without ever reaching
                // DoWorkCore or the terminal rejection, which are the only two ordered release sites.
                await eagerHandler.ReleaseAsync().ConfigureAwait(false);

                // An occurrence that has ended kicks its schedule: the fast path back to the materializer, so
                // a serial catch-up moves to the next slot as soon as this one is over instead of waiting for
                // the operational retry. KickAsync is non-throwing by contract, but REACHING it is not —
                // resolving the materializer builds a scope and a service — which is why the try is around
                // the whole statement and not inside the kick.
                //
                // With THIS delivery's token, for the same reason the advance takes it: the kick re-plans the
                // schedule, and since phase 6 that grid may be an INextOccurrenceProvider doing real I/O.
                // Without it a calendar that never answers holds the consumer slot, this delivery's registry
                // entry — so the occurrence stays "in delivery" and its schedule's budget of one is spent for
                // ever — and the host's shutdown, none of which the kick is allowed to cost: nothing is
                // written by a plan that could not be computed, and the operational retry and startup
                // recovery both bring the schedule back.
                if (task.ParentTaskId is { } scheduleId && Materializer is { } materializer)
                    await materializer.KickAsync(scheduleId, serviceToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.DeliveryEpilogueFailed(e, task.PersistenceId);
            }
            finally
            {
                // THE single End of this delivery (see TaskDeliveryRegistry's end discipline): the
                // LAST act of every consumed delivery, covering every exit path of DoWorkGuarded
                // (terminal completion, rate-limit deferral/rejection, retry re-park, blacklist
                // drop) with no per-path enumeration. Because nothing runs after this, a successor
                // delivery of the same id can only register after it — no delivery can ever release
                // a successor's registration.
                deliveryRegistry?.End(task.PersistenceId);
            }
        }
    }

    private async ValueTask DoWorkGuarded(TaskHandlerExecutor task, CancellationToken serviceToken,
                                          EagerHandlerOwnership eagerHandler)
    {
        // Blacklist check hoisted BEFORE the rate-limit gate: a cancelled task must be discarded
        // without burning rate-limit tokens (and without entering the execution path)
        if (IsTaskBlacklisted(task))
            return;

        // A delivery of a definition a reschedule has already replaced (S4). Right after the blacklist check
        // and before anything else touches it: the scheduler drops a registration the moment it is replaced,
        // but a delivery already handed to a worker queue is past its reach, and running it would execute the
        // schedule the caller has just changed.
        if (IsSupersededSchedule(task))
            return;

        // A delivery that exists only to ask the grid again: an occurrence provider could not answer, so
        // nothing was decided and nothing was written (V4). It re-reads the ROW and re-runs the decision it
        // could not make; running the handler here would execute a slot nobody has chosen yet.
        if (task.IsScheduleRetry)
        {
            await RetryScheduleDecisionAsync(task, serviceToken).ConfigureAwait(false);
            return;
        }

        // A DURABLE schedule row runs no handler at all: its slot firing means "materialize what is due".
        // Before the gate, deliberately — the rate limit belongs to the OCCURRENCES, per key, and letting a
        // schedule row consume the handler's budget would throttle the very series it is producing (M8).
        if (task.IsScheduleOnly)
        {
            await MaterializeScheduleAsync(task, serviceToken).ConfigureAwait(false);
            return;
        }

        if (rateLimitGate != null && task.RateLimitPolicy != null)
        {
            // A redelivery racing the still-unwinding original execution (e.g. a retry deferral
            // whose slot fired while the first delivery is still disposing) must NOT touch the
            // gate: redeeming the reservation and then hitting the in-flight guard would drop
            // the only live copy until restart. The gate re-parks it untouched instead.
            if (_inFlightTasks.ContainsKey(task.PersistenceId))
            {
                rateLimitGate.ReparkInFlightRedelivery(task);
                return;
            }

            // L2 parking-lot backpressure: gated consumers of a queue whose parked tasks hit
            // the cap pause (bounded) so no further tasks can park. Scoped to tasks WITH a
            // policy: tasks without one can never park, pausing them would only collapse
            // whole-queue throughput while the lot sits at cap. Cheap fast path under cap.
            await rateLimitGate.WaitForParkingCapacityAsync(task, serviceToken).ConfigureAwait(false);

            var gateResult = await rateLimitGate.TryPassAsync(task, serviceToken).ConfigureAwait(false);

            if (gateResult.EmitFailOpenEvent)
                RegisterFailOpenEvent(task, gateResult);

            if (gateResult.Outcome == RateLimitGateOutcome.Deferred)
            {
                // HARD RULE: the Deferred path NEVER enters DoWorkCore — its finally would
                // run QueueNextOccourrence (run-count corruption + lost occurrence). The gate
                // already re-parked the task in the scheduler; nothing was written to storage
                // and the status stays Queued (covered by startup recovery).
                // A Cancel racing this deferral is handled WITHOUT consuming the blacklist:
                // either the gate's set-then-check dropped the registration (epoch moved), or
                // the parked occurrence is discarded by the entry blacklist check at redelivery.
                RegisterDeferralEvent(task, gateResult);
                return;
            }

            // Gate waits (parking-lot pause + in-slot waits) can take seconds: a Cancel that
            // landed during them only reached the blacklist (the per-task token does not exist
            // yet) — honor it BEFORE applying the outcome. On Proceed the consumed budget
            // lapses; on Rejected this prevents clobbering the user's persisted Cancelled
            // status with Failed and firing a spurious OnError.
            if (IsTaskBlacklisted(task))
                return;

            if (gateResult.Outcome == RateLimitGateOutcome.Rejected)
            {
                // Terminal outcome (horizon exceeded / Discard / occurrence past RunUntil):
                // never enters DoWorkCore either
                await HandleRateLimitRejectionAsync(task, gateResult, eagerHandler, serviceToken)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (!_inFlightTasks.TryAdd(task.PersistenceId, 0))
        {
            if (rateLimitGate != null && task.RateLimitPolicy != null)
            {
                // Lost the pre-gate race by a hair (the original delivery was still unwinding
                // at TryAdd): same rule as above, re-park instead of dropping — the gate budget
                // was already redeemed, so the redelivery re-acquires at slot fire.
                rateLimitGate.ReparkInFlightRedelivery(task);
                return;
            }

            logger.DuplicateDeliverySkipped(task.PersistenceId);
            return;
        }

        try
        {
            await DoWorkCore(task, serviceToken, eagerHandler).ConfigureAwait(false);
        }
        finally
        {
            _inFlightTasks.TryRemove(task.PersistenceId, out _);
        }
    }

    /// <summary>
    /// Asks a schedule's grid again, after an <see cref="INextOccurrenceProvider"/> could not answer (V4).
    /// </summary>
    /// <remarks>
    /// It re-dispatches the ROW through the recovery path, which is the one that knows how to choose between
    /// the grace window and a skip-forward — and choosing between them is exactly what the provider left
    /// unanswered. Nothing is written here: a provider still down re-parks the schedule again, with the longer
    /// backoff its consecutive failures have earned, and a crash in between costs only the wait, since the row
    /// still carries the cursor it had.
    /// <para>
    /// With NO storage there is no row to re-read, and a series still runs (F18: the run counter is in
    /// memory). Everything the interrupted decision needs is then on the delivery itself — the slot it was
    /// about, the definition, the run number — so the retry re-runs that advance instead of the recovery
    /// decision. Abandoning it there is what left a storage-less provider series stopped for good.
    /// </para>
    /// </remarks>
    private async ValueTask RetryScheduleDecisionAsync(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        try
        {
            using var scope = serviceScopeFactory.CreateScope();

            var storage = scope.ServiceProvider.GetService<ITaskStorage>();

            if (storage == null)
            {
                // Back to the delivery the failed advance was: the slot it was about, and neither of the two
                // retry marks — what this advance parks is the next OCCURRENCE, and one still carrying them
                // would decide again instead of running the handler.
                var resumed = task with
                {
                    ExecutionTime        = task.ScheduleRetryFromUtc ?? task.ExecutionTime,
                    IsScheduleRetry      = false,
                    ScheduleRetryFromUtc = null
                };

                await QueueNextOccourrence(resumed, 0, null, serviceToken).ConfigureAwait(false);

                return;
            }

            if (scope.ServiceProvider.GetService<ITaskDispatcherInternal>() is not { } dispatcher)
            {
                logger.ScheduleRetryAbandoned(task.PersistenceId, "no dispatcher is registered");
                return;
            }

            var row = (await storage.Get(t => t.Id == task.PersistenceId, serviceToken).ConfigureAwait(false))
                .FirstOrDefault();

            // Gone, cancelled or finished under the wait: there is no decision left to retry.
            if (row is null || !row.IsRecurring || row.Status == QueuedTaskStatus.Cancelled ||
                row.NextRunUtc is null)
            {
                logger.ScheduleRetryAbandoned(task.PersistenceId, "the schedule is no longer waiting for a slot");
                return;
            }

            var recovered = RecoveredTaskFactory.FromRow(row,
                scope.ServiceProvider.GetService<OccurrenceProviderRegistry>());

            if (recovered.Task is null || recovered.Recurring is not { } definition)
            {
                // The VERDICT on a row nothing can rebuild belongs to startup recovery, which owns the bounded
                // retry and the terminal poison; here it is only the reason this retry stops.
                logger.ScheduleRetryAbandoned(task.PersistenceId, "the row cannot be rebuilt by this build");
                return;
            }

            await dispatcher.ExecuteDispatch(recovered.Task, row.NextRunUtc, definition, row.CurrentRunCount,
                    serviceToken, row.Id, row.TaskKey, recovered.AuditLevel, isRecovery: true,
                    recovered.RowMetadata)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
            // Shutdown: the row keeps its cursor and startup recovery asks the provider again.
        }
        catch (ScheduleDeferredByProviderException)
        {
            // The provider failed AGAIN: the dispatcher has already parked the next attempt and said so, as a
            // log line and a monitoring event. It is the normal answer to a calendar that is still down, and
            // reporting it as a retry that failed would blame this delivery for doing exactly its job.
        }
        catch (Exception e)
        {
            logger.ScheduleRetryFailed(e, task.PersistenceId);
        }
    }

    /// <summary>
    /// The whole delivery of a durable schedule row: hand it to the materializer, which decides which slots
    /// are owed and re-parks the row where its own decision says.
    /// </summary>
    /// <remarks>
    /// The failure path is the point of the wrapper. This delivery IS the schedule — the scheduler consumed
    /// the row's registration to make it — so an exception escaping here leaves the row parked nowhere, and
    /// nothing but a restart would bring it back. The materializer arms the same retry itself for a run that
    /// failed inside its per-schedule gate, including the runs it absorbed from a caller that found the gate
    /// taken; this is the backstop for anything that fails before or around it.
    /// </remarks>
    private async ValueTask MaterializeScheduleAsync(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        if (Materializer is not { } materializer)
        {
            logger.DurableScheduleWithoutMaterializer(task.PersistenceId);
            return;
        }

        try
        {
            await materializer.RunAsync(task.PersistenceId, task, serviceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (serviceToken.IsCancellationRequested)
        {
            // Shutdown: the row keeps its cursor and startup recovery parks it again.
        }
        catch (Exception ex)
        {
            logger.ScheduleMaterializationFailed(ex, task.PersistenceId);

            var retryAt = _timeProvider.GetUtcNow() + options.BacklogRetryInterval;

            // Conditional: this executor carries the definition THIS delivery was made from, and a reschedule
            // that has already parked a newer one owns the row's parking now.
            scheduler.TrySchedule(task.ToLazy() with { ExecutionTime = retryAt }, retryAt);
        }
    }

    private async ValueTask DoWorkCore(TaskHandlerExecutor task, CancellationToken serviceToken,
                                       EagerHandlerOwnership eagerHandler)
    {
        //Task storage could be a dbcontext wich is not thread safe.
        //So its safer to just use a new scope for each task
#pragma warning disable CA2007
        await using var scope       = serviceScopeFactory.CreateAsyncScope();
#pragma warning restore CA2007
        var taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        // Create log capture instance (always logs to ILogger, optionally persists)
        // Will be injected with proper handler type after handler resolution
        TaskLogCapture? logCapture = null;

        // Resolve handler (lazy or eager mode)
        object? handler = null!; // Will be assigned in both if and else branches

        // The identity of this delivery, published to the handler and to the ambient accessor once the
        // handler is resolved. Null until then: a delivery that cannot even resolve its handler never runs.
        TaskExecutionContext? executionContext = null;

        // Track execution time (initialized to 0, updated if task completes successfully)
        var executionTime = 0.0;

        // Set when a RETRY attempt was deferred by the rate limiter: the gate re-parked the
        // task, so the completion path AND the finally's post-execution logic must be skipped
        // (no storage write, no recurring re-scheduling — the parked occurrence is still alive)
        var rateLimitDeferred = false;

        // Set when a RECURRING occurrence completed successfully: the finally then writes the Completed
        // status AND the run-counter / next-run advance atomically (CU14/L29) instead of just advancing.
        var recurringRunCompleted = false;

        // Set (to the limiter's next available slot) when a RECURRING occurrence was SKIPPED without
        // executing (rate-limit horizon rejection): the finally then advances the schedule but does NOT
        // count it toward MaxRuns — only real executions consume the budget (a failed run still counts; a
        // skipped one does not). Presence of the slot IS the "was skipped" signal, and the skip-forward
        // jumps to it instead of grinding occurrence by occurrence (skip-ahead).
        DateTimeOffset? skippedOccurrenceSlot = null;

        try
        {
            serviceToken.ThrowIfCancellationRequested();

            // NOTE: the blacklist check happens in DoWork, before the rate-limit gate

            // Resolve handler instance
            if (task.IsLazy)
            {
                // Lazy mode: resolve fresh handler from DI
                try
                {
                    handler = task.GetOrResolveHandler(scope.ServiceProvider);

                    // Explicit guard: GetType().Name would otherwise run on every lazy execution —
                    // the generated method's own IsEnabled check happens only AFTER the arguments
                    // are evaluated (hence its SkipEnabledCheck).
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LazyHandlerResolved(handler.GetType().Name, task.PersistenceId);
                }
                catch (Exception ex)
                {
                    logger.HandlerResolutionFailed(ex, task.PersistenceId);

                    if (taskStorage != null)
                    {
                        await taskStorage.SetStatus(
                            task.PersistenceId,
                            QueuedTaskStatus.Failed,
                            ex,
                            task.AuditLevel,
                            executionTime,
                            serviceToken
                        ).ConfigureAwait(false);
                    }

                    return; // Cannot proceed without handler
                }
            }
            else
            {
                // Eager mode: use existing handler instance
                handler = task.Handler!; // Non-null assertion safe (validated at dispatch)
            }

            // Create log capture with proper handler type for ILogger<THandler>
            var handlerType = handler.GetType();
            logCapture = CreateLogCapture(handlerType, task.PersistenceId, scope.ServiceProvider);

            // Inject log capture and execution context into the handler BEFORE OnStarted, through the
            // delegates compiled once per handler type (both members are explicitly implemented, so they
            // are only reachable through the interface).
            var injectors = GetHandlerOptions(handler);
            injectors.SetLogCapture?.Invoke(handler, logCapture);

            executionContext = PublishExecutionContext(task, handler, injectors);

            // A cancel of the SCHEDULE that landed while this delivery was resolving its handler has ALREADY
            // written Cancelled on this occurrence's row (M15 cancels the pending occurrences together with
            // their schedule, in one transaction). SetInProgress below is unconditional, so without asking
            // again here it would write straight over that terminal status and the occurrence would run and
            // complete. Handler resolution is the wide window the two earlier checks cannot cover: they sit
            // before the rate-limit gate, and this one is the last question before the transition.
            // The entry never covers this delivery alone, so it is not consumed — it keeps covering the
            // siblings behind it.
            if (IsScheduleCancelled(task, out var cancelledSchedule))
            {
                RegisterOccurrenceOfCancelledSchedule(task, cancelledSchedule);
                return;
            }

            // Per-execution chatter for the LOG (Debug), but a first-class Information event for the
            // dashboard: the two levels are decoupled on purpose (see RegisterEvent).
            RegisterEvent(LogLevel.Debug, SeverityLevel.Information, task, null, null, task.PersistenceId,
                static (l, id, _) => l.TaskStarting(id),
                static id => string.Create(CultureInfo.InvariantCulture, $"Starting task with id {id}"));

            if (taskStorage != null)
                await taskStorage.SetInProgress(task.PersistenceId, task.AuditLevel, serviceToken)
                                 .ConfigureAwait(false);

            await ExecuteCallback(GetStartedCallback(task, handler), task, "Started").ConfigureAwait(false);

            var execution = await ExecuteTask(task, handler, executionContext, serviceToken)
                                .ConfigureAwait(false);
            executionTime = execution.ExecutionTimeMs;

            if (execution.Deferred)
            {
                // A retry attempt ran out of budget beyond MaxInSlotWait: the gate re-parked the
                // task at its reserved slot (attempt count restarts on redelivery — documented).
                // Storage keeps the InProgress status, which is recoverable, until the slot-fire
                // re-enqueue sets Queued.
                rateLimitDeferred = true;
                RegisterDeferralEvent(task, execution.RetryDeferral!.Value);
                return;
            }

            if (execution.RetryDeferral is { Outcome: RateLimitGateOutcome.Rejected } rejection)
            {
                if (task.RecurringTask != null)
                {
                    // Same semantics as the pre-execution rejection (HandleRateLimitRejectionAsync):
                    // the occurrence is SKIPPED with a warning — no Failed status, no OnError —
                    // and the series advances via the finally's QueueNextOccourrence. The status
                    // returns to Queued like any other parked occurrence (it was InProgress).
                    RegisterRateLimitSkippedOccurrence(task, rejection);

                    if (taskStorage != null)
                        await taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, serviceToken)
                                         .ConfigureAwait(false);

                    // Skipped occurrence: the finally must advance the schedule WITHOUT consuming MaxRuns,
                    // skipping ahead to the limiter's next available slot.
                    skippedOccurrenceSlot = rejection.SlotUtc;
                    return;
                }

                // One-shot: surface the typed exception through the standard failure path
                // (SetStatus Failed + OnError) handled by the catch below
                throw CreateRejectionException(task, rejection);
            }

            // A Cancel that landed AFTER the pre-gate blacklist check but BEFORE the per-task token was
            // created left no token to cancel, so the handler ran to completion on a fresh token. Re-check
            // the blacklist before writing the outcome so Completed never clobbers the user's persisted
            // Cancelled status (CU9/L46). The persisted Cancelled status is the durable terminal state.
            if (workerBlacklist.IsBlacklisted(task.PersistenceId))
            {
                workerBlacklist.Remove(task.PersistenceId);
                RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null, task.PersistenceId,
                    static (l, id, _) => l.TaskCancelledDuringExecution(id),
                    static id => string.Create(CultureInfo.InvariantCulture,
                        $"Task with id {id} was cancelled during execution; the completion is suppressed"));
                return;
            }

            // A recurring occurrence is marked Completed TOGETHER with its run-counter / next-run advance
            // by the finally's atomic CompleteRecurringRun (CU14/L29); a separate SetCompleted here would
            // re-open the crash window. Non-recurring tasks have no advance, so they complete here.
            if (taskStorage != null && task.RecurringTask == null)
                await taskStorage.SetCompleted(task.PersistenceId, executionTime, task.AuditLevel)
                                 .ConfigureAwait(false);

            recurringRunCompleted = task.RecurringTask != null;

            await ExecuteCallback(GetCompletedCallback(task, handler), task, "Completed").ConfigureAwait(false);

            // Get logs for completion event (if capture is enabled)
            var capturedLogs = logCapture.GetPersistedLogs();
            RegisterEvent(LogLevel.Debug, SeverityLevel.Information, task, null, capturedLogs,
                (TaskId: task.PersistenceId, ElapsedMs: executionTime),
                static (l, a, _) => l.TaskCompleted(a.TaskId, a.ElapsedMs),
                static a => string.Create(CultureInfo.InvariantCulture,
                    $"Task with id {a.TaskId} was completed in {a.ElapsedMs} ms"));
        }
        catch (Exception ex)
        {
            // Get logs for error event (if capture is enabled)
            var capturedLogs = logCapture?.GetPersistedLogs();
            await HandleExceptionAsync(ex, task, handler, capturedLogs, taskStorage, serviceToken)
                .ConfigureAwait(false);
        }
        finally
        {
            // Release the eager handler BEFORE recurring scheduling: the ordered site of this delivery
            // (DoWork's finally would otherwise catch it only after the next occurrence is scheduled).
            // Lazy-mode handlers are disposed by the worker's per-task scope instead.
            await eagerHandler.ReleaseAsync().ConfigureAwait(false);

            // Save persisted logs (AFTER handler disposal, BEFORE recurring scheduling)
            // Log save errors must NOT fail task execution.
            // Skipped on a rate-limit retry deferral: a deferral writes nothing to storage.
            // Log capture is per-delivery, so this attempt's captured logs are dropped from
            // persistence (they were still forwarded to ILogger) — the price of the no-write
            // invariant.
            if (logCapture != null && !rateLimitDeferred)
            {
                try
                {
                    // Only save if persistence is enabled and logs were persisted
                    if (options.PersistentLogger.Enabled && taskStorage != null)
                    {
                        var persistedLogs = logCapture.GetPersistedLogs();
                        if (persistedLogs.Count > 0)
                        {
                            // Use CancellationToken.None to ensure logs are saved even if task was cancelled
                            await taskStorage.SaveExecutionLogsAsync(task.PersistenceId, persistedLogs,
                                                 CancellationToken.None)
                                             .ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception logSaveEx)
                {
                    // Log the error but don't fail the task
                    logger.ExecutionLogsPersistFailed(logSaveEx, task.PersistenceId);
                }
            }

            cancellationSourceProvider.Delete(task.PersistenceId);

            // A rate-limit deferral must NOT schedule the next recurring occurrence: the
            // CURRENT occurrence is still parked in the scheduler (run-count integrity)
            if (!rateLimitDeferred)
                await QueueNextOccourrence(task, executionTime, taskStorage, serviceToken,
                        markCompleted: recurringRunCompleted, countsAsRun: skippedOccurrenceSlot == null,
                        skipAheadTo: skippedOccurrenceSlot)
                    .ConfigureAwait(false);

            // Last, so handler disposal and the lifecycle callbacks above still read the context of the
            // delivery they belong to.
            AmbientTaskExecutionContextAccessor.Set(null);
        }
    }

    /// <summary>
    /// Builds this delivery's execution context, hands it to the handler through the injector compiled once
    /// per handler type, and publishes it on the ambient accessor.
    /// </summary>
    /// <remarks>
    /// The ambient copy is what everything that is NOT the handler reads: an eager handler's own dependencies
    /// were built in the dispatcher's scope, long before this delivery existed, so handing them a scoped
    /// context would hand them nothing.
    /// </remarks>
    private TaskExecutionContext PublishExecutionContext(TaskHandlerExecutor task, object handler,
                                                         HandlerOptionsCache injectors)
    {
        var executionContext = TaskExecutionContext.Create(task, _timeProvider.GetUtcNow(),
            options.MisfireThreshold);

        AmbientTaskExecutionContextAccessor.Set(executionContext);
        injectors.SetExecutionContext?.Invoke(handler, executionContext);

        return executionContext;
    }

    /// <summary>
    /// Publishes the aggregated, machine-parseable deferral monitoring event when the gate
    /// signals it (per-deferral details are logged at Debug by the gate itself).
    /// </summary>
    private void RegisterDeferralEvent(TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        if (!gateResult.EmitDeferralEvent)
            return;

        // The rendered Message is a documented machine-parseable contract (docs/monitoring-events.md):
        // the "Rate limit deferred task <id>: key=… slotUtc=<O> policy=… deferredCount=…" shape and its
        // invariant formatting must not drift.
        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null,
            (TaskId: task.PersistenceId, Key: task.RateLimitKey!, gateResult.SlotUtc,
                TaskType: task.Task.GetType(), DeferredCount: gateResult.AggregatedDeferrals),
            static (l, a, _) => l.RateLimitDeferred(a.TaskId, a.Key, a.SlotUtc, a.TaskType, a.DeferredCount),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Rate limit deferred task {a.TaskId}: key={a.Key} slotUtc={a.SlotUtc:O} policy={a.TaskType} deferredCount={a.DeferredCount}"));
    }

    /// <summary>
    /// Publishes the mandatory tracked-keys fail-open monitoring event (L4): without it, a
    /// limiter silently executing unthrottled tasks under key-cardinality pressure would be
    /// invisible.
    /// </summary>
    private void RegisterFailOpenEvent(TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, null, null,
            (TaskId: task.PersistenceId, TaskType: task.Task.GetType(), gateResult.TotalFailOpenCount),
            static (l, a, _) => l.RateLimiterFailOpen(a.TaskId, a.TaskType, a.TotalFailOpenCount),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Rate limiter tracked-keys cap reached: new keys fail OPEN and execute unthrottled. Task {a.TaskId} (policy={a.TaskType}) totalFailOpenCount={a.TotalFailOpenCount}"));
    }

    /// <summary>
    /// Warns that the rate limiter skipped one occurrence of a recurring series (the series stays
    /// alive and the schedule advances).
    /// </summary>
    /// <remarks>
    /// The typed rejection exception is built INSIDE the L30 gate: on this path it is neither thrown
    /// nor persisted, it only enriches the log and the monitoring event — so an unconsumed warning
    /// allocates neither the exception nor its reason string.
    /// </remarks>
    private void RegisterRateLimitSkippedOccurrence(TaskHandlerExecutor task, RateLimitGateResult rejection)
    {
        if (!TryEnterEvent(LogLevel.Warning, out var logEnabled, out var publish))
            return;

        EmitEvent(SeverityLevel.Warning, task, CreateRejectionException(task, rejection), null,
            (TaskId: task.PersistenceId, Key: task.RateLimitKey!),
            static (l, a, e) => l.RateLimitSkippedOccurrence(e, a.TaskId, a.Key),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Rate limit skipped occurrence of recurring task {a.TaskId} (key {a.Key}): the series stays alive"),
            logEnabled, publish);
    }

    /// <summary>
    /// Builds the typed exception delivered to OnError (and persisted) for terminal rate-limit
    /// rejections.
    /// </summary>
    private static RateLimitRejectedException CreateRejectionException(
        TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        var reason = gateResult.RejectionKind switch
        {
            RateLimitRejectionKind.Discarded =>
                $"Rate limit discarded task {task.PersistenceId}: key={task.RateLimitKey} had no available budget " +
                "and the policy overflow behavior is Discard",
            RateLimitRejectionKind.OccurrencePastRunUntil =>
                $"Rate limit skipped occurrence of recurring task {task.PersistenceId}: key={task.RateLimitKey} " +
                $"reserved slot {gateResult.SlotUtc:O} falls past the series RunUntil",
            _ =>
                $"Rate limit rejected task {task.PersistenceId}: key={task.RateLimitKey} next available slot " +
                $"{gateResult.SlotUtc:O} exceeds the {task.RateLimitPolicy!.MaxReservationHorizon} reservation horizon"
        };

        return new RateLimitRejectedException(task.RateLimitKey!, gateResult.SlotUtc, task.RateLimitPolicy!, reason);
    }

    /// <summary>
    /// Applies a terminal rate-limit rejection: one-shot tasks are persisted as Failed — the
    /// only mandatory storage write of the design, otherwise the task would stay Queued and be
    /// re-rejected at every restart — with the typed <see cref="RateLimitRejectedException"/>
    /// delivered to the handler's OnError. Recurring tasks skip the occurrence through the
    /// normal next-occurrence path WITHOUT consuming the MaxRuns budget: the occurrence did not
    /// execute, so it only advances the schedule (like a downtime skip) — MaxRuns counts real
    /// executions only. The series stays alive and no callback is invoked.
    /// </summary>
    /// <remarks>
    /// Both outcomes end the delivery here, without ever entering <c>DoWorkCore</c>, so this method also
    /// owns the ordered release that method's finally would otherwise have done.
    /// </remarks>
    private async ValueTask HandleRateLimitRejectionAsync(TaskHandlerExecutor task, RateLimitGateResult gateResult,
                                                          EagerHandlerOwnership eagerHandler,
                                                          CancellationToken serviceToken)
    {
#pragma warning disable CA2007
        await using var scope = serviceScopeFactory.CreateAsyncScope();
#pragma warning restore CA2007
        var taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        try
        {
            if (task.RecurringTask != null)
            {
                RegisterRateLimitSkippedOccurrence(task, gateResult);

                // The ordered release, here and not in the finally below: this executor is dead the moment
                // the occurrence is skipped, and the next occurrence must not be scheduled while its scope
                // (and every scoped dependency in it) is still alive. Same position as DoWorkCore's.
                await eagerHandler.ReleaseAsync().ConfigureAwait(false);

                // Skipped occurrence: advance the schedule without consuming the MaxRuns budget, skipping
                // ahead to the limiter's next available slot instead of grinding occurrence by occurrence.
                await QueueNextOccourrence(task, 0, taskStorage, serviceToken, countsAsRun: false,
                        skipAheadTo: gateResult.SlotUtc)
                    .ConfigureAwait(false);
                return;
            }

            // One-shot rejection: the typed exception is persisted AND delivered to OnError, so it is built
            // unconditionally here (unlike the recurring skip above, where it only feeds the log/event).
            var exception = CreateRejectionException(task, gateResult);

            if (taskStorage != null)
            {
                await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, exception, task.AuditLevel,
                    null, serviceToken).ConfigureAwait(false);
            }

            // Resolve the handler once (rare terminal event, cost acceptable) to deliver OnError.
            // The callback instance is NOT an executing instance: rejection happens pre-execution.
            // For an eager executor this hands back the carried instance, released with its scope below;
            // a lazy one resolves into this method's own scope and is released with it.
            object? handler = null;
            try
            {
                handler = task.GetOrResolveHandler(scope.ServiceProvider);
            }
            catch (Exception resolveEx)
            {
                logger.RejectedTaskHandlerUnresolved(resolveEx, task.PersistenceId);
            }

            if (handler != null)
            {
                try
                {
                    // This path never enters DoWorkCore, so nothing else would give the handler the two
                    // per-delivery injections every callback is documented to have. Without them an OnError
                    // that compensates through Context throws (the getter refuses an uninjected context) and
                    // one that reports through Logger hits a null — and ExecuteCallback swallows both into a
                    // generic "callback override failed" event while the user's error handling silently never
                    // runs. The capture still forwards to ILogger; it is NOT persisted, because the only
                    // storage write a rejection cycle is allowed is the Failed status above.
                    var injectors = GetHandlerOptions(handler);
                    injectors.SetLogCapture?.Invoke(handler,
                        CreateLogCapture(handler.GetType(), task.PersistenceId, scope.ServiceProvider));

                    PublishExecutionContext(task, handler, injectors);

                    await ExecuteCallback(GetErrorCallback(task, handler), task, exception, exception.Message)
                        .ConfigureAwait(false);
                }
                finally
                {
                    AmbientTaskExecutionContextAccessor.Set(null);
                }
            }

            RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, exception, null, task.PersistenceId,
                static (l, id, e) => l.RateLimitRejected(e, id),
                static id => string.Create(CultureInfo.InvariantCulture,
                    $"Rate limit rejected task {id}: marked as Failed"));
        }
        finally
        {
            // The release of the one-shot branch, and the backstop of the recurring one, which already
            // released above (once per delivery: this is then a no-op). Same reason as DoWorkCore's: the
            // executor is dead once the rejection is applied — whatever the delivery continues into is a
            // ToLazy() copy, which drops the scope.
            await eagerHandler.ReleaseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves the configuration of the task's effective queue for the per-queue
    /// retry/timeout defaults. Unregistered custom queue names fall back to the default
    /// queue's configuration, mirroring the execution-time routing.
    /// </summary>
    private QueueConfiguration? ResolveQueueConfiguration(TaskHandlerExecutor task)
    {
        var queueName = task.QueueName ?? (task.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

        return options.Queues.TryGetValue(queueName, out var queueConfig)
                   ? queueConfig
                   : options.Queues.GetValueOrDefault(QueueNames.Default);
    }

    private bool IsTaskBlacklisted(TaskHandlerExecutor task)
    {
        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null, task.PersistenceId,
                static (l, id, _) => l.TaskCancellationSignaled(id),
                static id => string.Create(CultureInfo.InvariantCulture,
                    $"Task with id {id} is signaled to be cancelled and will not be executed"));

            // Consumed only when the entry covers THIS delivery alone. A durable schedule's own entry is
            // also the only thing covering the occurrences it already produced — they carry none of their
            // own — so a schedule row dropped from the queue must leave it standing, or the very next
            // occurrence out of the channel finds nothing blacklisted and runs after its series was
            // cancelled. It lapses on the blacklist's own TTL like any entry nobody consumes.
            if (!task.IsScheduleOnly)
                workerBlacklist.Remove(task.PersistenceId);

            return true;
        }

        // Cancelling a durable schedule cancels its pending occurrences in storage, but the ones ALREADY
        // parked in the scheduler, or already sitting in a channel, carry no blacklist entry of their own —
        // and the enqueue on their way in would write Queued over the Cancelled the cancel had just
        // persisted. The schedule's entry covers them: an occurrence of a cancelled schedule is cancelled.
        // The entry is NOT consumed here, because it has to keep covering the siblings behind this one.
        if (!IsScheduleCancelled(task, out var scheduleId))
            return false;

        RegisterOccurrenceOfCancelledSchedule(task, scheduleId);

        return true;
    }

    private void RegisterOccurrenceOfCancelledSchedule(TaskHandlerExecutor task, Guid scheduleId) =>
        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null,
            (OccurrenceId: task.PersistenceId, ScheduleId: scheduleId),
            static (l, a, _) => l.OccurrenceOfCancelledSchedule(a.OccurrenceId, a.ScheduleId),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Occurrence {a.OccurrenceId} belongs to cancelled schedule {a.ScheduleId} and will not be executed"));

    /// <summary>
    /// Whether this delivery is an occurrence whose durable SCHEDULE has been cancelled.
    /// </summary>
    /// <remarks>
    /// The entry is never consumed: one cancel covers every occurrence the schedule produced, and the first
    /// of them to ask must not answer for the rest.
    /// </remarks>
    private bool IsScheduleCancelled(TaskHandlerExecutor task, out Guid scheduleId)
    {
        scheduleId = task.ParentTaskId ?? Guid.Empty;

        return task.ParentTaskId.HasValue && workerBlacklist.IsBlacklisted(scheduleId);
    }

    /// <summary>
    /// Whether this delivery carries an INLINE schedule definition a reschedule has already replaced (S4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The contract is that a reschedule is immediate for occurrences that have not fired yet: the scheduler's
    /// registration is replaced latest-wins, so nothing parked survives. What the scheduler cannot reach is a
    /// delivery already written to a worker queue, and this is the only thing standing between it and a run of
    /// the definition the caller has just changed.
    /// </para>
    /// <para>
    /// The ABSENCE of an entry is not a version of zero: it is the absence of a lower bound, and nothing is
    /// dropped on it. That is what keeps an executor recovered at startup — a fresh process publishes nothing —
    /// from being read as stale and thrown away, which would silently stop every rescheduled series across a
    /// restart.
    /// </para>
    /// <para>
    /// DURABLE schedules are deliberately not dropped. Their delivery runs no handler at all: it means
    /// "materialize what is due", and the materializer re-reads the row, so an old version costs nothing —
    /// while dropping it would consume the registration that produced it and leave the row parked nowhere.
    /// </para>
    /// </remarks>
    private bool IsSupersededSchedule(TaskHandlerExecutor task)
    {
        if (task.RecurringTask is not { IsDurable: false } || ScheduleVersions is not { } versions)
            return false;

        if (!versions.TryGetLatest(task.PersistenceId, out var published) || task.ScheduleVersion >= published)
            return false;

        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null,
            (TaskId: task.PersistenceId, Delivered: task.ScheduleVersion, Published: published),
            static (l, a, _) => l.SupersededScheduleDelivery(a.TaskId, a.Delivered, a.Published),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Task with id {a.TaskId} carries schedule version {a.Delivered} and was superseded by version {a.Published}: the delivery is discarded"));

        return true;
    }

    private async Task<TaskExecutionResult> ExecuteTask(TaskHandlerExecutor task, object handler,
                                                        TaskExecutionContext? executionContext,
                                                        CancellationToken serviceToken)
    {
        serviceToken.ThrowIfCancellationRequested();

        var taskToken = cancellationSourceProvider.CreateToken(task.PersistenceId, serviceToken);

        // Performance optimization: Cache handler options to avoid repeated casts / reflection (F23)
        var handlerOptions = GetHandlerOptions(handler);

        // Resolution chain: handler override → queue default → global default. The queue is
        // the task's DECLARED queue (a FallbackToDefault reroute keeps the declared queue's
        // retry/timeout, consistent with rate limiting following the task type everywhere).
        var queueConfig = ResolveQueueConfiguration(task);
        var retryPolicy = handlerOptions.RetryPolicy ?? queueConfig?.DefaultRetryPolicy ?? options.DefaultRetryPolicy;
        var timeout     = handlerOptions.Timeout ?? queueConfig?.DefaultTimeout ?? options.DefaultTimeout;

        // WS4 retry throttling: closure state shared with the retry action below
        var                  attempt       = 0;
        RateLimitGateResult? retryDeferral = null;

        // Use GetTimestamp/GetElapsedTime to avoid Stopwatch allocation
        var startTime = Stopwatch.GetTimestamp();
        try
        {
            await DoExecute().ConfigureAwait(false);
        }
        finally
        {
            // OnRetry announces the attempt that is ABOUT to start (C1), and that retry can still never
            // start — a cancel inside the callback, one that lands during the retry delay, or the throttle
            // gate turning the attempt back before the handler. `attempt` only moves once an attempt has
            // been admitted INTO the handler, so it IS the last attempt that really ran: roll the
            // announcement back to it, or OnError would report an attempt that never ran. On every path
            // where the retry did start the two already agree, and the write is a no-op.
            if (attempt > 0)
                executionContext?.SetAttempt(attempt);
        }

        var elapsedTime = Stopwatch.GetElapsedTime(startTime);
        return new TaskExecutionResult(elapsedTime.TotalMilliseconds, retryDeferral);

        async Task DoExecute()
        {
            // Get or create handler callback
            Func<IEverTask, CancellationToken, Task> handlerCallback;
            if (task.HandlerCallback != null)
            {
                // Eager mode: use existing callback
                handlerCallback = task.HandlerCallback;
            }
            else
            {
                // Lazy mode: create callback from handler already resolved in DoWork
                // Use CreateHandlerCallback to avoid duplicate DI resolution (which would create
                // a second handler instance that gets disposed separately by the async scope)
                var (_, callback) = task.CreateHandlerCallback(handler);
                handlerCallback   = callback;
            }

            //Use WaitAsync for cancelling:
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#cancelling-uncancellable-operations
            await retryPolicy.Execute(
                action: async retryToken =>
                {
                    // The attempt this action stands for, 1-based. It is NOT committed to `attempt` yet:
                    // the throttle gate below can turn it back without ever reaching the handler, and the
                    // rollback in the finally above reads `attempt` as "the last attempt that really ran"
                    // (C1). Publishing here instead would leave OnError reporting an attempt whose only
                    // trace is a rejected gate pass.
                    var startingAttempt = attempt + 1;

                    // Retry throttling (BEFORE the timeout branch: the budget wait must never
                    // erode the per-attempt timeout). The FIRST attempt skips re-acquisition —
                    // the gate pass that admitted this delivery holds its budget. Retries of a
                    // ThrottleRetries policy re-acquire; a near slot is awaited in-slot by the
                    // gate, a far slot re-parks the task (Design A path) instead of surfacing a
                    // retryable exception, which would consume the shared retry budget and mark
                    // never-executed tasks Failed.
                    if (startingAttempt > 1
                        && task.RateLimitPolicy is { ThrottleRetries: true }
                        && rateLimitGate != null)
                    {
                        var gateResult = await rateLimitGate.TryPassAsync(task, retryToken).ConfigureAwait(false);
                        if (gateResult.Outcome != RateLimitGateOutcome.Proceed)
                        {
                            // Deferred: stop the retry loop without failing — the task was
                            // re-parked and the attempt sequence restarts on redelivery.
                            // Rejected (horizon/Discard): captured and turned into the typed
                            // terminal exception by DoWorkCore.
                            retryDeferral = gateResult;
                            return;
                        }
                    }

                    // Admitted: from here the handler IS entered, so this is the attempt the handler must
                    // read and the one OnError reports if it ends up being the last.
                    attempt = startingAttempt;
                    executionContext?.SetAttempt(attempt);

                    if (timeout.HasValue && timeout.Value > TimeSpan.Zero)
                    {
                        await ExecuteWithTimeout(
                            innerToken => handlerCallback.Invoke(task.Task, innerToken).WaitAsync(innerToken),
                            timeout.Value,
                            retryToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await handlerCallback.Invoke(task.Task, retryToken).WaitAsync(retryToken)
                                             .ConfigureAwait(false);
                    }
                },
                attemptLogger: logger,
                token: taskToken,
                onRetryCallback: async (attemptNumber, exception, delay) =>
                {
                    // OnRetry runs after the delay, immediately before the retry: the attempt the handler
                    // should see is the one about to start, which is one past the retry's own 1-based number
                    // (retry 1 starts attempt 2). The action above republishes the same value.
                    executionContext?.SetAttempt(attemptNumber + 1);

                    // Invoke handler's OnRetry method using cached MethodInfo
                    await InvokeOnRetryCallback(task, handler, handlerOptions.OnRetryMethod, attemptNumber, exception,
                            delay)
                        .ConfigureAwait(false);
                }
            ).ConfigureAwait(false);
        }
    }

    // CancelAfter rejects anything above the maximum timer duration (uint.MaxValue - 1 ms, ~49.7 days).
    // Same constant as Resilience.TaskDelayLimit in Abstractions (internal there, no IVT to this assembly).
    private static readonly TimeSpan MaxTimerDuration = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private static async Task ExecuteWithTimeout(Func<CancellationToken, Task> action, TimeSpan timeout,
                                          CancellationToken token)
    {
        // A configured timeout above the timer ceiling (handler override, queue or global default) is
        // indistinguishable from the ceiling in practice: clamp it instead of failing the task with an
        // infrastructure ArgumentOutOfRangeException. The ET0009 analyzer flags over-limit literals.
        if (timeout > MaxTimerDuration)
            timeout = MaxTimerDuration;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        var timeoutToken = timeoutCts.Token;
        try
        {
            await action(timeoutToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (token.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException();
        }
        finally
        {
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#always-dispose-cancellationtokensources-used-for-timeouts
            // CancelAsync, not Cancel: the synchronous Cancel runs every registered callback inline on
            // the thread completing the task (handler-registered continuations included), so a slow one
            // would stall this finally. The await still guarantees they finish before the using disposes.
            await timeoutCts.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ExecuteDisposeHandler(object handler)
    {
        if (handler is IAsyncDisposable asyncDisposable)
        {
            try
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);

                // Same explicit guard as the lazy-resolution site: the handler's simple name is
                // only worth computing once the level is known to be enabled.
                if (logger.IsEnabled(LogLevel.Debug))
                    logger.HandlerDisposed(handler.GetType().Name);
            }
            catch (Exception e)
            {
                if (logger.IsEnabled(LogLevel.Error))
                    logger.HandlerDisposeFailed(e, handler.GetType().Name);
            }
        }
    }

    // Disposes the EverTask-owned scope an eager handler was resolved into (L27), releasing the handler
    // instance(s) without pinning them in the root container. Disposal must never fail task execution.
    private async Task ExecuteDisposeHandlerScope(IAsyncDisposable handlerScope)
    {
        try
        {
            await handlerScope.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.HandlerScopeDisposeFailed(e);
        }
    }

    /// <summary>
    /// Gets the OnStarted callback from executor (eager mode) or extracts from handler (lazy mode)
    /// </summary>
    private static Func<Guid, ValueTask>? GetStartedCallback(TaskHandlerExecutor task, object handler)
    {
        // If executor has callback (eager mode), use it
        if (task.HandlerStartedCallback != null)
            return task.HandlerStartedCallback;

        // Lazy mode: read the MethodInfo from the per-type cache (resolved once, F23)
        var onStartedMethod = GetHandlerOptions(handler).OnStartedMethod;

        return onStartedMethod != null
                   ? persistenceId => (ValueTask)onStartedMethod.Invoke(handler, [persistenceId])!
                   : null;
    }

    /// <summary>
    /// Gets the OnCompleted callback from executor (eager mode) or extracts from handler (lazy mode)
    /// </summary>
    private static Func<Guid, ValueTask>? GetCompletedCallback(TaskHandlerExecutor task, object handler)
    {
        // If executor has callback (eager mode), use it
        if (task.HandlerCompletedCallback != null)
            return task.HandlerCompletedCallback;

        // Lazy mode: read the MethodInfo from the per-type cache (resolved once, F23)
        var onCompletedMethod = GetHandlerOptions(handler).OnCompletedMethod;

        return onCompletedMethod != null
                   ? persistenceId => (ValueTask)onCompletedMethod.Invoke(handler, [persistenceId])!
                   : null;
    }

    /// <summary>
    /// Gets the OnError callback from executor (eager mode) or extracts from handler (lazy mode)
    /// </summary>
    private static Func<Guid, Exception?, string, ValueTask>? GetErrorCallback(TaskHandlerExecutor task, object handler)
    {
        // If executor has callback (eager mode), use it
        if (task.HandlerErrorCallback != null)
            return task.HandlerErrorCallback;

        // Lazy mode: read the MethodInfo from the per-type cache (resolved once, F23)
        var onErrorMethod = GetHandlerOptions(handler).OnErrorMethod;

        return onErrorMethod != null
                   ? (persistenceId, exception, message) =>
                       (ValueTask)onErrorMethod.Invoke(handler, [persistenceId, exception, message])!
                   : null;
    }

    private async ValueTask ExecuteCallback(Func<Guid, ValueTask>? handler, TaskHandlerExecutor task,
                                            string callbackName)
    {
        if (handler == null) return;

        try
        {
            await handler.Invoke(task.PersistenceId).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RegisterCallbackFailure(task, callbackName, e);
        }
    }

    /// <summary>
    /// Reports a lifecycle callback override that threw. Shared by both <c>ExecuteCallback</c>
    /// overloads so the OnError report reads exactly like the OnStarted/OnCompleted ones.
    /// </summary>
    private void RegisterCallbackFailure(TaskHandlerExecutor task, string callbackName, Exception exception) =>
        RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, exception, null,
            (CallbackName: callbackName, TaskId: task.PersistenceId),
            static (l, a, e) => l.CallbackOverrideFailed(e, a.CallbackName, a.TaskId),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Error occurred executing the callback override {a.CallbackName} for task with id {a.TaskId}"));

    /// <summary>
    /// Invokes the OnRetry callback on the handler.
    /// Called by retry policy before each retry attempt.
    /// </summary>
    private async ValueTask InvokeOnRetryCallback(
        TaskHandlerExecutor task,
        object handler,
        MethodInfo? cachedOnRetryMethod,
        int attemptNumber,
        Exception exception,
        TimeSpan delay)
    {
        try
        {
            // Use cached MethodInfo instead of reflection lookup
            if (cachedOnRetryMethod != null)
            {
                var result = cachedOnRetryMethod.Invoke(handler, [task.PersistenceId, attemptNumber, exception, delay]);
                if (result is ValueTask valueTask)
                {
                    await valueTask.ConfigureAwait(false);
                }
            }

            // Publish retry event for monitoring
            RegisterRetryEvent(task, attemptNumber, exception, delay);
        }
        catch (Exception ex)
        {
            // OnRetry exceptions are logged but don't prevent retry
            logger.OnRetryCallbackFailed(ex, task.PersistenceId, attemptNumber);
        }
    }

    /// <summary>
    /// Publishes retry event for monitoring integrations (SignalR, etc.)
    /// </summary>
    private void RegisterRetryEvent(
        TaskHandlerExecutor task,
        int attemptNumber,
        Exception exception,
        TimeSpan delay) =>
        RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, exception, null,
            (TaskId: task.PersistenceId, Attempt: attemptNumber, DelayMs: delay.TotalMilliseconds),
            static (l, a, e) => l.RetryAttempt(e, a.TaskId, a.Attempt, a.DelayMs),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Task {a.TaskId} retry attempt {a.Attempt} after {a.DelayMs}ms"));

    private async ValueTask ExecuteCallback(Func<Guid, Exception?, string, ValueTask>? handler,
                                            TaskHandlerExecutor task,
                                            Exception? exception, string message)
    {
        if (handler == null) return;

        try
        {
            await handler.Invoke(task.PersistenceId, exception, message).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            RegisterCallbackFailure(task, "OnError", e);
        }
    }

    private async Task HandleExceptionAsync(Exception ex, TaskHandlerExecutor task, object handler,
                                            IReadOnlyList<TaskExecutionLog>? executionLogs,
                                            ITaskStorage? taskStorage,
                                            CancellationToken serviceToken)
    {
        if (ex is OperationCanceledException oce)
        {
            // A user cancel (blacklisted id) must classify as terminal Cancelled even when the service
            // token is ALSO cancelled (shutdown racing the user cancel): otherwise it would be
            // ServiceStopped (recoverable) and re-execute at the next restart (F17).
            // An OCCURRENCE never carries an entry of its own — cancelling a durable schedule blacklists the
            // SCHEDULE — so the same question has to be asked of its parent, or the one occurrence a cancel
            // deliberately lets finish (M15) is persisted ServiceStopped and requeued by the next startup
            // recovery: an execution of a series the user cancelled.
            var userCancelled      = workerBlacklist.IsBlacklisted(task.PersistenceId)
                                     || IsScheduleCancelled(task, out _);
            var cancelledByService = serviceToken.IsCancellationRequested && !userCancelled;
            if (taskStorage != null)
            {
                if (cancelledByService)
                    await taskStorage.SetCancelledByService(task.PersistenceId, oce, task.AuditLevel)
                                     .ConfigureAwait(false);
                else
                    await taskStorage.SetCancelledByUser(task.PersistenceId, task.AuditLevel).ConfigureAwait(false);
            }

            await ExecuteCallback(GetErrorCallback(task, handler), task, oce,
                $"Task with id {task.PersistenceId} was cancelled").ConfigureAwait(false);

            // The report must match the persisted classification above: a user cancel is not a shutdown.
            if (cancelledByService)
                RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, oce, executionLogs, task.PersistenceId,
                    static (l, id, e) => l.TaskCancelledByService(e, id),
                    static id => string.Create(CultureInfo.InvariantCulture,
                        $"Task with id {id} was cancelled by service while stopping"));
            else
                RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, oce, executionLogs, task.PersistenceId,
                    static (l, id, e) => l.TaskCancelledByUser(e, id),
                    static id => string.Create(CultureInfo.InvariantCulture,
                        $"Task with id {id} was cancelled by the user"));
        }
        else
        {
            // Logica per le altre eccezioni
            if (taskStorage != null)
                await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex, task.AuditLevel, null,
                                     serviceToken)
                                 .ConfigureAwait(false);

            // G11: the retry policy throws AggregateException("All retry attempts failed", ...) when
            // retries are exhausted. The PERSISTED status and the error log keep that aggregate (full
            // attempt history), but OnError must receive the REAL handler exception so type-based
            // handling (dead-letter, compensation) keyed on the exception type works — consistent with
            // the non-retryable path that delivers the raw exception.
            await ExecuteCallback(GetErrorCallback(task, handler), task, UnwrapForCallback(ex),
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, ex, executionLogs, task.PersistenceId,
                static (l, id, e) => l.TaskExecutionFailed(e, id),
                static id => string.Create(CultureInfo.InvariantCulture,
                    $"Error occurred executing task with id {id}"));
        }
    }

    // Unwraps a retry-policy AggregateException to the underlying handler failure for the OnError
    // callback (G11), so OnError sees the real exception instead of the wrapper. The aggregate itself is
    // still used for the persisted status and the error log. The last inner is the final attempt's
    // failure — the analogue of the raw exception delivered on the non-retryable path.
    private static Exception UnwrapForCallback(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions[^1]
            : ex;

    /// <param name="ct">
    /// The service token of the delivery this advance closes. It reaches the GRID, which since phase 6 may be
    /// an <see cref="INextOccurrenceProvider"/> doing real I/O: without it a provider that never returns holds
    /// a worker consumer, its delivery's registry entry and the host's shutdown for ever. The storage writes
    /// below deliberately do NOT take it — a shutdown must not cost the advance of a run that already
    /// happened.
    /// </param>
    private async Task QueueNextOccourrence(TaskHandlerExecutor task, double executionTimeMs,
                                            ITaskStorage? taskStorage, CancellationToken ct,
                                            bool markCompleted = false, bool countsAsRun = true,
                                            DateTimeOffset? skipAheadTo = null)
    {
        if (task.RecurringTask == null) return;

        // A user-cancelled recurring series must STOP: do not advance the run counter nor schedule the
        // next occurrence. The blacklist check works without storage too; the persisted Cancelled status
        // makes this DURABLE beyond the in-memory blacklist's ~1h TTL (a series with an interval > the
        // TTL would otherwise resurrect) (L23/CU10).
        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            _inMemoryRunCounts.TryRemove(task.PersistenceId, out _);
            logger.RecurringSeriesCancelled(task.PersistenceId);
            return;
        }

        // N: a SINGLE storage read serves both the Cancelled-status guard and the run counter below —
        // the durable row carries CurrentRunCount, so a separate GetCurrentRunCount round-trip (loading the
        // same row a second time) is redundant.
        QueuedTask? current = null;
        if (taskStorage != null)
        {
            current = (await taskStorage.Get(t => t.Id == task.PersistenceId).ConfigureAwait(false)).FirstOrDefault();
            if (current?.Status == QueuedTaskStatus.Cancelled)
            {
                logger.RecurringSeriesCancelled(task.PersistenceId);
                return;
            }
        }

        // Run counter source: the row already read from storage when present (no second round-trip),
        // otherwise an in-memory counter so the series keeps running and still honors MaxRuns/RunUntil
        // without persistence (F18).
        var currentRun = taskStorage != null
                             ? current?.CurrentRunCount ?? 0
                             : _inMemoryRunCounts.GetValueOrDefault(task.PersistenceId);

        // The cursor and the status THIS call decides against, taken with the run counter and never read back
        // at write time. The in-memory store hands back LIVE entities, so a property read at the end of the
        // call reports whatever a concurrent writer did in between and hands it to a compare-and-swap as its
        // own expectation — the guard would then confirm the very state it exists to refuse.
        var rowCursor = current?.NextRunUtc;
        var rowStatus = current?.Status ?? QueuedTaskStatus.Queued;

        // Fix for schedule drift: Use the scheduled execution time as base for next calculation,
        // not the current time. This ensures recurring tasks maintain their intended schedule
        // even when execution is delayed due to system load or downtime.
        // See: docs/recurring-task-schedule-drift-fix.md
        //
        // task.ExecutionTime is the scheduled execution time for THIS run:
        // - For first dispatch: the original scheduled time from the builder
        // - For tasks loaded from storage (after restart): NextRunUtc from the database
        //   (set in WorkerService.cs when loading pending tasks)
        var nowUtc        = _timeProvider.GetUtcNow();
        var scheduledTime = task.ExecutionTime ?? nowUtc;

        // Compute the next occurrence. A real execution (countsAsRun) advances the run number to
        // currentRun + 1 so the MaxRuns gate stops the series once MaxRuns real executions have happened.
        // A SKIPPED occurrence (rate-limit horizon rejection — countsAsRun == false) did not execute, so
        // it keeps the run number at currentRun: the MaxRuns gate counts only real executions, and the
        // skip never shortens the series (even the would-be MaxRuns-th occurrence is rescheduled for a
        // real run). isRecovery: true on the skip path only suppresses re-applying the first-run config
        // (InitialDelay/RunNow) when currentRun == 0 — the occurrence's time was already decided.
        //
        // skipAheadTo (the rate limiter's next available slot, only set on a horizon rejection): the
        // skip-forward jumps straight to the first occurrence at/after that slot — i.e. when the series
        // can actually run again — instead of grinding occurrence-by-occurrence and re-rejecting each one.
        // For a cadence far faster than the limiter's refill rate this collapses thousands of doomed
        // re-evaluations into one, while a correctly-configured series (near slot) barely moves. Passed as
        // the "now" reference of the skip-forward; null falls back to the actual now (next occurrence).
        // A real execution advances the run number to currentRun + 1 (so the MaxRuns gate stops the
        // series after MaxRuns real runs); a skip keeps it at currentRun and suppresses the first-run
        // config (isRecovery) — the occurrence's time was already decided — while jumping the "now"
        // reference to skipAheadTo.
        // I: defend against a misbehaving custom IRateLimitGate that returns a default/past SlotUtc — never
        // anchor the skip-ahead reference in the past (a MinValue/past `now` would make every occurrence look
        // "in the future" and defeat the skip). Floor to the real now by dropping it (the built-in gate's
        // PastSlotFloor already guarantees a future slot; this only hardens the public extension point).
        if (skipAheadTo.HasValue && skipAheadTo.Value <= nowUtc)
            skipAheadTo = null;

        var runNumber = countsAsRun ? currentRun + 1 : currentRun;

        NextRunResult result;

        try
        {
            result = await Evaluator.CalculateNextValidRunAsync(
                task.RecurringTask, scheduledTime, runNumber, nowUtc,
                referenceTime: countsAsRun ? null : skipAheadTo, isRecovery: !countsAsRun,
                computeSkippedCount: countsAsRun,
                identity: new ScheduleIdentity(task.PersistenceId, task.TaskKey, runNumber + 1), ct: ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown reached the grid before it answered. Exactly the shape a provider failure leaves —
            // nothing written, the cursor where it was — and startup recovery is what asks again.
            logger.ScheduleAdvanceAbandonedOnShutdown(task.PersistenceId);
            return;
        }
        catch (OccurrenceProviderException failure)
        {
            // The schedule's calendar could not answer, which is transient by contract (V4). Nothing is
            // written — not even the run that just happened, whose completion needs the cursor this call was
            // computing — so the row keeps the state a crash between a side effect and its storage write
            // leaves, and the at-least-once contract covers the replay. What must not happen is the series
            // stopping: it is parked to ask again after the backoff.
            DeferScheduleForProvider(task, failure, nowUtc);
            return;
        }

        // Log skipped occurrences if any, saying whether the number is the real total: a walked grid — and
        // above all a provider grid, where each step is a round trip — counts under a bound.
        if (result.SkippedCount > 0)
            logger.MissedOccurrencesSkipped(task.PersistenceId, result.SkippedCount, result.SkippedCountIsExact);

        // T6: the slots a daylight-saving transition folded into this one occurrence. They cost one run, not
        // one each, so the only place their number ever shows up is here.
        if (result.CollapsedSlotCount > 0)
            logger.DstSlotsCollapsed(task.PersistenceId, result.CollapsedSlotCount + 1, result.NextRun);

        // Advance the run counter by exactly ONE real execution. Occurrences skipped during a downtime
        // realign the schedule and are logged above, but they do NOT consume the MaxRuns budget: the
        // counter tracks real executions only (CurrentRunCount == RunsAudit rows), so MaxRuns means
        // "run this many times" (Option B accounting). Persistence is gated on storage; without storage
        // only the in-memory counter advances.
        //
        // A rate-limit-rejected occurrence (countsAsRun == false) did NOT execute: it advances the
        // schedule only and writes nothing to the run counter (mirroring the deferral's no-storage-write
        // invariant — the status was already set Queued by the caller). A failed run still counts.
        if (countsAsRun)
        {
            if (taskStorage != null)
            {
                // A schedule someone can reschedule at runtime advances under a compare-and-swap on its
                // version (S1/S3), so a run that finishes after a reschedule cannot write its stale next run
                // over the new definition. A schedule nobody can address keeps the historical unconditional
                // write, byte for byte.
                if (IsVersionedSchedule(task, current, taskStorage))
                {
                    var advance = await AdvanceVersionedRunAsync(task, executionTimeMs, result.NextRun,
                                          markCompleted, taskStorage)
                                      .ConfigureAwait(false);

                    if (!advance.OwnsNextOccurrence)
                    {
                        // The row belongs to a definition this delivery knows nothing about, so scheduling
                        // from here would put the old grid back. Its owner is SUPPOSED to have parked it —
                        // and a re-park that failed is the one case that brings this delivery here at all, so
                        // the schedule is parked from the row instead of being left in no scheduler at all.
                        await ReparkFromRowAsync(advance.Rebased, runNumber + 1, ct).ConfigureAwait(false);
                        return;
                    }
                }
                // On a successful run the Completed status is written in the SAME atomic operation as the
                // advance (CU14/L29); otherwise (failure) the status was already set and we only advance.
                else if (markCompleted)
                {
                    await taskStorage.CompleteRecurringRun(task.PersistenceId, executionTimeMs, result.NextRun,
                                         task.AuditLevel)
                                     .ConfigureAwait(false);
                }
                else
                {
                    await taskStorage.UpdateCurrentRun(task.PersistenceId, executionTimeMs, result.NextRun,
                                         task.AuditLevel)
                                     .ConfigureAwait(false);
                }
            }
            else
            {
                _inMemoryRunCounts[task.PersistenceId] = currentRun + 1;
            }
        }

        if (result.NextRun.HasValue)
        {
            // Update ExecutionTime for the next run so that subsequent calculations
            // use the correct scheduled time (not the original time from first dispatch).
            // Always continue LAZY: an eager first occurrence carries a single-use EverTask-owned
            // handler scope (disposed in the finally above), so reusing the same eager executor would
            // re-run on a disposed handler/scope. ToLazy() drops the carried instance and scope, so each
            // subsequent occurrence resolves a fresh handler in the worker's per-task scope (L27).
            // RunNumber travels with the occurrence so the handler can read it without a storage round-trip:
            // runNumber is the run this delivery WAS (currentRun + 1 for a real run, currentRun for a skipped
            // one, which consumed nothing), so the next occurrence is always one past it.
            var updatedTask = task.ToLazy() with { ExecutionTime = result.NextRun, RunNumber = runNumber + 1 };

            // CONDITIONAL, and on every path that reaches here. The compare-and-swap above owns the storage
            // write, not this registration: a reschedule can commit, park its own executor and publish its
            // version in the gap between them, and replacing it latest-wins would put the grid this delivery
            // knew back in the scheduler — where the published version then drops it as superseded, leaving
            // the series in no scheduler, no queue and no delivery until a restart. The rate-limit skip
            // (countsAsRun == false) never even reaches the compare-and-swap — it writes nothing — so this is
            // the ONLY thing standing between a superseded skip and that same end state.
            if (!scheduler.TrySchedule(updatedTask, result.NextRun))
                logger.NextOccurrenceRefusedBySuccessor(task.PersistenceId, task.ScheduleVersion);
        }
        else
        {
            // Series ended (MaxRuns/RunUntil reached): drop the in-memory counter, if any.
            _inMemoryRunCounts.TryRemove(task.PersistenceId, out _);

            // A series that ends on the SKIP path (its next limiter slot is past RunUntil) wrote nothing
            // above, so without this it would linger in a non-terminal Queued status forever. Persist a
            // terminal Completed AND clear NextRunUtc — mirroring how the counted paths end via
            // CompleteRecurringRun/UpdateCurrentRun with a null next run. A plain SetCompleted would leave
            // NextRunUtc populated, and a Completed recurring row with NextRunUtc != null is revived by
            // QueuedTask.IsRecoverable while RunUntil >= now — recovery would resurrect the finished
            // series. It does both atomically WITHOUT counting the skip (Option B).
            // Only the series-END writes here; a skip that continues still writes nothing (no-storage-write skip).
            if (!countsAsRun && taskStorage != null &&
                !await FinalizeSkippedSeriesAsync(task, executionTimeMs, currentRun, rowCursor, rowStatus,
                     taskStorage, current, ct).ConfigureAwait(false))
            {
                // The row belongs to a definition this delivery knows nothing about: it keeps its cursor, its
                // published lower bound and whatever the new owner parked for it.
                return;
            }

            // A schedule that will not run again is no longer a version this process publishes a lower bound
            // for (S4): the entry is dropped here and on cancellation, which are the two ways a series ends.
            // Its provider backoff, if it had one, has the same lifetime.
            ScheduleVersions?.Remove(task.PersistenceId);
            ProviderRetries?.Forget(task.PersistenceId);
        }
    }

    /// <summary>
    /// Parks a schedule whose occurrence provider could not answer, so the advance is retried after the
    /// backoff instead of waiting for a restart (V4).
    /// </summary>
    /// <remarks>
    /// The registration is a SCHEDULE RETRY, not the next occurrence: which slot comes next is precisely what
    /// the provider did not say, and parking an ordinary delivery would run the handler on a slot nobody
    /// chose. Conditional like every other re-park — a reschedule that has already parked a newer definition
    /// owns the row now — and the event is published beside the log line, because there is no poller behind
    /// this and a schedule waiting on its calendar has to be visible.
    /// <para>
    /// Two details. The retry CARRIES the slot this delivery was about (<c>ScheduleRetryFromUtc</c>) while its
    /// <c>ExecutionTime</c> becomes the instant it fires at: the decision it comes back to make is "what
    /// follows the slot that just ran", and on a host with no storage that slot exists nowhere but on the
    /// delivery. And the event saying the schedule "is parked to ask again" is published only once the
    /// registration is really in — announcing it first left a subscriber with a promise the very next line
    /// could break, and no event at all for the break (V4(2)).
    /// </para>
    /// </remarks>
    private void DeferScheduleForProvider(TaskHandlerExecutor task, OccurrenceProviderException failure,
                                          DateTimeOffset nowUtc)
    {
        var retryAt = nowUtc + failure.RetryAfter;

        try
        {
            var retry = task.ToLazy() with
            {
                ExecutionTime        = retryAt,
                IsScheduleRetry      = true,
                ScheduleRetryFromUtc = task.ExecutionTime
            };

            if (!scheduler.TrySchedule(retry, retryAt))
            {
                // A newer definition owns the row's parking now: this schedule is not the one waiting on a
                // provider any more, and saying so would describe a series nobody is running.
                logger.NextOccurrenceRefusedBySuccessor(task.PersistenceId, task.ScheduleVersion);
                return;
            }
        }
        catch (Exception e)
        {
            // The re-park IS what brings this schedule back, and nothing polls behind it: a failure here ends
            // the series until the next restart, so it is an error event and not only a log line.
            RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, e, null,
                (TaskId: task.PersistenceId, failure.ProviderKey),
                static (l, a, ex) => l.ProviderRetryParkFailed(ex, a.TaskId, a.ProviderKey),
                static a => string.Create(CultureInfo.InvariantCulture,
                    $"Schedule {a.TaskId} could not be parked to ask the occurrence provider " +
                    $"'{a.ProviderKey}' again: nothing was written and the series stays where it is " +
                    $"until the next startup recovery"));

            return;
        }

        RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, failure, null,
            (TaskId: task.PersistenceId, failure.ProviderKey, failure.ConsecutiveFailures, RetryAt: retryAt),
            static (l, a, e) => l.ScheduleAdvanceDeferredByProvider(e!, a.ProviderKey, a.TaskId,
                a.ConsecutiveFailures, a.RetryAt),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Occurrence provider '{a.ProviderKey}' could not answer for schedule {a.TaskId} " +
                $"({a.ConsecutiveFailures} consecutive failure(s)): nothing was written and the schedule is " +
                $"parked to ask again at {a.RetryAt:O}"));
    }

    /// <summary>
    /// Ends a series on the rate-limit SKIP path — the one advance that writes nothing above and therefore
    /// carries no compare-and-swap of its own.
    /// </summary>
    /// <returns>
    /// True when the series is this delivery's to end. False when the row now carries a definition this
    /// delivery never saw, in which case the schedule is parked from that row instead.
    /// </returns>
    /// <remarks>
    /// A skip is computed entirely from the definition the DELIVERY carries, so a reschedule that extends the
    /// bound while this delivery waits at the rate-limit gate — seconds, by design — leaves it computing a
    /// null next run from a definition that no longer exists. Writing that unconditionally set
    /// <c>Completed</c> and a null cursor over the row an operator had just extended: it then satisfies
    /// neither recovery predicate, so not even a restart brings the series back, and the audit says it ended
    /// normally. Every other finalization in the tree — recovery, the dispatcher's exhausted-series branch,
    /// the materializer — is compare-and-swapped on the version, cursor and status the decision was computed
    /// from, and this is the same guard, with the delivery's OWN version as the expectation: the row's would
    /// confirm the reschedule instead of losing to it.
    /// </remarks>
    private async Task<bool> FinalizeSkippedSeriesAsync(TaskHandlerExecutor task, double executionTimeMs,
                                                        int currentRun, DateTimeOffset? expectedCursorUtc,
                                                        QueuedTaskStatus expectedStatus, ITaskStorage taskStorage,
                                                        QueuedTask? row, CancellationToken ct)
    {
        // A schedule nobody can address, or a storage without the compare-and-swap, keeps the historical
        // unconditional write byte for byte.
        if (!IsVersionedSchedule(task, row, taskStorage))
        {
            await taskStorage.SetRecurringSeriesCompleted(task.PersistenceId, executionTimeMs, task.AuditLevel)
                             .ConfigureAwait(false);
            return true;
        }

        var finalized = await taskStorage
                              .TrySetRecurringSeriesCompleted(task.PersistenceId, expectedCursorUtc, expectedStatus,
                                  task.ScheduleVersion, executionTimeMs, task.AuditLevel)
                              .ConfigureAwait(false);

        if (finalized)
            return true;

        logger.SkippedSeriesFinalizationSuperseded(task.PersistenceId, task.ScheduleVersion);

        // Whoever owns the row now is SUPPOSED to have parked it, and a re-park that failed is the one case
        // that brings a superseded delivery here at all (S4) — so the row is parked from itself rather than
        // left in no scheduler, exactly as a re-aimed advance does.
        var rebased = (await taskStorage.Get(t => t.Id == task.PersistenceId).ConfigureAwait(false))
            .FirstOrDefault();

        await ReparkFromRowAsync(rebased, currentRun + 1, ct).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// How many times an advance re-aims at a row that was rescheduled under it before giving up ON THE GUARD.
    /// Two reschedules landing inside one advance is already the pathological case; the bound is here so a
    /// third party rewriting the row in a loop cannot spin this one. What it never gives up on is the run
    /// itself, which is recorded unconditionally once the attempts are spent.
    /// </summary>
    private const int MaxScheduleAdvanceAttempts = 3;

    /// <summary>
    /// Whether this schedule's advances go through the compare-and-swap overloads (S1).
    /// </summary>
    /// <remarks>
    /// A schedule <see cref="ITaskScheduleManager"/> can ADDRESS is compare-and-swapped from its very first
    /// advance, and the address is the taskKey: every entry point of that interface takes one, so a recurring
    /// row without a key can never be rescheduled and keeps the unconditional writes it always used, byte for
    /// byte. Deciding instead on "has it been rescheduled yet" cannot be done without a race — the row was read
    /// before the run was even evaluated, the delivery's version is older still, and the registry is published
    /// only after the re-park — so the FIRST reschedule of a schedule could linearize between that reading and
    /// this write and be silently overwritten by it, which is precisely the race S1 says must never be handled
    /// without a compare-and-swap. The version fields stay in the test for the schedules a key cannot answer
    /// for: a row whose key was cleared, and a delivery rebuilt from a row that already carries a version.
    /// </remarks>
    private bool IsVersionedSchedule(TaskHandlerExecutor task, QueuedTask? row, ITaskStorage taskStorage) =>
        taskStorage.SupportsScheduleVersioning
        && (task.ScheduleVersion > 0 || row?.ScheduleVersion > 0
            || (row?.TaskKey ?? task.TaskKey) != null
            || ScheduleVersions?.IsTracked(task.PersistenceId) == true);

    /// <summary>
    /// What a versioned advance leaves behind: whether the next occurrence is still this delivery's to
    /// schedule, and the row it has to be parked from when it is not.
    /// </summary>
    private readonly record struct ScheduleAdvance(bool OwnsNextOccurrence, QueuedTask? Rebased);

    /// <summary>
    /// Advances a versioned schedule's run counter and cursor, re-aiming the write if a reschedule linearized
    /// while this run was executing.
    /// </summary>
    /// <returns>
    /// <see cref="ScheduleAdvance.OwnsNextOccurrence"/> when the advance applied against the version this
    /// delivery ran, so the caller schedules the next occurrence itself. Otherwise the row now belongs to a
    /// definition this delivery knows nothing about, and it travels back in
    /// <see cref="ScheduleAdvance.Rebased"/> so the caller can park the schedule from it.
    /// </returns>
    /// <remarks>
    /// The run HAPPENED, so it is recorded whatever the version says — dropping the write on a mismatch would
    /// lose a completion and let recovery re-run the occurrence. What must not survive is this run's idea of
    /// what comes NEXT: the next run it computed belongs to the definition that was just replaced. That holds
    /// at the END of the loop too: once the re-aims are spent the guard is dropped, not the write.
    /// </remarks>
    private async Task<ScheduleAdvance> AdvanceVersionedRunAsync(TaskHandlerExecutor task, double executionTimeMs,
                                                                 DateTimeOffset? nextRun, bool markCompleted,
                                                                 ITaskStorage taskStorage)
    {
        var expectedVersion = task.ScheduleVersion;

        // The row the last re-aim read, kept so an advance that applied against a definition this delivery
        // never saw can hand it back WITHOUT a further round trip. Null while the first attempt is still the
        // one in flight, which is the only attempt an ordinary advance ever makes.
        QueuedTask? rebased = null;

        for (var attempt = 0; attempt < MaxScheduleAdvanceAttempts; attempt++)
        {
            var outcome = markCompleted
                              ? await taskStorage.CompleteRecurringRun(task.PersistenceId, executionTimeMs, nextRun,
                                                      task.AuditLevel, expectedVersion)
                                                 .ConfigureAwait(false)
                              : await taskStorage.UpdateCurrentRun(task.PersistenceId, executionTimeMs, nextRun,
                                                      task.AuditLevel, expectedVersion)
                                                 .ConfigureAwait(false);

            if (outcome == ScheduleCasResult.Applied)
                return new ScheduleAdvance(attempt == 0, rebased);

            var row = (await taskStorage.Get(t => t.Id == task.PersistenceId).ConfigureAwait(false))
                .FirstOrDefault();

            // Gone or cancelled: there is no series left to advance, and a cancel is terminal.
            if (row is null || row.Status == QueuedTaskStatus.Cancelled)
            {
                logger.RecurringSeriesCancelled(task.PersistenceId);
                return default;
            }

            // The row's own cursor is authoritative from here: it is the one the new definition produced.
            rebased         = row;
            expectedVersion = row.ScheduleVersion;
            nextRun         = row.NextRunUtc;
        }

        // The bound is reached only when somebody rewrote the row under EVERY re-aim, and giving up on the
        // guard is not the same thing as giving up on the run. The run happened: dropping its write leaves the
        // row in the InProgress this delivery set, the execution unaudited, the run counter — and with it
        // MaxRuns — one short for ever, and the series parked nowhere until a restart. So the last attempt
        // writes unconditionally, and it writes the cursor the last read carried: that value is the current
        // owner's own, so it advances nothing and the only thing a writer landing inside this final round trip
        // loses is one generation of the cursor, which the next advance of the definition that owns the row
        // overwrites. A lost run is permanent; a cursor one generation behind heals itself.
        logger.ScheduleAdvanceLost(task.PersistenceId, MaxScheduleAdvanceAttempts);

        if (markCompleted)
        {
            await taskStorage.CompleteRecurringRun(task.PersistenceId, executionTimeMs, nextRun, task.AuditLevel)
                             .ConfigureAwait(false);
        }
        else
        {
            await taskStorage.UpdateCurrentRun(task.PersistenceId, executionTimeMs, nextRun, task.AuditLevel)
                             .ConfigureAwait(false);
        }

        // Never this delivery's next occurrence — that grid is gone — but the row it was re-aimed at travels
        // back all the same, so the caller parks the schedule from it instead of leaving it in no scheduler.
        return new ScheduleAdvance(false, rebased);
    }

    /// <summary>
    /// Parks a schedule from its ROW — that row's definition, cursor and version, never this delivery's — after
    /// an advance applied against a definition this delivery never saw.
    /// </summary>
    /// <remarks>
    /// Whoever rewrote the row is supposed to have parked it, and normally has: this is then a second
    /// registration for the same instant, replaced latest-wins at the cost of one executor rebuild on a path an
    /// ordinary series never takes. It exists for the case where that parking is exactly what FAILED, which is
    /// also the only thing that lets a delivery of the replaced definition reach a rewritten row in the first
    /// place (S4): a re-park that threw publishes no version, so the old occurrence fires once more and lands
    /// here. Returning empty-handed there left the series in no scheduler, no queue and no delivery until a
    /// restart. A row that has turned DURABLE is handed to the materializer instead, because that is what owns
    /// the parking of a durable schedule and it re-reads the row anyway.
    /// </remarks>
    /// <param name="nextRunNumber">
    /// The run the parked occurrence will BE. Taken from this delivery's own accounting — the run it just was,
    /// plus one, exactly like the ordinary path — and never from the row's counter, whose snapshot was read
    /// before this advance incremented it on some providers and after it on the ones that hand back live
    /// entities.
    /// </param>
    /// <param name="ct">
    /// The service token of the delivery this park closes. A durable row is parked by the MATERIALIZER, which
    /// re-plans the schedule and may therefore ask an <see cref="INextOccurrenceProvider"/> doing real I/O:
    /// without the token a calendar that never answers holds this consumer, this delivery's registry entry and
    /// the host's shutdown for ever. Nothing is written by a plan that could not be computed, so a park the
    /// shutdown cancels costs only the wait — startup recovery parks the row again.
    /// </param>
    private async Task ReparkFromRowAsync(QueuedTask? row, int nextRunNumber, CancellationToken ct)
    {
        // Nothing to park: the advance never reached a row it could read, or the series has ended.
        if (row is not { NextRunUtc: { } cursor })
            return;

        try
        {
            var recovered = RecoveredTaskFactory.FromRow(row);

            if (recovered.Recurring is not { } definition || recovered.Task is null)
            {
                logger.ScheduleReparkFromRowFailed(recovered.ScheduleError ?? recovered.PayloadError, row.Id);
                return;
            }

            using var scope = serviceScopeFactory.CreateScope();

            var executor = await Dispatcher.Dispatcher.CreateCachedWrapper(recovered.Task.GetType())
                                           .Handle(recovered.Task, cursor, definition, scope.ServiceProvider,
                                               recovered.AuditLevel, row.Id, row.TaskKey, useLazyExecutor: true,
                                               recovered.RowMetadata with { RunNumber = nextRunNumber })
                                           .ConfigureAwait(false);

            if (definition.IsDurable && Materializer is { } materializer)
                await materializer.RunAsync(row.Id, executor, ct).ConfigureAwait(false);
            else if (!scheduler.TrySchedule(executor, cursor))
                return;

            logger.ScheduleReparkedFromRow(row.Id, cursor);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown, which is not a failure to report: nothing was written, the row keeps its cursor and
            // startup recovery parks it again.
        }
        catch (Exception e)
        {
            // Nothing else parks this row, so the situation has to be said out loud: the schedule stays
            // unparked until startup recovery finds it again.
            logger.ScheduleReparkFromRowFailed(e, row.Id);
        }
    }

    #region Logging and event pubblishing

    // EverTaskEventData.Severity values. severity.ToString() allocated one string per published event;
    // the enum has exactly three members, so three constants cover it. The wire values must not change.
    private const string SeverityInformation = nameof(SeverityLevel.Information);
    private const string SeverityWarning     = nameof(SeverityLevel.Warning);
    private const string SeverityError       = nameof(SeverityLevel.Error);

    /// <summary>
    /// L30, the single gate: nothing consumes an event when its log level is filtered out AND no
    /// monitoring subscriber is attached. The caller then produces nothing at all — no rendered
    /// sentence, no exception, no boxing.
    /// </summary>
    private bool TryEnterEvent(LogLevel logLevel, out bool logEnabled, out bool publish)
    {
        logEnabled = logger.IsEnabled(logLevel);
        publish    = TaskEventOccurredAsync != null;
        return logEnabled || publish;
    }

    /// <summary>
    /// Logs one event and publishes its monitoring counterpart.
    /// </summary>
    /// <remarks>
    /// The ILogger receives a compile-time template with named properties (<paramref name="log"/> calls a
    /// generated <c>[LoggerMessage]</c> method); <paramref name="render"/> produces the flat sentence
    /// that <c>EverTaskEventData.Message</c> needs, and runs ONLY when a subscriber is attached.
    /// <paramref name="args"/> is a value tuple and both delegates are <c>static</c>, so a call site
    /// allocates nothing and boxes nothing. <paramref name="logLevel"/> is decoupled from
    /// <paramref name="severity"/>: per-task chatter can log at Debug while the dashboard still gets an
    /// Information event.
    /// internal (not private): the deterministic L30/F24 gate tests drive this seam directly.
    /// </remarks>
    internal void RegisterEvent<TArgs>(LogLevel logLevel, SeverityLevel severity, TaskHandlerExecutor executor,
                                       Exception? exception, IReadOnlyList<TaskExecutionLog>? executionLogs,
                                       TArgs args, Action<ILogger, TArgs, Exception?> log,
                                       Func<TArgs, string> render)
    {
        if (!TryEnterEvent(logLevel, out var logEnabled, out var publish))
            return;

        EmitEvent(severity, executor, exception, executionLogs, args, log, render, logEnabled, publish);
    }

    // The emit half of RegisterEvent, split out so a call site that must build its exception behind the
    // gate (RegisterRateLimitSkippedOccurrence) can reuse the gate's own verdict instead of re-testing it.
    private void EmitEvent<TArgs>(SeverityLevel severity, TaskHandlerExecutor executor, Exception? exception,
                                  IReadOnlyList<TaskExecutionLog>? executionLogs, TArgs args,
                                  Action<ILogger, TArgs, Exception?> log, Func<TArgs, string> render,
                                  bool logEnabled, bool publish)
    {
        // The generated methods reached from here declare SkipEnabledCheck: TryEnterEvent already
        // tested IsEnabled for exactly this message's level.
        if (logEnabled)
            log(logger, args, exception);

        if (!publish)
            return;

        var message = render(args);

        try
        {
            PublishEvent(executor, severity, message, exception, executionLogs);
        }
        catch (Exception e)
        {
            logger.EventPublishFailed(e, message);
        }
    }

    // F24: cap concurrent in-flight monitoring callbacks. A slow/blocked subscriber (e.g. SignalR)
    // under high throughput × events × subscribers would otherwise spawn an unbounded number of
    // fire-and-forget Task.Run continuations and saturate the thread pool.
    internal static readonly int MonitoringMaxConcurrency = Math.Max(4, Environment.ProcessorCount * 2);

    private readonly SemaphoreSlim _monitoringConcurrency = new(MonitoringMaxConcurrency, MonitoringMaxConcurrency);

    // Observable for tests/diagnostics: events dropped because the in-flight cap was full.
    internal long MonitoringDroppedEvents;

    // Currently admitted (in-flight) monitoring callbacks. Incremented synchronously when a permit is
    // taken (in PublishEvent) and decremented when the callback finishes — so a test can read it
    // deterministically without depending on thread-pool scheduling.
    internal int MonitoringInFlightCount => MonitoringMaxConcurrency - _monitoringConcurrency.CurrentCount;

    /// <inheritdoc />
    bool IEverTaskWorkerExecutor.HasEventSubscribers => TaskEventOccurredAsync != null;

    /// <inheritdoc />
    void IEverTaskWorkerExecutor.PublishExternalEvent(TaskHandlerExecutor executor, SeverityLevel severity,
                                                      string message, Exception? exception)
    {
        try
        {
            PublishEvent(executor, severity, message, exception);
        }
        catch (Exception e)
        {
            logger.EventPublishFailed(e, message);
        }
    }

    private void PublishEvent(TaskHandlerExecutor task, SeverityLevel severity, string formattedMessage,
                              Exception? exception = null, IReadOnlyList<TaskExecutionLog>? executionLogs = null)
    {
        var eventHandlers = TaskEventOccurredAsync?.GetInvocationList();
        if (eventHandlers == null || eventHandlers.Length == 0)
            return;

        // Create event data ONCE outside loop, reuse for all subscribers
        var data = CreateEventDataCached(task, severity, formattedMessage, exception, executionLogs);

        foreach (var eventHandler in eventHandlers)
        {
            var handler = (Func<EverTaskEventData, Task>)eventHandler;

            // Never block DoWork: Wait(0) is a non-blocking acquire. Over-cap events are dropped —
            // monitoring is fire-and-forget by contract (see CLAUDE.md "Fire-and-Forget Monitoring").
            if (!_monitoringConcurrency.Wait(0))
            {
                Interlocked.Increment(ref MonitoringDroppedEvents);
                continue;
            }

            // Fire and forget with exception handling to prevent unobserved task exceptions
            _ = Task.Run(async () =>
            {
                try
                {
                    await handler(data).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.MonitoringSubscriberFailed(ex, data.TaskId);
                }
                finally
                {
                    _monitoringConcurrency.Release();
                }
            });
        }
    }

    private static EverTaskEventData CreateEventDataCached(TaskHandlerExecutor executor, SeverityLevel severity,
                                                    string message, Exception? exception,
                                                    IReadOnlyList<TaskExecutionLog>? executionLogs = null)
    {
        // Cache task JSON (weak reference - GC'd when task is collected). EverTaskJson uses private, isolated
        // System.Text.Json options (L33) so a hostile global JSON configuration cannot alter the monitoring
        // payload either.
        var taskJson = TaskJsonCache.GetValue(executor.Task, EverTaskJson.Serialize);

        // Cache type strings (permanent cache - types never unload)
        var taskType    = CachedTypeName(executor.Task.GetType());
        var handlerType = EverTaskEventData.ResolveHandlerTypeName(executor, CachedTypeName);

        var severityName = severity switch
        {
            SeverityLevel.Information => SeverityInformation,
            SeverityLevel.Warning => SeverityWarning,
            _ => SeverityError
        };

        // Built by the SAME mapper as every other call site: the occurrence context (parent, nominal slot,
        // schedule version) has one implementation, so what this hot path publishes cannot drift away from
        // what the tests pin.
        return EverTaskEventData.FromExecutor(executor, severityName, taskType, handlerType, taskJson, message,
            exception, executionLogs);
    }

    /// <summary>Type name through the permanent cache; hoisted so the delegate is allocated once.</summary>
    private static readonly Func<Type, string> CachedTypeName =
        static type => TypeStringCache.GetOrAdd(type, static t => t.ToString());

    #endregion

    /// <summary>
    /// Creates a log capture instance for the task execution.
    /// Always forwards to ILogger, optionally persists to database based on configuration.
    /// </summary>
    private TaskLogCapture CreateLogCapture(Type handlerType, Guid taskId, IServiceProvider serviceProvider)
    {
        // Create ILogger<THandler> for the specific handler type
        var handlerLogger = loggerFactory.CreateLogger(handlerType);

        // Resolve GUID generator (database-specific)
        var guidGenerator = serviceProvider.GetRequiredService<IGuidGenerator>();

        // Create proxy that always logs to ILogger and optionally persists
        return new TaskLogCapture(
            handlerLogger,
            taskId,
            guidGenerator,
            persistLogs: options.PersistentLogger.Enabled,
            minPersistLevel: options.PersistentLogger.MinimumLevel,
            maxPersistedLogs: options.PersistentLogger.MaxLogsPerTask
        );
    }
}
