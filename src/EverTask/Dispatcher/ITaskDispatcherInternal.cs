namespace EverTask.Dispatcher;

public interface ITaskDispatcherInternal : ITaskDispatcher
{
    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <returns>A task that represents the queue operation.</returns>
    internal Task ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null);

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
    internal Task ExecuteDispatch(IEverTask task, TimeSpan? scheduleDelay, CancellationToken ct = default, Guid? existingTaskId = null);

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
    internal Task ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null, CancellationToken ct = default, Guid? existingTaskId = null);
}
