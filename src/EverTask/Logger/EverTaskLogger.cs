namespace EverTask.Logger;

public class EverTaskLogger<T>(IServiceProvider serviceProvider) : IEverTaskLogger<T>
{
    private readonly Lazy<ILogger<T>> _logger = new(() =>
        serviceProvider.GetService<ILogger<T>>() ??
        serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<T>());

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter) =>
        _logger.Value.Log(logLevel, eventId, state, exception, formatter);

    public bool IsEnabled(LogLevel logLevel) =>
        _logger.Value.IsEnabled(logLevel);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
        _logger.Value.BeginScope(state);
}
