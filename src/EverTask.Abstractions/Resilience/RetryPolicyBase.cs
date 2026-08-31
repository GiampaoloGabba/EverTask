using Microsoft.Extensions.Logging;

namespace EverTask.Abstractions;

/// <summary>
/// Base class for delay-based retry policies with exception filtering and retry callbacks.
/// </summary>
/// <typeparam name="TPolicy">
/// The concrete policy type (self-referencing generic), so fluent configuration methods
/// like <see cref="Handle{TException}"/> return the concrete type for chaining.
/// </typeparam>
/// <remarks>
/// <para>
/// Derived policies (<see cref="LinearRetryPolicy"/>, <see cref="ExponentialRetryPolicy"/>)
/// only differ in how the per-attempt delays are computed; the execution loop, exception
/// filtering (whitelist/blacklist/predicate) and OnRetry callback handling are shared here.
/// </para>
/// <para>
/// This policy supports:
/// </para>
/// <list type="bullet">
/// <item><description>Exception filtering via whitelist (Handle) or blacklist (DoNotHandle)</description></item>
/// <item><description>Predicate-based filtering (HandleWhen)</description></item>
/// <item><description>OnRetry callback notification (1-based attempt numbers)</description></item>
/// </list>
/// </remarks>
public abstract class RetryPolicyBase<TPolicy> : IRetryPolicy where TPolicy : RetryPolicyBase<TPolicy>
{
    private readonly TimeSpan[] _retryDelays;
    private HashSet<Type>? _retryableExceptions;
    private HashSet<Type>? _nonRetryableExceptions;
    private Func<Exception, bool>? _retryPredicate;

    /// <summary>
    /// Creates the policy from the precomputed delays for each retry attempt.
    /// </summary>
    /// <param name="retryDelays">Array of delays for each retry attempt (must contain at least one element, all values &gt; TimeSpan.Zero and at most the largest delay <see cref="Task.Delay(TimeSpan, CancellationToken)"/> accepts, about 49.7 days)</param>
    /// <exception cref="ArgumentNullException">Thrown when retryDelays is null</exception>
    /// <exception cref="ArgumentException">Thrown when retryDelays is empty</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when any delay is &lt;= TimeSpan.Zero or above the maximum timer duration</exception>
    /// <exception cref="InvalidOperationException">Thrown when the derived class is not <typeparamref name="TPolicy"/> (or a subclass of it)</exception>
    protected RetryPolicyBase(TimeSpan[] retryDelays)
    {
        // The constraint only guarantees TPolicy derives from the base, not that THIS class is TPolicy:
        // `class Bad : RetryPolicyBase<Other>` compiles and would fail later on the first fluent cast.
        if (this is not TPolicy)
        {
            throw new InvalidOperationException(
                $"{GetType().Name} must derive from RetryPolicyBase<{GetType().Name}>, not RetryPolicyBase<{typeof(TPolicy).Name}>.");
        }

        ArgumentNullException.ThrowIfNull(retryDelays);

        if (retryDelays.Length == 0)
            throw new ArgumentException("The collection must contain at least one element.", nameof(retryDelays));

        if (retryDelays.Any(delay => delay <= TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelays), "All time spans must be greater than zero.");
        }

