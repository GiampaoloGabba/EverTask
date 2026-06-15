using BenchmarkDotNet.Attributes;

namespace EverTask.Benchmarks;

/// <summary>
/// P-B.3 / F24 — monitoring fan-out. Pre-fix PublishEvent spawned one fire-and-forget Task.Run per
/// subscriber per event with no cap; under throughput × events × subscribers this floods the thread
/// pool. Post-fix a SemaphoreSlim caps the concurrent in-flight callbacks (over-cap events dropped).
/// Models the dispatch cost of the fan-out (fast no-op subscribers).
/// </summary>
[MemoryDiagnoser]
[ThreadingDiagnoser]
public class MonitoringFanoutBenchmark
{
    [Params(1000)]
    public int Events { get; set; }

    [Params(8)]
    public int Subscribers { get; set; }

    private static readonly int Cap = Math.Max(4, Environment.ProcessorCount * 2);

    [Benchmark(Baseline = true, Description = "Unbounded Task.Run (pre-fix)")]
    public async Task Unbounded()
    {
        var tasks = new List<Task>(Events * Subscribers);
        for (var e = 0; e < Events; e++)
            for (var s = 0; s < Subscribers; s++)
                tasks.Add(Task.Run(static () => { }));
        await Task.WhenAll(tasks);
    }

    [Benchmark(Description = "Semaphore-bounded (post-fix)")]
    public async Task Bounded()
    {
        using var gate = new SemaphoreSlim(Cap, Cap);
        var tasks = new List<Task>(Cap);
        for (var e = 0; e < Events; e++)
        {
            for (var s = 0; s < Subscribers; s++)
            {
                if (!gate.Wait(0))
                    continue; // over-cap: dropped (monitoring is fire-and-forget)
                tasks.Add(Task.Run(() =>
                {
                    try { /* no-op subscriber */ }
                    finally { gate.Release(); }
                }));
            }
        }
        await Task.WhenAll(tasks);
    }
}
