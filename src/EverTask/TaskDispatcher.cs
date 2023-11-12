﻿namespace EverTask;

public class TaskDispatcher(
    IServiceProvider serviceProvider,
    IWorkerQueue workerQueue,
    EverTaskServiceConfiguration serviceConfiguration,
    IEverTaskLogger<TaskDispatcher> logger,
    ITaskStorage? taskStorage = null) : ITaskDispatcher
{
    public Task Dispatch<TTask>(TTask task, CancellationToken cancellationToken = default) where TTask : IEverTask
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        return ExecuteDispatch(task, cancellationToken);
    }

    public Task Dispatch(object task, CancellationToken cancellationToken = default) =>
        task switch
        {
            null => throw new ArgumentNullException(nameof(task)),
            IEverTask instance => ExecuteDispatch(instance, cancellationToken),
            _ => throw new ArgumentException($"{nameof(task)} does not implement ${nameof(IEverTask)}")
        };

    public async Task ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var taskType = task.GetType();

        var wrapperType = typeof(TaskHandlerWrapperImp<>).MakeGenericType(taskType);

        var handler = (TaskHandlerWrapper?)Activator.CreateInstance(wrapperType) ??
                      throw new InvalidOperationException($"Could not create wrapper for type {taskType}");

        var executor = handler.Handle(task, serviceProvider, existingTaskId);

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

        await workerQueue.Queue(executor).ConfigureAwait(false);
    }
}
