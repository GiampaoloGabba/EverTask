using Microsoft.Extensions.Logging;

namespace EverTask.Storage.EfCore;

// EventId range 2100-2199 (AuditCleanupHostedService). Ranges are allocated per component in the #32 plan;
// a reflection test asserts solution-wide uniqueness. Every site here is cold (service start/stop, one
// cleanup cycle per interval, misconfigured retention knobs): levels and wording are unchanged, the
// conversion only removes the object[] allocation and the pre-check boxing.
internal static partial class AuditCleanupLog
{
    [LoggerMessage(EventId = 2100, Level = LogLevel.Warning,
        Message = "AuditCleanupHostedService requires an EF Core task storage; cleanup is disabled for the configured storage")]
    public static partial void StorageIsNotEfCore(this ILogger logger);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Warning,
        Message = "AuditCleanupHostedService started but no AuditRetentionPolicy configured. Service will run but perform no cleanup")]
    public static partial void NoRetentionPolicyConfigured(this ILogger logger);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information,
        Message = "AuditCleanupHostedService started. Cleanup interval: {Interval}")]
    public static partial void ServiceStarted(this ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Error, Message = "Error occurred during audit cleanup")]
    public static partial void CleanupCycleFailed(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2104, Level = LogLevel.Information, Message = "AuditCleanupHostedService stopped")]
    public static partial void ServiceStopped(this ILogger logger);

    [LoggerMessage(EventId = 2105, Level = LogLevel.Information, Message = "Starting audit cleanup cycle")]
    public static partial void CleanupCycleStarting(this ILogger logger);

    [LoggerMessage(EventId = 2106, Level = LogLevel.Information,
        Message = "Cleanup complete. Deleted {StatusCount} status audits, {RunsCount} runs audits, {LogCount} execution logs, {TasksCount} completed tasks, {OccurrenceCount} terminal occurrences")]
    public static partial void CleanupComplete(this ILogger logger, int statusCount, int runsCount, int logCount,
                                               int tasksCount, int occurrenceCount);

    [LoggerMessage(EventId = 2107, Level = LogLevel.Warning,
        Message = "AuditRetentionPolicy.{Knob} is {Value} (<= 0) and is treated as DISABLED. " +
                  "Provide a positive value to enable it, or null to disable it explicitly")]
    public static partial void RetentionKnobDisabled(this ILogger logger, string knob, int? value);

    [LoggerMessage(EventId = 2108, Level = LogLevel.Warning,
        Message = "AuditCleanupOptions.{Option} of {Configured} exceeds the maximum timer duration and was clamped to {Clamped}")]
    public static partial void IntervalClampedToTimerLimit(this ILogger logger, string option, TimeSpan configured,
                                                           TimeSpan clamped);
}
