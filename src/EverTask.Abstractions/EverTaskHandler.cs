namespace EverTask.Abstractions;

/// <summary>
/// Base abstract class for handling EverTask tasks.
/// </summary>
/// <typeparam name="TTask">The type of EverTask to handle.</typeparam>
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    /// <inheritdoc/>
    public IRetryPolicy? RetryPolicy       { get; set; }
    /// <inheritdoc/>
    public TimeSpan?     Timeout           { get; set; }
    /// <inheritdoc/>
    public bool          CpuBoundOperation { get; set; }

    /// <summary>
    /// Gets the name of the queue where this task should be executed.
    /// Returns null to use the default queue or automatic routing for recurring tasks.
    /// Override this property to execute the handler in a specific queue.
    /// </summary>
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
