using Microsoft.Extensions.Logging;

namespace EverTask.Monitor.AspnetCore.SignalR;

// EventId range 3000-3099 (SignalRTaskMonitor / AppBuilderExtensions). Ranges are allocated per component
// in the #32 plan; a reflection test asserts solution-wide uniqueness.
internal static partial class SignalRMonitorLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information,
        Message = "EverTask SignalR MonitorHub created and subscribed")]
    public static partial void MonitorHubSubscribed(this ILogger logger);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug,
        Message = "EverTask SignalR MonitorHub, message received for task {TaskId} ({Severity})")]
    public static partial void MonitorHubEventReceived(this ILogger logger, Guid taskId, string severity);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information,
        Message = "EverTask SignalR MonitorHub unsubscribed")]
    public static partial void MonitorHubUnsubscribed(this ILogger logger);

    [LoggerMessage(EventId = 3003, Level = LogLevel.Warning,
        Message = "SignalRTaskMonitor not found in services. SignalR monitoring will not work. Did you call AddSignalRMonitoring()?")]
    public static partial void MonitorNotRegistered(this ILogger logger);
}
