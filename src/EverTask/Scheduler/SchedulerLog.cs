namespace EverTask.Scheduler;

// EventId range 1400–1499 (PeriodicTimerScheduler / ShardedScheduler, including its nested Shard).
// Ranges are allocated per component in the #32 plan; a reflection test asserts solution-wide uniqueness.
internal static partial class SchedulerLog
{
    // --- PeriodicTimerScheduler ---------------------------------------------------------------

    [LoggerMessage(EventId = 1400, Level = LogLevel.Debug,
        Message = "Scheduling task {TaskId} for {ScheduledTime}")]
    public static partial void SchedulingTask(this ILogger logger, Guid taskId, DateTimeOffset scheduledTime);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning,
        Message = "Scheduler is disposed, ignoring schedule request for task {TaskId}: " +
                  "the task stays in a recoverable status for the next startup")]
    public static partial void SchedulerDisposed(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1402, Level = LogLevel.Debug,
        Message = "Queue empty, sleeping until next task scheduled")]
    public static partial void QueueEmpty(this ILogger logger);

    [LoggerMessage(EventId = 1403, Level = LogLevel.Error,
        Message = "Error processing scheduled tasks")]
    public static partial void ErrorProcessingScheduledTasks(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1404, Level = LogLevel.Warning,
        Message = "Task {TaskId} not enqueued ({Result}), retrying dispatch in {RetryDelay}")]
    public static partial void TaskNotEnqueued(this ILogger logger, Guid taskId, EnqueueResult result,
                                               TimeSpan retryDelay);

    [LoggerMessage(EventId = 1405, Level = LogLevel.Debug,
        Message = "Dispatching scheduled task {TaskId} to queue '{QueueName}'")]
    public static partial void DispatchingTask(this ILogger logger, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1406, Level = LogLevel.Information,
        Message = "Dispatch of task {TaskId} cancelled by scheduler shutdown")]
    public static partial void DispatchCancelled(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1407, Level = LogLevel.Error,
        Message = "Unable to dispatch task {TaskId} to queue, retrying in {RetryDelay}")]
    public static partial void UnableToDispatchTask(this ILogger logger, Exception exception, Guid taskId,
                                                    TimeSpan retryDelay);

    // --- ShardedScheduler ---------------------------------------------------------------------

    [LoggerMessage(EventId = 1408, Level = LogLevel.Information,
        Message = "Initializing ShardedScheduler with {ShardCount} shards")]
    public static partial void InitializingShardedScheduler(this ILogger logger, int shardCount);

    [LoggerMessage(EventId = 1409, Level = LogLevel.Warning,
        Message = "Shard {ShardId}: scheduler is disposed, ignoring schedule request for task {TaskId}: " +
                  "the task stays in a recoverable status for the next startup")]
    public static partial void ShardSchedulerDisposed(this ILogger logger, int shardId, Guid taskId);

    [LoggerMessage(EventId = 1410, Level = LogLevel.Debug,
        Message = "Shard {ShardId}: Scheduling task {TaskId} for {ScheduledTime}")]
    public static partial void ShardSchedulingTask(this ILogger logger, int shardId, Guid taskId,
                                                   DateTimeOffset scheduledTime);

    [LoggerMessage(EventId = 1411, Level = LogLevel.Debug,
        Message = "Shard {ShardId}: Queue empty, sleeping")]
    public static partial void ShardQueueEmpty(this ILogger logger, int shardId);

    [LoggerMessage(EventId = 1412, Level = LogLevel.Error,
        Message = "Shard {ShardId}: Error processing scheduled tasks")]
    public static partial void ShardErrorProcessingScheduledTasks(this ILogger logger, Exception exception,
                                                                  int shardId);

    [LoggerMessage(EventId = 1413, Level = LogLevel.Warning,
        Message = "Shard {ShardId}: task {TaskId} not enqueued ({Result}), retrying dispatch in {RetryDelay}")]
    public static partial void ShardTaskNotEnqueued(this ILogger logger, int shardId, Guid taskId,
                                                    EnqueueResult result, TimeSpan retryDelay);

    [LoggerMessage(EventId = 1414, Level = LogLevel.Debug,
        Message = "Shard {ShardId}: Dispatching task {TaskId} to queue '{QueueName}'")]
    public static partial void ShardDispatchingTask(this ILogger logger, int shardId, Guid taskId, string queueName);

    [LoggerMessage(EventId = 1415, Level = LogLevel.Information,
        Message = "Shard {ShardId}: dispatch of task {TaskId} cancelled by shutdown")]
    public static partial void ShardDispatchCancelled(this ILogger logger, int shardId, Guid taskId);

    [LoggerMessage(EventId = 1416, Level = LogLevel.Error,
        Message = "Shard {ShardId}: unable to dispatch task {TaskId}, retrying in {RetryDelay}")]
    public static partial void ShardUnableToDispatchTask(this ILogger logger, Exception exception, int shardId,
                                                         Guid taskId, TimeSpan retryDelay);
}
