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

---

## P-D — Rate limiting (L14, CU20, L22)

These are **correctness / latency** fixes, not throughput micro-optimisations; the deterministic gates
own the proof, so there are no headline BenchmarkDotNet tables here.

- **L14 (in-slot wait → re-park):** the gate `Should_defer_near_slot_without_inslot_wait` pins the
  invariant — a near slot is re-parked (Defer) and the limiter is hit exactly once, so the consumer is
  never blocked by an inline `Task.Delay`. The benefit (no head-of-line blocking of ungated tasks on a
  single-consumer queue) is a latency property; a wall-clock throughput micro would be flaky and is
  intentionally omitted in favour of the deterministic gate.
- **CU20 (atomic tracked-keys cap):** correctness of the cap under concurrency
  (`Should_not_exceed_maxtrackedkeys_under_concurrent_distinct_acquisitions`). A contention benchmark
  is optional and not informative — the value is the bound, not speed. The new-key slow path takes a
  short lock only on first insertion; existing keys keep the lock-free fast path.
- **L22 (reservation redemption under congested latency):** pure correctness
  (`Should_redeem_reservation_after_realistic_congested_redelivery_latency`) — no throughput dimension.
  The reservation expiry margin now also covers the parking-lot pause (5 s → 10 s).

---

## P-E — Recovery robustness (L18, L34)

Robustness / correctness fixes — the deterministic gates own the proof, no headline benchmark.

- **L18 (poison persistently-failing re-dispatch + honest summary):** a persisted
  `RecoveryDispatchFailureCount` lets recovery poison a task (mark `Failed`) after a configurable number
  of failed re-dispatches instead of retrying it every restart while the summary logged false success.
  Pinned by `WorkerServiceRecoveryPoisonTests` (poison after K, no false success) and the cross-provider
  storage contract `IncrementRecoveryFailure_should_count_and_ClearRecoveryFailure_should_reset`.
