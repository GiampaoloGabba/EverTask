namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Configuration options for persistent handler logging.
/// When enabled, logs written via the Logger property in handlers are stored in the database for audit trails.
/// Logs are ALWAYS forwarded to ILogger infrastructure (console, file, Serilog, etc.) regardless of this setting.
/// </summary>
public class PersistentLoggerOptions
{
    /// <summary>
    /// Gets or sets whether task execution logs should be persisted to the database.
    /// Default: false (opt-in feature).
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Gets or sets the minimum log level to persist to database.
    /// Logs below this level will not be stored (but still forwarded to ILogger).
    /// Default: LogLevel.Information.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Gets or sets the maximum number of logs to persist per task execution.
    /// If a task exceeds this limit, persistence stops (oldest-first strategy).
    /// Set to null for unlimited (not recommended for production).
    /// Default: 1000.
    /// </summary>
    public int? MaxLogsPerTask { get; set; } = 1000;

    /// <summary>
    /// Enables persistent logging to the database.
    /// </summary>
    /// <returns>The options instance for method chaining.</returns>
    public PersistentLoggerOptions Enable()
    {
        Enabled = true;
        return this;
    }

    /// <summary>
    /// Disables persistent logging to the database.
    /// Logs will still be forwarded to ILogger infrastructure.
    /// </summary>
    /// <returns>The options instance for method chaining.</returns>
    public PersistentLoggerOptions Disable()
    {
        Enabled = false;
        return this;
    }

    /// <summary>
    /// Sets the minimum log level to persist to database.
    /// </summary>
    /// <param name="level">Minimum level to persist.</param>
    /// <returns>The options instance for method chaining.</returns>
    public PersistentLoggerOptions SetMinimumLevel(LogLevel level)
    {
        MinimumLevel = level;
        return this;
    }

    /// <summary>
    /// Sets the maximum number of logs to persist per task execution.
    /// </summary>
    /// <param name="maxLogs">Maximum logs to persist. Null = unlimited (not recommended for production).</param>
    /// <returns>The options instance for method chaining.</returns>
    public PersistentLoggerOptions SetMaxLogsPerTask(int? maxLogs)
    {
        MaxLogsPerTask = maxLogs;
        return this;
    }
}
