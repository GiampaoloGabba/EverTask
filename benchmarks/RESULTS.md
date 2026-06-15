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
