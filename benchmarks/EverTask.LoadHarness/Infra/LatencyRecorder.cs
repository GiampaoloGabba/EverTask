using System.Diagnostics;
using HdrHistogram;

namespace EverTask.LoadHarness.Infra;

/// <summary>
/// Thread-safe latency recorder over a fixed-memory HdrHistogram (concurrent variant), recording
/// in NANOSECONDS. Workers record from many threads; the main thread reads percentiles.
///
/// Coordinated-omission correction: for OPEN-LOOP scenarios feed the planned inter-arrival via
/// <see cref="RecordWithExpectedInterval"/> so a stalled producer re-materialises the samples it
/// "skipped" (BENCHMARK_PLAN §2.2/§2.4). Plain <see cref="Record"/> is for closed-loop throughput
/// runs where the latency is incidental.
/// </summary>
public sealed class LatencyRecorder
{
    // 1 ns .. 60 s, 3 significant digits → ~0.1% error, a few hundred KB fixed.
    private readonly LongConcurrentHistogram _histogram = new(1, 60_000_000_000L, 3);
    private static readonly double NsPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    public static long ToNanoseconds(long stopwatchTicks) =>
        stopwatchTicks < 1 ? 1 : (long)(stopwatchTicks * NsPerTick);

    /// <summary>Record a latency expressed in raw <see cref="Stopwatch"/> ticks.</summary>
    public void Record(long stopwatchTicks) => _histogram.RecordValue(ToNanoseconds(stopwatchTicks));

    /// <summary>
    /// Record a latency (ticks) with the expected inter-arrival interval (ns), correcting for
    /// coordinated omission in open-loop runs.
    /// </summary>
    public void RecordWithExpectedInterval(long stopwatchTicks, long expectedIntervalNs) =>
        _histogram.RecordValueWithExpectedInterval(ToNanoseconds(stopwatchTicks), expectedIntervalNs);

    public void Reset() => _histogram.Reset();

    public LatencySnapshot Snapshot()
    {
        if (_histogram.TotalCount == 0)
            return new LatencySnapshot(0, 0, 0, 0, 0, 0, 0);

        return new LatencySnapshot(
            Count: _histogram.TotalCount,
            P50Ns: _histogram.GetValueAtPercentile(50),
            P90Ns: _histogram.GetValueAtPercentile(90),
            P99Ns: _histogram.GetValueAtPercentile(99),
            P999Ns: _histogram.GetValueAtPercentile(99.9),
            MaxNs: _histogram.GetMaxValue(),
            MeanNs: _histogram.GetMean());
    }
}

/// <summary>Latency percentiles, all in nanoseconds.</summary>
public readonly record struct LatencySnapshot(
    long Count, long P50Ns, long P90Ns, long P99Ns, long P999Ns, long MaxNs, double MeanNs);
