namespace EverTask.Logging;

/// <summary>
/// Implementation of <see cref="ITaskLogCaptureInternal"/> that captures logs in-memory during task execution.
/// Thread-safe for async/await handlers.
/// </summary>
internal sealed class TaskLogCapture(Guid taskId, LogLevel minLevel, int? maxLogs) : ITaskLogCaptureInternal
{
    private readonly List<TaskExecutionLog> _logs = new(capacity: maxLogs ?? 100);
    private readonly object _lock = new();
    private int _sequenceNumber = 0;

    public void LogTrace(string message)
        => AddLog(LogLevel.Trace, message, null);

    public void LogDebug(string message)
        => AddLog(LogLevel.Debug, message, null);

    public void LogInformation(string message)
        => AddLog(LogLevel.Information, message, null);

    public void LogWarning(string message, Exception? exception = null)
        => AddLog(LogLevel.Warning, message, exception);

    public void LogError(string message, Exception? exception = null)
        => AddLog(LogLevel.Error, message, exception);

    public void LogCritical(string message, Exception? exception = null)
        => AddLog(LogLevel.Critical, message, exception);

    public IReadOnlyList<TaskExecutionLog> GetCapturedLogs()
    {
        lock (_lock)
        {
            return _logs.ToArray(); // Return defensive copy
        }
    }

    private void AddLog(LogLevel level, string message, Exception? exception)
    {
        // Filter by minimum level
        if (level < minLevel)
            return;

        lock (_lock)
        {
            // Check if we've exceeded max logs
            if (maxLogs.HasValue && _logs.Count >= maxLogs.Value)
            {
                // Stop capturing (keep oldest logs)
                return;
            }

            var log = new TaskExecutionLog
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                TimestampUtc = DateTimeOffset.UtcNow,
                Level = level.ToString(),
                Message = message,
                ExceptionDetails = exception?.ToString(),
                SequenceNumber = _sequenceNumber++
            };

            _logs.Add(log);
        }
    }
}
