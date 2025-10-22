using Microsoft.Extensions.Logging;

namespace EverTask.Abstractions;

/// <summary>
/// Defines retry policy for task execution with exception filtering and retry callbacks.
/// </summary>
/// <remarks>
/// <para>
/// Retry policies control how EverTask handles task execution failures. The policy
/// determines which exceptions should trigger retries, how many retry attempts to make,
/// and what delays to apply between attempts.
/// </para>
/// <para>
/// <strong>Key Responsibilities:</strong>
/// <list type="bullet">
/// <item><description>Determine if exception is retryable (ShouldRetry)</description></item>
/// <item><description>Execute retry logic with delays (Execute)</description></item>
/// <item><description>Notify caller of retry attempts (onRetryCallback)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Built-in Implementations:</strong>
/// <list type="bullet">
/// <item><description><see cref="LinearRetryPolicy"/>: Fixed delay between retries with optional exception filtering</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Integration with Polly:</strong>
/// If using Polly for advanced retry policies, implement this interface and delegate to Polly's
/// AsyncPolicy. The ShouldRetry method can delegate to Polly's exception predicates.
/// </para>
/// </remarks>
public interface IRetryPolicy
{
    /// <summary>
    /// Executes the provided action with retry logic.
    /// </summary>
    /// <param name="action">Action to execute with retry</param>
    /// <param name="attemptLogger">Logger for retry attempts</param>
    /// <param name="token">Cancellation token</param>
    /// <param name="onRetryCallback">
    /// Optional callback invoked before each retry attempt.
    /// Parameters: (attemptNumber, exception, delay)
    /// Exceptions from callback are logged but don't prevent retry.
    /// </param>
    /// <returns>Task completing when action succeeds or all retries exhausted</returns>
    Task Execute(
        Func<CancellationToken, Task> action,
        ILogger attemptLogger,
        CancellationToken token = default,
        Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback = null);

    /// <summary>
    /// Determines if the given exception should trigger a retry attempt.
    /// </summary>
    /// <param name="exception">Exception that occurred during execution</param>
    /// <returns>True if should retry, false to fail immediately</returns>
    /// <remarks>
    /// <para>
    /// Default implementation returns true for all exceptions except:
    /// </para>
    /// <list type="bullet">
    /// <item><description><see cref="OperationCanceledException"/>: User or service cancellation</description></item>
    /// <item><description><see cref="TimeoutException"/>: Task execution timeout</description></item>
    /// </list>
    /// <para>
    /// Override this method to implement custom exception filtering logic.
    /// Common patterns:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Whitelist: Only retry specific transient exceptions (DbException, HttpRequestException)</description></item>
    /// <item><description>Blacklist: Retry all except permanent errors (ArgumentException, NullReferenceException)</description></item>
    /// </list>
    /// <para>
    /// Polly integration: If using Polly's AsyncPolicy, this method can delegate to Polly's
    /// exception predicates for consistent behavior.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // Whitelist transient exceptions only
    /// public bool ShouldRetry(Exception exception)
    /// {
    ///     return exception is DbException
    ///         or HttpRequestException
    ///         or IOException;
    /// }
    /// </code>
    /// </example>
    bool ShouldRetry(Exception exception)
    {
        return exception is not OperationCanceledException
            and not TimeoutException;
    }
}
