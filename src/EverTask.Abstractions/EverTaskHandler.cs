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
