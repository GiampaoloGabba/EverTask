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
}
