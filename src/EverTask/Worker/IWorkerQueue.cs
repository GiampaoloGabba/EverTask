namespace EverTask.Worker;

public interface IWorkerQueue
{
    /// <summary>
    /// Gets the name of this queue.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the current number of tasks waiting in the queue.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the maximum capacity of the queue.
    /// </summary>
    int Capacity { get; }

    /// <summary>
    /// Queues a task, waiting for space when the queue is full (backpressure).
    /// </summary>
    /// <param name="task">The task to queue.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait when the queue is full (e.g. an aborted HTTP request or host shutdown).
    /// The task stays persisted and is recovered at the next startup.
    /// </param>
    ValueTask Queue(TaskHandlerExecutor task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to queue a task without waiting.
    /// </summary>
    /// <param name="task">The task to queue.</param>
    /// <param name="cancellationToken">Cancels the storage status updates.</param>
    /// <returns>The outcome of the enqueue attempt.</returns>
    ValueTask<EnqueueResult> TryQueue(TaskHandlerExecutor task, CancellationToken cancellationToken = default);

    Task<TaskHandlerExecutor> Dequeue(CancellationToken cancellationToken);
    IAsyncEnumerable<TaskHandlerExecutor> DequeueAll(CancellationToken cancellationToken);
}
