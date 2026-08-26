namespace EverTask.Worker;

// EventId range 1300–1399 (WorkerQueue / WorkerQueueManager). Ranges are allocated per component in the #32
// plan; a reflection test asserts solution-wide uniqueness.
internal static partial class WorkerQueueLog
{
    [LoggerMessage(EventId = 1300, Level = LogLevel.Warning,
        Message = "Failed to revert dropped task {TaskId} to WaitingQueue after a Drop* eviction on queue '{QueueName}'")]
    public static partial void DroppedTaskRevertFailed(this ILogger logger, Exception exception, Guid taskId,
                                                       string queueName);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Debug,
        Message = "Task {TaskId} already has a delivery in flight, skipping duplicate enqueue to queue '{QueueName}'")]
    public static partial void DuplicateEnqueueSkipped(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Debug,
        Message = "Task {TaskId} is no longer recoverable, recovery enqueue skipped")]
    public static partial void RecoveryEnqueueSkipped(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Debug,
        Message = "Queuing task with id {TaskId} to queue '{QueueName}'")]
    public static partial void QueuingTask(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning,
        Message = "Enqueue of task {TaskId} to queue '{QueueName}' was cancelled while waiting for space. " +
                  "The task remains persisted and will be recovered at startup")]
    public static partial void EnqueueCancelled(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Error,
        Message = "Unable to queue task with id {TaskId} to queue '{QueueName}'")]
    public static partial void UnableToQueueTask(this ILogger logger, Exception exception, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Debug,
        Message = "Queue '{QueueName}' is full, cannot enqueue task {TaskId}")]
    public static partial void QueueFull(this ILogger logger, string queueName, Guid taskId);

    [LoggerMessage(EventId = 1307, Level = LogLevel.Debug,
        Message = "Task {TaskId} already has a delivery in flight, not enqueued to queue '{QueueName}'")]
    public static partial void DuplicateDeliveryNotEnqueued(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1308, Level = LogLevel.Debug,
        Message = "Task {TaskId} is no longer recoverable, scheduler enqueue skipped")]
    public static partial void SchedulerEnqueueSkipped(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1309, Level = LogLevel.Debug,
        Message = "Task {TaskId} successfully enqueued to queue '{QueueName}'")]
    public static partial void TaskEnqueued(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1310, Level = LogLevel.Warning,
        Message = "Failed to revert status for task {TaskId} after full-queue write failure (status remains Queued, " +
                  "the task will still be recovered at startup)")]
    public static partial void RevertStatusFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1311, Level = LogLevel.Warning,
        Message = "Queue '{QueueName}' is full, falling back to 'default' queue for task {TaskId}")]
    public static partial void QueueFullFallingBackToDefault(this ILogger logger, string queueName, Guid taskId);

    [LoggerMessage(EventId = 1312, Level = LogLevel.Debug,
        Message = "Task {TaskId} enqueued to 'default' queue as fallback")]
    public static partial void EnqueuedToDefaultFallback(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1313, Level = LogLevel.Error,
        Message = "Failed to enqueue task {TaskId} to queue '{QueueName}'")]
    public static partial void EnqueueFailed(this ILogger logger, Exception exception, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1314, Level = LogLevel.Warning,
        Message = "Queue '{QueueName}' not found, falling back to 'default' queue")]
    public static partial void QueueNotFoundFallingBackToDefault(this ILogger logger, string queueName);

    [LoggerMessage(EventId = 1315, Level = LogLevel.Error,
        Message = "Error releasing the eager handler scope of dropped delivery {TaskId}")]
    public static partial void DroppedDeliveryReleaseFailed(this ILogger logger, Exception exception, Guid taskId);
}
