namespace EverTask.Abstractions;

/// <summary>
/// Interface for capturing logs during task execution.
/// Injected into <see cref="EverTaskHandler{TTask}"/> via the <see cref="EverTaskHandler{TTask}.Logger"/> property.
/// Supports structured logging with message templates and parameters.
/// </summary>
public interface ITaskLogCapture
{
    /// <summary>
    /// Logs a trace message.
    /// Only captured if configured minimum log level is Trace or lower.
    /// </summary>
    void LogTrace(string message);

    /// <summary>
    /// Logs a trace message with structured parameters.
    /// Only captured if configured minimum log level is Trace or lower.
    /// </summary>
    /// <param name="message">Message template with placeholders (e.g., "Processing {Step}/{Total}")</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogTrace(string message, params object?[] args);

    /// <summary>
    /// Logs a debug message.
    /// Only captured if configured minimum log level is Debug or lower.
    /// </summary>
    void LogDebug(string message);

    /// <summary>
    /// Logs a debug message with structured parameters.
    /// Only captured if configured minimum log level is Debug or lower.
    /// </summary>
    /// <param name="message">Message template with placeholders (e.g., "Processing {Step}/{Total}")</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogDebug(string message, params object?[] args);

    /// <summary>
    /// Logs an informational message.
    /// Only captured if configured minimum log level is Information or lower.
    /// </summary>
    void LogInformation(string message);

    /// <summary>
    /// Logs an informational message with structured parameters.
    /// Only captured if configured minimum log level is Information or lower.
    /// </summary>
    /// <param name="message">Message template with placeholders (e.g., "User {UserId} logged in")</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogInformation(string message, params object?[] args);

    /// <summary>
    /// Logs a warning message with optional exception.
    /// Only captured if configured minimum log level is Warning or lower.
    /// </summary>
    void LogWarning(string message, Exception? exception = null);

    /// <summary>
    /// Logs a warning message with structured parameters.
    /// Only captured if configured minimum log level is Warning or lower.
    /// </summary>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogWarning(string message, params object?[] args);

    /// <summary>
    /// Logs a warning with exception and structured parameters.
    /// Only captured if configured minimum log level is Warning or lower.
    /// </summary>
    /// <param name="exception">Exception to log</param>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogWarning(Exception? exception, string message, params object?[] args);

    /// <summary>
    /// Logs an error message with optional exception.
    /// Only captured if configured minimum log level is Error or lower.
    /// </summary>
    void LogError(string message, Exception? exception = null);

    /// <summary>
    /// Logs an error message with structured parameters.
    /// Only captured if configured minimum log level is Error or lower.
    /// </summary>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogError(string message, params object?[] args);

    /// <summary>
    /// Logs an error with exception and structured parameters.
    /// Only captured if configured minimum log level is Error or lower.
    /// </summary>
    /// <param name="exception">Exception to log</param>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogError(Exception? exception, string message, params object?[] args);

    /// <summary>
    /// Logs a critical message with optional exception.
    /// Always captured (Critical is highest level).
    /// </summary>
    void LogCritical(string message, Exception? exception = null);

    /// <summary>
    /// Logs a critical message with structured parameters.
    /// Always captured (Critical is highest level).
    /// </summary>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogCritical(string message, params object?[] args);

    /// <summary>
    /// Logs a critical error with exception and structured parameters.
    /// Always captured (Critical is highest level).
    /// </summary>
    /// <param name="exception">Exception to log</param>
    /// <param name="message">Message template with placeholders</param>
    /// <param name="args">Arguments to substitute into the template</param>
    void LogCritical(Exception? exception, string message, params object?[] args);
}
