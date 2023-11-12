namespace EverTask.Handler;

/// <summary>
/// Base abstract class for handling EverTask tasks.
/// </summary>
/// <typeparam name="TTask">The type of EverTask to handle.</typeparam>
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    /// <summary>
    /// Gets or sets the error handler function for handling exceptions.
    /// </summary>
    public Func<(Exception? exception, string? message), Task>? ErrorHandler { get; set; }

    /// <summary>
    /// Handles the specified EverTask object asynchronously.
    /// </summary>
    /// <param name="backgroundTask">The EverTask task to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous handling operation.</returns>
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

    /// <summary>
    /// Handles an error that occurred during the processing of an EverTask.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="message">An optional error message.</param>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the error handling.</returns>
    public virtual ValueTask OnError(Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Handles an error that occurred during the storage of an EverTask.
    /// </summary>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="message">An optional error message.</param>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the error handling.</returns>
    public virtual ValueTask OnStorageError(Exception? exception, string? message)
    {
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Notify when the task has been executed by EverTask
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the cleanup operation.</returns>
    public virtual ValueTask Completed()
    {
        return ValueTask.CompletedTask;
    }
}
