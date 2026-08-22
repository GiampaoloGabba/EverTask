using EverTask.Logger;
using Microsoft.Extensions.Logging;
using Serilog.Extensions.Logging;
using FrameworkLogger = Microsoft.Extensions.Logging.ILogger;
using ILogger = Serilog.ILogger;

namespace EverTask.Logging.Serilog;

/// <summary>
/// Routes <see cref="IEverTaskLogger{T}"/> to Serilog through the official bridge
/// (<c>Serilog.Extensions.Logging</c>). Message templates, named properties, the <c>EventId</c> and scopes
/// therefore reach the sinks intact, instead of being flattened into a rendered string used as the template.
/// </summary>
public class EverTaskSerilogLogger<T>(ILogger logger) : IEverTaskLogger<T>
{
    // One SerilogLoggerProvider per logger instance, deliberately. The bridge keeps its scope stack in an
    // AsyncLocal on the PROVIDER, so this gives each IEverTaskLogger<T> its own stack: a BeginScope opened on
    // this logger enriches this logger's events, which is the semantics EverTask needs. Sharing one provider
    // across every T (a ConditionalWeakTable keyed on the root Serilog.ILogger) would additionally bleed
    // scopes between unrelated components - nothing needs that, and it buys a static cache plus its lifetime
    // reasoning. The provider is a small object and IEverTaskLogger<> is registered as a singleton, so this
    // costs one instance per closed generic for the lifetime of the host.
    private readonly FrameworkLogger _inner =
        new SerilogLoggerProvider(logger, dispose: false).CreateLogger(typeof(T).FullName!);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter) =>
        _inner.Log(logLevel, eventId, state, exception, formatter);

    public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);

    // SerilogLoggerProvider.BeginScope always returns a scope; the non-nullable return is the shipped surface.
    public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
        _inner.BeginScope(state)!;
}
