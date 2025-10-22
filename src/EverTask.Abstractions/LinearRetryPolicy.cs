using EverTask.Abstractions;
using Microsoft.Extensions.Logging;

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
/// </remarks>
public class LinearRetryPolicy : IRetryPolicy
{
    private readonly TimeSpan[] _retryDelays;
    private HashSet<Type>? _retryableExceptions;
    private HashSet<Type>? _nonRetryableExceptions;
    private Func<Exception, bool>? _retryPredicate;

    /// <summary>
    /// Creates a linear retry policy with a fixed retry count and delay.
    /// </summary>
    /// <param name="retryCount">Number of retry attempts (must be > 0)</param>
    /// <param name="retryDelay">Delay between each retry attempt (must be > TimeSpan.Zero)</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when retryCount <= 0 or retryDelay <= TimeSpan.Zero</exception>
    public LinearRetryPolicy(int retryCount, TimeSpan retryDelay)
    {
        if (retryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retryCount));

        if (retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        _retryDelays = Enumerable.Repeat(retryDelay, retryCount).ToArray();
    }

    /// <summary>
    /// Creates a linear retry policy with custom delays for each retry attempt.
    /// </summary>
    /// <param name="retryDelays">Array of delays for each retry attempt (must contain at least one element with all values > TimeSpan.Zero)</param>
    /// <exception cref="ArgumentNullException">Thrown when retryDelays is null</exception>
    /// <exception cref="ArgumentException">Thrown when retryDelays is empty</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any delay is <= TimeSpan.Zero</exception>
    public LinearRetryPolicy(TimeSpan[] retryDelays)
    {
        ArgumentNullException.ThrowIfNull(retryDelays);

        if (retryDelays.Length == 0)
            throw new ArgumentException("The collection must contain at least one element.", nameof(retryDelays));

        if (retryDelays.Any(delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelays), "All time spans must be greater than zero.");
        }

