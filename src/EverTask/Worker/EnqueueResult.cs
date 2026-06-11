namespace EverTask.Worker;

/// <summary>
/// Result of a non-blocking enqueue attempt on a worker queue.
/// </summary>
public enum EnqueueResult
{
    /// <summary>
    /// The task was successfully written to the queue.
    /// </summary>
    Enqueued,

    /// <summary>
    /// The queue is at capacity and the task was not written.
    /// The task remains persisted with status <see cref="Storage.QueuedTaskStatus.WaitingQueue"/>
    /// and is picked up again by startup recovery (or by a later retry from the scheduler).
    /// </summary>
    QueueFull,

    /// <summary>
    /// The task was intentionally dropped (e.g. cancelled via blacklist) and must not be retried.
    /// </summary>
    Discarded
}
