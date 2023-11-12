using System.Linq.Expressions;

namespace EverTask.Storage;

/// <summary>
/// Represents a storage interface for tasks.
/// </summary>
public interface ITaskStorage
{
    /// <summary>
    /// Retrieves an array of queued tasks based on a specified condition.
    /// </summary>
    /// <param name="where">Expression to filter the queued tasks.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of queued tasks that match the condition.</returns>
    Task<QueuedTask[]> Get(Expression<Func<QueuedTask, bool>> where, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all queued tasks.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of all queued tasks.</returns>
    Task<QueuedTask[]> GetAll(CancellationToken ct = default);

    /// <summary>
    /// Persists a task in the queue.
    /// </summary>
    /// <param name="executor">The queued task to be persisted.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task PersistTask(QueuedTask executor, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all pending tasks.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of pending tasks.</returns>
    Task<QueuedTask[]> RetrievePendingTasks(CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to queued.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetTaskQueued(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to in progress.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetTaskInProgress(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to completed.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetTaskCompleted(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Sets the status of a task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="status">The new status of the task.</param>
    /// <param name="exception">Optional exception related to the task status change.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetTaskStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                       CancellationToken ct = default);
}
