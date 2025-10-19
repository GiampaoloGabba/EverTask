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
    /// Gets or sets whether this task is CPU-bound. Default is false.
    /// </summary>
    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }
}
