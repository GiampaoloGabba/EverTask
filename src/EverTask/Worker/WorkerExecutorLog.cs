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

    [LoggerMessage(EventId = 1230, Level = LogLevel.Error,
        Message = "The epilogue of task {TaskId} failed after the delivery itself had ended; the delivery is " +
                  "released anyway")]
    public static partial void DeliveryEpilogueFailed(this ILogger logger, Exception exception, Guid taskId);

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
        Message = "Task {TaskId} skipped {SkippedCount} missed occurrence(s) (exact count: {IsExact}) to maintain schedule")]
    public static partial void MissedOccurrencesSkipped(this ILogger logger, Guid taskId, int skippedCount,
                                                        bool isExact);

    [LoggerMessage(EventId = 1232, Level = LogLevel.Warning,
        Message = "The run of recurring task {TaskId} was recorded without its compare-and-swap: the schedule " +
                  "was rewritten under it {Attempts} times in a row, so the guard was given up rather than the " +
                  "run. The cursor written is the one the last read carried, and the schedule is parked from " +
                  "that row")]
    public static partial void ScheduleAdvanceLost(this ILogger logger, Guid taskId, int attempts);

    [LoggerMessage(EventId = 1233, Level = LogLevel.Debug,
        Message = "Recurring task {TaskId} was parked at {NextRun} from its own row, because the run that just " +
                  "ended belonged to a definition that has since been replaced")]
    public static partial void ScheduleReparkedFromRow(this ILogger logger, Guid taskId, DateTimeOffset nextRun);

    [LoggerMessage(EventId = 1234, Level = LogLevel.Error,
        Message = "Recurring task {TaskId} carries a definition that replaced the one that just ran, and it " +
                  "could not be parked from its own row: the series stops until startup recovery finds it")]
    public static partial void ScheduleReparkFromRowFailed(this ILogger logger, Exception? exception, Guid taskId);

    [LoggerMessage(EventId = 1235, Level = LogLevel.Information,
        Message = "The next occurrence of recurring task {TaskId} was computed at schedule version {Version} " +
                  "and not parked: a newer version of the schedule is already registered")]
    public static partial void NextOccurrenceRefusedBySuccessor(this ILogger logger, Guid taskId, int version);

    [LoggerMessage(EventId = 1236, Level = LogLevel.Information,
        Message = "A rate-limit skip of recurring task {TaskId} ended the series at schedule version {Version} " +
                  "and did not finalize it: the schedule was rewritten while the skip was decided")]
    public static partial void SkippedSeriesFinalizationSuperseded(this ILogger logger, Guid taskId, int version);

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

    [LoggerMessage(EventId = 1227, Level = LogLevel.Error,
        Message = "Durable schedule {TaskId} fired but no occurrence materializer is registered: no occurrence " +
                  "will be created. Register EverTask through AddEverTask")]
    public static partial void DurableScheduleWithoutMaterializer(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1228, Level = LogLevel.Error,
        Message = "Materialization of durable schedule {TaskId} failed; the schedule is re-parked for a retry")]
    public static partial void ScheduleMaterializationFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1238, Level = LogLevel.Debug,
        Message = "Schedule {TaskId} was asked to retry its occurrence provider, but {Reason}")]
    public static partial void ScheduleRetryAbandoned(this ILogger logger, Guid taskId, string reason);

    [LoggerMessage(EventId = 1239, Level = LogLevel.Error,
        Message = "The occurrence provider retry of schedule {TaskId} failed; the series waits for the next " +
                  "startup recovery")]
    public static partial void ScheduleRetryFailed(this ILogger logger, Exception exception, Guid taskId);

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

    [LoggerMessage(EventId = 1229, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Occurrence {OccurrenceId} belongs to cancelled schedule {ScheduleId} and will not be executed")]
    public static partial void OccurrenceOfCancelledSchedule(this ILogger logger, Guid occurrenceId, Guid scheduleId);

    [LoggerMessage(EventId = 1231, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Task with id {TaskId} carries schedule version {DeliveredVersion} and was superseded by " +
                  "version {PublishedVersion}: the delivery is discarded")]
    public static partial void SupersededScheduleDelivery(this ILogger logger, Guid taskId, int deliveredVersion,
                                                          int publishedVersion);

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

    [LoggerMessage(EventId = 1237, Level = LogLevel.Warning, SkipEnabledCheck = true,
        Message = "Occurrence provider '{ProviderKey}' could not answer for schedule {TaskId} ({Failures} " +
                  "consecutive failure(s)): nothing was written and the schedule is parked to ask again at " +
                  "{RetryAtUtc:O}")]
    public static partial void ScheduleAdvanceDeferredByProvider(this ILogger logger, Exception? exception,
                                                                 string providerKey, Guid taskId, int failures,
                                                                 DateTimeOffset retryAtUtc);

    [LoggerMessage(EventId = 1240, Level = LogLevel.Debug,
        Message = "Next occurrence of schedule {TaskId} was not computed: the host is stopping. Nothing was " +
                  "written and startup recovery asks the grid again")]
    public static partial void ScheduleAdvanceAbandonedOnShutdown(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1241, Level = LogLevel.Error,
        Message = "Schedule {TaskId} could not be parked to ask the occurrence provider '{ProviderKey}' " +
                  "again: nothing was written and the series stays where it is until the next startup " +
                  "recovery")]
    public static partial void ProviderRetryParkFailed(this ILogger logger, Exception? exception, Guid taskId,
                                                       string providerKey);
}
