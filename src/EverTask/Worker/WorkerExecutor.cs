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
    /// It does NOT log: the caller has already logged through its own <c>[LoggerMessage]</c> template, so a
    /// rendered sentence can never reach a logger as if it were a template (#32).
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
    /// The clock-less constructor, kept as a real overload so an assembly compiled against the previous
    /// release still binds. The container picks the longer one, the only one that carries the clock.
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

    // Every next-occurrence decision below reads it, so a test clock drives the whole series. Retry delays
    // deliberately stay on the real clock (IRetryPolicy owns its own waits).
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

    // The lifecycle MethodInfo are cached per handler type so a lazy-mode execution pays no GetMethod lookup
    // on the hot path. The two injectors are compiled delegates instead: both members are explicitly
    // implemented on IEverTaskHandler<T>, which otherwise costs an interface scan plus a reflective Invoke.
    private record HandlerOptionsCache(
        IRetryPolicy? RetryPolicy,
        TimeSpan? Timeout,
        MethodInfo? OnRetryMethod,
        MethodInfo? OnStartedMethod,
        MethodInfo? OnCompletedMethod,
        MethodInfo? OnErrorMethod,
        Action<object, ITaskLogCapture>? SetLogCapture,
        Action<object, ITaskExecutionContext>? SetExecutionContext);

    // Test seam: counts per-type reflection resolutions. Keyed per type so an assertion is immune to other
    // handler types resolved concurrently elsewhere in the process. Not on any production code path.
    internal static readonly ConcurrentDictionary<Type, int> LifecycleResolutionsByType = new();

    internal static int GetLifecycleResolutionCount(Type handlerType) =>
        LifecycleResolutionsByType.GetValueOrDefault(handlerType);

    private static HandlerOptionsCache ResolveHandlerOptions(Type _, object handlerInstance)
    {
        var type = handlerInstance.GetType();
        LifecycleResolutionsByType.AddOrUpdate(type, 1, static (_, count) => count + 1);

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

        return handlerInstance is IEverTaskHandlerOptions handlerOpts
                   ? new HandlerOptionsCache(handlerOpts.RetryPolicy, handlerOpts.Timeout,
                       onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod,
                       setLogCapture, setExecutionContext)
                   : new HandlerOptionsCache(null, null,
                       onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod,
                       setLogCapture, setExecutionContext);
    }

    // The call goes through the INTERFACE, so an explicit implementation and the interface's own default body
    // are both reached — a lookup on the concrete type misses them.
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

    // In-memory run counter for recurring series when NO storage is registered: without one a series would
    // die after a single run instead of honouring MaxRuns/RunUntil. Dropped when the series ends.
    private readonly ConcurrentDictionary<Guid, int> _inMemoryRunCounts = new();


    // One delivery's claim on the EAGER handler EverTask resolved for it. This delivery is the scope's last
    // owner: whatever it continues into (a re-park, a deferral, the next occurrence) is a ToLazy() copy.
    // Release happens exactly ONCE — the ordered call sites keep their position and DoWork's finally covers
    // every other exit. Not thread-safe, and does not need to be: one delivery's sequential steps.
    private sealed class EagerHandlerOwnership(WorkerExecutor executor, TaskHandlerExecutor task)
    {
        private bool _released;

        public async ValueTask ReleaseAsync()
        {
            // Lazy-mode handlers belong to the worker's per-task scope and are released with it.
            if (_released || task.IsLazy)
                return;

            _released = true;

            // Disposing only the handler instance would strand the scope and its scoped dependencies (a
            // DbContext and its pooled connection). An executor built without a scope — never by this
            // library — still gets its handler disposed.
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
            // Everything before the End is fallible — releasing a scope runs user DisposeAsync code, reaching
            // the materializer resolves a service — so it sits inside its own try. The End itself may never
            // be skipped: an id that stays registered can never be delivered again.
            try
            {
                // Covers the path where the whole delivery completed synchronously — nothing ever suspended,
                // so the value is still on this flow — and a post-execution step threw before DoWorkCore's
                // own clearing line.
                AmbientTaskExecutionContextAccessor.Set(null);

                // THE single release of this delivery's eager handler: it covers every exit of DoWorkGuarded
                // that never reaches DoWorkCore or the terminal rejection, with no per-path enumeration.
                await eagerHandler.ReleaseAsync().ConfigureAwait(false);

                // An occurrence that has ended kicks its schedule, so a serial catch-up moves to the next slot
                // at once instead of waiting for the operational retry. KickAsync is non-throwing, but
                // REACHING it is not, hence the try around the whole statement.
                // It takes THIS delivery's token: the kick re-plans over a grid that may do real I/O, and a
                // calendar that never answers would hold the consumer, the registry entry and the shutdown.
                if (task.ParentTaskId is { } scheduleId && Materializer is { } materializer)
                    await materializer.KickAsync(scheduleId, serviceToken).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.DeliveryEpilogueFailed(e, task.PersistenceId);
            }
            finally
            {
                // THE single End of this delivery, and its LAST act: because nothing runs after it, a
                // successor delivery of the same id can only register afterwards, so no delivery can ever
                // release a successor's registration.
                deliveryRegistry?.End(task.PersistenceId);
            }
        }
    }

    private async ValueTask DoWorkGuarded(TaskHandlerExecutor task, CancellationToken serviceToken,
                                          EagerHandlerOwnership eagerHandler)
    {
        // BEFORE the rate-limit gate: a cancelled task must be discarded without burning tokens.
        if (IsTaskBlacklisted(task))
            return;

        // A delivery of a definition a reschedule has already replaced. The scheduler drops a registration
        // the moment it is replaced, but a delivery already handed to a worker queue is past its reach, and
        // running it would execute the schedule the caller has just changed.
        if (IsSupersededSchedule(task))
            return;

        // A delivery that exists only to ask the grid again: an occurrence provider could not answer, so
        // nothing was decided and nothing written. Running the handler here would execute a slot nobody has
        // chosen yet.
        if (task.IsScheduleRetry)
        {
            await RetryScheduleDecisionAsync(task, serviceToken).ConfigureAwait(false);
            return;
        }

        // A DURABLE schedule row runs no handler: its slot firing means "materialize what is due". Before the
        // gate deliberately — the rate limit belongs to the OCCURRENCES, and letting the schedule row consume
        // the budget would throttle the very series it is producing.
        if (task.IsScheduleOnly)
        {
            await MaterializeScheduleAsync(task, serviceToken).ConfigureAwait(false);
            return;
        }

        if (rateLimitGate != null && task.RateLimitPolicy != null)
        {
            // A redelivery racing the still-unwinding original execution must NOT touch the gate: redeeming
            // the reservation and then hitting the in-flight guard would drop the only live copy until the
            // next restart. The gate re-parks it untouched instead.
            if (_inFlightTasks.ContainsKey(task.PersistenceId))
            {
                rateLimitGate.ReparkInFlightRedelivery(task);
                return;
            }

            // Parking-lot backpressure: consumers pause (bounded) once a queue's parked tasks hit the cap.
            // Scoped to tasks WITH a policy — one without can never park, so pausing it would only collapse
            // whole-queue throughput while the lot sits at cap.
            await rateLimitGate.WaitForParkingCapacityAsync(task, serviceToken).ConfigureAwait(false);

            var gateResult = await rateLimitGate.TryPassAsync(task, serviceToken).ConfigureAwait(false);

            if (gateResult.EmitFailOpenEvent)
                RegisterFailOpenEvent(task, gateResult);

            if (gateResult.Outcome == RateLimitGateOutcome.Deferred)
            {
                // The Deferred path NEVER enters DoWorkCore: its finally would run QueueNextOccourrence and
                // corrupt the run count. The gate already re-parked the task, nothing was written and the
                // status stays Queued. A Cancel racing this deferral does not consume the blacklist — the
                // gate's epoch bump drops the registration, or the redelivery's own check discards it.
                RegisterDeferralEvent(task, gateResult);
                return;
            }

            // Gate waits can take seconds, and a Cancel landing during them only reaches the blacklist (the
            // per-task token does not exist yet), so it is honoured BEFORE the outcome is applied: on
            // Rejected that keeps Failed and a spurious OnError off a row the user cancelled.
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

    // Asks a schedule's grid again after an INextOccurrenceProvider could not answer. It re-dispatches the
    // ROW through the recovery path, the one that knows how to choose between the grace window and a
    // skip-forward — the very choice the provider left unanswered — and writes nothing itself.
    // With NO storage there is no row to re-read, so the retry re-runs the interrupted advance from the
    // delivery, which is then the only place the slot it was about exists.
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

    // The whole delivery of a durable schedule row: hand it to the materializer, which decides which slots
    // are owed and re-parks the row itself. The failure path is the point of the wrapper — this delivery IS
    // the schedule (the scheduler consumed the row's registration to make it), so an exception escaping here
    // would leave the row parked nowhere until a restart.
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

        TaskLogCapture? logCapture = null;

        object? handler = null!;

        // The identity of this delivery, published to the handler and to the ambient accessor once the
        // handler is resolved. Null until then: a delivery that cannot even resolve its handler never runs.
        TaskExecutionContext? executionContext = null;

        var executionTime = 0.0;

        // Set when a RETRY attempt was deferred by the rate limiter: the gate re-parked the
        // task, so the completion path AND the finally's post-execution logic must be skipped
        // (no storage write, no recurring re-scheduling — the parked occurrence is still alive)
        var rateLimitDeferred = false;

        // Set when a RECURRING occurrence completed successfully: the finally then writes the Completed
        // status and the run-counter / next-run advance atomically instead of just advancing.
        var recurringRunCompleted = false;

        // The limiter's next available slot, set when a RECURRING occurrence was SKIPPED without executing.
        // The finally then advances the schedule without counting it toward MaxRuns — only real executions
        // consume the budget — and skips ahead to this slot instead of grinding occurrence by occurrence.
        DateTimeOffset? skippedOccurrenceSlot = null;

        try
        {
            serviceToken.ThrowIfCancellationRequested();

            // NOTE: the blacklist check happens in DoWork, before the rate-limit gate

            if (task.IsLazy)
            {
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

                    return;
                }
            }
            else
            {
                handler = task.Handler!; // Non-null assertion safe (validated at dispatch)
            }

            var handlerType = handler.GetType();
            logCapture = CreateLogCapture(handlerType, task.PersistenceId, scope.ServiceProvider);

            // Inject log capture and execution context into the handler BEFORE OnStarted, through the
            // delegates compiled once per handler type (both members are explicitly implemented, so they
            // are only reachable through the interface).
            var injectors = GetHandlerOptions(handler);
            injectors.SetLogCapture?.Invoke(handler, logCapture);

            executionContext = PublishExecutionContext(task, handler, injectors);

            // The last question before the unconditional SetInProgress below, which would otherwise write
            // straight over a Cancelled a cancel persisted while this delivery sat at the gate or resolved
            // its handler — a window the two earlier checks cannot cover, since both sit before the gate.
            // BOTH halves are asked, as at the queue boundary, and only the entry covering THIS delivery
            // alone is consumed: the schedule's keeps covering the siblings behind it.
            if (IsOccurrenceCancelled(task, out var cancelledSchedule))
            {
                if (cancelledSchedule is { } scheduleId)
                    RegisterOccurrenceOfCancelledSchedule(task, scheduleId);
                else
                    RegisterCancellationSignaled(task);

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
                // A retry attempt ran out of budget beyond MaxInSlotWait and the gate re-parked the task at
                // its reserved slot. Storage keeps the InProgress status, which is recoverable, until the
                // slot-fire re-enqueue sets Queued.
                rateLimitDeferred = true;
                RegisterDeferralEvent(task, execution.RetryDeferral!.Value);
                return;
            }

            if (execution.RetryDeferral is { Outcome: RateLimitGateOutcome.Rejected } rejection)
            {
                if (task.RecurringTask != null)
                {
                    // Same semantics as the pre-execution rejection: the occurrence is SKIPPED with a warning
                    // — no Failed status, no OnError — the series advances in the finally, and the status
                    // returns to Queued like any other parked occurrence.
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

            // A Cancel landing after the pre-gate check but before the per-task token existed had no token to
            // cancel, so the handler ran to completion on a fresh one. Asked again before the outcome is
            // written, so Completed never clobbers the user's persisted Cancelled status.
            if (workerBlacklist.IsBlacklisted(task.PersistenceId))
            {
                workerBlacklist.Remove(task.PersistenceId);
                RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null, task.PersistenceId,
                    static (l, id, _) => l.TaskCancelledDuringExecution(id),
                    static id => string.Create(CultureInfo.InvariantCulture,
                        $"Task with id {id} was cancelled during execution; the completion is suppressed"));
                return;
            }

            // A recurring occurrence is marked Completed TOGETHER with its run-counter / next-run advance by
            // the finally's atomic CompleteRecurringRun; a separate SetCompleted here would re-open the crash
            // window. Non-recurring tasks have no advance, so they complete here.
            if (taskStorage != null && task.RecurringTask == null)
                await taskStorage.SetCompleted(task.PersistenceId, executionTime, task.AuditLevel)
                                 .ConfigureAwait(false);

            recurringRunCompleted = task.RecurringTask != null;

            await ExecuteCallback(GetCompletedCallback(task, handler), task, "Completed").ConfigureAwait(false);

            var capturedLogs = logCapture.GetPersistedLogs();
            RegisterEvent(LogLevel.Debug, SeverityLevel.Information, task, null, capturedLogs,
                (TaskId: task.PersistenceId, ElapsedMs: executionTime),
                static (l, a, _) => l.TaskCompleted(a.TaskId, a.ElapsedMs),
                static a => string.Create(CultureInfo.InvariantCulture,
                    $"Task with id {a.TaskId} was completed in {a.ElapsedMs} ms"));
        }
        catch (Exception ex)
        {
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

            // AFTER handler disposal, BEFORE recurring scheduling. Skipped on a rate-limit retry deferral,
            // which writes nothing to storage: this attempt's captured logs are dropped from persistence
            // (they were still forwarded to ILogger), the price of that no-write invariant.
            if (logCapture != null && !rateLimitDeferred)
            {
                try
                {
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

    // The ambient copy is what everything that is NOT the handler reads: an eager handler's dependencies were
    // built in the dispatcher's scope, long before this delivery existed, so a scoped context reaches nothing.
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
    /// Publishes the tracked-keys fail-open monitoring event: without it a limiter executing unthrottled
    /// tasks under key-cardinality pressure would be invisible.
    /// </summary>
    private void RegisterFailOpenEvent(TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, null, null,
            (TaskId: task.PersistenceId, TaskType: task.Task.GetType(), gateResult.TotalFailOpenCount),
            static (l, a, _) => l.RateLimiterFailOpen(a.TaskId, a.TaskType, a.TotalFailOpenCount),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Rate limiter tracked-keys cap reached: new keys fail OPEN and execute unthrottled. Task {a.TaskId} (policy={a.TaskType}) totalFailOpenCount={a.TotalFailOpenCount}"));
    }

    // Warns that the rate limiter skipped one occurrence: the series stays alive and the schedule advances.
    // The typed exception is built INSIDE the enabled-check gate — on this path it is neither thrown nor
    // persisted — so an unconsumed warning allocates neither it nor its reason string.
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

    // A one-shot is persisted Failed — the only storage write a rejection may do, or the task stays Queued
    // and is re-rejected at every restart — with the typed exception delivered to OnError. A recurring task
    // only skips the occurrence, without consuming the MaxRuns budget, since nothing executed.
    // Both outcomes end the delivery without entering DoWorkCore, so this method owns the ordered release too.
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

            // Resolved once to deliver OnError; not an executing instance, since a rejection happens
            // pre-execution. An eager executor hands back its carried instance, released with its scope
            // below; a lazy one resolves into this method's own scope.
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
                    // This path never enters DoWorkCore, so nothing else gives the handler the two
                    // per-delivery injections every callback is documented to have: without them an OnError
                    // using Context or Logger throws, and ExecuteCallback swallows it while the user's error
                    // handling never runs. The capture is not persisted — the Failed status above is the only
                    // write a rejection may do.
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
            // The release of the one-shot branch, and the backstop of the recurring one that already released
            // above (a no-op then). The executor is dead once the rejection is applied.
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
            RegisterCancellationSignaled(task);

            // Consumed only when the entry covers THIS delivery alone. A durable schedule's entry is also the
            // only thing covering the occurrences it produced, so dropping the schedule row must leave it
            // standing; it lapses on the blacklist's own TTL like any entry nobody consumes.
            if (!task.IsScheduleOnly)
                workerBlacklist.Remove(task.PersistenceId);

            return true;
        }

        // An occurrence already parked in the scheduler, or already in a channel, carries no blacklist entry
        // of its own; the schedule's covers it. NOT consumed here — it has to keep covering the siblings
        // behind this one.
        if (!IsScheduleCancelled(task, out var scheduleId))
            return false;

        RegisterOccurrenceOfCancelledSchedule(task, scheduleId);

        return true;
    }

    private void RegisterCancellationSignaled(TaskHandlerExecutor task) =>
        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null, task.PersistenceId,
            static (l, id, _) => l.TaskCancellationSignaled(id),
            static id => string.Create(CultureInfo.InvariantCulture,
                $"Task with id {id} is signaled to be cancelled and will not be executed"));

    private void RegisterOccurrenceOfCancelledSchedule(TaskHandlerExecutor task, Guid scheduleId) =>
        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null,
            (OccurrenceId: task.PersistenceId, ScheduleId: scheduleId),
            static (l, a, _) => l.OccurrenceOfCancelledSchedule(a.OccurrenceId, a.ScheduleId),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Occurrence {a.OccurrenceId} belongs to cancelled schedule {a.ScheduleId} and will not be executed"));

    // The entry is never consumed: one cancel covers every occurrence the schedule produced, and the first of
    // them to ask must not answer for the rest.
    private bool IsScheduleCancelled(TaskHandlerExecutor task, out Guid scheduleId)
    {
        scheduleId = task.ParentTaskId ?? Guid.Empty;

        return task.ParentTaskId.HasValue && workerBlacklist.IsBlacklisted(scheduleId);
    }

    // Whether this occurrence was cancelled by its schedule (cancelledSchedule names it) or by an entry of
    // its own (null) — a Cancel addressed at it directly, or the revival of its schedule moving the cover
    // onto it. Only THAT entry is consumed here: it covers this delivery and nothing else.
    private bool IsOccurrenceCancelled(TaskHandlerExecutor task, out Guid? cancelledSchedule)
    {
        cancelledSchedule = null;

        if (task.ParentTaskId is not { } scheduleId)
            return false;

        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            workerBlacklist.Remove(task.PersistenceId);
            return true;
        }

        if (!workerBlacklist.IsBlacklisted(scheduleId))
            return false;

        cancelledSchedule = scheduleId;
        return true;
    }

    // Whether this delivery carries an INLINE definition a reschedule has already replaced: the one thing the
    // scheduler's latest-wins registration cannot reach is a delivery already written to a worker queue.
    // The ABSENCE of an entry is not a version of zero but of a lower bound, and nothing is dropped on it —
    // a fresh process publishes nothing, so a recovered executor would read as stale. A DURABLE schedule is
    // never dropped either: dropping it consumes the registration that produced it.
    private bool IsSupersededSchedule(TaskHandlerExecutor task)
    {
        if (!IsSupersededScheduleDelivery(task, out var published))
            return false;

        RegisterEvent(LogLevel.Information, SeverityLevel.Information, task, null, null,
            (TaskId: task.PersistenceId, Delivered: task.ScheduleVersion, Published: published),
            static (l, a, _) => l.SupersededScheduleDelivery(a.TaskId, a.Delivered, a.Published),
            static a => string.Create(CultureInfo.InvariantCulture,
                $"Task with id {a.TaskId} carries schedule version {a.Delivered} and was superseded by version {a.Published}: the delivery is discarded"));

        return true;
    }

    // The bare question IsSupersededSchedule answers, without reporting it: asked again by the paths that
    // reach it while the delivery is already RUNNING, where the answer means something else.
    private bool IsSupersededScheduleDelivery(TaskHandlerExecutor task, out int published)
    {
        published = 0;

        return task.RecurringTask is { IsDurable: false }
               && ScheduleVersions is { } versions
               && versions.TryGetLatest(task.PersistenceId, out published)
               && task.ScheduleVersion < published;
    }

    private async Task<TaskExecutionResult> ExecuteTask(TaskHandlerExecutor task, object handler,
                                                        TaskExecutionContext? executionContext,
                                                        CancellationToken serviceToken)
    {
        serviceToken.ThrowIfCancellationRequested();

        var taskToken = cancellationSourceProvider.CreateToken(task.PersistenceId, serviceToken);

        var handlerOptions = GetHandlerOptions(handler);

        // Resolution chain: handler override → queue default → global default. The queue is
        // the task's DECLARED queue (a FallbackToDefault reroute keeps the declared queue's
        // retry/timeout, consistent with rate limiting following the task type everywhere).
        var queueConfig = ResolveQueueConfiguration(task);
        var retryPolicy = handlerOptions.RetryPolicy ?? queueConfig?.DefaultRetryPolicy ?? options.DefaultRetryPolicy;
        var timeout     = handlerOptions.Timeout ?? queueConfig?.DefaultTimeout ?? options.DefaultTimeout;

        // Closure state shared with the retry action below.
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
            // OnRetry announces the attempt ABOUT to start, and that retry can still never start (a cancel,
            // or the throttle gate turning it back). `attempt` only moves once an attempt is admitted INTO
            // the handler, so rolling the announcement back to it keeps OnError from reporting an attempt
            // that never ran; where the retry did start the two already agree.
            if (attempt > 0)
                executionContext?.SetAttempt(attempt);
        }

        var elapsedTime = Stopwatch.GetElapsedTime(startTime);
        return new TaskExecutionResult(elapsedTime.TotalMilliseconds, retryDeferral);

        async Task DoExecute()
        {
            Func<IEverTask, CancellationToken, Task> handlerCallback;
            if (task.HandlerCallback != null)
            {
                handlerCallback = task.HandlerCallback;
            }
            else
            {
                // Built from the handler already resolved in DoWork: resolving again would create a second
                // instance, disposed separately by the async scope.
                var (_, callback) = task.CreateHandlerCallback(handler);
                handlerCallback   = callback;
            }

            //Use WaitAsync for cancelling:
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#cancelling-uncancellable-operations
            await retryPolicy.Execute(
                action: async retryToken =>
                {
                    // The attempt this action stands for, 1-based, NOT committed to `attempt` yet: the
                    // throttle gate below can turn it back without ever reaching the handler, and OnError
                    // would then report an attempt whose only trace is a rejected gate pass.
                    var startingAttempt = attempt + 1;

                    // BEFORE the timeout branch: the budget wait must never erode the per-attempt timeout.
                    // The FIRST attempt skips re-acquisition, since the gate pass that admitted this delivery
                    // holds its budget. A far slot re-parks the task rather than surfacing a retryable
                    // exception, which would consume the shared retry budget and mark it Failed unexecuted.
                    if (startingAttempt > 1
                        && task.RateLimitPolicy is { ThrottleRetries: true }
                        && rateLimitGate != null)
                    {
                        var gateResult = await rateLimitGate.TryPassAsync(task, retryToken).ConfigureAwait(false);
                        if (gateResult.Outcome != RateLimitGateOutcome.Proceed)
                        {
                            // Deferred: stop the retry loop without failing — the task was re-parked and the
                            // attempt sequence restarts on redelivery. Rejected: turned into the typed
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

    // Disposes the EverTask-owned scope an eager handler was resolved into, releasing the handler instances
    // without pinning them in the root container. Disposal must never fail task execution.
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
        if (task.HandlerStartedCallback != null)
            return task.HandlerStartedCallback;

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
        if (task.HandlerCompletedCallback != null)
            return task.HandlerCompletedCallback;

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
        if (task.HandlerErrorCallback != null)
            return task.HandlerErrorCallback;

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
            if (cachedOnRetryMethod != null)
            {
                var result = cachedOnRetryMethod.Invoke(handler, [task.PersistenceId, attemptNumber, exception, delay]);
                if (result is ValueTask valueTask)
                {
                    await valueTask.ConfigureAwait(false);
                }
            }

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
        // A run of a definition replaced WHILE it executed does not own the row any more, and Failed and
        // Cancelled are statuses no recovery predicate selects: either would land on the series that is now
        // live and kill it for good. Only the storage write is skipped — what is REPORTED does not change,
        // since the run really did end this way.
        var outcomeStore = taskStorage;

        if (IsSupersededScheduleDelivery(task, out var supersededBy))
        {
            logger.SupersededScheduleOutcomeNotPersisted(task.PersistenceId, task.ScheduleVersion, supersededBy);
            outcomeStore = null;
        }

        if (ex is OperationCanceledException oce)
        {
            // A user cancel must classify as terminal Cancelled even when the service token is ALSO cancelled
            // (a shutdown racing the cancel), or it is ServiceStopped, recoverable, and re-executes at the
            // next restart. An OCCURRENCE carries no entry of its own — a cancel blacklists the SCHEDULE — so
            // the parent is asked too, or the occurrence a cancel deliberately lets finish is requeued.
            var userCancelled      = workerBlacklist.IsBlacklisted(task.PersistenceId)
                                     || IsScheduleCancelled(task, out _);
            var cancelledByService = serviceToken.IsCancellationRequested && !userCancelled;
            if (outcomeStore != null)
            {
                // Never the service token: on a shutdown it is already cancelled, and the ending of the run it
                // is stopping still has to be persisted.
                await PersistEndingAsync(outcomeStore, task,
                        cancelledByService ? QueuedTaskStatus.ServiceStopped : QueuedTaskStatus.Cancelled,
                        cancelledByService ? oce : null, CancellationToken.None)
                    .ConfigureAwait(false);
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
            if (outcomeStore != null)
                await PersistEndingAsync(outcomeStore, task, QueuedTaskStatus.Failed, ex, serviceToken)
                    .ConfigureAwait(false);

            // Exhausted retries arrive as an AggregateException. The persisted status and the error log keep
            // it (full attempt history), but OnError must receive the REAL handler exception so type-based
            // handling works, exactly as on the non-retryable path.
            await ExecuteCallback(GetErrorCallback(task, handler), task, UnwrapForCallback(ex),
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, ex, executionLogs, task.PersistenceId,
                static (l, id, e) => l.TaskExecutionFailed(e, id),
                static id => string.Create(CultureInfo.InvariantCulture,
                    $"Error occurred executing task with id {id}"));
        }
    }

    // Persists the terminal outcome of a delivery that ENDED, without taking the row from whoever owns it now.
    // The version registry cannot answer this alone: it is EMPTY for the whole cancel-to-publish span, and a
    // plain cancel moves neither version nor cursor, so the write is the storage's compare-and-swap to
    // refuse. A storage without versioning keeps the unconditional writes.
    private async Task PersistEndingAsync(ITaskStorage store, TaskHandlerExecutor task, QueuedTaskStatus status,
                                          Exception? exception, CancellationToken ct)
    {
        if (!store.SupportsScheduleVersioning)
        {
            switch (status)
            {
                case QueuedTaskStatus.Cancelled:
                    await store.SetCancelledByUser(task.PersistenceId, task.AuditLevel).ConfigureAwait(false);
                    break;
                case QueuedTaskStatus.ServiceStopped when exception is { } serviceStop:
                    await store.SetCancelledByService(task.PersistenceId, serviceStop, task.AuditLevel)
                               .ConfigureAwait(false);
                    break;
                default:
                    await store.SetStatus(task.PersistenceId, status, exception, task.AuditLevel, null, ct)
                               .ConfigureAwait(false);
                    break;
            }

            return;
        }

        var applied = await store.TrySetTerminalOutcome(task.PersistenceId, status, exception,
                                     task.ScheduleVersion, task.AuditLevel, ct)
                                 .ConfigureAwait(false);

        if (!applied)
            logger.EndingOutcomeNotPersisted(task.PersistenceId, status, task.ScheduleVersion);
    }

    // Unwraps a retry-policy AggregateException so OnError sees the real handler failure, the aggregate
    // staying on the persisted status and the error log. The LAST inner is the final attempt's failure.
    private static Exception UnwrapForCallback(Exception ex) =>
        ex is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions[^1]
            : ex;

    // `ct` reaches the GRID, which may be an INextOccurrenceProvider doing real I/O: without it a provider
    // that never returns holds a worker consumer, this delivery's registry entry and the host's shutdown for
    // ever. The storage writes below deliberately do NOT take it — a shutdown must not cost the advance of a
    // run that already happened.
    private async Task QueueNextOccourrence(TaskHandlerExecutor task, double executionTimeMs,
                                            ITaskStorage? taskStorage, CancellationToken ct,
                                            bool markCompleted = false, bool countsAsRun = true,
                                            DateTimeOffset? skipAheadTo = null)
    {
        if (task.RecurringTask == null) return;

        // A user-cancelled series must STOP: no run-counter advance, no next occurrence. The blacklist works
        // without storage too, and the persisted Cancelled status below outlives its TTL — a series with an
        // interval longer than the TTL would otherwise resurrect.
        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            _inMemoryRunCounts.TryRemove(task.PersistenceId, out _);
            logger.RecurringSeriesCancelled(task.PersistenceId);
            return;
        }

        // A single storage read serves both the Cancelled-status guard and the run counter below: the durable
        // row carries CurrentRunCount, so a separate run-count round trip is redundant.
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

        // The row already read from storage when present, otherwise an in-memory counter so the series keeps
        // running and still honours MaxRuns/RunUntil without persistence.
        var currentRun = taskStorage != null
                             ? current?.CurrentRunCount ?? 0
                             : _inMemoryRunCounts.GetValueOrDefault(task.PersistenceId);

        // The cursor and status THIS call decides against, taken with the run counter and never read back at
        // write time: the in-memory store hands back LIVE entities, so a later read would give a
        // compare-and-swap the very state a concurrent writer just made — the guard confirming what it exists
        // to refuse.
        var rowCursor = current?.NextRunUtc;
        var rowStatus = current?.Status ?? QueuedTaskStatus.Queued;

        // The base is the SCHEDULED time of this run, not the current time, so a series delayed by load or
        // downtime keeps its intended grid instead of drifting. See
        // docs/recurring-task-schedule-drift-fix.md.
        var nowUtc        = _timeProvider.GetUtcNow();
        var scheduledTime = task.ExecutionTime ?? nowUtc;

        // A real execution advances the run number so MaxRuns stops the series after MaxRuns real runs; a
        // SKIPPED occurrence keeps it, and passes isRecovery to suppress the first-run config, since its time
        // was already decided. skipAheadTo, the limiter's next available slot, becomes the "now" reference of
        // the skip-forward: it jumps to when the series can actually run again instead of grinding
        // occurrence by occurrence and re-rejecting each one.
        // A past slot is DROPPED, since a custom IRateLimitGate may return one: anchoring the reference in
        // the past makes every occurrence look future and defeats the skip entirely.
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
            // The schedule's calendar could not answer, which is transient by contract. Nothing is written —
            // not even the run that just happened, whose completion needs the cursor this call was computing
            // — so the row is left as a crash between a side effect and its write leaves it, and
            // at-least-once covers the replay. The series is parked to ask again after the backoff.
            await DeferScheduleForProviderAsync(task, failure, nowUtc).ConfigureAwait(false);
            return;
        }

        // Log skipped occurrences if any, saying whether the number is the real total: a walked grid — and
        // above all a provider grid, where each step is a round trip — counts under a bound.
        if (result.SkippedCount > 0)
            logger.MissedOccurrencesSkipped(task.PersistenceId, result.SkippedCount, result.SkippedCountIsExact);

        // The slots a daylight-saving transition folded into this one occurrence. They cost one run, not one
        // each, so the only place their number ever shows up is here.
        if (result.CollapsedSlotCount > 0)
            logger.DstSlotsCollapsed(task.PersistenceId, result.CollapsedSlotCount + 1, result.NextRun);

        // Exactly ONE real execution. Occurrences skipped during a downtime realign the schedule but do NOT
        // consume the MaxRuns budget, so MaxRuns means "run this many times"; a failed run still counts. A
        // rate-limit-rejected occurrence never executed, so it advances the schedule and writes nothing.
        if (countsAsRun)
        {
            if (taskStorage != null)
            {
                // A schedule someone can reschedule at runtime advances under a compare-and-swap on its
                // version, so a run finishing after a reschedule cannot write its stale next run over the new
                // definition. A schedule nobody can address keeps the unconditional write, byte for byte.
                if (IsVersionedSchedule(task, current, taskStorage))
                {
                    var advance = await AdvanceVersionedRunAsync(task, executionTimeMs, result.NextRun,
                                          markCompleted, taskStorage)
                                      .ConfigureAwait(false);

                    if (!advance.OwnsNextOccurrence)
                    {
                        // The row belongs to a definition this delivery knows nothing about, so scheduling
                        // from here would put the old grid back. A re-park that FAILED is the one case that
                        // brings this delivery here, so the schedule is parked from the row instead.
                        await ReparkFromRowAsync(advance.Rebased, runNumber + 1, ct).ConfigureAwait(false);
                        return;
                    }
                }
                // On a successful run the Completed status is written in the SAME atomic operation as the
                // advance; on a failure the status was already set and only the advance is left.
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
            // Always continue LAZY: an eager first occurrence carries a single-use handler scope, already
            // disposed in the finally above, so reusing that executor would run on a disposed handler.
            // RunNumber travels with the occurrence so the handler reads it without a storage round trip, and
            // is one past the run this delivery WAS.
            var updatedTask = task.ToLazy() with { ExecutionTime = result.NextRun, RunNumber = runNumber + 1 };

            // CONDITIONAL on every path: a reschedule can commit, park its own executor and publish its
            // version in the gap after the compare-and-swap above, and replacing that registration
            // latest-wins would put this delivery's grid back — where the published version drops it as
            // superseded, leaving the series in no scheduler, no queue and no delivery until a restart. The
            // rate-limit skip writes nothing at all, so here this is the only guard it has.
            if (!scheduler.TrySchedule(updatedTask, result.NextRun))
                logger.NextOccurrenceRefusedBySuccessor(task.PersistenceId, task.ScheduleVersion);
        }
        else
        {
            // Series ended (MaxRuns/RunUntil reached): drop the in-memory counter, if any.
            _inMemoryRunCounts.TryRemove(task.PersistenceId, out _);

            // A series ending on the SKIP path wrote nothing above and would linger in a non-terminal Queued
            // status for ever. Completed and a cleared NextRunUtc go in ONE write, without counting the skip:
            // a Completed recurring row that keeps its cursor is revived by recovery while RunUntil >= now.
            // Only the series END writes here; a skip that continues still writes nothing.
            if (!countsAsRun && taskStorage != null &&
                !await FinalizeSkippedSeriesAsync(task, executionTimeMs, currentRun, rowCursor, rowStatus,
                     taskStorage, current, ct).ConfigureAwait(false))
            {
                // The row belongs to a definition this delivery knows nothing about: it keeps its cursor, its
                // published lower bound and whatever the new owner parked for it.
                return;
            }

            // A schedule that will not run again is no longer a version this process publishes a lower bound
            // for: the entry is dropped here and on cancellation, the two ways a series ends. Its provider
            // backoff has the same lifetime.
            ScheduleVersions?.Remove(task.PersistenceId);
            ProviderRetries?.Forget(task.PersistenceId);
        }
    }

    // Parks a schedule whose occurrence provider could not answer, so the advance is retried after the
    // backoff instead of waiting for a restart. The registration is a SCHEDULE RETRY, not the next
    // occurrence: which slot comes next is precisely what the provider did not say, and parking an ordinary
    // delivery would run the handler on a slot nobody chose. The retry CARRIES the slot this delivery was
    // about, which on a host with no storage exists nowhere else. An error event goes beside the log line
    // because nothing polls behind this, and "parked to ask again" is published only once the registration
    // is really in.
    private async Task DeferScheduleForProviderAsync(TaskHandlerExecutor task, OccurrenceProviderException failure,
                                                     DateTimeOffset nowUtc)
    {
        var retryAt = nowUtc + failure.RetryAfter;

        await ProviderRetryParker
              .ParkAsync(scheduler, retryAt,
                  () => new ValueTask<TaskHandlerExecutor?>(task.ToLazy()),
                  executor => executor with
                  {
                      ExecutionTime        = retryAt,
                      IsScheduleRetry      = true,
                      ScheduleRetryFromUtc = task.ExecutionTime
                  },
                  (_, _) => logger.NextOccurrenceRefusedBySuccessor(task.PersistenceId, task.ScheduleVersion),
                  (_, at) =>
                      RegisterEvent(LogLevel.Warning, SeverityLevel.Warning, task, failure, null,
                          (TaskId: task.PersistenceId, failure.ProviderKey, failure.ConsecutiveFailures,
                           RetryAt: at),
                          static (l, a, e) => l.ScheduleAdvanceDeferredByProvider(e!, a.ProviderKey, a.TaskId,
                              a.ConsecutiveFailures, a.RetryAt),
                          static a => string.Create(CultureInfo.InvariantCulture,
                              $"Occurrence provider '{a.ProviderKey}' could not answer for schedule {a.TaskId} " +
                              $"({a.ConsecutiveFailures} consecutive failure(s)): nothing was written and the " +
                              $"schedule is parked to ask again at {a.RetryAt:O}")),
                  (_, error) =>
                      RegisterEvent(LogLevel.Error, SeverityLevel.Error, task, error, null,
                          (TaskId: task.PersistenceId, failure.ProviderKey),
                          static (l, a, ex) => l.ProviderRetryParkFailed(ex, a.TaskId, a.ProviderKey),
                          static a => string.Create(CultureInfo.InvariantCulture,
                              $"Schedule {a.TaskId} could not be parked to ask the occurrence provider " +
                              $"'{a.ProviderKey}' again: nothing was written and the series stays where it is " +
                              $"until the next startup recovery")))
              .ConfigureAwait(false);
    }

    // Ends a series on the rate-limit SKIP path, the one advance that writes nothing above and so carries no
    // compare-and-swap of its own. Returns false when the row now carries a definition this delivery never
    // saw, the schedule being parked from that row instead.
    // A skip is computed from the DELIVERY's definition, so a reschedule extending the bound while it waits
    // at the gate leaves it holding a null next run from a definition that no longer exists — written
    // unconditionally, that answers neither recovery predicate. The expectation is the delivery's OWN
    // version; the row's would confirm the reschedule instead of losing to it.
    private async Task<bool> FinalizeSkippedSeriesAsync(TaskHandlerExecutor task, double executionTimeMs,
                                                        int currentRun, DateTimeOffset? expectedCursorUtc,
                                                        QueuedTaskStatus expectedStatus, ITaskStorage taskStorage,
                                                        QueuedTask? row, CancellationToken ct)
    {
        var policy = IsVersionedSchedule(task, row, taskStorage)
                         ? RecurringSeriesFinalizationPolicy.Conditional
                         : RecurringSeriesFinalizationPolicy.Unconditional;

        var finalized = await RecurringSeriesFinalizer
                              .FinalizeAsync(taskStorage, task.PersistenceId, expectedCursorUtc, expectedStatus,
                                  task.ScheduleVersion, executionTimeMs, task.AuditLevel, policy)
                              .ConfigureAwait(false);

        if (finalized)
            return true;

        logger.SkippedSeriesFinalizationSuperseded(task.PersistenceId, task.ScheduleVersion);

        // A re-park that FAILED is the one case that brings a superseded delivery here at all, so the row is
        // parked from itself rather than left in no scheduler, exactly as a re-aimed advance does.
        var rebased = (await taskStorage.Get(t => t.Id == task.PersistenceId).ConfigureAwait(false))
            .FirstOrDefault();

        await ReparkFromRowAsync(rebased, currentRun + 1, ct).ConfigureAwait(false);
        return false;
    }

    // How many times an advance re-aims at a row rescheduled under it before giving up ON THE GUARD — never
    // on the run itself, which is recorded unconditionally once the attempts are spent. The bound exists so a
    // third party rewriting the row in a loop cannot spin this one.
    private const int MaxScheduleAdvanceAttempts = 3;

    // Whether this schedule's advances go through the compare-and-swap overloads. A schedule
    // ITaskScheduleManager can ADDRESS is guarded from its FIRST advance, the address being the taskKey; a
    // recurring row without one can never be rescheduled and keeps the unconditional writes byte for byte.
    // Deciding on "has it been rescheduled yet" cannot be done without a race, since the row was read before
    // the run was evaluated and the registry is published only after the re-park. The version fields stay in
    // the test for what a key cannot answer for: a row whose key was cleared, and a delivery rebuilt from a
    // row that already carries a version.
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

    // Advances a versioned schedule's run counter and cursor, re-aiming the write if a reschedule linearized
    // while this run was executing. OwnsNextOccurrence says the advance applied against the version this
    // delivery ran; otherwise the row travels back so the caller parks the schedule from it.
    // The run HAPPENED, so it is recorded whatever the version says — dropping the write would lose a
    // completion and let recovery re-run the occurrence. What must not survive is this run's idea of what
    // comes NEXT. Past the last re-aim the GUARD is given up, never the write.
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

        // Past the last re-aim the GUARD is given up, never the write: dropping it would leave the row
        // InProgress, the run unaudited, MaxRuns permanently short and the series parked nowhere. The cursor
        // written is the last read's, which belongs to the current owner and so advances nothing — a lost run
        // is permanent, a cursor one generation behind heals itself.
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

    // Parks a schedule from its ROW — that row's definition, cursor and version, never this delivery's. The
    // one thing that lets a delivery of a replaced definition reach a rewritten row is a re-park that FAILED,
    // and returning empty-handed left the series in no scheduler until a restart. A row that turned DURABLE
    // goes to the materializer, which owns the parking of a durable schedule.
    // `nextRunNumber` comes from this delivery's accounting, never from the row's counter, whose snapshot is
    // read before this advance on some providers and after it on those handing back live entities.
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

    // The single gate: nothing consumes an event when its log level is filtered out AND no monitoring
    // subscriber is attached, so the caller then produces nothing at all — no rendered sentence, no
    // exception, no boxing.
    private bool TryEnterEvent(LogLevel logLevel, out bool logEnabled, out bool publish)
    {
        logEnabled = logger.IsEnabled(logLevel);
        publish    = TaskEventOccurredAsync != null;
        return logEnabled || publish;
    }

    // Logs one event and publishes its monitoring counterpart. The ILogger gets a compile-time template
    // through a generated [LoggerMessage] method, while `render` produces the flat sentence
    // EverTaskEventData.Message needs and runs ONLY when a subscriber is attached; `args` is a value tuple
    // and both delegates static, so a call site allocates and boxes nothing. `logLevel` is decoupled from
    // `severity` on purpose: per-task chatter logs at Debug while the dashboard still gets Information.
    // internal, not private, because the gate tests drive this seam directly.
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

    // Caps concurrent in-flight monitoring callbacks: a slow or blocked subscriber under high throughput
    // would otherwise spawn unbounded fire-and-forget continuations and saturate the thread pool.
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
        // Weak reference, GC'd with the task. EverTaskJson uses private, isolated System.Text.Json options, so
        // a host's global JSON configuration cannot alter the monitoring payload either.
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
        var handlerLogger = loggerFactory.CreateLogger(handlerType);
        var guidGenerator = serviceProvider.GetRequiredService<IGuidGenerator>();

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
