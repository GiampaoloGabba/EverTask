using System.Diagnostics;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EverTask.Configuration;
using EverTask.Logging;
using EverTask.RateLimiting;

namespace EverTask.Worker;

public interface IEverTaskWorkerExecutor
{
    public event Func<EverTaskEventData, Task>? TaskEventOccurredAsync;
    internal ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken);
}

public class WorkerExecutor(
    IWorkerBlacklist workerBlacklist,
    EverTaskServiceConfiguration options,
    IServiceScopeFactory serviceScopeFactory,
    IScheduler scheduler,
    ICancellationSourceProvider cancellationSourceProvider,
    IEverTaskLogger<WorkerExecutor> logger,
    ILoggerFactory loggerFactory,
    IRateLimitGate? rateLimitGate = null,
    TaskDeliveryRegistry? deliveryRegistry = null) : IEverTaskWorkerExecutor
{
    // Performance optimization: Cache for event data to avoid repeated serialization
    private static readonly ConditionalWeakTable<IEverTask, string> TaskJsonCache = new();
    private static readonly ConcurrentDictionary<Type, string> TypeStringCache = new();

    // Performance optimization: Cache handler options to avoid runtime casts per execution.
    // Stores the RAW handler overrides (null = no override): the fallback chain
    // handler → queue → global is resolved per execution, NOT baked into the cache.
    private static readonly ConcurrentDictionary<Type, HandlerOptionsCache> HandlerOptionsInternalCache = new();

    // F23: the lifecycle MethodInfo (OnStarted/OnCompleted/OnError) are cached per handler type just
    // like OnRetry, so lazy-mode executions no longer pay a GetMethod lookup per task on the hot path.
    private record HandlerOptionsCache(
        IRetryPolicy? RetryPolicy,
        TimeSpan? Timeout,
        MethodInfo? OnRetryMethod,
        MethodInfo? OnStartedMethod,
        MethodInfo? OnCompletedMethod,
        MethodInfo? OnErrorMethod);

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

        // Cast only once per handler type (first time): cache the RAW overrides
        return handlerInstance is IEverTaskHandlerOptions handlerOpts
            ? new HandlerOptionsCache(handlerOpts.RetryPolicy, handlerOpts.Timeout,
                                      onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod)
            : new HandlerOptionsCache(null, null,
                                      onRetryMethod, onStartedMethod, onCompletedMethod, onErrorMethod);
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


    public async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        try
        {
            await DoWorkGuarded(task, serviceToken).ConfigureAwait(false);
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

    private async ValueTask DoWorkGuarded(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        // Blacklist check hoisted BEFORE the rate-limit gate: a cancelled task must be discarded
        // without burning rate-limit tokens (and without entering the execution path)
        if (IsTaskBlacklisted(task))
            return;

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
                await HandleRateLimitRejectionAsync(task, gateResult, serviceToken).ConfigureAwait(false);
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

            logger.LogWarning(
                "Task {TaskId} is already executing in this process, skipping duplicate delivery",
                task.PersistenceId);
            return;
        }

        try
        {
            await DoWorkCore(task, serviceToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlightTasks.TryRemove(task.PersistenceId, out _);
        }
    }

    private async ValueTask DoWorkCore(TaskHandlerExecutor task, CancellationToken serviceToken)
    {
        //Task storage could be a dbcontext wich is not thread safe.
        //So its safer to just use a new scope for each task
        await using var scope       = serviceScopeFactory.CreateAsyncScope();
        ITaskStorage? taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        // Create log capture instance (always logs to ILogger, optionally persists)
        // Will be injected with proper handler type after handler resolution
        ITaskLogCaptureInternal? logCapture = null;

        // Resolve handler (lazy or eager mode)
        object? handler = null!; // Will be assigned in both if and else branches

        // Track execution time (initialized to 0, updated if task completes successfully)
        double executionTime = 0.0;

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

                    logger.LogDebug("Resolved handler {handlerType} for lazy task {taskId}",
                        handler.GetType().Name, task.PersistenceId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to resolve handler for task {taskId}", task.PersistenceId);

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

                    return;  // Cannot proceed without handler
                }
            }
            else
            {
                // Eager mode: use existing handler instance
                handler = task.Handler!;  // Non-null assertion safe (validated at dispatch)
            }

            // Create log capture with proper handler type for ILogger<THandler>
            var handlerType = handler.GetType();
            logCapture = CreateLogCapture(handlerType, task.PersistenceId, scope.ServiceProvider);

            // Inject log capture into handler BEFORE OnStarted
            // Find the SetLogCapture method via interface (explicitly implemented)
            var interfaces = handlerType.GetInterfaces();
            var handlerInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IEverTaskHandler<>));

            if (handlerInterface != null)
            {
                var setLogCaptureMethod = handlerInterface.GetMethod(nameof(IEverTaskHandler<IEverTask>.SetLogCapture));
                if (setLogCaptureMethod != null)
                {
                    setLogCaptureMethod.Invoke(handler, new object[] { logCapture });
                }
            }

            RegisterInfo(task, "Starting task with id {0}.", task.PersistenceId);

            if (taskStorage != null)
                await taskStorage.SetInProgress(task.PersistenceId, task.AuditLevel, serviceToken).ConfigureAwait(false);

            await ExecuteCallback(GetStartedCallback(task, handler), task, "Started").ConfigureAwait(false);

            var execution = await ExecuteTask(task, handler, scope.ServiceProvider, serviceToken, taskStorage);
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
                    RegisterWarning(CreateRejectionException(task, rejection), task,
                        "Rate limit skipped occurrence of recurring task {0} (key {1}): the series stays alive.",
                        task.PersistenceId, task.RateLimitKey!);

                    if (taskStorage != null)
                        await taskStorage.SetQueued(task.PersistenceId, task.AuditLevel, serviceToken).ConfigureAwait(false);

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
                RegisterInfo(task, "Task with id {0} was cancelled during execution; the completion is suppressed.",
                    task.PersistenceId);
                return;
            }

            // A recurring occurrence is marked Completed TOGETHER with its run-counter / next-run advance
            // by the finally's atomic CompleteRecurringRun (CU14/L29); a separate SetCompleted here would
            // re-open the crash window. Non-recurring tasks have no advance, so they complete here.
            if (taskStorage != null && task.RecurringTask == null)
                await taskStorage.SetCompleted(task.PersistenceId, executionTime, task.AuditLevel).ConfigureAwait(false);

            recurringRunCompleted = task.RecurringTask != null;

            await ExecuteCallback(GetCompletedCallback(task, handler), task, "Completed").ConfigureAwait(false);

            // Get logs for completion event (if capture is enabled)
            var capturedLogs = logCapture?.GetPersistedLogs();
            RegisterInfo(task, capturedLogs, "Task with id {0} was completed in {1} ms.", task.PersistenceId, executionTime);
        }
        catch (Exception ex)
        {
            // Get logs for error event (if capture is enabled)
            var capturedLogs = logCapture?.GetPersistedLogs();
            await HandleExceptionAsync(ex, task, handler, capturedLogs, serviceToken, taskStorage);
        }
        finally
        {
            // Dispose the eager handler (BEFORE recurring scheduling). Lazy-mode handlers are disposed
            // by the worker's per-task scope. Eager handlers are resolved into an EverTask-owned scope
            // carried on the executor (L27): disposing that scope releases the handler exactly once and
            // unpins it from the container. DisposeAsync is idempotent (CU17), so any extra dispose
            // (e.g. a reused recurring executor) is harmless.
            if (!task.IsLazy)
            {
                if (task.HandlerScope != null)
                    await ExecuteDisposeHandlerScope(task.HandlerScope);
                else if (handler != null)
                    await ExecuteDisposeHandler(handler);
            }

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
                            await taskStorage.SaveExecutionLogsAsync(task.PersistenceId, persistedLogs, CancellationToken.None)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception logSaveEx)
                {
                    // Log the error but don't fail the task
                    logger.LogError(logSaveEx, "Failed to persist execution logs for task {TaskId}", task.PersistenceId);
                }
            }

            cancellationSourceProvider.Delete(task.PersistenceId);

            // A rate-limit deferral must NOT schedule the next recurring occurrence: the
            // CURRENT occurrence is still parked in the scheduler (run-count integrity)
            if (!rateLimitDeferred)
                await QueueNextOccourrence(task, executionTime, taskStorage, markCompleted: recurringRunCompleted,
                                           countsAsRun: skippedOccurrenceSlot == null, skipAheadTo: skippedOccurrenceSlot);
        }
    }

    /// <summary>
    /// Publishes the aggregated, machine-parseable deferral monitoring event when the gate
    /// signals it (per-deferral details are logged at Debug by the gate itself).
    /// </summary>
    private void RegisterDeferralEvent(TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        if (!gateResult.EmitDeferralEvent)
            return;

        RegisterInfo(task,
            "Rate limit deferred task {0}: key={1} slotUtc={2:O} policy={3} deferredCount={4}",
            task.PersistenceId, task.RateLimitKey!, gateResult.SlotUtc, task.Task.GetType(),
            gateResult.AggregatedDeferrals);
    }

    /// <summary>
    /// Publishes the mandatory tracked-keys fail-open monitoring event (L4): without it, a
    /// limiter silently executing unthrottled tasks under key-cardinality pressure would be
    /// invisible.
    /// </summary>
    private void RegisterFailOpenEvent(TaskHandlerExecutor task, RateLimitGateResult gateResult)
    {
        RegisterWarning(null, task,
            "Rate limiter tracked-keys cap reached: new keys fail OPEN and execute unthrottled. " +
            "Task {0} (policy={1}) totalFailOpenCount={2}",
            task.PersistenceId, task.Task.GetType(), gateResult.TotalFailOpenCount);
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
    private async ValueTask HandleRateLimitRejectionAsync(TaskHandlerExecutor task, RateLimitGateResult gateResult,
                                                          CancellationToken serviceToken)
    {
        var exception = CreateRejectionException(task, gateResult);

        await using var scope = serviceScopeFactory.CreateAsyncScope();
        var taskStorage = scope.ServiceProvider.GetService<ITaskStorage>();

        if (task.RecurringTask != null)
        {
            RegisterWarning(exception, task,
                "Rate limit skipped occurrence of recurring task {0} (key {1}): the series stays alive.",
                task.PersistenceId, task.RateLimitKey!);

            // Skipped occurrence: advance the schedule without consuming the MaxRuns budget, skipping
            // ahead to the limiter's next available slot instead of grinding occurrence by occurrence.
            await QueueNextOccourrence(task, 0, taskStorage, countsAsRun: false, skipAheadTo: gateResult.SlotUtc)
                .ConfigureAwait(false);
            return;
        }

        if (taskStorage != null)
        {
            await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, exception, task.AuditLevel,
                null, serviceToken).ConfigureAwait(false);
        }

        // Resolve the handler once (rare terminal event, cost acceptable) to deliver OnError.
        // The callback instance is NOT an executing instance: rejection happens pre-execution.
        object? handler = null;
        try
        {
            handler = task.GetOrResolveHandler(scope.ServiceProvider);
        }
        catch (Exception resolveEx)
        {
            logger.LogWarning(resolveEx,
                "Unable to resolve handler for rejected task {TaskId}: OnError will not be invoked",
                task.PersistenceId);
        }

        if (handler != null)
        {
            await ExecuteCallback(GetErrorCallback(task, handler), task, exception, exception.Message)
                .ConfigureAwait(false);

            if (!task.IsLazy)
                await ExecuteDisposeHandler(handler);
        }

        RegisterError(exception, task, "Rate limit rejected task {0}: marked as Failed.", task.PersistenceId);
    }

    /// <summary>
    /// Resolves the configuration of the task's effective queue for the per-queue
    /// retry/timeout defaults. Unregistered custom queue names fall back to the default
    /// queue's configuration, mirroring the execution-time routing.
    /// </summary>
    private QueueConfiguration? ResolveQueueConfiguration(TaskHandlerExecutor task)
    {
        var queueName = task.QueueName ?? (task.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

        if (options.Queues.TryGetValue(queueName, out var queueConfig))
            return queueConfig;

        return options.Queues.TryGetValue(QueueNames.Default, out var defaultConfig) ? defaultConfig : null;
    }

    private bool IsTaskBlacklisted(TaskHandlerExecutor task)
    {
        if (workerBlacklist.IsBlacklisted(task.PersistenceId))
        {
            RegisterInfo(task, "Task with id {0} is signaled to be cancelled and will not be executed.",
                task.PersistenceId);
            workerBlacklist.Remove(task.PersistenceId);
            return true;
        }

        return false;
    }

    private async Task<TaskExecutionResult> ExecuteTask(TaskHandlerExecutor task, object handler, IServiceProvider serviceProvider, CancellationToken serviceToken, ITaskStorage? taskStorage)
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
        var attempt = 0;
        RateLimitGateResult? retryDeferral = null;

        // Use GetTimestamp/GetElapsedTime to avoid Stopwatch allocation
        var startTime = Stopwatch.GetTimestamp();
        await DoExecute();
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
                handlerCallback = callback;
            }

            //Use WaitAsync for cancelling:
            //https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md#cancelling-uncancellable-operations
            await retryPolicy.Execute(
                action: async retryToken =>
                {
                    // Retry throttling (BEFORE the timeout branch: the budget wait must never
                    // erode the per-attempt timeout). The FIRST attempt skips re-acquisition —
                    // the gate pass that admitted this delivery holds its budget. Retries of a
                    // ThrottleRetries policy re-acquire; a near slot is awaited in-slot by the
                    // gate, a far slot re-parks the task (Design A path) instead of surfacing a
                    // retryable exception, which would consume the shared retry budget and mark
                    // never-executed tasks Failed.
                    if (attempt++ > 0
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
                    // Invoke handler's OnRetry method using cached MethodInfo
                    await InvokeOnRetryCallback(task, handler, handlerOptions.OnRetryMethod, attemptNumber, exception, delay)
                        .ConfigureAwait(false);
                }
            ).ConfigureAwait(false);
        }
    }

    private async Task ExecuteWithTimeout(Func<CancellationToken, Task> action, TimeSpan timeout,
                                          CancellationToken token)
    {
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
            timeoutCts.Cancel();
        }
    }

    private async Task ExecuteDisposeHandler(object handler)
    {
        if (handler is IAsyncDisposable asyncDisposable)
        {
            try
            {
                await asyncDisposable.DisposeAsync();
                logger.LogDebug("Disposed handler {HandlerType}", handler.GetType().Name);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Error disposing handler {HandlerType}", handler.GetType().Name);
            }
        }
    }

    // Disposes the EverTask-owned scope an eager handler was resolved into (L27), releasing the handler
    // instance(s) without pinning them in the root container. Disposal must never fail task execution.
    private async Task ExecuteDisposeHandlerScope(IAsyncDisposable handlerScope)
    {
        try
        {
            await handlerScope.DisposeAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error disposing eager handler scope");
        }
    }

    /// <summary>
    /// Gets the OnStarted callback from executor (eager mode) or extracts from handler (lazy mode)
    /// </summary>
    private Func<Guid, ValueTask>? GetStartedCallback(TaskHandlerExecutor task, object handler)
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
    private Func<Guid, ValueTask>? GetCompletedCallback(TaskHandlerExecutor task, object handler)
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
    private Func<Guid, Exception?, string, ValueTask>? GetErrorCallback(TaskHandlerExecutor task, object handler)
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
            RegisterError(e, task,
                "Error occurred executing while executing the callback override {0} task with id {1}.", callbackName,
                task.PersistenceId);
        }
    }

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
            logger.LogError(ex,
                "Error occurred while executing OnRetry callback for task {TaskId} attempt {Attempt}",
                task.PersistenceId, attemptNumber);
        }
    }

    /// <summary>
    /// Publishes retry event for monitoring integrations (SignalR, etc.)
    /// </summary>
    private void RegisterRetryEvent(
        TaskHandlerExecutor task,
        int attemptNumber,
        Exception exception,
        TimeSpan delay)
    {
        var message = $"Task {task.PersistenceId} retry attempt {attemptNumber} after {delay.TotalMilliseconds}ms";

        RegisterEvent(
            SeverityLevel.Warning,
            task,
            message,
            exception);
    }

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
            RegisterError(e, task,
                "Error occurred executing the callback override OnError for task with id {0}.",
                task.PersistenceId);
        }
    }

    private async Task HandleExceptionAsync(Exception ex, TaskHandlerExecutor task, object handler,
                                            IReadOnlyList<TaskExecutionLog>? executionLogs,
                                            CancellationToken serviceToken,
                                            ITaskStorage? taskStorage)
    {
        if (ex is OperationCanceledException oce)
        {
            // A user cancel (blacklisted id) must classify as terminal Cancelled even when the service
            // token is ALSO cancelled (shutdown racing the user cancel): otherwise it would be
            // ServiceStopped (recoverable) and re-execute at the next restart (F17).
            var userCancelled = workerBlacklist.IsBlacklisted(task.PersistenceId);
            if (taskStorage != null)
            {
                if (serviceToken.IsCancellationRequested && !userCancelled)
                    await taskStorage.SetCancelledByService(task.PersistenceId, oce, task.AuditLevel).ConfigureAwait(false);
                else
                    await taskStorage.SetCancelledByUser(task.PersistenceId, task.AuditLevel).ConfigureAwait(false);
            }

            await ExecuteCallback(GetErrorCallback(task, handler), task, oce,
                $"Task with id {task.PersistenceId} was cancelled").ConfigureAwait(false);

            RegisterWarning(oce, task, executionLogs, "Task with id {0} was cancelled by service while stopping.", task.PersistenceId);
        }
        else
        {
            // Logica per le altre eccezioni
            if (taskStorage != null)
                await taskStorage.SetStatus(task.PersistenceId, QueuedTaskStatus.Failed, ex, task.AuditLevel, null, serviceToken)
                                 .ConfigureAwait(false);

            // G11: the retry policy throws AggregateException("All retry attempts failed", ...) when
            // retries are exhausted. The PERSISTED status and the error log keep that aggregate (full
            // attempt history), but OnError must receive the REAL handler exception so type-based
            // handling (dead-letter, compensation) keyed on the exception type works — consistent with
            // the non-retryable path that delivers the raw exception.
            await ExecuteCallback(GetErrorCallback(task, handler), task, UnwrapForCallback(ex),
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterError(ex, task, executionLogs, "Error occurred executing task with id {0}.", task.PersistenceId);
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

    private async Task QueueNextOccourrence(TaskHandlerExecutor task, double executionTimeMs, ITaskStorage? taskStorage,
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
            logger.LogInformation(
                "Recurring task {TaskId} was cancelled: the series is stopped, no next occurrence scheduled.",
                task.PersistenceId);
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
                logger.LogInformation(
                    "Recurring task {TaskId} was cancelled: the series is stopped, no next occurrence scheduled.",
                    task.PersistenceId);
                return;
            }
        }

        // Run counter source: the row already read from storage when present (no second round-trip),
        // otherwise an in-memory counter so the series keeps running and still honors MaxRuns/RunUntil
        // without persistence (F18).
        var currentRun = taskStorage != null
            ? current?.CurrentRunCount ?? 0
            : _inMemoryRunCounts.GetValueOrDefault(task.PersistenceId);

        // Fix for schedule drift: Use the scheduled execution time as base for next calculation,
        // not the current time. This ensures recurring tasks maintain their intended schedule
        // even when execution is delayed due to system load or downtime.
        // See: docs/recurring-task-schedule-drift-fix.md
        //
        // task.ExecutionTime is the scheduled execution time for THIS run:
        // - For first dispatch: the original scheduled time from the builder
        // - For tasks loaded from storage (after restart): NextRunUtc from the database
        //   (set in WorkerService.cs when loading pending tasks)
        var scheduledTime = task.ExecutionTime ?? DateTimeOffset.UtcNow;

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
        if (skipAheadTo.HasValue && skipAheadTo.Value <= DateTimeOffset.UtcNow)
            skipAheadTo = null;

        var runNumber = countsAsRun ? currentRun + 1 : currentRun;
        var result    = task.RecurringTask.CalculateNextValidRun(
            scheduledTime, runNumber, referenceTime: countsAsRun ? null : skipAheadTo, isRecovery: !countsAsRun,
            computeSkippedCount: countsAsRun);

        // Log skipped occurrences if any
        if (result.SkippedCount > 0)
        {
            logger.LogInformation(
                "Task {TaskId} skipped {SkippedCount} missed occurrence(s) to maintain schedule",
                task.PersistenceId, result.SkippedCount);
        }

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
                // On a successful run the Completed status is written in the SAME atomic operation as the
                // advance (CU14/L29); otherwise (failure) the status was already set and we only advance.
                if (markCompleted)
                    await taskStorage.CompleteRecurringRun(task.PersistenceId, executionTimeMs, result.NextRun,
                                                           task.AuditLevel)
                                     .ConfigureAwait(false);
                else
                    await taskStorage.UpdateCurrentRun(task.PersistenceId, executionTimeMs, result.NextRun, task.AuditLevel)
                                     .ConfigureAwait(false);
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
            var updatedTask = task.ToLazy() with { ExecutionTime = result.NextRun };
            scheduler.Schedule(updatedTask, result.NextRun);
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
            // series. SetRecurringSeriesCompleted does both atomically WITHOUT counting the skip (Option B).
            // Only the series-END writes here; a skip that continues still writes nothing (no-storage-write skip).
            if (!countsAsRun && taskStorage != null)
                await taskStorage.SetRecurringSeriesCompleted(task.PersistenceId, executionTimeMs, task.AuditLevel)
                                 .ConfigureAwait(false);
        }
    }

    #region Logging and event pubblishing

    private void RegisterInfo(TaskHandlerExecutor executor, IReadOnlyList<TaskExecutionLog>? executionLogs, string message, params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Information, executor, message, null, executionLogs, messageArgs);

    // internal (not private): the deterministic L30/F24 gates drive RegisterEvent through this overload.
    internal void RegisterInfo(TaskHandlerExecutor executor, string message, params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Information, executor, message, null, null, messageArgs);

    private void RegisterWarning(Exception? exception, TaskHandlerExecutor executor, IReadOnlyList<TaskExecutionLog>? executionLogs, string message,
                                 params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Warning, executor, message, exception, executionLogs, messageArgs);

    private void RegisterWarning(Exception? exception, TaskHandlerExecutor executor, string message,
                                 params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Warning, executor, message, exception, null, messageArgs);

    private void RegisterError(Exception exception, TaskHandlerExecutor executor, IReadOnlyList<TaskExecutionLog>? executionLogs, string message,
                               params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Error, executor, message, exception, executionLogs, messageArgs);

    private void RegisterError(Exception exception, TaskHandlerExecutor executor, string message,
                               params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Error, executor, message, exception, null, messageArgs);

    private void RegisterEvent(SeverityLevel severity, TaskHandlerExecutor executor, string message,
                               Exception? exception = null, IReadOnlyList<TaskExecutionLog>? executionLogs = null, params object[] messageArgs)
    {
        var logLevel = severity switch
        {
            SeverityLevel.Information => LogLevel.Information,
            SeverityLevel.Warning     => LogLevel.Warning,
            _                          => LogLevel.Error
        };

        // L30: when nobody consumes the event — the level is filtered out AND there are no monitoring
        // subscribers — skip the string.Format + object[] boxing entirely. Otherwise that cost was paid
        // per task even for a discarded Info event.
        if (!logger.IsEnabled(logLevel) && TaskEventOccurredAsync == null)
            return;

        // Format message once for both logging and event publishing
        // This avoids "Message template should be compile time constant" warning
        var formattedMessage = messageArgs.Length > 0
            ? string.Format(message, messageArgs)
            : message;

        switch (severity)
        {
            case SeverityLevel.Information:
                logger.LogInformation(formattedMessage);
                break;
            case SeverityLevel.Warning:
                logger.LogWarning(exception, formattedMessage);
                break;
            case SeverityLevel.Error:
            default:
                logger.LogError(exception, formattedMessage);
                break;
        }

        try
        {
            PublishEvent(executor, severity, formattedMessage, exception, executionLogs);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to publish event {Message}", formattedMessage);
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
                    logger.LogError(ex, "Event handler failed for task {TaskId}", data.TaskId);
                }
                finally
                {
                    _monitoringConcurrency.Release();
                }
            });
        }
    }

    private EverTaskEventData CreateEventDataCached(TaskHandlerExecutor executor, SeverityLevel severity,
                                                     string message, Exception? exception, IReadOnlyList<TaskExecutionLog>? executionLogs = null)
    {
        // Cache task JSON (weak reference - GC'd when task is collected). EverTaskJson uses private, isolated
        // System.Text.Json options (L33) so a hostile global JSON configuration cannot alter the monitoring
        // payload either.
        var taskJson = TaskJsonCache.GetValue(executor.Task, EverTaskJson.Serialize);

        // Cache type strings (permanent cache - types never unload)
        var taskType = TypeStringCache.GetOrAdd(executor.Task.GetType(), type => type.ToString());

        // Handler type: get from Handler instance (eager) or HandlerTypeName (lazy)
        string handlerType;
        if (executor.Handler != null)
        {
            handlerType = TypeStringCache.GetOrAdd(executor.Handler.GetType(), type => type.ToString());
        }
        else if (!string.IsNullOrEmpty(executor.HandlerTypeName))
        {
            // Lazy mode: extract simple type name from AssemblyQualifiedName
            handlerType = executor.HandlerTypeName.Split(',')[0].Trim();
        }
        else
        {
            handlerType = "Unknown";
        }

        return new EverTaskEventData(
            executor.PersistenceId,
            DateTimeOffset.UtcNow,
            severity.ToString(),
            taskType,
            handlerType,
            taskJson,
            message,
            exception?.ToDetailedString(),
            executionLogs
        );
    }

    #endregion

    /// <summary>
    /// Creates a log capture instance for the task execution.
    /// Always forwards to ILogger, optionally persists to database based on configuration.
    /// </summary>
    private ITaskLogCaptureInternal CreateLogCapture(Type handlerType, Guid taskId, IServiceProvider serviceProvider)
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
