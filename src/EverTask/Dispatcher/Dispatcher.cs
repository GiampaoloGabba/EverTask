using EverTask.Configuration;
using EverTask.RateLimiting;
using EverTask.Scheduler.Recurring.Builder;
using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace EverTask.Dispatcher;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the Mediator.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Mediator.cs

/// <inheritdoc />
public class Dispatcher(
    IServiceProvider serviceProvider,
    IWorkerQueueManager queueManager,
    IScheduler scheduler,
    EverTaskServiceConfiguration serviceConfiguration,
    IEverTaskLogger<Dispatcher> logger,
    IWorkerBlacklist workerBlacklist,
    ICancellationSourceProvider cancellationSourceProvider,
    ITaskStorage? taskStorage = null) : ITaskDispatcherInternal
{
    // Cache for compiled TaskHandlerWrapper constructors to avoid reflection overhead
    private static readonly ConcurrentDictionary<Type, Func<TaskHandlerWrapper>> WrapperFactoryCache = new();

    // Per-taskKey critical-section lock: serializes the GetByTaskKey -> decide -> Persist/Update of a
    // single taskKey across concurrent dispatches so two dispatches can never both insert (or one
    // delete under the other), the source of the taskKey dedup races (G13/G14/CU23/G17).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _taskKeyLocks = new();

    // Resolved lazily: optional components (registered by AddEverTask, may be absent in
    // hand-wired unit-test providers)
    private IGateInvalidationRegistry? _gateInvalidation;
    private bool _gateInvalidationResolved;
    private RateLimitParkingLot? _parkingLot;
    private bool _parkingLotResolved;
    private TaskDeliveryRegistry? _deliveryRegistry;
    private bool _deliveryRegistryResolved;

    private TaskDeliveryRegistry? DeliveryRegistry
    {
        get
        {
            if (!_deliveryRegistryResolved)
            {
                _deliveryRegistry         = serviceProvider.GetService<TaskDeliveryRegistry>();
                _deliveryRegistryResolved = true;
            }

            return _deliveryRegistry;
        }
    }

    private IGateInvalidationRegistry? GateInvalidation
    {
        get
        {
            if (!_gateInvalidationResolved)
            {
                _gateInvalidation         = serviceProvider.GetService<IGateInvalidationRegistry>();
                _gateInvalidationResolved = true;
            }

            return _gateInvalidation;
        }
    }

    private RateLimitParkingLot? ParkingLot
    {
        get
        {
            if (!_parkingLotResolved)
            {
                _parkingLot         = serviceProvider.GetService<RateLimitParkingLot>();
                _parkingLotResolved = true;
            }

            return _parkingLot;
        }
    }

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, null, null, null, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, TimeSpan executionDelay, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionDelay, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, DateTimeOffset executionTime, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionTime, null, null, cancellationToken, null, taskKey, auditLevel);

    /// <inheritdoc />
    public async Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken cancellationToken = default)
    {
        var builder = new RecurringTaskBuilder();
        recurring(builder);

        return await ExecuteDispatch(task, null, builder.RecurringTask, null, cancellationToken, null, taskKey, auditLevel).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Cancel(Guid taskId, CancellationToken cancellationToken = default)
    {
        // Blacklist FIRST, before persisting Cancelled: a concurrent enqueue (scheduler slot / gate)
        // racing this Cancel must see the blacklist and be discarded, instead of slipping through the
        // (still-false) blacklist check and writing SetQueued over the Cancelled status we are about to
        // persist (CU13).
        workerBlacklist.Add(taskId);

        // Must not abort the rest of the cleanup if the CTS was already disposed (CU12).
        cancellationSourceProvider.CancelTokenForTask(taskId);

        // Drop any occurrence still parked in the scheduler so it isn't even dispatched.
        scheduler.TryUnschedule(taskId);

        // Invalidate any rate-limit gate operation in flight for this task: a deferral being re-parked
        // concurrently with this Cancel must not survive it. The parking-lot entry is released too
        // (a cancelled parked task never re-enters a channel).
        GateInvalidation?.Invalidate(taskId);
        ParkingLot?.Remove(taskId);

        // Persist Cancelled LAST so it is the final write of the cancel: any SetQueued a racing enqueue
        // managed to issue before the blacklist took effect is overwritten by Cancelled.
        if (taskStorage != null)
        {
            await taskStorage.SetCancelledByUser(taskId, AuditLevel.ErrorsOnly).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null) =>
        await ExecuteDispatch(task, null, null, null, ct, existingTaskId, taskKey, null).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, TimeSpan? executionDelay = null,
                                            CancellationToken ct = default,
                                            Guid? existingTaskId = null,
                                            string? taskKey = null,
                                            AuditLevel? auditLevel = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var executionTime = executionDelay != null
                                ? DateTimeOffset.UtcNow.Add(executionDelay.Value)
                                : (DateTimeOffset?)null;

        return await ExecuteDispatch(task, executionTime, null, null, ct, existingTaskId, taskKey, auditLevel).ConfigureAwait(false);
    }

    public async Task<Guid> ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null,
                                            RecurringTask? recurring = null, int? currentRun = null,
                                            CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null, AuditLevel? auditLevel = null,
                                            bool isRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Serialize the read-decide-write of this taskKey against concurrent dispatches (held for the
        // whole dispatch, including the enqueue). No-op when there is no taskKey, no storage, or the id
        // is already known (recovery / internal re-dispatch).
        using var taskKeyLock = await AcquireTaskKeyLockAsync(taskKey, existingTaskId, ct).ConfigureAwait(false);

        // Track existing task's NextRunUtc and CurrentRunCount for recurring tasks to preserve schedule across restarts
        DateTimeOffset? existingNextRunUtc = null;
        int? existingCurrentRunCount = null;

        // Handle taskKey resolution if provided
        if (!string.IsNullOrWhiteSpace(taskKey) && taskStorage != null && existingTaskId == null)
        {
            var existingTask = await taskStorage.GetByTaskKey(taskKey, ct).ConfigureAwait(false);

            if (existingTask != null)
            {
                logger.LogInformation("Found existing task with key {TaskKey}, ID {TaskId}, Status {Status}",
                    taskKey, existingTask.Id, existingTask.Status);

                // An IMMEDIATE one-shot re-dispatch of a row whose delivery is already in flight (in a
                // channel or executing) would either lose the new payload (the in-flight delivery already
                // captured the old one) or, on a terminal Remove, delete the row under the live delivery
                // (double execution). Reject it and return the existing id (CU6/L31, G17). A delayed or
                // recurring re-dispatch parks a fresh occurrence in the scheduler, so it is left to proceed.
                if (executionTime == null && recurring == null && DeliveryRegistry?.IsDelivering(existingTask.Id) == true)
                {
                    logger.LogWarning(
                        "Dispatch with task key {TaskKey} discarded: task {TaskId} has a delivery in flight; " +
                        "the new dispatch is rejected to avoid losing the payload or double-executing.",
                        taskKey, existingTask.Id);
                    return existingTask.Id;
                }

                // A recurring row must not be converted to a one-shot by a taskKey re-dispatch that carries
                // no recurring config — that would silently destroy its schedule and history (G16).
                if (existingTask.IsRecurring && recurring == null)
                {
                    logger.LogWarning(
                        "Dispatch with task key {TaskKey} discarded: task {TaskId} is recurring and cannot be " +
                        "converted to a one-shot via taskKey.", taskKey, existingTask.Id);
                    return existingTask.Id;
                }

                // For RECURRING tasks: preserve history and don't remove/recreate on terminal status
                // Recurring tasks with Completed/Failed status are not truly "terminated" - they should continue
                if (existingTask.IsRecurring && recurring != null)
                {
                    // If task is in progress, return existing ID (cannot modify running task)
                    if (existingTask.Status is QueuedTaskStatus.InProgress)
                    {
                        logger.LogWarning(
                            "Dispatch with task key {TaskKey} discarded: recurring task {TaskId} is in progress and nothing was scheduled. " +
                            "Self-redispatch from inside a handler must use a null or per-attempt task key.",
                            taskKey, existingTask.Id);
                        return existingTask.Id;
                    }

                    // For all other statuses (including Completed/Failed): update, don't remove
                    logger.LogInformation("Updating recurring task {TaskId} (preserving history)", existingTask.Id);
                    existingTaskId = existingTask.Id;

                    // Preserve existing NextRunUtc (even if in the past) to maintain schedule rhythm
                    // Also preserve CurrentRunCount for correct calculation
                    if (existingTask.NextRunUtc.HasValue)
                    {
                        existingNextRunUtc = existingTask.NextRunUtc;
                        existingCurrentRunCount = existingTask.CurrentRunCount;
                        logger.LogDebug("Preserving existing NextRunUtc {NextRunUtc} and CurrentRunCount {CurrentRunCount} for recurring task {TaskId}",
                            existingNextRunUtc, existingCurrentRunCount, existingTask.Id);
                    }
                }
                // For NON-RECURRING tasks: original behavior
                else
                {
                    // If task is terminated (Completed/Failed/Cancelled), remove it and create new
                    if (existingTask.Status is QueuedTaskStatus.Completed
                        or QueuedTaskStatus.Failed
                        or QueuedTaskStatus.Cancelled
                        or QueuedTaskStatus.ServiceStopped)
                    {
                        logger.LogInformation("Removing terminated task {TaskId} to create new one", existingTask.Id);
                        await taskStorage.Remove(existingTask.Id, ct).ConfigureAwait(false);
                    }
                    // If task is in progress, return existing ID (cannot modify running task)
                    else if (existingTask.Status is QueuedTaskStatus.InProgress)
                    {
                        logger.LogWarning(
                            "Dispatch with task key {TaskKey} discarded: task {TaskId} is in progress and nothing was scheduled. " +
                            "Self-redispatch from inside a handler must use a null or per-attempt task key.",
                            taskKey, existingTask.Id);
                        return existingTask.Id;
                    }
                    // If task is pending (Queued/WaitingQueue/Pending), update it
                    else
                    {
                        logger.LogInformation("Updating pending task {TaskId}", existingTask.Id);
                        existingTaskId = existingTask.Id;
                    }
                }
            }
        }

        DateTimeOffset? nextRun = null;

        // Recovery re-dispatch of a recurring task: executionTime is the stored NextRunUtc
        // (or ScheduledExecutionUtc). Treat it as the preserved next occurrence, exactly like
        // the taskKey path: using it as a bare recalculation base would compute the occurrence
        // strictly AFTER it, skipping one occurrence at every restart (and killing the last
        // occurrence before RunUntil).
        if (isRecovery && recurring != null && existingNextRunUtc == null && executionTime.HasValue)
        {
            existingNextRunUtc      = executionTime;
            existingCurrentRunCount = currentRun;
        }

        if (recurring != null)
        {
            // If we have a valid existing NextRunUtc from a task with TaskKey
            if (existingNextRunUtc.HasValue)
            {
                // If NextRunUtc is still in the future, use it directly
                if (existingNextRunUtc.Value > DateTimeOffset.UtcNow)
                {
                    nextRun = existingNextRunUtc;
                    executionTime = nextRun;
                    logger.LogInformation("Using preserved NextRunUtc {NextRun} for recurring task (still in future)", nextRun);
                }
                else
                {
                    // NextRunUtc is in the past - use it as base to maintain schedule rhythm
                    // Use CalculateNextValidRun with O(1) math to skip forward while preserving rhythm
                    var result = recurring.CalculateNextValidRun(
                        existingNextRunUtc.Value,
                        existingCurrentRunCount ?? 0);

                    if (result.NextRun == null)
                        throw new ArgumentException("Invalid scheduler recurring expression", nameof(recurring));

                    nextRun = result.NextRun;
                    executionTime = nextRun;
                    logger.LogInformation("Calculated NextRunUtc {NextRun} from past NextRunUtc {PastNextRun} (skipped {SkippedCount})",
                        nextRun, existingNextRunUtc, result.SkippedCount);
                }
            }
            else
            {
                // New task - calculate from current time
                var scheduledTime = (existingTaskId != null && executionTime.HasValue)
                    ? executionTime.Value
                    : DateTimeOffset.UtcNow;

                var result = recurring.CalculateNextValidRun(scheduledTime, currentRun ?? 0);

                if (result.NextRun == null)
                    throw new ArgumentException("Invalid scheduler recurring expression", nameof(recurring));

                nextRun = result.NextRun;
                executionTime = nextRun;
            }
        }

        var taskType = task.GetType();

        var handler = CreateCachedWrapper(taskType);

        // Use provided audit level or fall back to global default
        var effectiveAuditLevel = auditLevel ?? serviceConfiguration.DefaultAuditLevel;

        // Lazy executors never carry a handler instance: the wrapper resolves one in a
        // short-lived scope for metadata extraction only, and the worker resolves a fresh
        // instance in its per-task scope at execution time
        var useLazyExecutor = ShouldUseLazyResolution(executionTime, recurring);

        var executor = await handler.Handle(task, executionTime, recurring, serviceProvider, effectiveAuditLevel,
            existingTaskId, taskKey, useLazyExecutor).ConfigureAwait(false);

        // Persist or update task (lazy serialize only if storage exists).
        // Recovery dispatches skip the update entirely: the definition was just read from storage
        // unchanged, and rewriting it could overwrite a concurrent live re-registration via taskKey
        // (lost update). Recalculated schedule data is re-derived deterministically at the next
        // restart and persisted by UpdateCurrentRun after each run.
        if (taskStorage != null && !(isRecovery && existingTaskId != null))
        {
            var taskEntity = executor.ToQueuedTask();

            try
            {
                if (existingTaskId == null)
                {
                    // New task - persist it
                    logger.LogDebug("Persisting Task: {Type}", taskEntity.Type);
                    await taskStorage.Persist(taskEntity, ct).ConfigureAwait(false);
                }
                else
                {
                    // Existing task - update it
                    logger.LogDebug("Updating Task: {Type}", taskEntity.Type);
                    await taskStorage.UpdateTask(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                // A concurrent insert may have won the taskKey unique constraint (cross-process, or any
                // path that bypassed the in-process keyed lock): re-read the winner and return its id
                // instead of proceeding with our own duplicate PersistenceId (G14/CU23).
                if (existingTaskId == null && !string.IsNullOrWhiteSpace(taskKey))
                {
                    var winner = await taskStorage.GetByTaskKey(taskKey, ct).ConfigureAwait(false);
                    if (winner != null && winner.Id != taskEntity.Id)
                    {
                        logger.LogWarning(
                            "Task key {TaskKey} was won by a concurrent dispatch ({WinnerId}); returning the winner id.",
                            taskKey, winner.Id);
                        return winner.Id;
                    }
                }

                logger.LogError(e, "Unable to {Action} the task {FullType}",
                    existingTaskId == null ? "persist" : "update", task);
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }

        // The executor is already lazy when useLazyExecutor is true (built by the wrapper
        // without a handler instance), eager otherwise
        TaskHandlerExecutor executorToSchedule = executor;

        if (executorToSchedule.ExecutionTime > DateTimeOffset.UtcNow || recurring != null)
        {
            scheduler.Schedule(executorToSchedule, nextRun);
        }
        else
        {
            // Immediate re-dispatch of an existing task (e.g. taskKey update of a previously delayed
            // task): invalidate any stale registration still parked in the scheduler — or in the
            // dequeue->re-park gate limbo the scheduler cannot see — BEFORE enqueuing, so a concurrent
            // re-park of the SAME id (e.g. a rate-limit deferral of the new delivery) is created
            // afterwards and survives the invalidation.
            InvalidateStaleRegistration(existingTaskId, executorToSchedule.PersistenceId);

            // Determine queue name with automatic routing for recurring tasks
            var queueName = executorToSchedule.QueueName ??
                            (executorToSchedule.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);

            if (isRecovery)
            {
                // Recovery path: never fail fast, wait for queue space (consumers are draining concurrently)
                await queueManager.EnqueueBlocking(queueName, executorToSchedule, ct).ConfigureAwait(false);
            }
            else if (existingTaskId != null)
            {
                try
                {
                    await queueManager.TryEnqueue(queueName, executorToSchedule, ct).ConfigureAwait(false);
                }
                catch
                {
                    // The immediate enqueue of an already-accepted (parked) task failed (full
                    // ThrowException queue, or a cancelled Wait that threw). Its parked occurrence was
                    // just dropped above, so re-schedule it for retry instead of losing it / leaking its
                    // parking-lot reservation, then propagate the failure (CU15).
                    scheduler.Schedule(executorToSchedule with { ExecutionTime = DateTimeOffset.UtcNow });
                    throw;
                }
            }
            else
            {
                await queueManager.TryEnqueue(queueName, executorToSchedule, ct).ConfigureAwait(false);
            }
        }

        return executor.PersistenceId;
    }

    /// <summary>
    /// Immediate re-dispatch of an existing task: drop any registration still parked in the scheduler —
    /// or in the dequeue→re-park gate limbo the scheduler cannot see — so a stale occurrence cannot fire
    /// a duplicate. Done BEFORE the enqueue so a re-park of the same id (rate-limit deferral) survives.
    /// </summary>
    private void InvalidateStaleRegistration(Guid? existingTaskId, Guid persistenceId)
    {
        if (existingTaskId == null)
            return;

        scheduler.TryUnschedule(persistenceId);
        GateInvalidation?.Invalidate(persistenceId);
    }

    private async ValueTask<IDisposable> AcquireTaskKeyLockAsync(string? taskKey, Guid? existingTaskId, CancellationToken ct)
    {
        // Only the taskKey resolution path needs serialization: no taskKey, no storage, or an
        // already-known id (recovery / internal re-dispatch) never reads/decides on a taskKey.
        if (string.IsNullOrWhiteSpace(taskKey) || taskStorage == null || existingTaskId != null)
            return NoopDisposable.Instance;

        var gate = _taskKeyLocks.GetOrAdd(taskKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreReleaser(gate);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// Determines if a task should use lazy handler resolution based on adaptive algorithm.
    /// </summary>
    /// <param name="executionTime">Scheduled execution time (null for immediate)</param>
    /// <param name="recurring">Recurring task configuration (null for one-time)</param>
    /// <returns>True if task should use lazy mode, false for eager mode</returns>
    private bool ShouldUseLazyResolution(DateTimeOffset? executionTime, RecurringTask? recurring)
    {
        // Feature disabled globally
        if (!serviceConfiguration.UseLazyHandlerResolution)
        {
            logger.LogDebug("Lazy handler resolution disabled globally (UseLazyHandlerResolution = false)");
            return false;
        }

        // Recurring tasks: adaptive based on interval
        if (recurring != null)
        {
            var minInterval = recurring.GetMinimumInterval();
            return minInterval >= TimeSpan.FromMinutes(5);
        }

        // Delayed tasks: lazy if delay >= 30 minutes
        if (executionTime.HasValue)
        {
            var delay = executionTime.Value - DateTimeOffset.UtcNow;
            return delay >= TimeSpan.FromMinutes(30);
        }

        // Immediate tasks: lazy by default (MEM-2). An eager handler resolved at dispatch from
        // the singleton dispatcher's root provider is pinned in the root container's disposables
        // list until shutdown; the worker resolves and disposes a fresh instance per task anyway.
        return true;
    }

    /// <summary>
    /// Creates or retrieves a cached TaskHandlerWrapper instance for the specified task type.
    /// Uses compiled Expression trees to avoid reflection overhead on repeated calls.
    /// </summary>
    private static TaskHandlerWrapper CreateCachedWrapper(Type taskType)
    {
        var factory = WrapperFactoryCache.GetOrAdd(taskType, type =>
        {
            // Create wrapper type: TaskHandlerWrapperImp<TTask>
            var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(type);

            // Get parameterless constructor
            var constructor = wrapperType.GetConstructor(Type.EmptyTypes)
                              ?? throw new InvalidOperationException(
                                  $"Could not find parameterless constructor for {wrapperType}");

            // Compile constructor call into a fast delegate: () => new TaskHandlerWrapperImp<TTask>()
            var newExpression = Expression.New(constructor);
            var lambda        = Expression.Lambda<Func<TaskHandlerWrapper>>(newExpression);
            return lambda.Compile();
        });

        return factory();
    }
}
