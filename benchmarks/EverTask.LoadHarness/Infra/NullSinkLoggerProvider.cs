using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace EverTask.LoadHarness.Infra;

/// <summary>How much work the harness's fake sink does per log record (<c>--sink</c>).</summary>
public enum LogSinkMode
{
    /// <summary>No provider at all — the harness's historical wiring: MEL drops the record on the floor.</summary>
    None,

    /// <summary>Calls the formatter and discards the string: a console/file sink minus the I/O.</summary>
    Render,

    /// <summary>Reads the event id and walks the state's key/value pairs: a structured sink (Serilog/Seq) minus the I/O.</summary>
    Enumerate
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that pays a real sink's per-record cost and writes nothing. With no
/// provider registered, MEL never builds a message at all, so the harness cannot see what EverTask's own
/// logging costs; with a console or file sink the I/O would dominate. This sits in between — the record is
/// fully consumed, nothing leaves the process.
///
/// It adds no allocation of its own: one shared stateless logger for every category, no scopes, no locks,
/// no buffers. What it does allocate is exactly what the sink under test would — the string the formatter
/// builds (<see cref="LogSinkMode.Render"/>), and the box the state takes on when read through
/// <c>IReadOnlyList&lt;KeyValuePair&lt;string, object?&gt;&gt;</c> plus the boxes its indexer makes for
/// value-typed arguments (<see cref="LogSinkMode.Enumerate"/>). Those ARE the cost being measured, and they
/// are the reason <c>BytesPerTask</c> is only comparable across runs with the same <c>--log</c>/<c>--sink</c>.
///
/// Level filtering stays with the pipeline (<c>--log</c> → <c>SetMinimumLevel</c>); this logger says yes to
/// everything it is handed.
/// </summary>
public sealed class NullSinkLoggerProvider(LogSinkMode mode) : ILoggerProvider
{
    private readonly SinkLogger _logger = new(mode);

    /// <summary>Applies the run's <c>--log</c>/<c>--sink</c> knobs. Shared by <see cref="HostFactory"/> and <see cref="StorageMatrix"/>.</summary>
    public static void Configure(ILoggingBuilder builder, RunConfig cfg)
    {
        builder.SetMinimumLevel(cfg.Log);
        if (cfg.Sink != LogSinkMode.None)
            builder.AddProvider(new NullSinkLoggerProvider(cfg.Sink));
    }

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose() { }

    private sealed class SinkLogger(LogSinkMode mode) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        // null = no scope object, so scopes cost nothing here.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
        {
            if (mode == LogSinkMode.Render)
            {
                Consume(formatter(state, exception));
                return;
            }

            if (mode != LogSinkMode.Enumerate)
                return;

            Consume(eventId.Id);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
            {
                // Indexer, not foreach: enumerating through the interface would allocate an enumerator too.
                for (var i = 0; i < list.Count; i++)
                {
                    var pair = list[i];
                    Consume(pair.Key, pair.Value);
                }
            }
            else if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                    Consume(pair.Key, pair.Value);
            }
        }

        // Empty and non-inlinable: the JIT must emit the call, so it cannot drop the reads that feed it.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Consume(string rendered) { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Consume(int eventId) { }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Consume(string key, object? value) { }
    }
}
