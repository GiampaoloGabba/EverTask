namespace EverTask.Worker;

// EventId range 1100–1199 (WorkerService). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
internal static partial class WorkerServiceLog
{
    [LoggerMessage(EventId = 1100, Level = LogLevel.Trace,
        Message = "EverTask BackgroundService is running")]
    public static partial void BackgroundServiceRunning(this ILogger logger);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Warning,
        Message = "Handler registration: {Warning}")]
    public static partial void HandlerRegistrationWarning(this ILogger logger, string warning);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning,
        Message = "MaxDegreeOfParallelism is set to 1, which severely limits throughput. " +
                  "For production workloads, consider increasing to {RecommendedParallelism} (ProcessorCount * 2) or higher. " +
                  "Use SetMaxDegreeOfParallelism() in AddEverTask configuration")]
    public static partial void SingleDegreeOfParallelism(this ILogger logger, int recommendedParallelism);

    // SkipEnabledCheck: the call site evaluates string.Join + a LINQ projection, so it is guarded by an
    // explicit IsEnabled — the generator's own guard would run only AFTER those arguments are evaluated.
    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, SkipEnabledCheck = true,
        Message = "Starting consumption of {QueueCount} queue(s): {QueueNames}")]
    public static partial void StartingQueueConsumption(this ILogger logger, int queueCount, string queueNames);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information,
        Message = "Pending task recovery cancelled by host shutdown")]
    public static partial void RecoveryCancelled(this ILogger logger);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Error,
        Message = "Pending task recovery failed. Queue consumers keep running; " +
                  "unrecovered tasks will be retried at the next startup")]
    public static partial void RecoveryFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1106, Level = LogLevel.Warning,
        Message = "Queue '{QueueName}' is configured with MaxDegreeOfParallelism={Configured} (< 1): " +
                  "clamped to 1 consumer to avoid a startup deadlock")]
    public static partial void QueueParallelismClamped(this ILogger logger, string queueName, int configured);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Trace,
        Message = "Starting {ConsumerCount} dedicated consumer(s) for queue '{QueueName}'")]
    public static partial void StartingConsumers(this ILogger logger, int consumerCount, string queueName);

    [LoggerMessage(EventId = 1108, Level = LogLevel.Trace,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' started")]
    public static partial void ConsumerStarted(this ILogger logger, int consumerId, string queueName);

    [LoggerMessage(EventId = 1109, Level = LogLevel.Information,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' cancelled")]
    public static partial void ConsumerCancelled(this ILogger logger, int consumerId, string queueName);

    [LoggerMessage(EventId = 1110, Level = LogLevel.Error,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' faulted")]
    public static partial void ConsumerFaulted(this ILogger logger, Exception exception, int consumerId, string queueName);

    [LoggerMessage(EventId = 1111, Level = LogLevel.Trace,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' stopped")]
    public static partial void ConsumerStopped(this ILogger logger, int consumerId, string queueName);

    [LoggerMessage(EventId = 1112, Level = LogLevel.Trace,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' received cancellation during task execution")]
    public static partial void ConsumerCancelledDuringExecution(this ILogger logger, int consumerId, string queueName);

    [LoggerMessage(EventId = 1113, Level = LogLevel.Error,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' error processing task {TaskId}")]
    public static partial void ConsumerTaskProcessingError(this ILogger logger, Exception exception, int consumerId,
                                                           string queueName, Guid taskId);

    [LoggerMessage(EventId = 1114, Level = LogLevel.Trace,
        Message = "Consumer #{ConsumerId} for queue '{QueueName}' exited (channel completed)")]
    public static partial void ConsumerExited(this ILogger logger, int consumerId, string queueName);

    [LoggerMessage(EventId = 1115, Level = LogLevel.Warning,
        Message = "Persistence is not active. In your DI, use .AddSqlStorage() for persistent tasks or .AddMemoryStorage() for tests")]
    public static partial void PersistenceNotActive(this ILogger logger);

    [LoggerMessage(EventId = 1116, Level = LogLevel.Information,
        Message = "Processing batch with {Count} pending tasks (lastCreatedAt={LastCreatedAt}, lastId={LastId})")]
    public static partial void ProcessingPendingBatch(this ILogger logger, int count, DateTimeOffset? lastCreatedAt,
                                                      Guid? lastId);

    [LoggerMessage(EventId = 1117, Level = LogLevel.Warning,
        Message = "Recovery processed {Total} pending task(s): {Recovered} re-dispatched, {Transient} failed " +
                  "(still recoverable, will retry at the next startup), {Permanent} marked Failed (poisoned/unprocessable)")]
    public static partial void RecoverySummaryWithFailures(this ILogger logger, int total, int recovered, int transient,
                                                           int permanent);

    [LoggerMessage(EventId = 1118, Level = LogLevel.Information,
        Message = "Completed processing {TotalCount} pending tasks")]
    public static partial void RecoveryCompleted(this ILogger logger, int totalCount);

    [LoggerMessage(EventId = 1119, Level = LogLevel.Error,
        Message = "Unable to deserialize task with id {TaskId}")]
    public static partial void TaskDeserializationFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1120, Level = LogLevel.Error,
        Message = "Unable to deserialize or validate recurring task info with id {TaskId}")]
    public static partial void RecurringMetadataDeserializationFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1121, Level = LogLevel.Error,
        Message = "Recurring task {TaskId} has missing or corrupt recurring metadata and was poisoned " +
                  "terminally (marked Failed, NextRunUtc cleared) so it is not revived or re-executed as a " +
                  "one-shot at every restart")]
    public static partial void RecurringMetadataPoisoned(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1122, Level = LogLevel.Error,
        Message = "Pending task {TaskId} failed re-dispatch {Attempts} time(s) and is poisoned (marked Failed); " +
                  "it will no longer be retried")]
    public static partial void RecoveryDispatchPoisoned(this ILogger logger, Exception exception, Guid taskId, int attempts);

    [LoggerMessage(EventId = 1123, Level = LogLevel.Warning,
        Message = "Error re-dispatching pending task {TaskId} (attempt {Attempts}/{Max}); the task remains " +
                  "recoverable and will be retried at the next startup")]
    public static partial void RecoveryDispatchFailed(this ILogger logger, Exception exception, Guid taskId, int attempts,
                                                      int max);

    [LoggerMessage(EventId = 1124, Level = LogLevel.Error,
        Message = "Pending task {TaskId} has a loadable type but an unusable payload after {Attempts} attempt(s) " +
                  "and is poisoned (marked Failed); it will no longer be retried")]
    public static partial void UnusablePayloadPoisoned(this ILogger logger, Exception exception, Guid taskId, int attempts);

    [LoggerMessage(EventId = 1125, Level = LogLevel.Warning,
        Message = "Pending task {TaskId} has a loadable type but its payload could not be deserialized " +
                  "(attempt {Attempts}/{Max}); the task stays recoverable and will be retried at the next startup")]
    public static partial void UnusablePayloadRetry(this ILogger logger, Exception exception, Guid taskId, int attempts,
                                                    int max);

    [LoggerMessage(EventId = 1126, Level = LogLevel.Information,
        Message = "EverTask BackgroundService is stopping")]
    public static partial void BackgroundServiceStopping(this ILogger logger);

    [LoggerMessage(EventId = 1127, Level = LogLevel.Debug,
        Message = "Recovered recurring series {TaskId} has nothing left to run (pending slot {NextRunUtc}, " +
                  "RunUntil {RunUntil}): finalized without executing")]
    public static partial void RecoverySeriesFinalized(this ILogger logger, Guid taskId, DateTimeOffset? nextRunUtc,
                                                       DateTimeOffset? runUntil);

    [LoggerMessage(EventId = 1128, Level = LogLevel.Debug,
        Message = "Finalization of recurring series {TaskId} was superseded by a concurrent write; the row is " +
                  "left as it stands")]
    public static partial void RecoverySeriesFinalizationSuperseded(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1129, Level = LogLevel.Warning,
        Message = "Could not clear the recovery-failure counter of task {TaskId} after its terminal write; the " +
                  "write itself is committed, and the stale counter stays on a row no recovery will read again")]
    public static partial void RecoveryFailureCounterResetFailed(this ILogger logger, Exception exception, Guid taskId);

    [LoggerMessage(EventId = 1130, Level = LogLevel.Information,
        Message = "Occurrence {TaskId} belongs to cancelled schedule {ScheduleId} and was cancelled instead of " +
                  "being put back in a queue")]
    public static partial void OccurrenceOfCancelledScheduleDropped(this ILogger logger, Guid taskId, Guid scheduleId);
}
