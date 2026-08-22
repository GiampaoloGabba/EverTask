namespace EverTask.Resilience;

/// <summary>
/// Exponential backoff retry policy with growing delays between retry attempts and optional exception filtering.
/// </summary>
/// <remarks>
/// <para>
/// The delay before retry attempt <c>n</c> (1-based) is <c>initialDelay × backoffFactor^(n-1)</c>,
/// optionally capped at <c>maxDelay</c>. With the defaults (factor 2.0) and an initial delay of 500ms,
/// the delays are 500ms, 1s, 2s, 4s, 8s, ... Without a <c>maxDelay</c>, growth is still clamped at the
/// largest delay <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts (about 49.7 days).
/// </para>
/// <para>
/// This policy supports:
/// </para>
/// <list type="bullet">
/// <item><description>Exponentially increasing delay between retries</description></item>
/// <item><description>Optional maximum delay cap (maxDelay)</description></item>
/// <item><description>Optional ±20% uniform jitter, computed per attempt, to spread out concurrent retries</description></item>
/// <item><description>Exception filtering via whitelist (Handle) or blacklist (DoNotHandle)</description></item>
/// <item><description>Predicate-based filtering (HandleWhen)</description></item>
/// </list>
/// <para>
/// Filtering and execution semantics are shared with all delay-based policies —
/// see <see cref="RetryPolicyBase{TPolicy}"/>. For a fixed delay between attempts,
/// see <see cref="LinearRetryPolicy"/>.
/// </para>
/// </remarks>
public class ExponentialRetryPolicy : RetryPolicyBase<ExponentialRetryPolicy>
{
    // The largest delay a single attempt can wait: Task.Delay rejects anything above the maximum timer
    // duration (uint.MaxValue - 1 ms, about 49.7 days) with an ArgumentOutOfRangeException, which would
    // surface from Execute as an unexpected failure. Uncapped growth clamps here instead, and the jitter
    // in GetRetryDelay re-caps against the same bound so it cannot overflow past it either.
    private static readonly TimeSpan MaxSupportedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    private readonly TimeSpan _delayCap;
    private readonly bool _useJitter;

    /// <summary>
    /// Creates an exponential backoff retry policy.
    /// </summary>
    /// <param name="retryCount">Number of retry attempts (must be &gt; 0)</param>
    /// <param name="initialDelay">Delay before the first retry attempt (must be &gt; TimeSpan.Zero)</param>
    /// <param name="backoffFactor">Multiplier applied to the delay after each attempt (must be &gt;= 1.0; a factor of 1.0 behaves like a linear policy)</param>
    /// <param name="maxDelay">Optional upper bound for the computed delays (must be &gt;= initialDelay when provided)</param>
    /// <param name="useJitter">When true, applies ±20% uniform jitter to each delay at execution time (still capped at maxDelay)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when retryCount &lt;= 0, initialDelay &lt;= TimeSpan.Zero, backoffFactor &lt; 1.0 or not finite, or maxDelay &lt; initialDelay</exception>
    /// <example>
    /// <code>
    /// // 500ms, 1s, 2s, 4s, 8s
    /// RetryPolicy = new ExponentialRetryPolicy(5, TimeSpan.FromMilliseconds(500));
    ///
    /// // 1s, 3s, 9s, 10s, 10s — capped, with jitter
    /// RetryPolicy = new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1),
    ///     backoffFactor: 3.0, maxDelay: TimeSpan.FromSeconds(10), useJitter: true);
    /// </code>
    /// </example>
    public ExponentialRetryPolicy(
        int retryCount,
        TimeSpan initialDelay,
        double backoffFactor = 2.0,
        TimeSpan? maxDelay = null,
        bool useJitter = false)
        : base(BuildDelays(retryCount, initialDelay, backoffFactor, maxDelay))
    {
        _delayCap  = EffectiveCap(maxDelay);
        _useJitter = useJitter;
    }

    /// <summary>
    /// Gets the delay to wait before the retry attempt at the given zero-based index,
    /// applying jitter at execution time when enabled so concurrent tasks spread out.
    /// </summary>
    /// <param name="attemptIndex">Zero-based retry attempt index</param>
    protected override TimeSpan GetRetryDelay(int attemptIndex)
    {
        var delay = base.GetRetryDelay(attemptIndex);

        if (!_useJitter)
            return delay;

        // Computed in ticks: a double→milliseconds round trip truncates sub-millisecond delays, and the
        // Math.Max keeps the base's positive-delay invariant for a one-tick delay jittered downwards.
        var jitterFactor  = 0.8 + Random.Shared.NextDouble() * 0.4;
        var jitteredTicks = Math.Max(1L, (long)(delay.Ticks * jitterFactor));

        return jitteredTicks > _delayCap.Ticks ? _delayCap : TimeSpan.FromTicks(jitteredTicks);
    }

    private static TimeSpan EffectiveCap(TimeSpan? maxDelay) =>
        maxDelay is { } max && max < MaxSupportedDelay ? max : MaxSupportedDelay;

    private static TimeSpan[] BuildDelays(int retryCount, TimeSpan initialDelay, double backoffFactor, TimeSpan? maxDelay)
    {
        if (retryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryCount));

        if (initialDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(initialDelay));

        if (!double.IsFinite(backoffFactor) || backoffFactor < 1.0)
            throw new ArgumentOutOfRangeException(nameof(backoffFactor), "The backoff factor must be a finite value greater than or equal to 1.");

        if (maxDelay is { } max && max < initialDelay)
            throw new ArgumentOutOfRangeException(nameof(maxDelay), "The max delay must be greater than or equal to the initial delay.");

        var capMs   = EffectiveCap(maxDelay).TotalMilliseconds;
        var delays  = new TimeSpan[retryCount];
        var delayMs = Math.Min(initialDelay.TotalMilliseconds, capMs);

        for (var i = 0; i < retryCount; i++)
        {
            delays[i] = TimeSpan.FromMilliseconds(delayMs);
            delayMs   = Math.Min(delayMs * backoffFactor, capMs);
        }

        return delays;
    }
}
