namespace EverTask.Monitor.AspnetCore.SignalR;

/// <summary>
/// Configuration options for SignalR monitoring integration.
/// </summary>
public class SignalRMonitoringOptions
{
    /// <summary>
    /// Gets or sets whether to include execution logs in SignalR events.
    /// When enabled, logs captured during task execution are serialized and sent to SignalR clients.
    /// Default: false (logs are excluded from SignalR events to reduce network overhead).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Execution logs are always available via:
    /// - ILogger output (console, file, Serilog, etc.)
    /// - Database persistence (if EnablePersistentHandlerLogging is true)
    /// </para>
    /// <para>
    /// Enable this option only when you need real-time log streaming to monitoring dashboards.
    /// Be aware that this can significantly increase SignalR message size and network bandwidth usage.
    /// </para>
    /// </remarks>
    public bool IncludeExecutionLogs { get; set; } = false;
}
