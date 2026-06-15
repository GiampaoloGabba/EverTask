using BenchmarkDotNet.Attributes;

namespace EverTask.Benchmarks;

/// <summary>
/// P-C / CU19 — scheduler latest-wins replacement of the same id. Pre-fix every re-registration left
/// the previous node in the priority queue (retained until its due time); post-fix the stale node is
/// evicted at replacement, so the queue holds a single entry per id.
///
/// Models the heap behaviour with a plain PriorityQueue (the real ConcurrentPriorityQueue is internal).
/// The headline metric is RETAINED ENTRIES (final Count), pinned deterministically by the gate
/// SchedulerOrphanHeapTests; here MemoryDiagnoser shows the per-replacement allocation trade-off
/// (the eviction rebuilds a ~1-element heap, so its churn is bounded — it never accumulates).
/// </summary>
[MemoryDiagnoser]
public class SchedulerReplacementBenchmark
{
    [Params(50)]
    public int Replacements { get; set; }

    [Benchmark(Baseline = true, Description = "Enqueue only (pre-fix): retains every replacement")]
    public int NoRemove()
    {
        var pq = new PriorityQueue<object, long>();
        for (var i = 0; i < Replacements; i++)
            pq.Enqueue(new object(), 1);
        return pq.Count; // == Replacements: all orphans retained
    }

    [Benchmark(Description = "Evict-then-enqueue (post-fix): retains one")]
    public int WithRemove()
    {
        var pq = new PriorityQueue<object, long>();
        object? previous = null;
        for (var i = 0; i < Replacements; i++)
        {
            var item = new object();
            if (previous != null)
            {
                var keep = pq.UnorderedItems.Where(p => !ReferenceEquals(p.Element, previous)).ToArray();
                pq.Clear();
                pq.EnqueueRange(keep);
            }
            pq.Enqueue(item, 1);
            previous = item;
        }
        return pq.Count; // == 1: stale nodes evicted
    }
}
