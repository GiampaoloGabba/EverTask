using EverTask.Logger;
using EverTask.Worker;
using Microsoft.Extensions.Logging;

namespace EverTask.Tests.TestHelpers;

/// <summary>
/// The worker's logger, kept for one thing: it says when the startup recovery is over.
/// </summary>
/// <remarks>
/// Recovery captures its cutoff when it BEGINS, on a thread pool thread, not when <c>StartAsync</c>
/// returns: a row the test dispatches in between is created before that cutoff and is re-dispatched
/// like any leftover of a previous process. The re-dispatch is legitimate, and it resolves the handler
/// once more for its per-type metadata — so any test that COUNTS resolutions, registrations or
/// deliveries has to order itself after recovery instead of assuming it.
/// The recovery has no other completion signal — it is a task nothing hands back — and every way
/// <c>ProcessPendingAsync</c> can end counts: completed, completed with failures, cancelled by a
/// shutdown, no persistence at all, or thrown (<c>WorkerServiceLog</c> 1104, 1105, 1115, 1117, 1118).
/// A watch that waited only for the happy one would hang on the very hosts whose storage it made fail.
/// </remarks>
internal sealed class StartupRecoveryWatch : IEverTaskLogger<WorkerService>
{
    private static readonly int[] RecoveryEnded = [1104, 1105, 1115, 1117, 1118];

    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Finished => _finished.Task;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                            Func<TState, Exception?, string> formatter)
    {
        if (Array.IndexOf(RecoveryEnded, eventId.Id) >= 0)
            _finished.TrySetResult();
    }

    // Unconditionally enabled: the generated log methods check this first, and a false here would drop
    // the very line this watch exists to see.
    public bool IsEnabled(LogLevel logLevel) => true;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
