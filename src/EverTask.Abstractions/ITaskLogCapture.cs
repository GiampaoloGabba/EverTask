namespace EverTask.Abstractions;

/// <summary>
/// Interface for capturing logs during task execution.
/// Injected into <see cref="EverTaskHandler{TTask}"/> via the <see cref="EverTaskHandler{TTask}.Logger"/> property.
/// </summary>
public interface ITaskLogCapture
{
    /// <summary>
    /// Logs a trace message.
    /// Only captured if configured minimum log level is Trace or lower.
    /// </summary>
    void LogTrace(string message);

    /// <summary>
    /// Logs a debug message.
    /// Only captured if configured minimum log level is Debug or lower.
    /// </summary>
    void LogDebug(string message);

    /// <summary>
    /// Logs an informational message.
    /// Only captured if configured minimum log level is Information or lower.
    /// </summary>
    void LogInformation(string message);

    /// <summary>
    /// Logs a warning message with optional exception.
    /// Only captured if configured minimum log level is Warning or lower.
    /// </summary>
    void LogWarning(string message, Exception? exception = null);

    /// <summary>
    /// Logs an error message with optional exception.
    /// Only captured if configured minimum log level is Error or lower.
    /// </summary>
    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Logs a critical message with optional exception.
    /// Always captured (Critical is highest level).
    /// </summary>
    void LogCritical(string message, Exception? exception = null);
}
