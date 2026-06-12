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
    IRateLimitGate? rateLimitGate = null) : IEverTaskWorkerExecutor
{
    // Performance optimization: Cache for event data to avoid repeated serialization
    private static readonly ConditionalWeakTable<IEverTask, string> TaskJsonCache = new();
    private static readonly ConcurrentDictionary<Type, string> TypeStringCache = new();

    // Performance optimization: Cache handler options to avoid runtime casts per execution.
    // Stores the RAW handler overrides (null = no override): the fallback chain
    // handler → queue → global is resolved per execution, NOT baked into the cache.
    private static readonly ConcurrentDictionary<Type, HandlerOptionsCache> HandlerOptionsInternalCache = new();

    private record HandlerOptionsCache(IRetryPolicy? RetryPolicy, TimeSpan? Timeout, MethodInfo? OnRetryMethod);

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


    public async ValueTask DoWork(TaskHandlerExecutor task, CancellationToken serviceToken)
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

                    return;
                }

                // One-shot: surface the typed exception through the standard failure path
                // (SetStatus Failed + OnError) handled by the catch below
                throw CreateRejectionException(task, rejection);
            }

            if (taskStorage != null)
                await taskStorage.SetCompleted(task.PersistenceId, executionTime, task.AuditLevel).ConfigureAwait(false);

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
            // Dispose handler if created (BEFORE recurring scheduling)
            // Only dispose explicitly in eager mode - lazy mode handlers are disposed by the async scope
            if (handler != null && !task.IsLazy)
            {
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
                await QueueNextOccourrence(task, executionTime, taskStorage);
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
    /// normal next-occurrence path (the skip counts toward MaxRuns, same semantics as
    /// downtime); the series stays alive and no callback is invoked.
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

            await QueueNextOccourrence(task, 0, taskStorage).ConfigureAwait(false);
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

        // Performance optimization: Cache handler options to avoid repeated casts
        // Use GetOrAdd overload with factoryArgument to avoid closure allocation
        var handlerOptions = HandlerOptionsInternalCache.GetOrAdd(
            handler.GetType(),  // Use resolved handler
            static (_, handlerInstance) =>
            {
                // Resolve OnRetry method once and cache it
                var onRetryMethod = handlerInstance.GetType().GetMethod(
                    nameof(IEverTaskHandler<IEverTask>.OnRetry),
                    BindingFlags.Public | BindingFlags.Instance);

                // Cast only once per handler type (first time): cache the RAW overrides
                return handlerInstance is IEverTaskHandlerOptions handlerOpts
                    ? new HandlerOptionsCache(handlerOpts.RetryPolicy, handlerOpts.Timeout, onRetryMethod)
                    : new HandlerOptionsCache(null, null, onRetryMethod);
            },
            handler);

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

    /// <summary>
    /// Gets the OnStarted callback from executor (eager mode) or extracts from handler (lazy mode)
    /// </summary>
    private Func<Guid, ValueTask>? GetStartedCallback(TaskHandlerExecutor task, object handler)
    {
        // If executor has callback (eager mode), use it
        if (task.HandlerStartedCallback != null)
            return task.HandlerStartedCallback;

        // Lazy mode: extract from handler using reflection
        // All handlers implement IEverTaskHandler<T> which has OnStarted method
        var onStartedMethod = handler.GetType().GetMethod("OnStarted");

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

        // Lazy mode: extract from handler using reflection
        var onCompletedMethod = handler.GetType().GetMethod("OnCompleted");

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

        // Lazy mode: extract from handler using reflection
        var onErrorMethod = handler.GetType().GetMethod("OnError");

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
                "Error occurred executing while executing the callback override OnError task with id {1}.",
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
            if (taskStorage != null)
            {
                if (serviceToken.IsCancellationRequested)
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

            await ExecuteCallback(GetErrorCallback(task, handler), task, ex,
                $"Error occurred executing the task with id {task.PersistenceId}").ConfigureAwait(false);

            RegisterError(ex, task, executionLogs, "Error occurred executing task with id {0}.", task.PersistenceId);
        }
    }

    private async Task QueueNextOccourrence(TaskHandlerExecutor task, double executionTimeMs, ITaskStorage? taskStorage)
    {
        if (task.RecurringTask == null) return;

        // If we reach here for a recurring task, it means an error occurred
        // In that case, we still need to schedule the next run
        if (taskStorage != null)
        {
            var currentRun = await taskStorage.GetCurrentRunCount(task.PersistenceId);

            // Fix for schedule drift: Use the scheduled execution time as base for next calculation,
            // not the current time. This ensures recurring tasks maintain their intended schedule
            // even when execution is delayed due to system load or downtime.
            // See: docs/recurring-task-schedule-drift-fix.md

            // task.ExecutionTime is the scheduled execution time for THIS run:
            // - For first dispatch: the original scheduled time from the builder
            // - For tasks loaded from storage (after restart): NextRunUtc from the database
            //   (set in WorkerService.cs when loading pending tasks)
            //
            // We use this directly to calculate the next run. No reconstruction needed
            // because task.ExecutionTime is already the correct scheduled time for THIS run.
            var scheduledTime = task.ExecutionTime ?? DateTimeOffset.UtcNow;

            // Use extension method to calculate next valid run and get skip information
            var result = task.RecurringTask.CalculateNextValidRun(scheduledTime, currentRun + 1);

            // Log skipped occurrences if any
            if (result.SkippedCount > 0)
            {
                logger.LogInformation(
                    "Task {TaskId} skipped {SkippedCount} missed occurrence(s) to maintain schedule",
                    task.PersistenceId, result.SkippedCount);
            }

            await taskStorage.UpdateCurrentRun(task.PersistenceId, executionTimeMs, result.NextRun, task.AuditLevel)
                             .ConfigureAwait(false);

            if (result.NextRun.HasValue)
            {
                // Update ExecutionTime for the next run so that subsequent calculations
                // use the correct scheduled time (not the original time from first dispatch)
                var updatedTask = task with { ExecutionTime = result.NextRun };
                scheduler.Schedule(updatedTask, result.NextRun);
            }
        }
    }

    #region Logging and event pubblishing

    private void RegisterInfo(TaskHandlerExecutor executor, IReadOnlyList<TaskExecutionLog>? executionLogs, string message, params object[] messageArgs) =>
        RegisterEvent(SeverityLevel.Information, executor, message, null, executionLogs, messageArgs);

    private void RegisterInfo(TaskHandlerExecutor executor, string message, params object[] messageArgs) =>
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
            });
        }
    }

    private EverTaskEventData CreateEventDataCached(TaskHandlerExecutor executor, SeverityLevel severity,
                                                     string message, Exception? exception, IReadOnlyList<TaskExecutionLog>? executionLogs = null)
    {
        // Cache task JSON (weak reference - GC'd when task is collected)
        var taskJson = TaskJsonCache.GetValue(executor.Task, JsonConvert.SerializeObject);

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
