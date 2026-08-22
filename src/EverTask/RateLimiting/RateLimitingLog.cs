namespace EverTask.RateLimiting;

// EventId range 1500–1599 (RateLimitGate / InMemoryKeyedRateLimiter). Ranges are allocated per component in
// the #32 plan; a reflection test asserts solution-wide uniqueness.
internal static partial class RateLimitingLog
{
    // ---------------------------------------------------------------- RateLimitGate

    [LoggerMessage(EventId = 1500, Level = LogLevel.Warning,
        Message = "Task type {TaskType} declares a RateLimitPolicy but produced a null/empty rate-limit key: " +
                  "tasks execute WITHOUT rate limiting. Implement IRateLimitedTask or override GetRateLimitKey")]
    public static partial void EmptyRateLimitKey(this ILogger logger, string taskType);

    [LoggerMessage(EventId = 1501, Level = LogLevel.Warning,
        Message = "Rate limit: in-flight redelivery of recurring task {TaskId} (key {Key}) dropped — re-park " +
                  "slot {SlotUtc:O} falls past RunUntil {RunUntil:O}")]
    public static partial void InFlightRedeliveryDroppedPastRunUntil(this ILogger logger, Guid taskId, string? key,
                                                                    DateTimeOffset slotUtc, DateTimeOffset runUntil);

    [LoggerMessage(EventId = 1502, Level = LogLevel.Debug,
        Message = "Task {TaskId} redelivered while still executing in this process: re-parked at {SlotUtc:O}")]
    public static partial void InFlightRedeliveryReparked(this ILogger logger, Guid taskId, DateTimeOffset slotUtc);

    [LoggerMessage(EventId = 1503, Level = LogLevel.Debug,
        Message = "Rate limit: parked registration of task {TaskId} dropped (cancelled or re-dispatched during re-park)")]
    public static partial void StaleParkedRegistrationDropped(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Warning,
        Message = "Rate limit: best-effort release of the reservation of task {TaskId} (key {Key}) failed, " +
                  "the reservation lapses via TTL")]
    public static partial void BestEffortReleaseFailed(this ILogger logger, Exception exception, Guid taskId, string key);

    [LoggerMessage(EventId = 1505, Level = LogLevel.Warning,
        Message = "Rate limiter failed for task {TaskId} (key {Key}): failing OPEN, the task executes unthrottled")]
    public static partial void LimiterFailedOpen(this ILogger logger, Exception exception, Guid taskId, string key);

    [LoggerMessage(EventId = 1506, Level = LogLevel.Warning,
        Message = "Rate limit: task {TaskId} (key {Key}) rejected — next available slot {SlotUtc:O} exceeds " +
                  "the {Horizon} reservation horizon")]
    public static partial void SlotExceedsReservationHorizon(this ILogger logger, Guid taskId, string key,
                                                             DateTimeOffset slotUtc, TimeSpan horizon);

    [LoggerMessage(EventId = 1507, Level = LogLevel.Warning,
        Message = "Rate limit: occurrence of recurring task {TaskId} (key {Key}) skipped — reserved slot " +
                  "{SlotUtc:O} falls past RunUntil {RunUntil:O}")]
    public static partial void OccurrenceSkippedPastRunUntil(this ILogger logger, Guid taskId, string key,
                                                             DateTimeOffset slotUtc, DateTimeOffset runUntil);

    [LoggerMessage(EventId = 1508, Level = LogLevel.Debug,
        Message = "Rate limit deferred task {TaskId}: key={Key} slotUtc={SlotUtc:O} policy={TaskType}")]
    public static partial void TaskDeferred(this ILogger logger, Guid taskId, string key, DateTimeOffset slotUtc,
                                            Type taskType);

    // ---------------------------------------------------------------- InMemoryKeyedRateLimiter

    [LoggerMessage(EventId = 1509, Level = LogLevel.Warning,
        Message = "Rate limiter tracked-keys cap ({MaxTrackedKeys}) reached: new keys fail OPEN " +
                  "(tasks execute without throttling). Task type: {TaskType}. Total fail-open count: {FailOpenCount}")]
    public static partial void TrackedKeysCapReached(this ILogger logger, int maxTrackedKeys, string taskType,
                                                     long failOpenCount);
}
