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
    Discarded,

    /// <summary>
    /// A delivery of the same persistence id is already in flight in this process
    /// (in a channel or executing — see <see cref="TaskDeliveryRegistry"/>): nothing was written.
    /// Recovery treats this as an idempotent success (the dedup is the point); schedulers treat
    /// it like <see cref="QueueFull"/> and retry shortly, because their slot may have fired while
    /// the previous delivery of the same task was still unwinding.
    /// </summary>
    DuplicateInProcess
}
