namespace EverTask.Abstractions;

/// <summary>
/// Defines a handler for a background task
/// </summary>
/// <typeparam name="TTask">The type of task being handled</typeparam>
public interface IEverTaskHandler<in TTask> : IAsyncDisposable where TTask : IEverTask
{
    /// <summary>
    /// Handles the specified EverTask object asynchronously.
    /// </summary>
    /// <param name="backgroundTask">The EverTask task to handle.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous handling operation.</returns>
    Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

    /// <summary>
    /// Handles an error that occurred during the processing of an EverTask.
    /// </summary>
    /// <param name="persistenceId">The persistence identifier of the EverTask.</param>
    /// <param name="exception">The exception that occurred.</param>
    /// <param name="message">An optional error message.</param>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the error handling.</returns>
    ValueTask OnError(Guid persistenceId, Exception? exception, string? message);

    /// <summary>
    /// Notify when the task has been started by EverTask.
    /// </summary>
    /// <param name="persistenceId">The persistence identifier of the EverTask.</param>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the start operation.</returns>
    ValueTask OnStarted(Guid persistenceId);

    /// <summary>
    /// Notify when the task has been executed by EverTask
    /// </summary>
    /// <param name="persistenceId">The persistence identifier of the EverTask.</param>
    /// <returns>A <see cref="ValueTask"/> representing the completion of the task handling operation.</returns>
    ValueTask OnCompleted(Guid persistenceId);
}
