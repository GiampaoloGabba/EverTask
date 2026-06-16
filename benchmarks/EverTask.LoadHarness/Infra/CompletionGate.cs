using System.Diagnostics;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// End-to-end completion detection without per-task shared atomics. Workers signal via
/// <see cref="PaddedCounters"/> (own slot each); the caller spin-waits on the summed count.
/// Used by scenarios where work runs inside EverTask and cannot be awaited per task
/// (L1, L8, …). Tier-0 raw scenarios (A1/A2) await their tasks directly and don't need this.
/// </summary>
public sealed class CompletionGate(int workerCount)
{
    private readonly PaddedCounters _counters = new(workerCount);
    private readonly int _slots = workerCount;

    public void MarkDone(int workerId) => _counters.Increment(workerId);

    /// <summary>
    /// Mark completion when no stable worker id is available (EverTask handlers run on pool threads we
    /// don't index). Buckets by managed thread id: a given worker thread hits the same slot every time,
    /// so contention stays low without the engine handing us an id. Collisions just share a line —
    /// far cheaper than one global counter. Size <c>workerCount</c> generously (e.g. cores × 4).
    /// </summary>
    public void MarkDoneByThread() =>
        _counters.Increment((Environment.CurrentManagedThreadId & 0x7fffffff) % _slots);

    public long Completed => _counters.Sum();

    public void Reset() => _counters.Reset();

    /// <summary>
    /// Block the calling (main) thread until <paramref name="target"/> tasks have completed.
    /// SpinWait escalates to brief sleeps so a saturated machine doesn't burn a full core,
    /// while never adding latency to the worker hot path (workers never touch shared state here).
    /// </summary>
    public void WaitFor(long target, CancellationToken ct = default)
    {
        var spin = new SpinWait();
        while (_counters.Sum() < target)
        {
            ct.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }
    }
}
