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
    private static readonly ConcurrentDictionary<Type, Func<TaskHandlerWrapper>> _wrapperFactoryCache = new();

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, null, null, null, cancellationToken, null, taskKey);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, TimeSpan executionDelay, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionDelay, cancellationToken, null, taskKey);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, DateTimeOffset executionTime, string? taskKey = null, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionTime, null, null, cancellationToken, null, taskKey);

    /// <inheritdoc />
    public async Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, string? taskKey = null, CancellationToken cancellationToken = default)
    {
        var builder = new RecurringTaskBuilder();
        recurring(builder);

        return await ExecuteDispatch(task, null, builder.RecurringTask, null, cancellationToken, null, taskKey).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task Cancel(Guid taskId, CancellationToken cancellationToken = default)
    {
        if (taskStorage != null)
            await taskStorage.SetCancelledByUser(taskId).ConfigureAwait(false);

        cancellationSourceProvider.CancelTokenForTask(taskId);

        workerBlacklist.Add(taskId);
    }

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null) =>
        await ExecuteDispatch(task, null, null, null, ct, existingTaskId, taskKey).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, TimeSpan? executionDelay = null,
                                            CancellationToken ct = default,
                                            Guid? existingTaskId = null,
                                            string? taskKey = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var executionTime = executionDelay != null
                                ? DateTimeOffset.UtcNow.Add(executionDelay.Value)
                                : (DateTimeOffset?)null;

        return await ExecuteDispatch(task, executionTime, null, null, ct, existingTaskId, taskKey).ConfigureAwait(false);
    }

    public async Task<Guid> ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null,
                                            RecurringTask? recurring = null, int? currentRun = null,
                                            CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        // Handle taskKey resolution if provided
        if (!string.IsNullOrWhiteSpace(taskKey) && taskStorage != null && existingTaskId == null)
        {
            var existingTask = await taskStorage.GetByTaskKey(taskKey, ct).ConfigureAwait(false);

            if (existingTask != null)
            {
                logger.LogInformation("Found existing task with key {taskKey}, ID {taskId}, Status {status}",
                    taskKey, existingTask.Id, existingTask.Status);

                // If task is terminated (Completed/Failed/Cancelled), remove it and create new
                if (existingTask.Status is QueuedTaskStatus.Completed
                                        or QueuedTaskStatus.Failed
                                        or QueuedTaskStatus.Cancelled
                                        or QueuedTaskStatus.ServiceStopped)
                {
                    logger.LogInformation("Removing terminated task {taskId} to create new one", existingTask.Id);
                    await taskStorage.Remove(existingTask.Id, ct).ConfigureAwait(false);
                }
                // If task is in progress, return existing ID (cannot modify running task)
                else if (existingTask.Status is QueuedTaskStatus.InProgress)
                {
                    logger.LogInformation("Task {taskId} is in progress, returning existing ID", existingTask.Id);
                    return existingTask.Id;
                }
                // If task is pending (Queued/WaitingQueue/Pending), update it
                else
                {
                    logger.LogInformation("Updating pending task {taskId}", existingTask.Id);
                    existingTaskId = existingTask.Id;
                    // Will continue with update logic below
                }
            }
        }

        DateTimeOffset? nextRun = null;

        if (recurring != null)
        {
            // Use CalculateNextValidRun to properly handle past RunAt times and skip past occurrences
            // This ensures tasks with past SpecificRunTime are scheduled for the next future occurrence
            // Use the same reference time for both scheduledTime and referenceTime to avoid timing issues
            // where RunNow gets incorrectly skipped due to millisecond differences
            var referenceTime = DateTimeOffset.UtcNow;
            var result = recurring.CalculateNextValidRun(referenceTime, currentRun ?? 0, referenceTime: referenceTime);

            if (result.NextRun == null)
                throw new ArgumentException("Invalid scheduler recurring expression", nameof(recurring));

            nextRun = result.NextRun;
            executionTime = nextRun;

            // DEBUG
            Console.WriteLine($"[Dispatcher] Scheduled recurring task for {executionTime:O}, skipped {result.SkippedCount} occurrences");
        }

        var taskType = task.GetType();

        var handler = CreateCachedWrapper(taskType);

        var executor = handler.Handle(task, executionTime, recurring, serviceProvider, existingTaskId, taskKey);

        // Persist or update task (lazy serialize only if storage exists)
        if (taskStorage != null)
        {
            var taskEntity = executor.ToQueuedTask();

            try
            {
                if (existingTaskId == null)
                {
                    // New task - persist it
                    logger.LogInformation("Persisting Task: {type}", taskEntity.Type);
                    await taskStorage.Persist(taskEntity, ct).ConfigureAwait(false);
                }
                else
                {
                    // Existing task - update it
                    logger.LogInformation("Updating Task: {type}", taskEntity.Type);
                    await taskStorage.UpdateTask(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to {action} the task {fullType}",
                    existingTaskId == null ? "persist" : "update", task);
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }

        if (executor.ExecutionTime > DateTimeOffset.UtcNow || recurring != null)
        {
            scheduler.Schedule(executor, nextRun);
        }
        else
        {
            // Determine queue name with automatic routing for recurring tasks
            string? queueName = executor.QueueName ?? (executor.RecurringTask != null ? QueueNames.Recurring : QueueNames.Default);
            await queueManager.TryEnqueue(queueName, executor).ConfigureAwait(false);
        }

        return executor.PersistenceId;
    }

    /// <summary>
    /// Creates or retrieves a cached TaskHandlerWrapper instance for the specified task type.
    /// Uses compiled Expression trees to avoid reflection overhead on repeated calls.
    /// </summary>
    private static TaskHandlerWrapper CreateCachedWrapper(Type taskType)
    {
        var factory = _wrapperFactoryCache.GetOrAdd(taskType, type =>
        {
            // Create wrapper type: TaskHandlerWrapperImp<TTask>
            var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(type);

            // Get parameterless constructor
            var constructor = wrapperType.GetConstructor(Type.EmptyTypes)
                ?? throw new InvalidOperationException($"Could not find parameterless constructor for {wrapperType}");

            // Compile constructor call into a fast delegate: () => new TaskHandlerWrapperImp<TTask>()
            var newExpression = Expression.New(constructor);
            var lambda = Expression.Lambda<Func<TaskHandlerWrapper>>(newExpression);
            return lambda.Compile();
        });

        return factory();
    }
}
