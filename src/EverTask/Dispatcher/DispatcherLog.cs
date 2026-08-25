namespace EverTask.Dispatcher;

// EventId range 1000–1099 (Dispatcher). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
internal static partial class DispatcherLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug,
        Message = "Found existing task with key {TaskKey}, ID {TaskId}, Status {Status}")]
    public static partial void FoundExistingTaskByKey(this ILogger logger, string taskKey, Guid taskId,
                                                      QueuedTaskStatus status);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning,
        Message = "Dispatch with task key {TaskKey} discarded: task {TaskId} has a delivery in flight; " +
                  "the new dispatch is rejected to avoid losing the payload or double-executing")]
    public static partial void DispatchDiscardedDeliveryInFlight(this ILogger logger, string taskKey, Guid taskId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning,
        Message = "Dispatch with task key {TaskKey} discarded: task {TaskId} is recurring and cannot be " +
                  "converted to a one-shot via taskKey")]
    public static partial void DispatchDiscardedRecurringToOneShot(this ILogger logger, string taskKey, Guid taskId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning,
        Message = "Dispatch with task key {TaskKey} discarded: recurring task {TaskId} is in progress and nothing was scheduled. " +
                  "Self-redispatch from inside a handler must use a null or per-attempt task key")]
    public static partial void DispatchDiscardedRecurringInProgress(this ILogger logger, string taskKey, Guid taskId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Debug,
        Message = "Updating recurring task {TaskId} (preserving history)")]
    public static partial void UpdatingRecurringTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Debug,
        Message = "Preserving existing NextRunUtc {NextRunUtc} and CurrentRunCount {CurrentRunCount} for recurring task {TaskId}")]
    public static partial void PreservingRecurringSchedule(this ILogger logger, DateTimeOffset? nextRunUtc,
                                                           int? currentRunCount, Guid taskId);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug,
        Message = "Removing terminated task {TaskId} to create new one")]
    public static partial void RemovingTerminatedTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning,
        Message = "Dispatch with task key {TaskKey} discarded: task {TaskId} is in progress and nothing was scheduled. " +
                  "Self-redispatch from inside a handler must use a null or per-attempt task key")]
    public static partial void DispatchDiscardedTaskInProgress(this ILogger logger, string taskKey, Guid taskId);

    [LoggerMessage(EventId = 1008, Level = LogLevel.Debug,
        Message = "Updating pending task {TaskId}")]
    public static partial void UpdatingPendingTask(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1009, Level = LogLevel.Debug,
        Message = "Using preserved NextRunUtc {NextRun} for recurring task {TaskId} (still in future)")]
    public static partial void UsingPreservedNextRun(this ILogger logger, DateTimeOffset? nextRun, Guid? taskId);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Recovery: executing pending occurrence {NextRun} of task {TaskId} that slipped into the recent past (grace window)")]
    public static partial void RecoveryExecutingSlippedOccurrence(this ILogger logger, DateTimeOffset? nextRun,
                                                                  Guid? taskId);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Information,
        Message = "Recovery: recurring task {TaskId} has no occurrence left before RunUntil; finalizing the series as completed")]
    public static partial void RecoverySeriesExhausted(this ILogger logger, Guid? taskId);

    [LoggerMessage(EventId = 1012, Level = LogLevel.Information,
        Message = "Calculated NextRunUtc {NextRun} for task {TaskId} from past NextRunUtc {PastNextRun} (skipped {SkippedCount})")]
    public static partial void CalculatedNextRunFromPast(this ILogger logger, DateTimeOffset? nextRun, Guid? taskId,
                                                         DateTimeOffset? pastNextRun, int skippedCount);

    [LoggerMessage(EventId = 1013, Level = LogLevel.Debug, Message = "Persisting Task: {Type}")]
    public static partial void PersistingTask(this ILogger logger, string type);

    [LoggerMessage(EventId = 1014, Level = LogLevel.Debug, Message = "Updating Task: {Type}")]
    public static partial void UpdatingTask(this ILogger logger, string type);

    [LoggerMessage(EventId = 1015, Level = LogLevel.Warning,
        Message = "Task key {TaskKey} was won by a concurrent dispatch ({WinnerId}); returning the winner id")]
    public static partial void TaskKeyWonByConcurrentDispatch(this ILogger logger, string taskKey, Guid winnerId);

    [LoggerMessage(EventId = 1016, Level = LogLevel.Error, Message = "Unable to {Action} the task {TaskType}")]
    public static partial void UnableToPersistTask(this ILogger logger, Exception exception, string action,
                                                   Type taskType);

    [LoggerMessage(EventId = 1017, Level = LogLevel.Debug,
        Message = "Lazy handler resolution disabled globally (UseLazyHandlerResolution = false)")]
    public static partial void LazyResolutionDisabledGlobally(this ILogger logger);

    [LoggerMessage(EventId = 1018, Level = LogLevel.Warning,
        Message = "Could not read the occurrences of task {TaskId} while cancelling it: cancelling the row " +
                  "alone. Any occurrence of it is dropped by the blacklist in this process and cancelled by " +
                  "the next startup recovery")]
    public static partial void OccurrenceLookupForCancelFailed(this ILogger logger, Exception exception,
                                                               Guid taskId);
}
