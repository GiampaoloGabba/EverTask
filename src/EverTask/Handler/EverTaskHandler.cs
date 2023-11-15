namespace EverTask.Handler;

/// <summary>
/// Base abstract class for handling EverTask tasks.
/// </summary>
/// <typeparam name="TTask">The type of EverTask to handle.</typeparam>
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    /// <inheritdoc/>
    Task IEverTaskHandler<TTask>.Handle(TTask backgroundTask, CancellationToken cancellationToken)
    {
        Handle(backgroundTask, cancellationToken).ConfigureAwait(false);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the specified EverTask object asynchronously.
    /// </summary>
    /// <param name="backgroundTask">The EverTask task to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous handling operation.</returns>
    public abstract Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

    /// <inheritdoc/>
    public virtual ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual ValueTask OnStarted(Guid persistenceId)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual ValueTask OnCompleted(Guid persistenceId)
    {
        return ValueTask.CompletedTask;
    }
}
