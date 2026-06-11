namespace EverTask.Abstractions;

public interface IEverTaskHandlerOptions
{
    /// <summary>
    /// Gets the retry policy for this task. Default is a linear retry policy with 3 tries every 500 milliseconds
    /// </summary>
    public IRetryPolicy? RetryPolicy { get; }

    /// <summary>
    /// Gets the timeout for this task. Default is null (no timeout)
    /// </summary>
    public TimeSpan? Timeout { get; }

    /// <summary>
    /// Gets the name of the queue where this task should be executed.
    /// Returns null to use the default queue or automatic routing for recurring tasks.
    /// </summary>
    public string? QueueName { get; }

    /// <summary>
    /// Gets the per-key rate-limit policy for this task type. Default is null (no rate limiting).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When non-null, every execution must acquire budget for the task's rate-limit key
    /// (see <see cref="IRateLimitedTask"/>) before running. Tasks whose key has no budget are
    /// re-scheduled at the next available slot without blocking a worker; tasks for other keys
    /// keep flowing.
    /// </para>
    /// <para>
    /// Declared as a default interface member so existing implementors of this interface are not
    /// broken: implement/override it only to opt in to rate limiting.
    /// </para>
    /// </remarks>
    public RateLimitPolicy? RateLimitPolicy => null;

    /// <summary>
    /// Gets or sets whether this task is CPU-bound. Default is false.
    /// </summary>
    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }
}
