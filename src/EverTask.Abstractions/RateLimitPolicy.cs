namespace EverTask.Abstractions;

/// <summary>
/// Declares a per-key execution frequency constraint for a task handler:
/// at most <see cref="Permits"/> executions per <see cref="Period"/> for each rate-limit key,
/// with an optional burst allowance.
/// </summary>
/// <remarks>
/// <para>
/// The policy is declared on the handler (override
/// <c>EverTaskHandler&lt;TTask&gt;.RateLimitPolicy</c>) and applies independently to every
/// rate-limit key produced by the task (see <see cref="IRateLimitedTask"/>) — typically one
/// bucket per tenant, account or external resource. A key without budget never stalls tasks of
/// other keys, and no worker slot is held while waiting for budget.
/// </para>
/// <para>
/// Internally EverTask uses a GCRA limiter (sliding window): the steady emission interval is
/// <c>Period / Permits</c> and <see cref="Burst"/> controls how many executions may happen
/// back-to-back before the steady rate is enforced.
/// </para>
/// <para>
/// Instances are immutable and validated at construction:
/// <c>permits &gt; 0</c>, <c>period &gt; TimeSpan.Zero</c>, <c>Burst &gt;= 1</c>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public class SyncTenantDataHandler : EverTaskHandler&lt;SyncTenantData&gt;
/// {
///     // Each tenant gets 15 calls per minute — other tenants are unaffected
///     public override RateLimitPolicy? RateLimitPolicy =>
///         new RateLimitPolicy(15, TimeSpan.FromMinutes(1))
///         {
///             Burst           = 15,
///             ThrottleRetries = true,
///             StartEmpty      = false
///         };
/// }
/// </code>
/// </example>
public sealed class RateLimitPolicy
{
    private readonly int _burst;

    /// <summary>
    /// Creates a rate-limit policy allowing <paramref name="permits"/> executions per
    /// <paramref name="period"/> for each rate-limit key.
    /// </summary>
    /// <param name="permits">Maximum executions per <paramref name="period"/>. Must be positive.</param>
    /// <param name="period">The window the permits refer to. Must be positive.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="permits"/> is not positive or <paramref name="period"/> is not positive.
    /// </exception>
    public RateLimitPolicy(int permits, TimeSpan period)
    {
        if (permits <= 0)
            throw new ArgumentOutOfRangeException(nameof(permits), permits, "Permits must be positive.");
        if (period <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(period), period, "Period must be positive.");

        Permits = permits;
        Period  = period;
        _burst  = permits;
    }

    /// <summary>
    /// Gets the maximum number of executions allowed per <see cref="Period"/> for each key.
    /// </summary>
    public int Permits { get; }

    /// <summary>
    /// Gets the time window the <see cref="Permits"/> refer to.
    /// </summary>
    public TimeSpan Period { get; }

    /// <summary>
    /// Gets the number of executions that may happen back-to-back before the steady rate
    /// (<c>Period / Permits</c>) is enforced. Defaults to <see cref="Permits"/>.
    /// Set to 1 for strictly evenly-spaced executions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when set below 1.</exception>
    public int Burst
    {
        get => _burst;
        init
        {
            if (value < 1)
                throw new ArgumentOutOfRangeException(nameof(Burst), value, "Burst must be at least 1.");
            _burst = value;
        }
    }

    /// <summary>
    /// Gets whether retry attempts (after the first execution attempt) must also acquire budget.
    /// Defaults to <c>true</c>: retries of tasks calling a rate-limited API should respect the
    /// same limit. Set to <c>false</c> to let retries bypass the limiter.
    /// </summary>
    /// <remarks>
    /// When a retry has to wait longer than <see cref="MaxInSlotWait"/> for budget, the task is
    /// re-scheduled at the reserved slot and the attempt count restarts on redelivery.
    /// </remarks>
    public bool ThrottleRetries { get; init; } = true;

    /// <summary>
    /// Gets whether a key's bucket starts with no accumulated burst.
    /// Defaults to <c>false</c>: a fresh key (including every key after a process restart)
    /// starts with the full <see cref="Burst"/> available.
    /// </summary>
    /// <remarks>
    /// The limiter is in-memory: after a restart all buckets are fresh, so a backlog can burst up
    /// to ~2× <see cref="Burst"/> at the external resource across the restart boundary (documented
    /// restart semantics). Opt in to <c>StartEmpty</c> to cap the post-restart burst: executions
    /// are admitted at the steady rate from the first one. This also limits the effect of a
    /// forward NTP clock jump.
    /// </remarks>
    public bool StartEmpty { get; init; } = false;

    /// <summary>
    /// Gets the maximum scheduling horizon for a deferred task. When the next available slot for
    /// a key lies further than this in the future, the task is rejected instead of parked:
    /// one-shot tasks are marked <c>Failed</c> (with <see cref="RateLimitRejectedException"/>
    /// delivered to <c>OnError</c>), recurring tasks skip the occurrence.
    /// Defaults to 1 hour.
    /// </summary>
    /// <remarks>
    /// The horizon bounds the per-key backlog (≈ horizon / emission interval) and prevents
    /// far-future poisoning of the scheduler under sustained overload.
    /// </remarks>
    public TimeSpan MaxReservationHorizon { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets the maximum near-future wait performed in-slot: when the reserved slot is at most
    /// this far away, the consumer awaits it inline (saving a scheduler round-trip) instead of
    /// re-scheduling the task. Defaults to 1 second.
    /// </summary>
    public TimeSpan MaxInSlotWait { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets the behavior applied when the key has no available budget.
    /// Defaults to <see cref="RateLimitOverflowBehavior.WaitForCapacity"/>.
    /// </summary>
    public RateLimitOverflowBehavior OverflowBehavior { get; init; } = RateLimitOverflowBehavior.WaitForCapacity;
}
