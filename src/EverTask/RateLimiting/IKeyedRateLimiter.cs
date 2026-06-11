namespace EverTask.RateLimiting;

/// <summary>
/// The outcome of a budget acquisition attempt.
/// </summary>
/// <param name="Acquired">
/// True when the caller may execute now. False when execution must be deferred to
/// <paramref name="RetryAt"/>.
/// </param>
/// <param name="RetryAt">
/// The wall-clock UTC instant at which the reserved slot opens. Meaningful only when
/// <paramref name="Acquired"/> is false. Directly comparable with
/// <see cref="DateTimeOffset.UtcNow"/> (it is handed to the scheduler as-is).
/// </param>
public readonly record struct RateLimitDecision(bool Acquired, DateTimeOffset RetryAt);

/// <summary>
/// Per-key rate limiter used by the worker-side gate. This interface is the DI seam for a
/// future distributed implementation (e.g. Redis-based GCRA): replace the default in-memory
/// registration (<c>services.TryAddSingleton&lt;IKeyedRateLimiter, InMemoryKeyedRateLimiter&gt;()</c>)
/// to share budgets across instances.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Contract invariants — any implementation MUST emulate them:</strong>
/// </para>
/// <list type="number">
/// <item><description>
/// A deferred result (Acquired = false with a reservation booked) reserves capacity for
/// <c>reservationId</c> until consumed or TTL-expired. Re-calling
/// <see cref="TryAcquireAsync"/> with the same <c>(taskType, key, reservationId)</c> at or
/// after <c>RetryAt</c> returns Acquired WITHOUT consuming extra capacity (idempotent
/// redemption); before <c>RetryAt</c> it returns the same deferred slot. Without this,
/// redelivery of the same task would double-consume tokens (halving the effective rate) or
/// livelock when the backlog exceeds the burst.
/// </description></item>
/// <item><description>
/// <c>RetryAt</c> values are non-decreasing across distinct reservationIds on the same key.
/// </description></item>
/// <item><description>
/// The call never blocks waiting for capacity.
/// </description></item>
/// <item><description>
/// <c>RetryAt</c> is wall-clock UTC, directly comparable with
/// <see cref="DateTimeOffset.UtcNow"/> (it is handed to the scheduler). Never use a
/// monotonic clock for slot math.
/// </description></item>
/// <item><description>
/// <strong>Fail policy</strong>: if the limiter throws (e.g. a distributed implementation
/// with the network down), the gate fails OPEN — the task executes with a warning log —
/// consistent with EverTask's never-lose-a-task contract. Implementations must not assume
/// fail-closed semantics.
/// </description></item>
/// </list>
/// <para>
/// Additionally, when the computed slot for a deferral would exceed
/// <see cref="RateLimitPolicy.MaxReservationHorizon"/>, the implementation returns the deferred
/// decision WITHOUT booking capacity (no reservation): the gate maps it to a terminal rejection,
/// and unbooked far-future slots must not grow per-key state.
/// </para>
/// </remarks>
public interface IKeyedRateLimiter
{
    /// <summary>
    /// Attempts to acquire one execution permit for the given key, reserving the next available
    /// slot when no budget is available.
    /// </summary>
    /// <param name="policy">The rate-limit policy declared by the task's handler.</param>
    /// <param name="taskType">The task type; buckets are scoped per (task type, key).</param>
    /// <param name="key">The throttling key (e.g. tenant id).</param>
    /// <param name="reservationId">
    /// Stable identity of the acquiring task (its persistence id): used for idempotent
    /// redemption of the reserved slot across redeliveries.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The acquisition decision (see invariants in the interface remarks).</returns>
    ValueTask<RateLimitDecision> TryAcquireAsync(
        RateLimitPolicy policy, Type taskType, string key, Guid reservationId, CancellationToken ct = default);

    /// <summary>
    /// Best-effort release of a reservation that will never be redeemed (e.g. the task was
    /// cancelled while parked). A no-op implementation is allowed; implementations MUST NOT
    /// perform a general budget rollback (at most a newest-only compare-and-swap), because
    /// rolling back a non-newest reservation would re-open capacity already promised to later
    /// reservations. Unreleased orphans lapse via TTL: the waste is exactly one emission
    /// interval per orphan — under-use only, never a rate violation.
    /// </summary>
    /// <param name="taskType">The task type of the original acquisition.</param>
    /// <param name="key">The throttling key of the original acquisition.</param>
    /// <param name="reservationId">The reservation to release.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask ReleaseAsync(Type taskType, string key, Guid reservationId, CancellationToken ct = default);
}
