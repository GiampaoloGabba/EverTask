using EverTask.Logger;
using EverTask.Scheduler;

namespace EverTask;

// This code was adapted from MediatR by Jimmy Bogard.
// Specific inspiration was taken from the Mediator.cs file.
// Source: https://github.com/jbogard/MediatR/blob/master/src/MediatR/Mediator.cs

/// <inheritdoc />
public class TaskDispatcher(
    IServiceProvider serviceProvider,
    IWorkerQueue workerQueue,
    IScheduler scheduler,
    EverTaskServiceConfiguration serviceConfiguration,
    IEverTaskLogger<TaskDispatcher> logger,
    ITaskStorage? taskStorage = null) : ITaskDispatcher
{

    /// <inheritdoc />
    public Task Dispatch(IEverTask task,  CancellationToken cancellationToken = default)=>
        ExecuteDispatch(task, (DateTimeOffset?)null, cancellationToken);

    /// <inheritdoc />
    public Task Dispatch(IEverTask task, TimeSpan executionDelay, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionDelay, cancellationToken);

    /// <inheritdoc />
    public Task Dispatch(IEverTask task, DateTimeOffset executionTime, CancellationToken cancellationToken = default) =>
        ExecuteDispatch(task, executionTime, cancellationToken);


    /// <inheritdoc />
    public async Task ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null) =>
        await ExecuteDispatch(task, (DateTimeOffset?)null, ct, existingTaskId).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task ExecuteDispatch(IEverTask task, TimeSpan? executionDelay = null, CancellationToken ct = default,
                                      Guid? existingTaskId = null)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        var executionTime = executionDelay != null
                                ? DateTimeOffset.UtcNow.Add(executionDelay.Value)
                                : (DateTimeOffset?)null;

        await ExecuteDispatch(task, executionTime, ct, existingTaskId).ConfigureAwait(false);
    }

    public async Task ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null, CancellationToken ct = default, Guid? existingTaskId = null)
    {
        if (task == null)
            throw new ArgumentNullException(nameof(task));

        var taskType = task.GetType();

        var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(taskType);

        var handler = (TaskHandlerWrapper?)Activator.CreateInstance(wrapperType) ??
                      throw new InvalidOperationException($"Could not create wrapper for type {taskType}");

        var executor = handler.Handle(task, executionTime, serviceProvider, existingTaskId);

        if (existingTaskId == null)
        {
            try
            {
                if (taskStorage != null)
                {
                    var taskEntity = executor.ToQueuedTask();
                    logger.LogInformation("Persisting Task: {type}", taskEntity.Type);
                    await taskStorage.PersistTask(taskEntity, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "Unable to persists the task {fullType}", task);
                if (serviceConfiguration.ThrowIfUnableToPersist)
                    throw;
            }
        }

        if (executor.ExecutionTime > DateTimeOffset.UtcNow)
        {
            scheduler.Schedule(executor);
        }
        else
        {
            await workerQueue.Queue(executor).ConfigureAwait(false);
        }
    }
}
