using EverTask.Configuration;
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
        if (taskStorage != null)
        {
            await taskStorage.SetCancelledByUser(taskId, AuditLevel.ErrorsOnly).ConfigureAwait(false);
        }

        cancellationSourceProvider.CancelTokenForTask(taskId);

        workerBlacklist.Add(taskId);
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
                                            CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null, AuditLevel? auditLevel = null)
    {
        ArgumentNullException.ThrowIfNull(task);

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

                // For RECURRING tasks: preserve history and don't remove/recreate on terminal status
                // Recurring tasks with Completed/Failed status are not truly "terminated" - they should continue
                if (existingTask.IsRecurring && recurring != null)
                {
                    // If task is in progress, return existing ID (cannot modify running task)
                    if (existingTask.Status is QueuedTaskStatus.InProgress)
                    {
                        logger.LogInformation("Recurring task {TaskId} is in progress, returning existing ID", existingTask.Id);
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
                        logger.LogInformation("Task {TaskId} is in progress, returning existing ID", existingTask.Id);
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

        var executor = handler.Handle(task, executionTime, recurring, serviceProvider, effectiveAuditLevel, existingTaskId, taskKey);

        // Persist or update task (lazy serialize only if storage exists)
        if (taskStorage != null)
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
                logger.LogError(e, "Unable to {Action} the task {FullType}",
                    existingTaskId == null ? "persist" : "update", task);
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }

        // Determine if task should use lazy handler resolution
        TaskHandlerExecutor executorToSchedule = executor;
        if (ShouldUseLazyResolution(executionTime, recurring))
        {
            // ⚠️ DO NOT DISPOSE HANDLER HERE!
            // Handler has not executed yet, user's DisposeAsyncCore() expects execution first.
            // GC will collect the unused handler instance naturally (lightweight, only DI references).
            // See implementation plan section 2.2 for detailed rationale.

            // Convert to lazy mode (handler → null, only metadata kept)
            executorToSchedule = executor.ToLazy();

            logger.LogDebug("Task {TaskId} converted to lazy handler resolution mode", executor.PersistenceId);
        }

        if (executorToSchedule.ExecutionTime > DateTimeOffset.UtcNow || recurring != null)
        {
            scheduler.Schedule(executorToSchedule, nextRun);
        }
        else
        {
            // Determine queue name with automatic routing for recurring tasks
            var queueName = executorToSchedule.QueueName ??
                            (executorToSchedule.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);
            await queueManager.TryEnqueue(queueName, executorToSchedule).ConfigureAwait(false);
        }

        return executor.PersistenceId;
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

        // Immediate tasks: always eager
        return false;
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
