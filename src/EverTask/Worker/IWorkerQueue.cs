namespace EverTask.Worker;

public interface IWorkerQueue
{
    ValueTask Queue(TaskHandlerExecutor task);

    /// <summary>
    /// Attempts to queue a task without waiting. Returns false if the queue is full.
    /// </summary>
    /// <param name="task">The task to queue.</param>
    /// <returns>True if the task was queued successfully, false if the queue is full.</returns>
    ValueTask<bool> TryQueue(TaskHandlerExecutor task);

    Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken);
    IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken);
}