        // Task.Delay would reject these later, mid-Execute, with an infrastructure exception; fail at
        // construction instead, where the wrong value is visible. ExponentialRetryPolicy pre-clamps its
        // computed delays, so only explicit user-provided delays can trip this.
        if (retryDelays.Any(delay => delay > TaskDelayLimit.Max))
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelays),
                $"Delays must not exceed the maximum timer duration ({TaskDelayLimit.Max.TotalDays:F1} days).");
        }

        _retryDelays = retryDelays;
    }

    /// <summary>
    /// Gets the delay to wait before the retry attempt at the given zero-based index.
    /// Override to adjust the precomputed delay at execution time (e.g. jitter).
    /// </summary>
    /// <param name="attemptIndex">Zero-based retry attempt index</param>
    protected virtual TimeSpan GetRetryDelay(int attemptIndex) => _retryDelays[attemptIndex];

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
    public TPolicy Handle<TException>() where TException : Exception
    {
        if (_nonRetryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use Handle() after DoNotHandle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        _retryableExceptions ??= [];
        _retryableExceptions.Add(typeof(TException));
        return (TPolicy)this;
    }

    /// <summary>
    /// Configures this policy to only retry exceptions of the specified types (whitelist mode).
    /// Useful when you need to whitelist many exception types.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to retry</param>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentException">Thrown when any type does not derive from Exception</exception>
    /// <exception cref="InvalidOperationException">Thrown when mixing Handle() with DoNotHandle()</exception>
    public TPolicy Handle(params Type[] exceptionTypes)
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

            _retryableExceptions ??= [];
            _retryableExceptions.Add(type);
        }
        return (TPolicy)this;
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
    public TPolicy DoNotHandle<TException>() where TException : Exception
    {
        if (_retryableExceptions is { Count: > 0 })
        {
            throw new InvalidOperationException(
                "Cannot use DoNotHandle() after Handle(). Choose whitelist (Handle) or blacklist (DoNotHandle) approach.");
        }

        _nonRetryableExceptions ??= [];
        _nonRetryableExceptions.Add(typeof(TException));
        return (TPolicy)this;
    }

    /// <summary>
    /// Configures this policy to NOT retry exceptions of the specified types (blacklist mode).
    /// Useful when you need to blacklist many exception types.
    /// </summary>
    /// <param name="exceptionTypes">Exception types to NOT retry</param>
    /// <returns>This policy instance for fluent chaining</returns>
    /// <exception cref="ArgumentException">Thrown when any type does not derive from Exception</exception>
    /// <exception cref="InvalidOperationException">Thrown when mixing DoNotHandle() with Handle()</exception>
    public TPolicy DoNotHandle(params Type[] exceptionTypes)
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

            _nonRetryableExceptions ??= [];
            _nonRetryableExceptions.Add(type);
        }
        return (TPolicy)this;
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
    ///     .HandleWhen(ex => ex is HttpRequestException httpEx &amp;&amp; httpEx.StatusCode &gt;= 500);
    /// </code>
    /// </example>
    public TPolicy HandleWhen(Func<Exception, bool> predicate)
    {
        _retryPredicate = predicate ?? throw new ArgumentNullException(nameof(predicate));
        return (TPolicy)this;
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

        for (var i = 0; i <= _retryDelays.Length; i++)
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
                    if (attemptLogger.IsEnabled(LogLevel.Warning))
                    {
                        attemptLogger.ExceptionNotRetryable(ex, ex.GetType().Name);
                    }

                    throw; // Fail-fast for non-retryable exceptions
                }

                exceptions.Add(ex);

                // Check if we have more retries available
                if (i < _retryDelays.Length)
                {
                    var delay = GetRetryDelay(i);
                    var retryAttemptNumber = i + 1; // 1-based for user callback

                    if (attemptLogger.IsEnabled(LogLevel.Warning))
                    {
                        attemptLogger.RetryAttempt(ex, retryAttemptNumber, _retryDelays.Length,
                                                   delay.TotalMilliseconds, ex.GetType().Name);
                    }

                    // Wait for retry delay
                    try
                    {
                        await Task.Delay(delay, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException oce) when (exceptions.Count > 0)
                    {
                        // A cancel during the inter-retry delay would otherwise discard the causes
                        // accumulated so far, losing WHY the task had been retrying. It stays an
                        // OperationCanceledException, so the terminal Cancelled classification is unchanged.
                        throw new OperationCanceledException(
                            $"Cancelled during the retry delay after {exceptions.Count} failed attempt(s)",
                            new AggregateException(exceptions),
                            oce.CancellationToken);
                    }

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
                            attemptLogger.OnRetryCallbackFailed(callbackEx, retryAttemptNumber);
                        }
                    }
                }
            }
        }

        throw new AggregateException("All retry attempts failed", exceptions);
    }
}
