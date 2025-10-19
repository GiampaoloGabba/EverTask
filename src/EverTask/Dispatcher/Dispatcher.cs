using EverTask.Configuration;
using EverTask.Scheduler.Recurring.Builder;

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
            nextRun = recurring.CalculateNextRun(DateTimeOffset.UtcNow, currentRun ?? 0);

            if (nextRun == null)
                throw new ArgumentException("Invalid scheduler recurring expression", nameof(recurring));

            executionTime = nextRun;
        }

        var taskType = task.GetType();

        var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(taskType);

        var handler = (TaskHandlerWrapper?)Activator.CreateInstance(wrapperType) ??
                      throw new InvalidOperationException($"Could not create wrapper for type {taskType}");

        var executor = handler.Handle(task, executionTime, recurring, serviceProvider, existingTaskId, taskKey);

        // Persist or update task
        if (existingTaskId == null)
        {
            // New task - persist it
            try
            {
                if (taskStorage != null)
                {
                    var taskEntity = executor.ToQueuedTask();
                    logger.LogInformation("Persisting Task: {type}", taskEntity.Type);
                    await taskStorage.Persist(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to persists the task {fullType}", task);
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }
        else
        {
            // Existing task - update it
            try
            {
                if (taskStorage != null)
                {
                    var taskEntity = executor.ToQueuedTask();
                    logger.LogInformation("Updating Task: {type}", taskEntity.Type);
                    await taskStorage.UpdateTask(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to update the task {fullType}", task);
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
}
