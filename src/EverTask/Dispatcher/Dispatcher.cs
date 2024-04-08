using EverTask.Scheduler.Recurring.Builder;

namespace EverTask.Dispatcher;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the Mediator.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Mediator.cs

/// <inheritdoc />
public class Dispatcher(
    IServiceProvider serviceProvider,
    IWorkerQueue workerQueue,
    IScheduler scheduler,
    EverTaskServiceConfiguration serviceConfiguration,
    IEverTaskLogger<Dispatcher> logger,
    IWorkerBlacklist workerBlacklist,
    ICancellationSourceProvider cancellationSourceProvider,
    ITaskStorage? taskStorage = null) : ITaskDispatcherInternal
{
    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task,cancellationToken);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, TimeSpan executionDelay, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionDelay, cancellationToken);

    /// <inheritdoc />
    public Task<Guid> Dispatch(IEverTask task, DateTimeOffset executionTime, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionTime, null, null, cancellationToken);

    /// <inheritdoc />
    public async Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, CancellationToken cancellationToken = default)
    {
        var builder = new RecurringTaskBuilder();
        recurring(builder);

        return await ExecuteDispatch(task, null, builder.RecurringTask, null,cancellationToken).ConfigureAwait(false);
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
    public async Task<Guid> ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null) =>
        await ExecuteDispatch(task, null, null, null, ct, existingTaskId).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<Guid> ExecuteDispatch(IEverTask task, TimeSpan? executionDelay = null,
                                            CancellationToken ct = default,
                                            Guid? existingTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(task);

        var executionTime = executionDelay != null
                                ? DateTimeOffset.UtcNow.Add(executionDelay.Value)
                                : (DateTimeOffset?)null;

        return await ExecuteDispatch(task, executionTime, null, null, ct, existingTaskId).ConfigureAwait(false);
    }

    public async Task<Guid> ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null,
                                            RecurringTask? recurring = null, int? currentRun = null,
                                            CancellationToken ct = default, Guid? existingTaskId = null)
    {
        ArgumentNullException.ThrowIfNull(task);

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

        var executor = handler.Handle(task, executionTime, recurring, serviceProvider, existingTaskId);

        if (existingTaskId == null)
        {
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

        if (executor.ExecutionTime > DateTimeOffset.UtcNow || recurring != null)
        {
            scheduler.Schedule(executor, nextRun);
        }
        else
        {
            await workerQueue.Queue(executor).ConfigureAwait(false);
        }

        return executor.PersistenceId;
    }
}
