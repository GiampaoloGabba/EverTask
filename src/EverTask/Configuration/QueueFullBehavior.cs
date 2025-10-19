namespace EverTask.Configuration;

/// <summary>
/// Defines the behavior when a queue reaches its capacity limit.
/// </summary>
public enum QueueFullBehavior
{
    /// <summary>
    /// Wait until space becomes available in the queue (blocking behavior).
    /// This is the current default behavior.
    /// </summary>
    Wait,

    /// <summary>
    /// Fall back to the default queue when the target queue is full.
    /// Provides graceful degradation for non-critical queues.
    /// </summary>
    FallbackToDefault,

    /// <summary>
    /// Throw an exception when the queue is full.
    /// Use this for critical queues where task loss is unacceptable.
    /// </summary>
    ThrowException
}