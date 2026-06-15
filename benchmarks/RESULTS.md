# EverTask — Performance benchmark results

BenchmarkDotNet A/B measurements quantifying the magnitude of the PERF & stability hardening
(`review/evertask-perf-plan.md`). **These are not CI gates** — the deterministic xUnit gates own
pass/fail. The numbers here only show *how much* each fix saves.

## How to run

```bash
dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *
# or one block:
dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *Recurring*
```

The project is intentionally **outside `EverTask.sln`** so it never runs in the CI test pass.

> Environment (P-A run): BenchmarkDotNet v0.14.0, Windows 11 (10.0.26200), AMD Ryzen 9 7950X
> (16 physical / 32 logical), .NET SDK 10.0.202, runtime net9.0. Numbers are a `--job short`
> (3 warmup + 3 iterations) run — directional magnitude, not a precision benchmark.

---

## P-A — F22: recurring `ToString` cache leak

`RecurringDispatchBenchmark` — 500 distinct recurring dispatches resolved per op.

- **Allocation (per op):** the pre-fix `GetOrAdd` path allocates an extra dictionary node + a lambda
  closure capture per distinct dispatch on top of the `ToString()` it already had to compute; the
  inline path allocates only the `ToString()` result.
- **Retention (the actual bug):** the old static cache was keyed by `RecurringTask` reference
  identity and never evicted, so every distinct persisted dispatch added a permanent entry — a leak
  that grows without bound in long-running processes. The deterministic gate
  `MemoryLeakRegressionTests.Should_not_grow_recurring_tostring_cache_unbounded_across_distinct_dispatches`
  pins this; the inline fix retains nothing.

| Method | Mean | Allocated | Gen0 | Alloc ratio |
|--------|-----:|----------:|-----:|------------:|
| GetOrAdd (pre-fix) | 123.95 us | 260.88 KB | 15.87 | 1.00 |
| Inline ToString (post-fix) | 53.75 us | 128.84 KB | 7.87 | 0.49 |

Inline halves per-op allocation (260.88 KB → 128.84 KB) and roughly halves Gen0 pressure, on top of
removing the unbounded retention entirely. The ~2.3× wall-clock gap is amplified by the ShortRun
noise but is consistent with skipping the dictionary node + closure allocation per dispatch.

---

## P-B — WorkerExecutor hot path

ShortRun (3 warmup + 3 iterations), same machine as P-A.

### P-B.1 — F23: lifecycle MethodInfo resolution (`LifecycleResolutionBenchmark`)

Per-call `GetMethod` ×3 vs a cached per-type lookup (the gate proves it resolves once per type).

| Method | Mean | Ratio |
|--------|-----:|------:|
| GetMethod per call (pre-fix) | 51.75 ns | 1.00 |
| Cached lookup (post-fix) | 9.63 ns | 0.19 |

~5× faster per execution; the cache removes the lookup from the lazy hot path (plus the per-task
`object[]` for `MethodInfo.Invoke` is now built only when a callback actually fires).

### P-B.2 — L30: discarded-event formatting (`EventFormattingBenchmark`)

The "nobody consumes it" case: level filtered out **and** no subscribers.

| Method | Mean | Allocated |
|--------|-----:|----------:|
| Always format (pre-fix) | 107.95 ns | 176 B |
| Guarded skip (post-fix) | ~0 ns | 0 B |

Every discarded Info event previously paid a `string.Format` + arg-array boxing (176 B); the guard
removes it entirely.

### P-B.3 — F24: monitoring fan-out (`MonitoringFanoutBenchmark`, 1000 events × 8 subscribers)

| Method | Mean | Completed work items | Allocated |
|--------|-----:|---------------------:|----------:|
| Unbounded Task.Run (pre-fix) | 2.31 ms | 8000 | 562.74 KB |
| Semaphore-bounded (post-fix) | 2.91 ms | 5982 | 480.31 KB |

With **fast no-op** subscribers the bounded version is slightly slower (semaphore contention) but
already schedules ~25% fewer thread-pool work items and allocates less. The real win is the tail it
cannot show: with a **slow/blocked** subscriber the pre-fix path spawns one fire-and-forget Task per
subscriber per event without limit (thread-pool saturation), while the bounded path holds in-flight
at the cap and drops the overflow — see the deterministic gate `Should_bound_monitoring_fanout_under_load`.

---

## P-C — CU19: scheduler orphan heap entries

`SchedulerReplacementBenchmark` — 50 latest-wins replacements of the same id.

| Method | Mean | Allocated | Retained entries |
|--------|-----:|----------:|-----------------:|
| Enqueue only (pre-fix) | 341.9 ns | 3.27 KB | 50 (all orphans, live) |
| Evict-then-enqueue (post-fix) | 2,920.8 ns | 6.78 KB | 1 |

The headline is **retained entries**: pre-fix the heap keeps one node per replacement (each holding an
executor + payload + policy until its possibly far-future due time); post-fix it keeps exactly one,
pinned by the deterministic gate `SchedulerOrphanHeapTests` (Count == 1 vs ~N). The post-fix
`Allocated` is higher because each eviction rebuilds the heap, but that allocation is **transient and
collectable** (the queue stays ~1 element), whereas the pre-fix 3.27 KB is **live and grows with K**.
The rebuild is O(current size) and only runs when an already-parked id is re-registered.
