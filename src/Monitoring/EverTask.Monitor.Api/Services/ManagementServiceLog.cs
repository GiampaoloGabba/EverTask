using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.Api.Services;

// EventId range 3300-3399 (ManagementService). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness.
internal static partial class ManagementServiceLog
{
    [LoggerMessage(EventId = 3300, Level = LogLevel.Information,
        Message = "Monitoring API requeued occurrence {TaskId}")]
    public static partial void OccurrenceRequeued(this ILogger logger, Guid taskId);

    [LoggerMessage(EventId = 3301, Level = LogLevel.Information,
        Message = "Monitoring API resumed schedule '{TaskKey}' ({TaskId}), halt released: {ReleasedHalt}")]
    public static partial void ScheduleResumed(this ILogger logger, string taskKey, Guid taskId, bool releasedHalt);

    [LoggerMessage(EventId = 3302, Level = LogLevel.Information,
        Message = "Monitoring API cancelled schedule '{TaskKey}' ({TaskId})")]
    public static partial void ScheduleCancelled(this ILogger logger, string taskKey, Guid taskId);
}
