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

The residual ~70 KB/task is **not** DbContext anymore: it's the payload serialization at `Persist`, the
`SqlParameter[]` arrays, and the engine's ~4.5 KB/task. The Newtonsoft → System.Text.Json switch attacks
the serialization slice — measured in P-G (serializer micro) and P-H (end-to-end durable A/B).

---

## P-G — Task payload serialization: Newtonsoft → System.Text.Json (A/B)

`PayloadSerializationBenchmark` — both serializers measured in **one run** (Newtonsoft = baseline) so the
deltas are apples-to-apples, no cross-run machine drift. Settings replicated exactly on both sides: OLD =
Newtonsoft `TypeNameHandling.None`; NEW = the STJ options `EverTaskJson` now uses (PascalCase /
case-insensitive read / relaxed encoder / `AllowReadingFromString`; the internal tolerant-enum converter is
N/A for these enum-free payloads). `Serialize` is what `ToQueuedTask()` pays at dispatch; `Deserialize` is
what recovery / monitoring pay. The concrete type lives in `QueuedTask.Type`, so neither emits `$type`.

ShortRun (3 warmup + 3 iterations), same machine as P-A. **Allocations are reliable** (deterministic);
**times are directional** (high CV at ShortRun).

| Payload | Op | Newtonsoft alloc | STJ alloc | Alloc Δ | Newtonsoft (≈) | STJ (≈) |
|---------|----|-----------------:|----------:|--------:|---------------:|--------:|
| tiny (primitives)     | Serialize   |   1,688 B |     216 B | **-87%** |   310 ns |   124 ns |
| tiny (primitives)     | Deserialize |   3,184 B |     176 B | **-94%** |   640 ns |   242 ns |
| blob 1K (string)      | Serialize   |   7,552 B |   2,184 B | **-71%** | 1,111 ns |   270 ns |
| blob 1K (string)      | Deserialize |   9,200 B |   2,224 B | **-76%** | 1,906 ns |   395 ns |
| nested (50-item graph)| Serialize   |  32,680 B |   8,680 B | **-73%** |  20.2 µs |  10.6 µs |
| nested (50-item graph)| Deserialize |  36,064 B |  12,168 B | **-66%** |  32.0 µs |  13.9 µs |
| blob 64K (string)     | Serialize   | 279,521 B | 131,250 B | **-53%** |  79.2 µs |  38.7 µs |
| blob 64K (string)     | Deserialize | 654,632 B | 131,290 B | **-80%** | 194.5 µs |  40.8 µs |

Reads:
- **STJ cuts serialization allocation 53–94%** across the board, and is ~2–5× faster. The win grows toward
  the small/primitive payloads (`tiny` serialize -87%, deserialize -94%) because Newtonsoft's fixed
  per-call overhead (writer + contract + buffers) dwarfs the actual data there.
- **The LOH/Gen2 blowup is tamed**: Newtonsoft deserialized a 64K blob in **639 KB all on Gen2**; STJ does
  it in **131 KB** (-80%) and in ~1/5 the time. A 64K payload still lands on the LOH (the 64K string itself
  is a large object), but STJ stops *multiplying* it.
- **Deserialize — the recovery/monitoring path — wins biggest** (up to -94%), exactly where it was the
  heavier half under Newtonsoft (1.5–2.4× serialize).

---

## P-H — STJ vs Newtonsoft end-to-end (load harness, real A/B, same machine)

The serializer micro (P-G) isolates the layer; this is what the **whole task lifecycle** pays. Captured by
temporarily reverting the shipped serializer to Newtonsoft and re-running the LoadHarness on the same
machine/Docker (a true A/B, not a reconstruction). Engine alloc is `GC.GetTotalAllocatedBytes` per task;
durable runs use Postgres/SqlServer (Testcontainers, WSL2), audit none, parallelism 16. Smoke-level,
directional — DB throughput swings ±~8% run-to-run.

### Engine layer (A4W — real engine, no persistence)

| Serializer | Allocated/task |
|------------|---------------:|
| Newtonsoft | 4,540 B |
| **STJ**    | **3,334 B (-27%)** |

The serializer is the only thing that changed; the ~1.2 KB/task drop is the `tiny` serialize delta from
P-G landing in the engine total. (Engine *throughput* is too noisy here — CV >6% — to attribute.)

