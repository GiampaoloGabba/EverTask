namespace EverTask.Dispatcher;

// EventId range 2200–2299 (TaskScheduleManager). Ranges are allocated per component in the #32 plan;
// keep every new log line inside this range and never reuse an id.
//
// Like the materializer's, these are ordinary generator-guarded methods called directly: a schedule change
// happens outside any delivery, so it logs through its own category and publishes the monitoring counterpart
// as a separate, already-rendered message. Never hand a rendered sentence to a logger as if it were a template.
internal static partial class TaskScheduleManagerLog
{
    [LoggerMessage(EventId = 2200, Level = LogLevel.Information,
        Message = "Schedule {TaskId} rescheduled from version {PreviousScheduleVersion} to version " +
                  "{ScheduleVersion} ({Mode}): cursor {PreviousCursorUtc:O} -> {CursorUtc:O}")]
    public static partial void ScheduleRescheduled(this ILogger logger, Guid taskId, int previousScheduleVersion,
                                                   int scheduleVersion, RescheduleMode mode,
                                                   DateTimeOffset? previousCursorUtc, DateTimeOffset? cursorUtc);

    [LoggerMessage(EventId = 2201, Level = LogLevel.Warning,
        Message = "Reschedule of durable schedule {TaskId} discarded {Count} due slot(s) from " +
                  "{FromUtc:O} (exact count: {IsExact}): the new cursor starts after now")]
    public static partial void BacklogDiscarded(this ILogger logger, Guid taskId, int count, bool isExact,
                                                DateTimeOffset fromUtc);

    [LoggerMessage(EventId = 2202, Level = LogLevel.Error,
        Message = "Schedule {TaskId} was updated to version {ScheduleVersion} but could not be handed back to " +
                  "the scheduler; the previous occurrence runs once more and its advance applies the new definition")]
    public static partial void ReparkFailed(this ILogger logger, Exception exception, Guid taskId,
                                            int scheduleVersion);

    [LoggerMessage(EventId = 2203, Level = LogLevel.Information,
        Message = "Occurrence {OccurrenceId} of schedule {ParentId} was requeued from {PreviousStatus}")]
    public static partial void OccurrenceRequeued(this ILogger logger, Guid occurrenceId, Guid parentId,
                                                  QueuedTaskStatus previousStatus);

    [LoggerMessage(EventId = 2204, Level = LogLevel.Debug,
        Message = "Schedule {TaskId} cancelled through the schedule manager")]
    public static partial void ScheduleCancelled(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 2206, Level = LogLevel.Warning,
        Message = "Occurrence {OccurrenceId} was put back and cancelled again: schedule {ParentId} was " +
                  "cancelled while the requeue was in flight, and a cancellation is terminal for the series")]
    public static partial void OccurrenceRequeueUndoneByCancel(this ILogger logger, Guid occurrenceId,
                                                               Guid parentId);

    [LoggerMessage(EventId = 2205, Level = LogLevel.Information,
        Message = "Catch-up halt of schedule {TaskId} released; the cursor stays at {CursorUtc:O} so the " +
                  "backlog is planned again")]
    public static partial void HaltReleased(this ILogger logger, Guid taskId, DateTimeOffset? cursorUtc);
}
