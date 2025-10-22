namespace EverTask.Abstractions;

/// <summary>
/// Base abstract class for handling EverTask tasks.
/// </summary>
/// <typeparam name="TTask">The type of EverTask to handle.</typeparam>
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    /// <inheritdoc/>
    public virtual IRetryPolicy? RetryPolicy => null;
    /// <inheritdoc/>
    public virtual TimeSpan?     Timeout     => null;
    /// <inheritdoc/>
    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool          CpuBoundOperation { get; set; }

    /// <inheritdoc/>
    public virtual string? QueueName => null;

    /// <inheritdoc/>
    public abstract Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public virtual ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual ValueTask OnStarted(Guid taskId)
    {
        return default;
    }

    /// <inheritdoc/>
    public virtual ValueTask OnCompleted(Guid taskId)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Called before each retry attempt after the initial execution failed.
    /// This callback is invoked AFTER the retry delay, immediately before retrying Handle().
    /// </summary>
    /// <param name="taskId">Task persistence identifier for tracking</param>
    /// <param name="attemptNumber">Current retry attempt number (1-based, so first retry = 1)</param>
    /// <param name="exception">Exception that triggered this retry attempt</param>
    /// <param name="delay">Delay that was applied before this retry attempt</param>
    /// <returns>ValueTask for async operations (logging, metrics, alerting)</returns>
    /// <remarks>
    /// <para>
    /// This callback is useful for:
    /// </para>
    /// <list type="bullet">
    /// <item><description>Logging retry attempts with context</description></item>
    /// <item><description>Tracking retry metrics (counters, histograms)</description></item>
    /// <item><description>Alerting on excessive retries</description></item>
    /// <item><description>Debugging intermittent failures</description></item>
    /// <item><description>Implementing circuit breaker patterns</description></item>
    /// </list>
    /// <para>
    /// Execution order:
    /// </para>
    /// <list type="number">
    /// <item><description>Handle() executes and throws exception</description></item>
    /// <item><description>Retry policy determines if should retry (ShouldRetry())</description></item>
    /// <item><description>If yes: Retry policy waits for delay period</description></item>
    /// <item><description>OnRetry() called with attempt details ← YOU ARE HERE</description></item>
    /// <item><description>Handle() retried</description></item>
    /// </list>
    /// <para>
    /// If OnRetry throws an exception, it is logged but does not prevent the retry attempt.
    /// The retry will proceed regardless of OnRetry success/failure.
    /// </para>
    /// <para>
    /// OnRetry is NOT called for the initial execution attempt (only retries).
    /// If task succeeds on first attempt, OnRetry is never called.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    /// {
    ///     _logger.LogWarning(exception,
    ///         "Task {TaskId} retry {Attempt} after {DelayMs}ms: {ErrorMessage}",
    ///         taskId, attemptNumber, delay.TotalMilliseconds, exception.Message);
    ///
    ///     _metrics.IncrementCounter("task_retries", new { handler = GetType().Name, attempt = attemptNumber });
    ///
    ///     return ValueTask.CompletedTask;
    /// }
    /// </code>
    /// </example>
    public virtual ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeAsyncCore()
    {
        return ValueTask.CompletedTask;
    }
}
