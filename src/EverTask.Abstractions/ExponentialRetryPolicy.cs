namespace EverTask.Resilience;

/// <summary>
/// Exponential backoff retry policy with growing delays between retry attempts and optional exception filtering.
/// </summary>
/// <remarks>
/// <para>
/// The delay before retry attempt <c>n</c> (1-based) is <c>initialDelay × backoffFactor^(n-1)</c>,
/// optionally capped at <c>maxDelay</c>. With the defaults (factor 2.0) and an initial delay of 500ms,
/// the delays are 500ms, 1s, 2s, 4s, 8s, ...
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
    // TimeSpan.FromMilliseconds rounds to whole milliseconds; stay strictly below the last
    // representable value so uncapped exponential growth clamps instead of overflowing.
    private static readonly double MaxDelayMilliseconds = (double)(long.MaxValue / TimeSpan.TicksPerMillisecond - 1);

    private readonly TimeSpan? _maxDelay;
    private readonly bool _useJitter;

    /// <summary>
    /// Creates an exponential backoff retry policy.
    /// </summary>
    /// <param name="retryCount">Number of retry attempts (must be > 0)</param>
    /// <param name="initialDelay">Delay before the first retry attempt (must be > TimeSpan.Zero)</param>
    /// <param name="backoffFactor">Multiplier applied to the delay after each attempt (must be >= 1.0; a factor of 1.0 behaves like a linear policy)</param>
    /// <param name="maxDelay">Optional upper bound for the computed delays (must be >= initialDelay when provided)</param>
    /// <param name="useJitter">When true, applies ±20% uniform jitter to each delay at execution time (still capped at maxDelay)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when retryCount <= 0, initialDelay <= TimeSpan.Zero, backoffFactor < 1.0 or not finite, or maxDelay < initialDelay</exception>
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
        _maxDelay = maxDelay;
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

        var jitterFactor = 0.8 + Random.Shared.NextDouble() * 0.4;
        var jittered     = TimeSpan.FromMilliseconds(delay.TotalMilliseconds * jitterFactor);

        return _maxDelay is { } cap && jittered > cap ? cap : jittered;
    }

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

        var capMs   = Math.Min(maxDelay?.TotalMilliseconds ?? double.MaxValue, MaxDelayMilliseconds);
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
