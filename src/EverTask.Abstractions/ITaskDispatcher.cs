namespace EverTask.Abstractions;

/// <summary>
/// Push a task through the background task queue to be handled in the background.
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>
    /// Asynchronously push a task to the backgorundqueue
    /// </summary>
    /// <param name="task">Task object</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task Dispatch(object task, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously push a task to the backgorundqueue
    /// </summary>
    /// <param name="task">Task object</param>
    /// <param name="cancellationToken">Optional cancellation token</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task Dispatch<TTask>(TTask task, CancellationToken cancellationToken = default)
        where TTask : IEverTask;

    /// <summary>
    /// Asynchronously push a task to the backgorundqueue. For internal use.
    /// </summary>
    /// <param name="task">Task object</param>
    /// <param name="ct">Optional cancellation token</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null);
}
