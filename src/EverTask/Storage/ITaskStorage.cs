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
    Task Persist(QueuedTask executor, CancellationToken ct = default);

    /// <summary>
    /// Retrieves all pending tasks.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>An array of pending tasks.</returns>
    Task<QueuedTask[]> RetrievePending(CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to queued.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetQueued(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to in progress.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetInProgress(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Sets a task's status to completed.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCompleted(Guid taskId);

    /// <summary>
    /// Sets a task's status to manually cancelled by the user.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCancelledByUser(Guid taskId);

    /// <summary>
    /// Sets a task's status to SystemStopped, indicating that the task was cancelled by the background service while stopping.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="exception">The exception that caused the task to be cancelled.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetCancelledByService(Guid taskId, Exception exception);

    /// <summary>
    /// Sets the status of a task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="status">The new status of the task.</param>
    /// <param name="exception">Optional exception related to the task status change.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception = null,
                       CancellationToken ct = default);

    /// <summary>
    /// Get the current run counter for this task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <returns>The current run count for this task.</returns>
    Task<int> GetCurrentRunCount(Guid taskId);

    /// <summary>
    /// Add the current run to the run counter for this task.
    /// </summary>
    /// <param name="taskId">The ID of the task.</param>
    /// <param name="nextRun">The next run date.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun);

    /// <summary>
    /// Retrieves a task by its unique task key.
    /// </summary>
    /// <param name="taskKey">The unique task key.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>The queued task with the specified key, or null if not found.</returns>
    Task<QueuedTask?> GetByTaskKey(string taskKey, CancellationToken ct = default);

    /// <summary>
    /// Updates an existing task in storage.
    /// </summary>
    /// <param name="task">The task to update with new values.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task UpdateTask(QueuedTask task, CancellationToken ct = default);

    /// <summary>
    /// Removes a task from storage.
    /// </summary>
    /// <param name="taskId">The ID of the task to remove.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task Remove(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Records information about skipped recurring task occurrences in the audit trail.
    /// This creates a RunsAudit entry with details about which scheduled runs were skipped.
    /// </summary>
    /// <param name="taskId">The ID of the recurring task.</param>
    /// <param name="skippedOccurrences">List of DateTimeOffset values representing skipped execution times.</param>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    /// <remarks>
    /// This method is called when a recurring task resumes after downtime and needs to skip
    /// past occurrences to maintain its schedule. The audit entry provides a permanent record
    /// of which scheduled executions were skipped.
    /// </remarks>
    Task RecordSkippedOccurrences(Guid taskId, List<DateTimeOffset> skippedOccurrences, CancellationToken ct = default);

    /// <summary>
    /// Saves execution logs for a task. Called by WorkerExecutor after task execution.
    /// If <paramref name="logs"/> is empty, implementations should skip the database write.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="logs">The logs to save (ordered by SequenceNumber).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveExecutionLogsAsync(Guid taskId, IReadOnlyList<TaskExecutionLog> logs, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves all execution logs for a task, ordered by SequenceNumber ascending.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logs ordered by SequenceNumber (oldest first).</returns>
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves execution logs for a task with pagination, ordered by SequenceNumber ascending.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="skip">Number of logs to skip (for pagination).</param>
    /// <param name="take">Number of logs to take (for pagination).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Logs ordered by SequenceNumber (oldest first).</returns>
    Task<IReadOnlyList<TaskExecutionLog>> GetExecutionLogsAsync(Guid taskId, int skip, int take, CancellationToken cancellationToken);
}
