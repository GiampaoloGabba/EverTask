namespace EverTask.Abstractions;

/// <summary>
/// Defines a handler for a background task
/// </summary>
/// <typeparam name="TTask">The type of task being handled</typeparam>
public interface IEverTaskHandler<in TTask> : IEverTaskHandlerOptions, IAsyncDisposable where TTask : IEverTask
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

    /// <summary>
    /// Called before each retry attempt after the initial execution failed.
    /// This callback is invoked AFTER the retry delay, immediately before retrying Handle().
    /// </summary>
    /// <param name="taskId">Task persistence identifier for tracking</param>
    /// <param name="attemptNumber">Current retry attempt number (1-based, so first retry = 1)</param>
    /// <param name="exception">Exception that triggered this retry attempt</param>
    /// <param name="delay">Delay that was applied before this retry attempt</param>
    /// <returns>ValueTask for async operations (logging, metrics, alerting)</returns>
    ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay);

    /// <summary>
    /// Internal method called by WorkerExecutor to inject log capture instance.
    /// DO NOT call manually. DO NOT implement explicitly (base class handles it).
    /// </summary>
    /// <param name="logCapture">The log capture instance for this task execution.</param>
    void SetLogCapture(ITaskLogCapture logCapture);
}
