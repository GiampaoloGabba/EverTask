namespace EverTask.Configuration;

/// <summary>
/// Defines the behavior when a queue reaches its capacity limit.
/// Applies to IMMEDIATE dispatches from the dispatcher only: delayed/recurring tasks
/// dispatched by the scheduler always use a non-blocking write and, when the queue is
/// full, are retried with a backoff (no fallback, no exception) so that a saturated
/// queue cannot stall the scheduling of other queues.
/// </summary>
public enum QueueFullBehavior
{
    /// <summary>
    /// Wait until space becomes available in the queue (backpressure).
    /// The wait is cancellable via the CancellationToken passed to the dispatch call
    /// (e.g. HttpContext.RequestAborted); on cancellation the task stays persisted
    /// and is recovered at the next startup.
    /// This is the behavior of the auto-created default queue.
    /// </summary>
    Wait,

    /// <summary>
    /// Fall back to the default queue when the target queue is full.
    /// Provides graceful degradation for non-critical queues.
    /// Note: the fallback enqueue on the default queue uses Wait semantics,
    /// and tasks executed on the default queue do not honor the target queue's
    /// parallelism/isolation settings.
    /// This is the default for queues added via AddQueue().
    /// </summary>
    FallbackToDefault,

    /// <summary>
    /// Throw a QueueFullException to the dispatcher when the queue is full.
    /// The task stays persisted with status WaitingQueue and is re-enqueued by
    /// startup recovery; use a taskKey to make caller-side retries idempotent.
    /// Use this for queues where the caller must be notified of saturation immediately.
    /// </summary>
    ThrowException
}
