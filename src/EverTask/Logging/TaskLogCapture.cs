namespace EverTask.Logging;

/// <summary>
/// Proxy implementation that forwards all logs to ILogger and optionally persists to database.
/// Thread-safe for async/await handlers.
/// </summary>
internal sealed class TaskLogCapture : ITaskLogCaptureInternal
{
    private readonly ILogger _logger;
    private readonly Guid _taskId;
    private readonly IGuidGenerator _guidGenerator;
    private readonly bool _persistLogs;
    private readonly LogLevel _minPersistLevel;
    private readonly int? _maxPersistedLogs;
    private readonly List<TaskExecutionLog>? _logs;
    private readonly object? _lock;
    private int _sequenceNumber = 0;

    public TaskLogCapture(
        ILogger logger,
        Guid taskId,
        IGuidGenerator guidGenerator,
        bool persistLogs,
        LogLevel minPersistLevel,
        int? maxPersistedLogs)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _taskId = taskId;
        _guidGenerator = guidGenerator ?? throw new ArgumentNullException(nameof(guidGenerator));
        _persistLogs = persistLogs;
        _minPersistLevel = minPersistLevel;
        _maxPersistedLogs = maxPersistedLogs;

        // Only allocate list and lock if persistence is enabled
        if (_persistLogs)
        {
            _logs = new List<TaskExecutionLog>(capacity: maxPersistedLogs ?? 100);
            _lock = new object();
        }
    }

    public void LogTrace(string message)
        => LogWithPersistence(LogLevel.Trace, message, null);

    public void LogDebug(string message)
        => LogWithPersistence(LogLevel.Debug, message, null);

    public void LogInformation(string message)
        => LogWithPersistence(LogLevel.Information, message, null);

    public void LogWarning(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Warning, message, exception);

    public void LogError(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Error, message, exception);

    public void LogCritical(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Critical, message, exception);

    public IReadOnlyList<TaskExecutionLog> GetPersistedLogs()
    {
        if (!_persistLogs || _logs == null || _lock == null)
            return [];

        lock (_lock)
        {
            return _logs.ToArray(); // Return defensive copy
        }
    }

    private void LogWithPersistence(LogLevel level, string message, Exception? exception)
    {
        // ALWAYS log to ILogger infrastructure (console, file, Serilog, etc.)
        // Use the correct ILogger.Log signature
        _logger.Log(level, new EventId(0), message, exception, (state, ex) => state?.ToString() ?? string.Empty);

        // Optionally persist to database
        if (_persistLogs && _logs != null && _lock != null)
        {
            PersistLog(level, message, exception);
        }
    }

    private void PersistLog(LogLevel level, string message, Exception? exception)
    {
        // Filter by minimum persistence level
        if (level < _minPersistLevel)
            return;

        lock (_lock!)
        {
            // Check if we've exceeded max persisted logs
            if (_maxPersistedLogs.HasValue && _logs!.Count >= _maxPersistedLogs.Value)
            {
                // Stop persisting (keep oldest logs)
                return;
            }

            var log = new TaskExecutionLog
            {
                Id               = _guidGenerator.NewDatabaseFriendly(),
                TaskId           = _taskId,
                TimestampUtc     = DateTimeOffset.UtcNow,
                Level            = level.ToString(),
                Message          = message,
                ExceptionDetails = exception?.ToString(),
                SequenceNumber   = _sequenceNumber++
            };

            _logs!.Add(log);
        }
    }
}
