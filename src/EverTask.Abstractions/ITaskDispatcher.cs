namespace EverTask.Abstractions;

/// <summary>
/// Push a task through the background task queue to be handled in the background.
/// </summary>
public interface ITaskDispatcher
{
    /// <summary>
    /// Asynchronously enqueues a task to the background queue
    /// </summary>
    /// <param name="task">The IEverTask to be executed.</param>
    /// <param name="taskKey">
    /// Optional. A unique key for idempotent task registration. If a task with the same key exists and is active,
    /// the existing task will be updated; if terminated, it will be replaced with a new one.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the dispatch operation.
    /// </param>
    /// <returns>A task that represents the asyncronous queue operation.</returns>
    Task<Guid> Dispatch(IEverTask task, string? taskKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously schedules a task to the background queue with a delay before execution.
    /// </summary>
    /// <param name="task">The IEverTask to be executed.</param>
    /// <param name="scheduleDelay">
    /// The amount of time to delay the execution of the task.
    /// </param>
    /// <param name="taskKey">
    /// Optional. A unique key for idempotent task registration. If a task with the same key exists and is active,
    /// the existing task will be updated; if terminated, it will be replaced with a new one.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the dispatch operation.
    /// </param>
    /// <returns>A task that represents the asynchronous queue operation.</returns>
    Task<Guid> Dispatch(IEverTask task, TimeSpan scheduleDelay, string? taskKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously schedules a recurring task to the background queue to be executed at specified times.
    /// </summary>
    /// <param name="task">The IEverTask to be executed.</param>
    /// <param name="recurring">
    /// The <see cref="IRecurringTaskBuilder"/> used to configure the recurring task.
    /// </param>
    /// <param name="taskKey">
    /// Optional. A unique key for idempotent task registration. If a task with the same key exists and is active,
    /// the existing task will be updated; if terminated, it will be replaced with a new one.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the dispatch operation.
    /// </param>
    /// <returns>A task that represents the asynchronous queue operation.</returns>
    Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, string? taskKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously schedules a task to the background queue to be executed at a specified time.
    /// </summary>
    /// <param name="task">The IEverTask to be executed.</param>
    /// <param name="scheduleTime">
    /// The specific time when the task is to be executed.
    /// </param>
    /// <param name="taskKey">
    /// Optional. A unique key for idempotent task registration. If a task with the same key exists and is active,
    /// the existing task will be updated; if terminated, it will be replaced with a new one.
    /// </param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the dispatch operation.
    /// </param>
    /// <returns>A task that represents the asynchronous queue operation.</returns>
    Task<Guid> Dispatch(IEverTask task, DateTimeOffset scheduleTime, string? taskKey = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asynchronously cancel a dispatched task
    /// </summary>
    /// <param name="taskId">The id of the task to cancel</param>
    /// <param name="cancellationToken">
    /// Optional. A token for canceling the dispatch operation.
    /// </param>
    /// <returns>A task that represents the asynchronous queue operation.</returns>
    Task Cancel(Guid taskId, CancellationToken cancellationToken = default);
}
