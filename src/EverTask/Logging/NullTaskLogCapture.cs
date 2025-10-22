namespace EverTask.Logging;

/// <summary>
/// Null implementation of <see cref="ITaskLogCaptureInternal"/> used when log capture is disabled.
/// All methods are no-ops. JIT compiler should inline and eliminate dead code.
/// </summary>
internal sealed class NullTaskLogCapture : ITaskLogCaptureInternal
{
    public static readonly NullTaskLogCapture Instance = new();

    private NullTaskLogCapture() { }

    public void LogTrace(string message) { }
    public void LogDebug(string message) { }
    public void LogInformation(string message) { }
    public void LogWarning(string message, Exception? exception = null) { }
    public void LogError(string message, Exception? exception = null) { }
    public void LogCritical(string message, Exception? exception = null) { }

    public IReadOnlyList<TaskExecutionLog> GetCapturedLogs() => [];
}
