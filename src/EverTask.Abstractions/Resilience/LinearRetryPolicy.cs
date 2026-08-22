namespace EverTask.Abstractions;

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
    /// <param name="retryCount">Number of retry attempts (must be &gt; 0)</param>
    /// <param name="retryDelay">Delay between each retry attempt (must be &gt; TimeSpan.Zero and at most the largest delay <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts, about 49.7 days)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when retryCount &lt;= 0, retryDelay &lt;= TimeSpan.Zero or retryDelay is above the maximum timer duration</exception>
    public LinearRetryPolicy(int retryCount, TimeSpan retryDelay)
        : base(BuildDelays(retryCount, retryDelay))
    {
    }

    /// <summary>
    /// Creates a linear retry policy with custom delays for each retry attempt.
    /// </summary>
    /// <param name="retryDelays">Array of delays for each retry attempt (must contain at least one element, all values &gt; TimeSpan.Zero and at most the largest delay <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts, about 49.7 days)</param>
    /// <exception cref="ArgumentNullException">Thrown when retryDelays is null</exception>
    /// <exception cref="ArgumentException">Thrown when retryDelays is empty</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any delay is &lt;= TimeSpan.Zero or above the maximum timer duration</exception>
    public LinearRetryPolicy(TimeSpan[] retryDelays)
        : base(retryDelays)
    {
    }

    private static TimeSpan[] BuildDelays(int retryCount, TimeSpan retryDelay)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(retryCount);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryDelay, TimeSpan.Zero);

        // Checked again by the base on the whole array; this copy reports the scalar parameter name
        if (retryDelay > TaskDelayLimit.Max)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay),
                $"The delay must not exceed the maximum timer duration ({TaskDelayLimit.Max.TotalDays:F1} days).");
        }

        return [.. Enumerable.Repeat(retryDelay, retryCount)];
    }
}
