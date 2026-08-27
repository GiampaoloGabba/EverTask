namespace EverTask.Scheduler.Occurrences;

// EventId range 2300–2399 (ProviderScheduleGrid: the occurrence provider seam). Ranges are allocated per
// component (issue #32); keep every new log line inside this range and never reuse an id.
internal static partial class OccurrenceProviderLog
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Warning,
        Message = "Occurrence provider '{ProviderKey}' could not answer for schedule {ScheduleId} " +
                  "({Failures} consecutive failure(s)); the schedule keeps its cursor and is asked again in {Delay}")]
    public static partial void OccurrenceProviderFailed(this ILogger logger, Exception? exception, string providerKey,
                                                        Guid scheduleId, int failures, TimeSpan delay);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Error,
        Message = "Occurrence provider '{ProviderKey}' broke its contract for schedule {ScheduleId}: it " +
                  "answered {AnsweredUtc:O} for the occurrence strictly after {AfterUtc:O}")]
    public static partial void OccurrenceProviderBrokeContract(this ILogger logger, string providerKey,
                                                               Guid scheduleId, DateTimeOffset afterUtc,
                                                               DateTimeOffset answeredUtc);
}