### Durable lifecycle (L8 Postgres) — allocation scales with payload, throughput is DB-bound

| Payload | Newtonsoft alloc/task | STJ alloc/task | Alloc Δ | Newtonsoft thr | STJ thr | Newtonsoft p99 | STJ p99 |
|---------|----------------------:|---------------:|--------:|---------------:|--------:|---------------:|--------:|
| tiny | 75,068 B | 73,658 B | **-1.9%** | 2,556/s | 2,635/s | 3.9 ms | 3.2 ms |
| 1K   | 81,018 B | 75,760 B | **-6.5%** | 2,690/s | 2,480/s | 3.3 ms | 3.5 ms |
| 64K  | 355,650 B | 262,270 B | **-26%** | 1,400/s | 1,431/s | 10.9 ms | **6.2 ms (-43%)** |

SqlServer tiny mirrors Postgres: ~68 KB/task either way (745/s Newtonsoft baseline → 784/s STJ), i.e. flat.

Reads (**this is the honest answer for real apps on a durable DB**):
- **On a tiny task (the recommended "pass IDs, keep it small" shape) STJ is ~invisible on the durable
  path**: serialization is only ~1.4 KB of the ~73 KB/task, the rest is the EF command pipeline +
  `SqlParameter[]` + the DB round-trip. So the durable allocation barely moves and throughput is flat
  (DB-bound — the win does NOT show up as more tasks/sec).
- **The win grows with payload size**: -1.9% (tiny) → -6.5% (1K) → -26% (64K). The bigger the task body,
  the more of the per-task allocation is serialization, and that is exactly the slice STJ shrinks.
- **Where it matters most under load: the tail.** At 64K, STJ cuts ~93 KB/task and **halves p99 latency
  (10.9 → 6.2 ms)** — the Gen2/LOH pressure Newtonsoft generated was showing up as GC-pause tail, and STJ
  removes it. Throughput stays flat (the Postgres round-trip, not CPU/GC, caps it).
- **Net**: STJ is a clear win on the engine layer (-27%) and on the serialize/deserialize APIs (-53…-94%,
  P-G), and on the durable path it pays off proportionally to payload size and most visibly in tail
  latency. With tiny ID-only tasks it is allocation-neutral on the durable path — but never negative, and
  it drops the Newtonsoft dependency entirely.

---

## P-I — net9 → net10 (runtime + EF Core 10): faster on CPU, same durable footprint

Benchmark TFM moved **net9.0 → net10.0** (`benchmarks/Directory.Build.props`), which also swaps EF Core
9.0.17 → 10.0.9 and the .NET 9 → .NET 10 runtime. Same machine; STJ on both sides (System.Text.Json was
already pinned to 10.0.9, so only the runtime/EF changed).

- **Serializer micro — allocations identical, times faster.** Managed allocation is deterministic, so every
  cell matches net9 to the byte; the runtime only moved *time*: Newtonsoft nested serialize 20.2 → 14.5 µs
  (-28%), STJ nested 10.6 → 7.4 µs (-30%), Newtonsoft 64K deserialize 195 → 158 µs (-19%). The
  STJ-vs-Newtonsoft ratios are unchanged — P-G's conclusions are TFM-invariant.
- **Engine (A4W)**: 3,334 → **3,207 B/task** (-4%); throughput steadier (CV 3.9% vs net9's noisy 16%).
- **Durable (L8 Postgres, STJ)** — the headline for real apps:

  | Payload | net9 alloc/task | net10 alloc/task | net9 thr | net10 thr |
  |---------|----------------:|-----------------:|---------:|----------:|
  | tiny | 73,658 B | 74,067 B | 2,635/s | 2,544/s |
  | 1K   | 75,760 B | 76,154 B | 2,480/s | 2,610/s |
  | 64K  | 262,270 B | 269,697 B | 1,431/s | 1,395/s |

**Read: net10 / EF Core 10 did NOT shrink the durable per-task allocation, nor move durable throughput**
(both flat within run-to-run noise). The .NET 10 gains land on CPU-bound work — serialization, the engine —
not on the DB-round-trip-bound durable path. **So the storage-layer opportunities below are exactly as
relevant on net10 as on net9** — upgrading the runtime does not recover the ~73 KB/task durable footprint.