- **L34 (per-queue recovery parallelism):** recovery is partitioned by target queue, so a blocking
  enqueue toward one saturated queue can no longer occupy every global slot and head-of-line-block the
  recovery of idle queues. Pinned by `RecoveryParallelismIntegrationTests` (a wedged queue does not
  starve an idle queue's recovery). A wall-clock recovery-throughput micro would be flaky; the
  TaskCompletionSource-gated integration test is the deterministic proof.

---

## P-F — DbContext pooling on the EF Core storage path

From the load-benchmark / storage-allocation work (`benchmarks/BENCHMARK_PLAN.md`,
`benchmarks/EverTask.LoadHarness`), not the original hardening plan.

**The bug:** every storage op opened a FRESH `DbContext` (`contextFactory.CreateDbContextAsync()`), even
when the SQL is a stored proc (SqlServer) / writable CTE (Postgres) / `ExecuteUpdate` (base). A task's
lifecycle does ~4 such ops → ~4 contexts/task. The three providers registered `AddDbContextFactory`
(**not pooled**) while the code comments + `CHANGELOG` claimed "built-in pooling" — false; only
`AddPooledDbContextFactory` pools. Pooling was blocked by the context's 2nd ctor param
(`IOptions<ITaskStoreOptions>` for the schema, which EF pooling forbids); the fix routes the schema via a
custom `IDbContextOptionsExtension` (`UseEverTaskSchema`) so the ctor takes a single `DbContextOptions`,
then switches the three registrations to `AddPooledDbContextFactory`.

### Micro (`DbContextPoolingBenchmark`, BenchmarkDotNet DefaultJob, N=15, same machine as P-A)

| Method | Mean | Allocated | Alloc ratio |
|--------|-----:|----------:|------------:|
| Create context — non-pooled (pre-fix) | 3,816 ns | 6,608 B | 1.00 |
| Create context — **production, pooled** (post-fix) | **71.6 ns** | **104 B** | **0.02** |
| Create + 1 write — non-pooled | 51,419 ns | 55,155 B | 8.35 |
| Create + 1 write — pooled | 11,053 ns | 6,408 B | 0.97 |

The production `SqliteTaskStoreContext` (`Create_Real_Pooled`) matches the pool-compatible proxy
(104 B, ~71 ns) — the win landed in production: **6,616 B → 104 B (-98%), 53× faster** per context. With a
real write the cold non-pooled context pays ~48 KB of one-time pipeline init it then throws away on
dispose; the pooled context reuses it (**55 KB → 6.4 KB per write**).

### End-to-end (`EverTask.LoadHarness` L8, audit none, parallelism 16, count 5k — smoke-level, directional)

| Storage | Throughput | Allocated/task | p999 latency |
|---------|-----------:|---------------:|-------------:|
| Postgres (pre-fix) | 2,127/s | ~250 KB | 9.2 ms |
| **Postgres (post-fix)** | **2,543/s (+20%)** | **~73 KB (-71%)** | **4.1 ms (-55%)** |
| SqlServer (pre-fix) | 729/s | ~362 KB | 21.7 ms |
| **SqlServer (post-fix)** | **745/s (~flat)** | **~68 KB (-81%)** | ~flat |

Pooling cuts per-task allocation **-71% (Postgres) / -81% (SqlServer)**. Throughput rises **+20% on
Postgres** (GC-pressure-sensitive) and is **flat on SqlServer** (write/round-trip-bound) — pooling saves
allocations and GC pauses, not the DB round-trip. The **p999 tail halves on Postgres** (9.2 → 4.1 ms),
the GC-pause reduction showing up where it matters.

The residual ~70 KB/task is **not** DbContext anymore: it's the Newtonsoft payload serialization at
`Persist`, the `SqlParameter[]` arrays, and the engine's ~4.5 KB/task. The Newtonsoft → System.Text.Json
switch is the next allocation frontier (see P-G).

---

## P-G — Task payload serialization (Newtonsoft baseline)

`PayloadSerializationBenchmark` — the **BEFORE** baseline for the Newtonsoft → System.Text.Json switch.
Replicates EverTask's serializer settings exactly (`EverTaskJson` is internal: `TypeNameHandling.None` —
the concrete type is stored in `QueuedTask.Type` and deserialized to it, no `$type` markers). `Serialize`
is what `ToQueuedTask()` pays at dispatch; `Deserialize` is what recovery / monitoring pay.

ShortRun (3 warmup + 3 iterations), same machine as P-A. **Allocations are reliable** (deterministic);
**times are directional** (high CV at ShortRun). After the STJ switch, re-run `--filter
*PayloadSerialization*` for the per-payload after.

| Payload | Serialize alloc | Deserialize alloc | Serialize (≈) | Deserialize (≈) |
|---------|----------------:|------------------:|--------------:|----------------:|
| tiny (primitives) | 1.65 KB | 3.11 KB | 0.31 µs | 0.61 µs |
| blob 1K (string) | 7.38 KB | 8.98 KB | 0.98 µs | 1.44 µs |
| nested (50-item graph) | 31.91 KB | 35.22 KB | 17.0 µs | 27.9 µs |
| blob 64K (string) | 272.92 KB | 639.18 KB | 66.2 µs | 156.7 µs |

Reads:
- **Even a trivial task pays ~1.65 KB to serialize** — part of the ~70 KB/task residual from P-F (Persist
  serializes once per dispatch).
- **Allocation explodes super-linearly with size**: blob 64K serializes in **272 KB** (~4× the payload)
  and deserializes in **639 KB** (~10×), all of it on the **LOH/Gen2** (Gen0=Gen1=Gen2 for blob64k) —
  expensive Gen2 pauses under load. The nested graph pays ~8× its on-wire size.
- **Deserialize costs more than Serialize** (~1.5–2.4× allocation) — the path recovery and monitoring pay.

This is where System.Text.Json (pooled UTF-8 buffers, direct byte writing) is expected to win most: the
large/nested payloads and the LOH pressure. `tiny` should drop toward ~0.3–0.5 KB with source-gen.
