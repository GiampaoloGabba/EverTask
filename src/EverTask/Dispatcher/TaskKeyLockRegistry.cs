using System.Collections.Concurrent;

namespace EverTask.Dispatcher;

/// <summary>
/// The per-taskKey critical section: it serializes the read-decide-write of a single task key so two callers
/// can never both act on the row they each read before the other wrote.
/// </summary>
/// <remarks>
/// It started inside the dispatcher, guarding the <c>GetByTaskKey</c> → decide → <c>Persist</c>/<c>UpdateTask</c>
/// of a dispatch against a concurrent dispatch of the same key. A runtime reschedule is the same shape of
/// critical section over the same row — read the schedule, decide the new definition and cursor, write it — and
/// it needs the SAME lock, not one of its own: <c>UpdateTask</c> rewrites the definition and the cursor without
/// touching the schedule version, so a dispatch that interleaved with a reschedule would overwrite it with the
/// row it had read before. Hence one registry, resolved from the container by both.
/// </remarks>
internal sealed class TaskKeyLockRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    /// <summary>
    /// Holds the critical section of <paramref name="taskKey"/> until the returned handle is disposed. A null
    /// or blank key has no section to hold and returns immediately.
    /// </summary>
    public async ValueTask<IDisposable> AcquireAsync(string? taskKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(taskKey))
            return NoopDisposable.Instance;

        var gate = _locks.GetOrAdd(taskKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new SemaphoreReleaser(gate);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
