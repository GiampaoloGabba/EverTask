using EverTask.Logger;

namespace EverTask.Worker;

public class WorkerQueue(
    EverTaskServiceConfiguration configuration,
    IEverTaskLogger<WorkerQueue> logger,
    ITaskStorage? taskStorage = null) : IWorkerQueue
{
    private readonly Channel<TaskHandlerExecutor> _queue =
        Channel.CreateBounded<TaskHandlerExecutor>(configuration.ChannelOptions);

    public async ValueTask Queue(TaskHandlerExecutor task)
    {
        ArgumentNullException.ThrowIfNull(task);

        if (taskStorage != null)
            await taskStorage.SetTaskQueued(task.PersistenceId).ConfigureAwait(false);
        try
        {
            logger.LogInformation("Queuing task with id {taskId}", task.PersistenceId);
            await _queue.Writer.WriteAsync(task).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Unable to queuing task with id {taskId}", task.PersistenceId);
            if (taskStorage != null)
                await taskStorage.SetTaskStatus(task.PersistenceId, QueuedTaskStatus.Failed, e).ConfigureAwait(false);
        }
    }

    public async Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken)
    {
        return await _queue.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken)
    {
        return _queue.Reader.ReadAllAsync(cancellationToken);
    }
}
