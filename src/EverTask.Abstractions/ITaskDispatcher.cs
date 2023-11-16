namespace EverTask.Abstractions;

/// <summary>
/// Push a task through the background task queue to be handled in the background.
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>
    /// Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="scheduleDelay">
    /// Optional. The amount of time to delay before executing the task.
    /// Defaults to immediate execution if not specified.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the scheduling before its execution.
    /// </param>
    /// <returns>A task that represents the asyncronous queue operation.</returns>
    Task Dispatch(object task, TimeSpan? scheduleDelay = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="scheduleDelay">
    /// Optional. The amount of time to delay before executing the task.
    /// Defaults to immediate execution if not specified.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the scheduling before its execution.
    /// </param>
    /// <returns>A task that represents the asyncronous queue operation.</returns>
    Task Dispatch<TTask>(TTask task, TimeSpan? scheduleDelay = null, CancellationToken cancellationToken = default)
        where TTask : IEverTask;

    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null);

    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="scheduleDelay">
    /// Optional. The amount of time to delay before executing the task.
    /// Defaults to immediate execution if not specified.
    /// </param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task ExecuteDispatch(IEverTask task, TimeSpan? scheduleDelay, CancellationToken ct = default, Guid? existingTaskId = null);

    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="executionTime">
    /// Optional. The DateTimeOffset for the task execution.
    /// Defaults to immediate execution if not specified.
    /// </param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <returns>A task that represents the queue operation.</returns>
    Task ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null, CancellationToken ct = default, Guid? existingTaskId = null);
}
