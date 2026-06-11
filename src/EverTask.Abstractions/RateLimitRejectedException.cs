namespace EverTask.Abstractions;

/// <summary>
/// Signals that a task was terminally rejected by the rate limiter: either its computed slot
/// exceeded <see cref="RateLimitPolicy.MaxReservationHorizon"/>, or the policy uses
/// <see cref="RateLimitOverflowBehavior.Discard"/> and no budget was available.
/// </summary>
/// <remarks>
/// <para>
/// Handlers receive this exception in their existing <c>OnError</c> callback when a rate-limit
/// rejection marks the task <c>Failed</c> (one-shot tasks) — use a type check to discriminate
/// rate-limit rejections from execution errors. Recurring tasks never fail the series on
/// rejection: the occurrence is skipped instead, and no callback is invoked.
/// </para>
/// <para>
/// Ordinary deferrals (task re-scheduled at the reserved slot) are infrastructure routing and do
/// NOT raise this exception nor any handler callback; they are observable through monitoring
/// events and logs.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
/// {
///     if (exception is RateLimitRejectedException rejected)
///     {
///         _logger.LogWarning("Task {TaskId} rejected for key {Key}, next slot {Slot}",
///             taskId, rejected.RateLimitKey, rejected.ComputedSlotUtc);
///     }
///     return ValueTask.CompletedTask;
/// }
/// </code>
/// </example>
public sealed class RateLimitRejectedException : Exception
{
    /// <summary>
    /// Creates a new rejection exception.
    /// </summary>
    /// <param name="rateLimitKey">The throttling key whose budget was exhausted.</param>
    /// <param name="computedSlotUtc">
    /// The next available slot computed by the limiter (UTC), or null when not applicable.
    /// </param>
    /// <param name="policy">The policy that produced the rejection.</param>
    /// <param name="message">Human-readable rejection reason.</param>
    public RateLimitRejectedException(
        string rateLimitKey,
        DateTimeOffset? computedSlotUtc,
        RateLimitPolicy policy,
        string message) : base(message)
    {
        RateLimitKey    = rateLimitKey;
        ComputedSlotUtc = computedSlotUtc;
        Policy          = policy;
    }

    /// <summary>
    /// Gets the throttling key whose budget was exhausted.
    /// </summary>
    public string RateLimitKey { get; }

    /// <summary>
    /// Gets the next available slot computed by the limiter (UTC) at rejection time,
    /// or null when not applicable.
    /// </summary>
    public DateTimeOffset? ComputedSlotUtc { get; }

    /// <summary>
    /// Gets the rate-limit policy that produced the rejection.
    /// </summary>
    public RateLimitPolicy Policy { get; }
}
