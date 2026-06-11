namespace EverTask.Dispatcher;

public interface ITaskDispatcherInternal : ITaskDispatcher
{
    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <param name="taskKey">Optional. A unique key for idempotent task registration.</param>
    /// <returns>A task that represents the queue operation.</returns>
    internal Task<Guid> ExecuteDispatch(IEverTask task, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null);

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
    /// <param name="taskKey">Optional. A unique key for idempotent task registration.</param>
    /// <param name="auditLevel">Optional. Audit level for this task.</param>
    /// <returns>A task that represents the queue operation.</returns>
    internal Task<Guid> ExecuteDispatch(IEverTask task, TimeSpan? scheduleDelay, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null, AuditLevel? auditLevel = null);

    /// <summary>
    /// For internal use only! - Asynchronously enqueues a task to the background queue with an optional delay before execution.
    /// </summary>
    /// <param name="task">The task object to be executed.</param>
    /// <param name="executionTime">
    /// Optional. The DateTimeOffset for the task execution.
    /// Defaults to immediate execution if not specified.
    /// </param>
    /// <param name="currentRun">Optional. The number of times the task has been executed.</param>
    /// <param name="recurring">Optional. The RecurringTask for the recurring execution.</param>
    /// <param name="ct">/// Optional. A token for canceling the scheduling before its execution.</param>
    /// <param name="existingTaskId">Optional existing persistence guid for the task</param>
    /// <param name="taskKey">Optional. A unique key for idempotent task registration.</param>
    /// <param name="auditLevel">Optional. Audit level for this task.</param>
    /// <param name="isRecovery">
    /// Optional. Marks a startup-recovery dispatch. Recovery dispatches:
    /// (1) wait for queue space regardless of the queue's configured
    /// <see cref="EverTask.Configuration.QueueFullBehavior"/> (no caller to fail fast to, tasks must never drop);
    /// (2) treat a future <paramref name="executionTime"/> (the stored NextRunUtc) as the preserved next
    /// occurrence for recurring tasks instead of recalculating past it (which would skip one occurrence);
    /// (3) skip the storage definition rewrite (UpdateTask), since the definition came from storage
    /// unchanged and rewriting could overwrite a concurrent live re-registration (lost update).
    /// </param>
    /// <returns>A task that represents the queue operation.</returns>
    internal Task<Guid> ExecuteDispatch(IEverTask task, DateTimeOffset? executionTime = null, RecurringTask? recurring = null, int? currentRun = null, CancellationToken ct = default, Guid? existingTaskId = null, string? taskKey = null, AuditLevel? auditLevel = null, bool isRecovery = false);
}
