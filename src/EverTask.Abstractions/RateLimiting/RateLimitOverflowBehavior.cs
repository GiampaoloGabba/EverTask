namespace EverTask.Abstractions;

/// <summary>
/// Defines what happens to a task when its rate-limit key has no available budget.
/// </summary>
public enum RateLimitOverflowBehavior
{
    /// <summary>
    /// The task waits for capacity: EverTask reserves the next available slot and re-schedules
    /// the task automatically. No worker is blocked and tasks for other keys keep flowing.
    /// This is the default.
    /// </summary>
    WaitForCapacity = 0,

    /// <summary>
    /// The task is discarded when no budget is available: it is marked as <c>Failed</c> with a
    /// structured reason and the handler's <c>OnError</c> callback receives a
    /// <see cref="RateLimitRejectedException"/>. Use for workloads where a late execution has
    /// no value (e.g. ephemeral notifications).
    /// </summary>
    Discard = 1
}
