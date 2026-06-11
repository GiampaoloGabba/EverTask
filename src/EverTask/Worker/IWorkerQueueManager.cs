namespace EverTask.Worker;

/// <summary>
/// Manages multiple execution queues for workload isolation and prioritization.
/// </summary>
public interface IWorkerQueueManager
{
    /// <summary>
    /// Gets the queue with the specified name.
    /// </summary>
    /// <param name="name">The name of the queue.</param>
    /// <returns>The worker queue instance.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the queue doesn't exist.</exception>
    IWorkerQueue GetQueue(string name);

    /// <summary>
    /// Tries to get the queue with the specified name.
    /// </summary>
    /// <param name="name">The name of the queue.</param>
    /// <param name="queue">The worker queue if found, otherwise null.</param>
    /// <returns>True if the queue exists, otherwise false.</returns>
    bool TryGetQueue(string name, out IWorkerQueue? queue);

    /// <summary>
    /// Attempts to enqueue a task to the specified queue, honoring the queue's configured
    /// <see cref="Configuration.QueueFullBehavior"/>.
    /// </summary>
    /// <param name="queueName">The name of the target queue.</param>
    /// <param name="task">The task to enqueue.</param>
    /// <param name="cancellationToken">
    /// Cancels a wait on a full queue (e.g. an aborted HTTP request or host shutdown).
    /// </param>
    /// <returns>True if the task was successfully enqueued, false otherwise.</returns>
    Task<bool> TryEnqueue(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a task waiting for space when the queue is full, regardless of the queue's
    /// configured <see cref="Configuration.QueueFullBehavior"/>. Used by startup recovery,
    /// which has no caller to fail fast to and must never drop tasks.
    /// </summary>
    /// <param name="queueName">The name of the target queue.</param>
    /// <param name="task">The task to enqueue.</param>
    /// <param name="cancellationToken">Cancels the wait (host shutdown).</param>
    Task EnqueueBlocking(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to enqueue a task without ever waiting, regardless of the queue's configured
    /// <see cref="Configuration.QueueFullBehavior"/>. Used by the scheduler so that a saturated
    /// queue cannot stall the dispatch of tasks targeting other queues (head-of-line blocking).
    /// </summary>
    /// <param name="queueName">The name of the target queue.</param>
    /// <param name="task">The task to enqueue.</param>
    /// <param name="cancellationToken">Cancels the storage status updates.</param>
    /// <returns>The outcome of the enqueue attempt.</returns>
    Task<EnqueueResult> TryEnqueueImmediate(string? queueName, TaskHandlerExecutor task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all registered queues for consumption.
    /// </summary>
    /// <returns>An enumerable of queue names and their instances.</returns>
    IEnumerable<(string Name, IWorkerQueue Queue)> GetAllQueues();
}
