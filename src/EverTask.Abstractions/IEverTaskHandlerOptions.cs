namespace EverTask.Abstractions;

public interface IEverTaskHandlerOptions
{
    /// <summary>
    /// Gets or sets the retry policy for this task. Default is a linear retry policy with 3 tries every 500 milliseconds
    /// </summary>
    public IRetryPolicy? RetryPolicy { get; set; }

    /// <summary>
    /// Gets or sets the timeout for this task.Default is null (no timeout)
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// Gets or sets whether this task is CPU-bound. Default is false.
    /// </summary>
    [Obsolete("This property is deprecated and has no effect. EverTask's async/await execution is non-blocking and suitable for all workloads. For CPU-intensive synchronous operations, use Task.Run within your handler instead.")]
    public bool CpuBoundOperation { get; set; }
}
