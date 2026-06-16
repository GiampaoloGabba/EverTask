using System.Runtime.InteropServices;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Per-worker counters laid out one-per-cache-line to avoid false sharing.
/// Each worker writes ONLY its own slot (single-writer per slot → plain increment is safe);
/// the main thread reads the sum. This keeps the worker hot path free of any shared atomic,
/// which is the whole point: a single hammered <c>Interlocked.Decrement</c> on a dual-CCD box
/// (7950X) measures cross-CCD cache-line ping-pong, not the engine (see BENCHMARK_PLAN §2.1).
/// </summary>
public sealed class PaddedCounters
{
    // 128-byte stride: comfortably more than one 64-byte cache line, dodging adjacent-line prefetch.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct PaddedLong
    {
        [FieldOffset(0)] public long Value;
    }

    private readonly PaddedLong[] _slots;

    public PaddedCounters(int workerCount) => _slots = new PaddedLong[workerCount];

    /// <summary>Increment the calling worker's own slot. Single-writer per slot.</summary>
    public void Increment(int workerId) => _slots[workerId].Value++;

    /// <summary>Sum across all slots. Read from the main thread only. Torn reads impossible on 64-bit.</summary>
    public long Sum()
    {
        long total = 0;
        for (int i = 0; i < _slots.Length; i++)
            total += _slots[i].Value;
        return total;
    }

    public void Reset() => Array.Clear(_slots, 0, _slots.Length);
}
