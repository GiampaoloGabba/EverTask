using System.Collections.Concurrent;
using EverTask.Logger;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// The real logger interface, keeping the EventIds it is handed: where a component says something that
/// publishes no monitoring event of its own — or says it SYNCHRONOUSLY, while the event beside it is
/// fire-and-forget — the log is the only place a test can read it without waiting on a race.
/// </summary>
/// <remarks>
/// Registered for one component (<c>IEverTaskLogger&lt;T&gt;</c>) so a test reads that component's lines and
/// nobody else's.
/// </remarks>
public sealed class RecordingLogger<T> : IEverTaskLogger<T>
{
    private readonly ConcurrentBag<int> _events = [];
    private readonly ConcurrentBag<string> _messages = [];

    /// <summary>How many times <paramref name="eventId"/> was written.</summary>
    public int Count(int eventId) => _events.Count(id => id == eventId);

    /// <summary>
    /// Every line as it was rendered, for what an EventId alone cannot answer: a message whose ARGUMENTS are
    /// the assertion (a summary that counts the same outcomes under one id, say).
    /// </summary>
    public IReadOnlyCollection<string> Messages => _messages;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        _events.Add(eventId.Id);
        _messages.Add(formatter(state, exception));
    }

    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
