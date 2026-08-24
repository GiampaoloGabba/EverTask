namespace EverTask.Worker;

/// <summary>
/// Source-generated log definitions for <see cref="WorkerExecutor"/>. EventId range 1200–1299
/// (ranges are allocated per component in the #32 plan).
/// </summary>
/// <remarks>
/// Three groups. The first one is called directly and keeps the generator's own <c>IsEnabled</c> guard.
/// <para>
/// The second one (<c>SkipEnabledCheck = true</c>) is called directly too, but only from inside an
/// explicit <c>if (logger.IsEnabled(...))</c> at the call site: its <c>HandlerType</c> argument costs a
/// <c>GetType().Name</c>, and the generator's own guard would run AFTER the arguments are evaluated.
/// </para>
/// <para>
/// The third one (<c>SkipEnabledCheck = true</c>) is reached ONLY through
/// <c>WorkerExecutor.RegisterEvent</c>, whose L30 gate has already tested <c>IsEnabled</c> for exactly
/// that level: calling one of those methods from anywhere else would log unconditionally.
/// </para>
/// </remarks>
internal static partial class WorkerExecutorLog
{
    // ---- Direct sites (generator-guarded) ----

    [LoggerMessage(EventId = 1200, Level = LogLevel.Warning,
        Message = "Task {TaskId} is already executing in this process, skipping duplicate delivery")]
    public static partial void DuplicateDeliverySkipped(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Error,
        Message = "Failed to resolve handler for task {TaskId}")]
    public static partial void HandlerResolutionFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Error,
        Message = "Failed to persist execution logs for task {TaskId}")]
    public static partial void ExecutionLogsPersistFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning,
        Message = "Unable to resolve handler for rejected task {TaskId}: OnError will not be invoked")]
    public static partial void RejectedTaskHandlerUnresolved(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1207, Level = LogLevel.Error, Message = "Error disposing eager handler scope")]
    public static partial void HandlerScopeDisposeFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1208, Level = LogLevel.Error,
        Message = "Error occurred while executing OnRetry callback for task {TaskId} attempt {Attempt}")]
    public static partial void OnRetryCallbackFailed(this ILogger logger, Exception exception, Guid taskId,
                                                     int attempt);

    [LoggerMessage(EventId = 1209, Level = LogLevel.Information,
        Message = "Recurring task {TaskId} was cancelled: the series is stopped, no next occurrence scheduled")]
    public static partial void RecurringSeriesCancelled(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information,
        Message = "Task {TaskId} skipped {SkippedCount} missed occurrence(s) to maintain schedule")]
    public static partial void MissedOccurrencesSkipped(this ILogger logger, Guid taskId, int skippedCount);

    /// <summary>
    /// T6's "compressed slots" counter. A daylight-saving transition can make several nominal wall-clock slots
    /// of the same schedule stand for one instant; EverTask fires once, and this is the line that says how many
    /// slots that one occurrence answered for. Only a zoned calendar schedule can produce it, and only around a
    /// transition, so it stays silent the rest of the year.
    /// </summary>
    [LoggerMessage(EventId = 1226, Level = LogLevel.Information,
        Message = "Task {TaskId} collapsed {CollapsedCount} nominal slot(s) into the occurrence at {NextRun}: " +
                  "a daylight-saving transition maps them to the same instant")]
    public static partial void DstSlotsCollapsed(this ILogger logger, Guid taskId, int collapsedCount,
                                                 DateTimeOffset? nextRun);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Error, Message = "Unable to publish event {Message}")]
    public static partial void EventPublishFailed(this ILogger logger, Exception exception, string message);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Error, Message = "Event handler failed for task {TaskId}")]
    public static partial void MonitoringSubscriberFailed(this ILogger logger, Exception exception, Guid taskId);

    // ---- Handler-type sites: call them ONLY inside an explicit logger.IsEnabled(...) block ----

    [LoggerMessage(EventId = 1201, Level = LogLevel.Debug, SkipEnabledCheck = true,
        Message = "Resolved handler {HandlerType} for lazy task {TaskId}")]
    public static partial void LazyHandlerResolved(this ILogger logger, string handlerType, Guid taskId);

    [LoggerMessage(EventId = 1205, Level = LogLevel.Debug, SkipEnabledCheck = true,
        Message = "Disposed handler {HandlerType}")]
    public static partial void HandlerDisposed(this ILogger logger, string handlerType);

    [LoggerMessage(EventId = 1206, Level = LogLevel.Error, SkipEnabledCheck = true,
        Message = "Error disposing handler {HandlerType}")]
    public static partial void HandlerDisposeFailed(this ILogger logger, Exception exception, string handlerType);

    // ---- Monitoring-event sites: gated by WorkerExecutor.RegisterEvent, never call them directly ----

    [LoggerMessage(EventId = 1213, Level = LogLevel.Debug, SkipEnabledCheck = true,
        Message = "Starting task with id {TaskId}")]
    public static partial void TaskStarting(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1214, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Rate limit skipped occurrence of recurring task {TaskId} (key {Key}): the series stays alive")]
    public static partial void RateLimitSkippedOccurrence(this ILogger logger, Exception? exception, Guid taskId,
                                                          string key);

    [LoggerMessage(EventId = 1215, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} was cancelled during execution; the completion is suppressed")]
    public static partial void TaskCancelledDuringExecution(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1216, Level = LogLevel.Debug, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} was completed in {ElapsedMs} ms")]
    public static partial void TaskCompleted(this ILogger logger, Guid taskId, double elapsedMs);

    [LoggerMessage(EventId = 1217, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Rate limit deferred task {TaskId}: key={Key} slotUtc={SlotUtc:O} policy={TaskType} " +
                  "deferredCount={DeferredCount}")]
    public static partial void RateLimitDeferred(this ILogger logger, Guid taskId, string key,
                                                 DateTimeOffset slotUtc, Type taskType, int deferredCount);

    [LoggerMessage(EventId = 1218, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Rate limiter tracked-keys cap reached: new keys fail OPEN and execute unthrottled. " +
                  "Task {TaskId} (policy={TaskType}) totalFailOpenCount={TotalFailOpenCount}")]
    public static partial void RateLimiterFailOpen(this ILogger logger, Guid taskId, Type taskType,
                                                   long totalFailOpenCount);

    [LoggerMessage(EventId = 1219, Level = LogLevel.Error, SkipEnabledCheck = true,
        Message = "Rate limit rejected task {TaskId}: marked as Failed")]
    public static partial void RateLimitRejected(this ILogger logger, Exception? exception, Guid taskId);

    [LoggerMessage(EventId = 1220, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} is signaled to be cancelled and will not be executed")]
    public static partial void TaskCancellationSignaled(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1221, Level = LogLevel.Error, SkipEnabledCheck = true,
        Message = "Error occurred executing the callback override {CallbackName} for task with id {TaskId}")]
    public static partial void CallbackOverrideFailed(this ILogger logger, Exception? exception, string callbackName,
                                                      Guid taskId);

    [LoggerMessage(EventId = 1222, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Task {TaskId} retry attempt {Attempt} after {DelayMs}ms")]
    public static partial void RetryAttempt(this ILogger logger, Exception? exception, Guid taskId, int attempt,
                                            double delayMs);

    [LoggerMessage(EventId = 1223, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} was cancelled by the user")]
    public static partial void TaskCancelledByUser(this ILogger logger, Exception? exception, Guid taskId);

    [LoggerMessage(EventId = 1224, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} was cancelled by service while stopping")]
    public static partial void TaskCancelledByService(this ILogger logger, Exception? exception, Guid taskId);

    [LoggerMessage(EventId = 1225, Level = LogLevel.Error, SkipEnabledCheck = true,
        Message = "Error occurred executing task with id {TaskId}")]
    public static partial void TaskExecutionFailed(this ILogger logger, Exception? exception, Guid taskId);
}
