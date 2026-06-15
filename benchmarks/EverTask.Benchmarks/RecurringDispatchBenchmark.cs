using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using EverTask.Scheduler.Recurring;

namespace EverTask.Benchmarks;

/// <summary>
/// P-A / F22 — the static <c>RecurringTaskToStringCache</c> was a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// keyed by <see cref="RecurringTask"/> reference identity. Every persisted recurring dispatch built a fresh
/// instance, so <c>GetOrAdd</c> never hit: it paid the dictionary node + lambda closure allocation AND retained
/// the entry forever (the leak).
///
/// This benchmark reproduces both halves of the A/B as it stood before/after the fix:
///   * <see cref="Cached_Batch"/> mirrors the old behaviour (GetOrAdd into a dictionary keyed by reference).
///   * <see cref="Inline_Batch"/> is the shipped fix (plain <c>ToString()</c>).
///
/// The deterministic xUnit gate owns the retention proof (cache must not grow per distinct dispatch);
/// here [MemoryDiagnoser] quantifies the per-op allocation delta.
/// </summary>
[MemoryDiagnoser]
public class RecurringDispatchBenchmark
{
    [Params(500)]
    public int DistinctDispatches { get; set; }

    private RecurringTask[] _tasks = [];

    [GlobalSetup]
    public void Setup()
    {
        // Distinct instances, exactly like distinct persisted dispatches: each is a cache miss.
        _tasks = new RecurringTask[DistinctDispatches];
        for (var i = 0; i < DistinctDispatches; i++)
            _tasks[i] = new RecurringTask { RunNow = true, MaxRuns = i + 1 };
    }

    [Benchmark(Baseline = true, Description = "GetOrAdd (pre-fix)")]
    public string Cached_Batch()
    {
        // Fresh dictionary per invocation so the benchmark itself does not leak across iterations,
        // while still paying GetOrAdd's node + closure allocation that the old hot path paid.
        var cache = new ConcurrentDictionary<RecurringTask, string>();
        var last = "";
        foreach (var rt in _tasks)
            last = cache.GetOrAdd(rt, static r => r.ToString() ?? "Recurring Task");
        return last;
    }

    [Benchmark(Description = "Inline ToString (post-fix)")]
    public string Inline_Batch()
    {
        var last = "";
        foreach (var rt in _tasks)
            last = rt.ToString() ?? "Recurring Task";
        return last;
    }
}
