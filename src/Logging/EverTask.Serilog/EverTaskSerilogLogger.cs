using EverTask.Logger;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using Serilog.Core;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace EverTask.Serilog;

public class EverTaskSerilogLogger<T>(ILogger logger) : IEverTaskLogger<T>
{
    private readonly ILogger _logger = logger.ForContext<T>();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull
    {
        if (state is not IEnumerable<KeyValuePair<string, object>> properties)
            return NoOpDisposable.Instance;

        var enrichers = properties.Select(p => new LogEventEnricherProperty(p.Key, p.Value) as ILogEventEnricher).ToArray();
        return LogContext.Push(enrichers);
    }

    public bool IsEnabled(LogLevel logLevel)
    {
        var serilogLevel = ConvertToSerilogLevel(logLevel);
        return _logger.IsEnabled(serilogLevel);
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        var serilogLevel = ConvertToSerilogLevel(logLevel);
        if (!_logger.IsEnabled(serilogLevel)) return;

        var message = formatter(state, exception);
        _logger.Write(serilogLevel, exception, message);
    }

    private static LogEventLevel ConvertToSerilogLevel(LogLevel logLevel) =>
        logLevel switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            LogLevel.None => LogEventLevel.Verbose,
            _ => throw new ArgumentOutOfRangeException(nameof(logLevel))
        };

    private class LogEventEnricherProperty(string propertyName, object value) : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty(propertyName, value));
        }
    }

    private class NoOpDisposable : IDisposable
    {
        public static NoOpDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
