namespace EverTask.Resilience;

/// <summary>
/// Linear retry policy with fixed delays between retry attempts and optional exception filtering.
/// </summary>
/// <remarks>
/// <para>
/// This policy supports:
/// </para>
/// <list type="bullet">
/// <item><description>Fixed delay between retries (linear backoff)</description></item>
/// <item><description>Custom delay array for variable retry intervals</description></item>
/// <item><description>Exception filtering via whitelist (Handle) or blacklist (DoNotHandle)</description></item>
/// <item><description>Predicate-based filtering (HandleWhen)</description></item>
/// </list>
/// <para>
/// Filtering and execution semantics are shared with all delay-based policies —
/// see <see cref="RetryPolicyBase{TPolicy}"/>. For increasing delays between attempts,
/// see <see cref="ExponentialRetryPolicy"/>.
/// </para>
/// </remarks>
public class LinearRetryPolicy : RetryPolicyBase<LinearRetryPolicy>
{
    /// <summary>
    /// Creates a linear retry policy with a fixed retry count and delay.
    /// </summary>
    /// <param name="retryCount">Number of retry attempts (must be > 0)</param>
    /// <param name="retryDelay">Delay between each retry attempt (must be > TimeSpan.Zero)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when retryCount <= 0 or retryDelay <= TimeSpan.Zero</exception>
    public LinearRetryPolicy(int retryCount, TimeSpan retryDelay)
        : base(BuildDelays(retryCount, retryDelay))
    {
    }

    /// <summary>
    /// Creates a linear retry policy with custom delays for each retry attempt.
    /// </summary>
    /// <param name="retryDelays">Array of delays for each retry attempt (must contain at least one element with all values > TimeSpan.Zero)</param>
    /// <exception cref="ArgumentNullException">Thrown when retryDelays is null</exception>
    /// <exception cref="ArgumentException">Thrown when retryDelays is empty</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any delay is <= TimeSpan.Zero</exception>
    public LinearRetryPolicy(TimeSpan[] retryDelays)
        : base(retryDelays)
    {
    }

    private static TimeSpan[] BuildDelays(int retryCount, TimeSpan retryDelay)
    {
        if (retryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryCount));

        if (retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        return Enumerable.Repeat(retryDelay, retryCount).ToArray();
    }
}
