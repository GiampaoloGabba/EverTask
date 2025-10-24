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
    /// Attempts to enqueue a task to the specified queue, handling queue full behavior.
    /// </summary>
    /// <param name="queueName">The name of the target queue.</param>
    /// <param name="task">The task to enqueue.</param>
    /// <returns>True if the task was successfully enqueued, false otherwise.</returns>
    Task<bool> TryEnqueue(string? queueName, TaskHandlerExecutor task);

    /// <summary>
    /// Gets all registered queues for consumption.
    /// </summary>
    /// <returns>An enumerable of queue names and their instances.</returns>
    IEnumerable<(string Name, IWorkerQueue Queue)> GetAllQueues();
}