        _retryDelays = retryDelays;
    }

    /// <summary>
    /// Configures this policy to only retry exceptions of the specified type (whitelist mode).
    /// Can be called multiple times to whitelist multiple exception types.
    /// </summary>
    /// <typeparam name="TException">Exception type to retry</typeparam>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <remarks>
    /// When using Handle(), the policy operates in whitelist mode - only exceptions
    /// matching the configured types (or derived types) will be retried.
    /// Cannot be combined with DoNotHandle() - use one approach or the other.
    /// </remarks>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    ///     .Handle&lt;DbException&gt;()
    ///     .Handle&lt;HttpRequestException&gt;();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">Thrown when mixing Handle() with DoNotHandle()</exception>
    public LinearRetryPolicy Handle<TException>() where TException : Exception
    {
        if (_nonRetryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use Handle() after DoNotHandle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        _retryableExceptions ??= new HashSet<Type>();
        _retryableExceptions.Add(typeof(TException));
        return this;
    }

    /// <summary>
    /// Configures this policy to only retry exceptions of the specified types (whitelist mode).
    /// Useful when you need to whitelist many exception types.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to retry</param>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentException">Thrown when any type does not derive from Exception</exception>
    /// <exception cref="InvalidOperationException">Thrown when mixing Handle() with DoNotHandle()</exception>
    public LinearRetryPolicy Handle(params Type[] exceptionTypes)
    {
        if (_nonRetryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use Handle() after DoNotHandle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        foreach (var type in exceptionTypes)
        {
            if (!typeof(Exception).IsAssignableFrom(type))
                throw new ArgumentException($"Type {type.Name} must derive from Exception", nameof(exceptionTypes));

            _retryableExceptions ??= new HashSet<Type>();
            _retryableExceptions.Add(type);
        }
        return this;
    }

    /// <summary>
    /// Configures this policy to NOT retry exceptions of the specified type (blacklist mode).
    /// Can be called multiple times to blacklist multiple exception types.
    /// </summary>
    /// <typeparam name="TException">Exception type to NOT retry</typeparam>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <remarks>
    /// When using DoNotHandle(), the policy operates in blacklist mode - all exceptions
    /// will be retried EXCEPT those matching the configured types (or derived types).
    /// Cannot be combined with Handle() - use one approach or the other.
    /// </remarks>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    ///     .DoNotHandle&lt;ArgumentException&gt;()
    ///     .DoNotHandle&lt;NullReferenceException&gt;();
    /// </code>
    /// </example>
    /// <exception cref="InvalidOperationException">Thrown when mixing DoNotHandle() with Handle()</exception>
    public LinearRetryPolicy DoNotHandle<TException>() where TException : Exception
    {
        if (_retryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use DoNotHandle() after Handle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        _nonRetryableExceptions ??= new HashSet<Type>();
        _nonRetryableExceptions.Add(typeof(TException));
        return this;
    }

    /// <summary>
    /// Configures this policy to NOT retry exceptions of the specified types (blacklist mode).
    /// Useful when you need to blacklist many exception types.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to NOT retry</param>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentException">Thrown when any type does not derive from Exception</exception>
    /// <exception cref="InvalidOperationException">Thrown when mixing DoNotHandle() with Handle()</exception>
    public LinearRetryPolicy DoNotHandle(params Type[] exceptionTypes)
    {
        if (_retryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use DoNotHandle() after Handle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        foreach (var type in exceptionTypes)
        {
            if (!typeof(Exception).IsAssignableFrom(type))
                throw new ArgumentException($"Type {type.Name} must derive from Exception", nameof(exceptionTypes));

            _nonRetryableExceptions ??= new HashSet<Type>();
            _nonRetryableExceptions.Add(type);
        }
        return this;
    }

    /// <summary>
    /// Configures this policy to retry based on custom predicate logic.
    /// Predicate takes precedence over whitelist/blacklist configuration.
    /// </summary>
    /// <param name="predicate">Function that returns true if exception should be retried</param>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentNullException">Thrown when predicate is null</exception>
    /// <example>
    /// <code>
    /// RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    ///     .HandleWhen(ex => ex is HttpRequestException httpEx &amp;&amp; httpEx.StatusCode >= 500);
    /// </code>
    /// </example>
    public LinearRetryPolicy HandleWhen(Func<Exception, bool> predicate)
    {
        _retryPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return this;
    }

    /// <summary>
    /// Determines if the given exception should trigger a retry attempt.
    /// </summary>
    /// <param name="exception">Exception that occurred during execution</param>
    /// <returns>True if should retry, false to fail immediately</returns>
    /// <remarks>
    /// <para>
    /// Logic priority:
    /// </para>
    /// <list type="number">
    /// <item><description>OperationCanceledException and TimeoutException: Never retry (fail-fast)</description></item>
    /// <item><description>If predicate configured (HandleWhen): Use predicate (takes precedence)</description></item>
    /// <item><description>If whitelist configured (Handle&lt;T&gt;): Only retry if exception type matches whitelist</description></item>
    /// <item><description>If blacklist configured (DoNotHandle&lt;T&gt;): Retry all except blacklisted types</description></item>
    /// <item><description>If neither configured: Retry all (default behavior)</description></item>
    /// </list>
    /// <para>
    /// Uses Type.IsAssignableFrom() to support derived exception types.
    /// Example: Handle&lt;IOException&gt;() will also retry FileNotFoundException.
    /// </para>
    /// </remarks>
    public virtual bool ShouldRetry(Exception exception)
    {
        // Always fail-fast on cancellation and timeout
        if (exception is OperationCanceledException or TimeoutException)
            return false;

        // Predicate mode: use custom logic (takes precedence)
        if (_retryPredicate != null)
            return _retryPredicate(exception);

        // Whitelist mode: only retry configured exception types
        if (_retryableExceptions is { Count: > 0 })
        {
            return _retryableExceptions.Any(exType => exType.IsAssignableFrom(exception.GetType()));
        }

        // Blacklist mode: retry all except configured exception types
        if (_nonRetryableExceptions is { Count: > 0 })
        {
            return !_nonRetryableExceptions.Any(exType => exType.IsAssignableFrom(exception.GetType()));
        }

        // Default: retry all exceptions (backward compatible)
        return true;
    }

    public async Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null)
    {
        ArgumentNullException.ThrowIfNull(action);

        var exceptions = new List<Exception>();

        for (int i = 0; i <= _retryDelays.Length; i++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await action(token).ConfigureAwait(false);
                return; // Success
            }
            catch (Exception ex)
            {
                // Check if exception should be retried
                if (!ShouldRetry(ex))
                {
                    attemptLogger.LogWarning(ex,
                        "Exception {ExceptionType} is not retryable, failing immediately",
                        ex.GetType().Name);
                    throw; // Fail-fast for non-retryable exceptions
                }

                exceptions.Add(ex);

                // Check if we have more retries available
                if (i < _retryDelays.Length)
                {
                    var delay = _retryDelays[i];
                    var retryAttemptNumber = i + 1; // 1-based for user callback

                    attemptLogger.LogWarning(ex,
                        "Retry attempt {Attempt} of {MaxRetries} after {DelayMs}ms for {ExceptionType}",
                        retryAttemptNumber, _retryDelays.Length, delay.TotalMilliseconds, ex.GetType().Name);

                    // Wait for retry delay
                    await Task.Delay(delay, token).ConfigureAwait(false);

                    // Invoke OnRetry callback if provided
                    if (onRetryCallback != null)
                    {
                        try
                        {
                            await onRetryCallback(retryAttemptNumber, ex, delay).ConfigureAwait(false);
                        }
                        catch (Exception callbackEx)
                        {
                            // OnRetry callback exceptions are logged but don't prevent retry
                            attemptLogger.LogError(callbackEx,
                                "OnRetry callback failed for attempt {Attempt}, continuing with retry",
                                retryAttemptNumber);
                        }
                    }
                }
            }
        }

        throw new AggregateException("All retry attempts failed", exceptions);
    }
}
