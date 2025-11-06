using System.Globalization;
using System.Text;

namespace EverTask.Logging;

/// <summary>
/// Proxy implementation that forwards all logs to ILogger and optionally persists to database.
/// Thread-safe for async/await handlers.
/// </summary>
internal sealed class TaskLogCapture : ITaskLogCaptureInternal
{
    private static readonly Func<string, Exception?, string> s_messageFormatter = static (state, _) => state ?? string.Empty;

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
        => LogWithPersistence(LogLevel.Trace, message, null, null);

    public void LogTrace(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Trace, message, null, args);

    public void LogDebug(string message)
        => LogWithPersistence(LogLevel.Debug, message, null, null);

    public void LogDebug(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Debug, message, null, args);

    public void LogInformation(string message)
        => LogWithPersistence(LogLevel.Information, message, null, null);

    public void LogInformation(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Information, message, null, args);

    public void LogWarning(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Warning, message, exception, null);

    public void LogWarning(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Warning, message, null, args);

    public void LogWarning(Exception? exception, string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Warning, message, exception, args);

    public void LogError(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Error, message, exception, null);

    public void LogError(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Error, message, null, args);

    public void LogError(Exception? exception, string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Error, message, exception, args);

    public void LogCritical(string message, Exception? exception = null)
        => LogWithPersistence(LogLevel.Critical, message, exception, null);

    public void LogCritical(string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Critical, message, null, args);

    public void LogCritical(Exception? exception, string message, params object?[]? args)
        => LogWithPersistence(LogLevel.Critical, message, exception, args);

    public IReadOnlyList<TaskExecutionLog> GetPersistedLogs()
    {
        if (!_persistLogs || _logs == null || _lock == null)
            return [];

        lock (_lock)
        {
            return _logs.ToArray(); // Return defensive copy
        }
    }

    private void LogWithPersistence(LogLevel level, string message, Exception? exception, object?[]? args)
    {
        // ALWAYS log to ILogger infrastructure (console, file, Serilog, etc.)
        // Use LoggerExtensions for structured logging support
        if (args is { Length: > 0 })
        {
            // Use structured logging with parameters
            _logger.Log(level, exception, message, args);
        }
        else
        {
            // Simple message without parameters
            _logger.Log(level, new EventId(0), message, exception, s_messageFormatter);
        }

        // Optionally persist to database
        if (_persistLogs && _logs != null && _lock != null)
        {
            PersistLog(level, message, args, exception);
        }
    }

    private static string FormatMessage(string template, object?[] args)
    {
        if (string.IsNullOrEmpty(template) || args.Length == 0)
            return template;

        var span = template.AsSpan();
        var builder = new StringBuilder(template.Length + args.Length * 8);
        var culture = CultureInfo.CurrentCulture;
        var literalStart = 0;
        var argumentIndex = 0;

        for (var pos = 0; pos < span.Length; pos++)
        {
            if (span[pos] == '{')
            {
                if (pos + 1 < span.Length && span[pos + 1] == '{')
                {
                    builder.Append(span.Slice(literalStart, pos - literalStart));
                    builder.Append('{');
                    pos++;
                    literalStart = pos + 1;
                    continue;
                }

                var end = pos + 1;
                while (end < span.Length && span[end] != '}')
                {
                    end++;
                }

                if (end >= span.Length)
                {
                    builder.Append(span.Slice(literalStart, pos - literalStart + 1));
                    literalStart = pos + 1;
                    continue;
                }

                builder.Append(span.Slice(literalStart, pos - literalStart));

                var placeholder = span.Slice(pos + 1, end - pos - 1);
                AppendFormattedPlaceholder(builder, placeholder, args, culture, ref argumentIndex);

                pos = end;
                literalStart = pos + 1;
            }
            else if (span[pos] == '}' && pos + 1 < span.Length && span[pos + 1] == '}')
            {
                builder.Append(span.Slice(literalStart, pos - literalStart));
                builder.Append('}');
                pos++;
                literalStart = pos + 1;
            }
        }

        if (literalStart < span.Length)
        {
            builder.Append(span.Slice(literalStart));
        }

        return builder.ToString();
    }

    private static void AppendFormattedPlaceholder(
        StringBuilder builder,
        ReadOnlySpan<char> placeholder,
        object?[] args,
        CultureInfo culture,
        ref int argumentIndex)
    {
        if (argumentIndex >= args.Length)
        {
            builder.Append('{');
            builder.Append(placeholder);
            builder.Append('}');
            return;
        }

        int alignment = 0;
        bool hasAlignment = false;
        ReadOnlySpan<char> formatSpan = default;

        var colonIndex = placeholder.IndexOf(':');
        ReadOnlySpan<char> nameSpan;

        if (colonIndex >= 0)
        {
            nameSpan = placeholder[..colonIndex];
            formatSpan = placeholder[(colonIndex + 1)..];
        }
        else
        {
            nameSpan = placeholder;
        }

        var alignmentIndex = nameSpan.IndexOf(',');
        if (alignmentIndex >= 0)
        {
            var alignmentSpan = nameSpan[(alignmentIndex + 1)..].Trim();
            nameSpan = nameSpan[..alignmentIndex];

            if (int.TryParse(alignmentSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedAlignment))
            {
                alignment = parsedAlignment;
                hasAlignment = true;
            }
        }

        if (!nameSpan.IsEmpty && (nameSpan[0] == '@' || nameSpan[0] == '$'))
        {
            nameSpan = nameSpan[1..];
        }

        var value = args[argumentIndex++];
        var formatted = FormatValue(value, formatSpan, culture);

        if (hasAlignment)
        {
            var width = Math.Abs(alignment);
            if (formatted.Length < width)
            {
                var padding = width - formatted.Length;
                if (alignment < 0)
                {
                    builder.Append(formatted);
                    builder.Append(' ', padding);
                    return;
                }

                builder.Append(' ', padding);
                builder.Append(formatted);
                return;
            }
        }

        builder.Append(formatted);
    }

    private static string FormatValue(object? value, ReadOnlySpan<char> format, CultureInfo culture)
    {
        if (value == null)
            return "null";

        if (!format.IsEmpty)
        {
            var formatString = new string(format);
            if (value is IFormattable formattableWithFormat)
                return formattableWithFormat.ToString(formatString, culture);

            return string.Format(culture, "{0:" + formatString + "}", value);
        }

        if (value is IFormattable formattable)
            return formattable.ToString(null, culture);

        return value.ToString() ?? "null";
    }

    private void PersistLog(LogLevel level, string template, object?[]? args, Exception? exception)
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

            var message = args is { Length: > 0 } capturedArgs
                ? FormatMessage(template, capturedArgs)
                : template;

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
