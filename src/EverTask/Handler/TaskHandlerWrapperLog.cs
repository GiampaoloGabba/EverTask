namespace EverTask.Handler;

// EventId range 1700–1799 (TaskHandlerWrapper). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
// Non-generic on purpose: the call sites live in the generic TaskHandlerWrapperImp<TTask> and pass the
// task type name as a plain string, so a single set of generated methods serves every closed generic.
internal static partial class TaskHandlerWrapperLog
{
    [LoggerMessage(EventId = 1700, Level = LogLevel.Warning,
        Message = "Recurring task {TaskType} runs every {MinInterval} but its rate-limit policy refills one " +
                  "permit every {EmissionInterval}: occurrences will steadily accumulate behind the limiter")]
    public static partial void RecurringFasterThanRateLimit(this ILogger logger, string taskType,
                                                            TimeSpan minInterval, TimeSpan emissionInterval);

    [LoggerMessage(EventId = 1701, Level = LogLevel.Warning,
        Message = "GetRateLimitKey failed for task type {TaskType}: the task will execute WITHOUT rate limiting")]
    public static partial void RateLimitKeySelectorFailed(this ILogger logger, Exception exception, string taskType);
}
