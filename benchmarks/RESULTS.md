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

---

## P-J — Issue #23 phase 1: base vs patched (gate D7)

Phase 1 of issue #23 is meant to change no behaviour except X3, so the D7 gate asks a single question: does
it cost anything on dispatch, execute or the recurring advance? This is the answer — a real A/B, two
checkouts of the same commit, one with the phase-1 diff applied and one without.

- **base** = `V:/Temp/claude/evertask-issue23-baseline`, the `issue23-baseline` tag (`c71b5e2`), the tree as
  it was before phase 1. The `LRA` scenario and its `Program.cs` registration were copied over verbatim so
  both sides run the same cell.
- **patched** = the working tree with the phase-1 diff, **re-measured on 2026-08-23 after the five ratified
  fixes landed** (decisions §3.1) and after the second-jury round. An earlier A/B against the pre-fix tree is
  superseded by this one; the numbers below are the delivered tree's.
- Same machine (Ryzen 9 7950X, 32 logical cores), .NET 10.0.11, Workstation GC, `--log Warning --sink none`,
  idle box. **Checkouts alternated** (base, patched, base, patched, …) for 3 repetitions, medians reported —
  a whole base block followed by a whole patched block would bake thermal drift into the delta.

```bash
# one repetition, per side; repeated 3× alternating
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4W --count 1m --parallelism 16 --producers 8 --warmup 3 --measured 7
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- LRA --storage inmemory --count 2m --parallelism 8 --warmup 3 --measured 7
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- LRA --storage sqlite   --count 2000 --parallelism 4 --warmup 2 --measured 5
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage sqlite --parallelism 1 --count 1000 --warmup 2 --measured 5
```

| Cell | What it covers | base thr | patched thr | Δ thr | base B/task | patched B/task | Δ alloc |
|------|----------------|---------:|------------:|------:|------------:|---------------:|--------:|
| A4W | dispatch + execute, real engine, no persistence | 1,230,907/s | 1,233,817/s | +0.2% | 2,744 | 2,955 | **+7.7%** |
| LRA in-memory | recurring advance, evaluator-dominated | 11.09 M/s | 11.16 M/s | +0.6% | 143.5 | 143.6 | +0.1% |
| LRA SQLite | recurring advance on the widened table | 1,671/s | 1,663/s | -0.5% | 21,759 | 22,049 | +1.3% |
| A4S SQLite (p1) | the 3 lifecycle writes on the widened table | 516/s | 522/s | +1.2% | 81,917 | 87,430 | **+6.7%** |

Dispatch-call latency (`LDP`) is missing on purpose: on SQLite four concurrent producers hit the
single-writer convoy (11 tasks/s, p50 159 ms) and on In-Memory the O(n)-per-write store degrades across
iterations (CV 39%). Neither number would measure the diff. `A4S --parallelism 1` is the clean per-write
cell the README recommends instead.

**Throughput: no regression.** Every cell lands within ±1.2%, and the two SQLite cells (spread ≤ 0.7% across
their six runs) are the ones to read: -0.5% and +1.2%, with A4S's p50 slightly better (1844 → 1817 µs). The
two engine cells swing 6–10% between repetitions of the SAME side, so their ±0.5% deltas say nothing beyond
"no regression visible at this resolution". A4W's p50 is the one number worth naming as unusable: it ranged
102–1289 µs across six runs on both sides alike, because that cell's latency is the producer/consumer convoy,
not the work being measured.

**Allocation grew, and that part is real.** Managed allocation is deterministic, so the two allocation
deltas are not noise: +6.7% on the durable write path and +7.7% (~210 B/task) on the engine. Both track the
schema and record changes phase 1 makes, and both were expected:

- `QueuedTasks` gained three columns, so every INSERT and every tracked entity carries more — that is the
  A4S +5.5 KB/task, on a path where ~82 KB/task is EF command pipeline and `SqlParameter[]` to begin with.
- `QueuedTask` also gained an `Occurrences` navigation collection, eagerly allocated per row exactly like
  the pre-existing `ExecutionLogs` one, and `TaskHandlerExecutor` gained five properties that travel with
  every executor and every `with` copy.
- LRA is unaffected (+0.1% / +1.3%): it advances an existing row and never builds a new one.

Neither delta is a round-trip — the D7 wording — and neither moves wall-clock. Worth a follow-up all the
same: making the two navigation collections lazy would hand back most of the engine's ~210 B/task, and it is
a change to `QueuedTask` alone.

The post-fix re-measurement changed no conclusion, and it could not have: none of the five ratified fixes is
on a path these four cells walk. R1 removed a `Get` round-trip from a recovery branch, R15 added an `EXISTS`
to a background cleanup pass, and the schedule validation added at the dispatch entry point costs seven null
checks and one `Enum.IsDefined` per recurring dispatch — a path no cell dispatches on (A4W dispatches
one-shots, LRA advances an existing schedule row without going through the dispatcher). That is a statement
about which code runs, not something the table above measures; the table's job here is to show the columns,
the navigation collection and the executor properties still cost nothing in wall-clock.

## P-K — Issue #23 final: the whole 4.0 branch vs master (gate D7, release measurement)

P-J measured phase 1 alone; this is the release-closing A/B of the ENTIRE `feature/issue23-durable-occurrences`
branch (`ce5f054`, all seven phases) against master (`5fdd2a1`, the 3.x tip). Same four cells as P-J, same
machine (Ryzen 9 7950X, 32 logical cores), .NET 10.0.11, Workstation GC, `--log Warning --sink none`.

- **base** = a git worktree of master at `V:/Temp/claude/evertask-40-baseline`, with the branch's `LRA`
  scenario and its `Program.cs` registration copied over verbatim (it compiles clean against the 3.x
  surface), so both sides run the identical harness.
- Checkouts alternated (base, patched, base, patched, …), 3 repetitions per side, medians reported. The
  A4W deltas came out pairwise-consistent with base always first in the pair, so A4W got **two extra
  control pairs in reversed order** (patched first): the gap survived the reversal, so it is not an
  ordering/thermal artifact. A4W medians below are over all 5 runs per side.
- Caveat on ambient noise: an IDE was open (idle) during the run. Same-side spread stayed within P-J's
  observed 6–10% band on the engine cells and ≤1.6% on the SQLite cells; allocation numbers are
  deterministic and unaffected.

```bash
# one repetition, per side; repeated 3× alternating (+2 reversed-order A4W control pairs)
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4W --count 1m --parallelism 16 --producers 8 --warmup 3 --measured 7
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- LRA --storage inmemory --count 2m --parallelism 8 --warmup 3 --measured 7
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- LRA --storage sqlite   --count 2000 --parallelism 4 --warmup 2 --measured 5
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage sqlite --parallelism 1 --count 1000 --warmup 2 --measured 5
```

| Cell | What it covers | base thr | patched thr | Δ thr | base B/task | patched B/task | Δ alloc |
|------|----------------|---------:|------------:|------:|------------:|---------------:|--------:|
| A4W (5 runs/side) | dispatch + execute, real engine, no persistence | 930,932/s | 871,660/s | **-6.4%** | 2,951 | 3,498 | **+18.5%** |
| LRA in-memory | recurring advance, evaluator-dominated | 10.78 M/s | 10.76 M/s | -0.2% | 143.5 | 151.5 | +5.6% |
| LRA SQLite | recurring advance on the widened table | 1,709/s | 1,701/s | -0.5% | 21,809 | 22,048 | +1.1% |
| A4S SQLite (p1) | the 3 lifecycle writes on the widened table | 525/s | 525/s | 0.0% | 81,797 | 87,429 | +6.9% |

**Production paths: parity.** The three cells that touch what a durable app actually pays — the lifecycle
writes and the recurring advance — moved 0.0%, -0.5% and -0.2%, with A4S's p50 marginally better
(1,817 → 1,804 µs). The durable write's +6.9% alloc is the P-J number carried through (the three columns
plus the navigation collection on every tracked row); on a path that is fsync- and round-trip-bound it
buys no wall-clock, exactly as in phase 1.

**Engine ceiling: the branch costs ~6% in vitro, and it tracks the allocation growth.** A4W is the
diagnostic cell — one-shot dispatch + execute over `NullTaskStorage`, no DB to hide behind — and the whole
branch shows -6.4% throughput with +547 B/task (+18.5%). Phase 1 alone was +211 B/task at flat throughput
(P-J); phases 2–6 added the rest: the execution-context state that now travels with every executor
(`ScheduledAtUtc`/`ScheduledAtLocal`/misfire fields and their `with` copies), the wider `QueuedTask` row
built per dispatch, and the schedule-manager/provider seams on the dispatch path. At ~900k tasks/s the
extra ~550 B/task is ~500 MB/s of additional Gen0 pressure, which is where the 6% goes. No production cell
sees it — their per-task cost is 6–25× larger and DB-bound — but reclaiming it is tracked with the storage
allocation issues (#48–#53); the lazy navigation collections from P-J's follow-up note remain the first
candidate, now joined by the executor's context fields.

## P-L — Storage write costs: the measure-first pass for #16 / #17 / #18

the pooling win.

### What was added

| File | Purpose |
|------|---------|
| `benchmarks/EverTask.Benchmarks/StorageBenchInfra.cs` | Container provisioning, a production-wired host per provider, and the model-derived raw `INSERT` builder |
| `benchmarks/EverTask.Benchmarks/PersistInsertBenchmark.cs` | #17 — tracked `Persist` vs raw parameterized INSERT (EF command / raw ADO) |
| `benchmarks/EverTask.Benchmarks/ProcRawAdoBenchmark.cs` | #18 — proc/CTE through a pooled `DbContext` vs raw ADO command; its baseline is also #16's standalone `SetQueued` cost |
| `benchmarks/EverTask.Benchmarks/Program.cs` | Provisions the two containers once for the whole run |
| `benchmarks/EverTask.Benchmarks/EverTask.Benchmarks.csproj` | Adds the SqlServer/Postgres providers + Testcontainers |

```bash
# both micros, all three providers (Docker for postgres/sqlserver)
dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *PersistInsert* *ProcRawAdo* --job short
# one backend at a time
EVERTASK_BENCH_PROVIDERS=sqlite dotnet run -c Release --project benchmarks/EverTask.Benchmarks -- --filter *PersistInsert* --job short
```

### Methodology

- **Production wiring, not a proxy.** Each case builds the real DI graph (`AddSqliteStorage` /
  `AddPostgresStorage` / `AddSqlServerStorage`): pooled `DbContextFactory`, migrations applied, the real
  `ITaskStorage`. The baseline variant calls the storage interface itself, so it is literally the shipped path.
- **The A/B is the write mechanism, nothing else.** The `QueuedTask` is allocated once in `[GlobalSetup]`
  and only its `Id` changes per op; row construction is the caller's cost either way.
- **The raw INSERT is verified, not assumed.** Its column list is derived from the EF model (a mapped column
  missing from the hand-written value list throws), and `[GlobalSetup]` writes one row through each variant
  and compares every column of the three rows read back through EF, offsets included. A raw INSERT that
  stored a different shape would be a bug, not a win.
- **Raw ADO uses the same pool.** `SqlConnection` / `NpgsqlConnection` / `SqliteConnection` open on the
  *same connection string* the EF context uses, so there is no connection-churn artefact.
- **Containers provisioned once** (`postgres:16-alpine`, `mcr.microsoft.com/mssql/server:2022-latest`) by the
  host process and handed to BenchmarkDotNet's per-case child processes through a temp file. Tables are
  emptied at `[GlobalSetup]` so every case starts from a comparable table size.
- **Job**: `--job short` (3 warmup + 3 iterations), `[MemoryDiagnoser]`, net10.0 / EF Core 10, tree at
  `23cbc0c`.

### Noise caveat, and when each run was captured

**Allocation is deterministic and is the decision metric. Times are indicative.** Every op here is a real
database round-trip on a shared machine. Quote the `Allocated` columns; treat the `Mean` columns as order of
magnitude. SQL Server runs in a WSL2 container and is ~3x slower per round-trip than Postgres here, exactly
as in P-H — that is the container, not the provider.

One window matters for the timings: **16:35-17:30 UTC**, during which the orchestrator ran the #19 AFTER
benchmark (`L8 sqlite`) on the main tree, so both processes were fsync-bound on the same disk. The capture
timeline:

| Run | UTC | In the contention window? |
|-----|-----|---------------------------|
| `PersistInsertBenchmark` + `ProcRawAdoBenchmark`, postgres + sqlserver | 16:00-16:02 | no |
| `PersistInsertBenchmark` + `ProcRawAdoBenchmark`, sqlite | ~16:07 | no |
| `L8` postgres, audit none (P-L.3) | 16:04 | no |
| `L8` sqlite, audit none (P-L.5) | 16:19 | no |
| `L8` sqlite, audit full — **discarded** | 16:41-17:44 | **yes**, and it never completed |
| `L8` sqlite, audit full — the kept run (P-L.5) | 18:00-18:54 | no |

**Every number this document draws a conclusion from was captured outside that window.** The one run that
fell inside it was thrown away and re-run on a quiet machine — and the re-run reproduced its per-iteration
throughput exactly (6-7 tasks/s either way), so `--audit full` on SQLite is simply that slow rather than
having been starved.

---

### P-L.1 — #17: the tracked `Persist` insert

`PersistInsertBenchmark`, allocated bytes/op and Gen0 collections per 1,000 ops.

| Provider | Variant | Allocated | vs tracked | Gen0 | Mean (indicative) |
|----------|---------|----------:|-----------:|-----:|------------------:|
| SQLite | `Tracked_Persist` (production) | **38.63 KB** | — | 1.95 | 684 µs |
| SQLite | `Raw_Insert_EfCommand` | 17.53 KB | **−55%** | 0.98 | 649 µs (0.95x) |
| SQLite | `Raw_Insert_AdoCommand` | 6.41 KB | **−83%** | 0 | 635 µs (0.93x) |
| Postgres | `Tracked_Persist` (production) | **37.42 KB** | — | 1.95 | 466 µs |
| Postgres | `Raw_Insert_EfCommand` | 21.05 KB | **−44%** | 0.98 | 438 µs (0.94x) |
| Postgres | `Raw_Insert_AdoCommand` | 12.10 KB | **−68%** | 0 | 441 µs (0.95x) |
| SqlServer | `Tracked_Persist` (production) | **40.03 KB** | — | 1.95 | 1,436 µs |
| SqlServer | `Raw_Insert_EfCommand` | 20.87 KB | **−48%** | 0 | 1,351 µs (0.94x) |
| SqlServer | `Raw_Insert_AdoCommand` | 10.89 KB | **−73%** | 0 | 1,327 µs (0.92x) |

Reads:

- **The tracked insert costs 37–40 KB per call on every provider** — the change tracker entry, the property
  snapshot for a 24-column row and the `ModificationCommandBatch`. It is by far the single heaviest write of
  the four, and #17's "biggest single allocation slice" hypothesis is confirmed with room to spare.
- **Keeping EF but dropping the tracker (`ExecuteSqlRaw` of a parameterized INSERT) already returns 44–55%**
  of it. Going all the way to a raw ADO command returns **68–83%**.
- **Time is flat** (0.92–0.95x, inside the noise band). Nothing here buys throughput: the durable path is
  round-trip-bound, exactly as P-H/P-I found. What it buys is allocation and Gen0 pressure — the tracked path
  is the only variant that pushes a measurable Gen0 rate.
- **The per-provider SQL the issue worried about does not have to be hand-written.** The benchmark builds the
  statement once at startup from the EF model — `IEntityType.GetProperties()` for the column list,
  `ISqlGenerationHelper.DelimitIdentifier` for quoting and schema, the property's value converter for the
  store type. One code path produced a correct INSERT for all three providers, and the round-trip comparison
  passed column by column on all three. Only the 24-value list is hand-written, and a model column missing
  from it throws at startup.

### P-L.2 — #18: the proc/CTE through a `DbContext`

`ProcRawAdoBenchmark`. The op is the `Queued` status write at `AuditLevel.None` — literally the `SetQueued`
call `WorkerQueue` makes after enqueuing, so the baseline column is also #16's number.

| Provider | Variant | Allocated | vs production | Mean (indicative) |
|----------|---------|----------:|--------------:|------------------:|
| Postgres | `Production_SetQueued` (CTE via pooled ctx) | **12.20 KB** | — | 494 µs |
| Postgres | `Ef_ExecuteSqlRaw` (same CTE, cached SQL string) | 10.81 KB | −11% | 509 µs |
| Postgres | `Raw_AdoCommand` (`NpgsqlCommand`, same pool) | 5.49 KB | **−55%** | 490 µs |
| SqlServer | `Production_SetQueued` (proc via pooled ctx) | **8.67 KB** | — | 1,477 µs |
| SqlServer | `Ef_ExecuteSqlRaw` (same EXEC, cached SQL string) | 8.26 KB | −5% | 1,338 µs |
| SqlServer | `Raw_AdoCommand` (`SqlCommand`, same pool) | 5.42 KB | **−37%** | 1,377 µs |
| SQLite\* | `Production_SetQueued` (base: transaction + `ExecuteUpdate`) | **21.26 KB** | — | 660 µs |
| SQLite\* | `Ef_ExecuteSqlRaw` (plain UPDATE, no transaction) | 9.03 KB | −58% | 583 µs (0.88x) |
| SQLite\* | `Raw_AdoCommand` (`SqliteCommand`, same pool) | 1.85 KB | **−91%** | 570 µs (0.86x) |

\* SQLite has no proc/CTE, so it is **not** in #18's scope. Its rows compare the base relational `SetStatus`
(explicit transaction + `ExecuteUpdate`) against the single UPDATE that path reduces to when no audit row is
written — context for **#19**, and an upper bound on it, since the two raw rows also drop `ExecuteUpdate`'s
own translation, not just the `BEGIN`/`COMMIT`.

Reads:

- **The saving is real but the mechanism is not the predicted one.** The issue expected ~6.4 KB per call
  falling to ~1–2 KB. Measured: the production call is **8.67–12.20 KB** (higher than 6.4 KB — that estimate
  came from the SQLite pooling micro, and the network drivers cost more), and the raw ADO floor is
  **~5.4 KB**, not 1–2 KB. The 1–2 KB figure *is* right for a driver with no network stack: SQLite's raw
  command lands at **1.85 KB**. On Postgres and SQL Server the ~5.4 KB is Npgsql's/SqlClient's own
  per-command cost (connection rent, command + parameter objects, read/write buffers) and no rewrite removes it.
- **Net per call: −6.71 KB (Postgres), −3.25 KB (SQL Server).** Times unchanged (0.93–1.03x).
- **Postgres benefits about twice as much as SQL Server**, because its statement is a ~700-character CTE
  while SQL Server's is a 90-character `EXEC`.
- **There is a free half of this issue.** The gap between `Production_SetQueued` and the identical statement
  issued from a *cached* SQL string is **1.39 KB per call on Postgres** and 0.41 KB on SQL Server —
  `PostgresTaskStorage.SetStatus` re-interpolates its whole CTE (`$"""…{_schema}…"""`, ~700 chars ≈ 1.4 KB of
  UTF-16) on **every call**, and the same pattern is in `UpdateCurrentRun`, `CompleteRecurringRun` and the
  SQL Server `EXEC` builders. The schema is fixed at construction; caching the string per instance is a
  one-line change with no transactional risk at all.

### P-L.3 — the accounting closes

Fresh `L8` baseline on this tree (`23cbc0c`, net10, Postgres/Testcontainers, audit none, tiny payload,
parallelism 16, 4 producers, 5k tasks, warmup 2 / measured 5):

```
Throughput  : 2,708 tasks/s  (stdev 63, CV 2.3%)
Latency (µs): p50=2,193  p90=2,515  p99=3,170  p999=9,765  max=16,990
Allocated   : 80,078.5 bytes/task  (78.20 KB)
```

(Higher than P-I's 74,067 B/task because that predates the 4.0 branch; P-K measured +6.9% on the durable
write path from the three new columns and the occurrence navigation collection. 74,067 × 1.069 = 79,178 —
consistent.)

Summing the micros for the four writes a task performs (`Persist` → `SetQueued` → `SetInProgress` →
`SetCompleted`):

| | Postgres |
|---|---:|
| `Tracked_Persist` | 38,318 B |
| 3 × `Production_SetQueued` | 37,479 B |
| **Sum of the four storage writes** | **75,797 B** |
| L8 measured, end to end | 80,078 B |
| **Storage share of the per-task allocation** | **94.7%** |

The 4,281 B remainder matches the engine layer measured independently (A4W: 3,207 B/task on P-I, 3,498 B on
the 4.0 branch in P-K) plus dispatch. **There is nothing else to optimize on the durable path** — the four
writes *are* the per-task allocation, and #17 + #18 are aimed at exactly them.

### P-L.4 — what each issue would buy

Per task, against the 78.20 KB/task Postgres baseline above. SQL Server has no fresh `L8` here; its
percentages are against the 66.04 KB its own four writes sum to (RESULTS.md P-H measured ~68 KB/task
end to end on the pre-4.0 tree).

| Change | SQLite | Postgres | SQL Server | Round-trips |
|--------|-------:|---------:|-----------:|------------:|
| **#17** raw INSERT via EF `ExecuteSqlRaw` (option 1) | −21.10 KB | −16.37 KB (−20.9%) | −19.16 KB (−29%) | 0 |
| **#17** raw INSERT via raw ADO (option 2) | −32.22 KB | −25.32 KB (−32.4%) | −29.14 KB (−44%) | 0 |
| **#18** raw ADO for the 3 status writes | n/a | −20.13 KB (−25.7%) | −9.75 KB (−15%) | 0 |
| **#18** free half: cache the interpolated SQL | n/a | −4.17 KB (−5.3%) | −1.23 KB (−2%) | 0 |
| **#16** fold `SetQueued` into `Persist` | −21.26 KB | −12.20 KB (−15.6%) | −8.67 KB (−13%) | **−1 of 4** |
| **#17 (ADO) + #18 together** | — | **−45.45 KB (−58.1%)** | **−38.89 KB (−59%)** | 0 |

### #16's throughput estimate (an estimate, with its assumptions)

#16 is the only one of the three that removes a round-trip, and P-H/P-I established that the durable path is
round-trip-bound. Modelling throughput as inversely proportional to the serialized DB time per task, using the
micro means:

| Provider | 4 writes | 3 writes (folded) | Ceiling |
|----------|---------:|------------------:|--------:|
| Postgres | 466 + 3×494 = 1,948 µs | 466 + 2×494 = 1,454 µs | **+34%** |
| SQL Server | 1,436 + 3×1,477 = 5,867 µs | 4,390 µs | **+34%** |
| SQLite | 684 + 3×660 = 2,664 µs | 2,004 µs | **+33%** |

All three land on the naive `4/3` model, i.e. **up to ~+33% throughput** — on Postgres that would be
2,708 → ~3,630 tasks/s. Assumptions, all of which push the real number *down*: the writes are serialized in
the model but overlap across 16 workers; commit/fsync grouping means the marginal cost of the removed
statement is below its serialized latency; and p50 latency (2,193 µs at parallelism 16) is queueing-dominated,
not write-dominated. Treat +33% as a **ceiling**, not a forecast. What is *not* an estimate is the allocation:
folding removes one whole `SetQueued`, −12.20 KB/task on Postgres.

---

### Verdicts

### #17 — replace the tracked `Persist` insert. **Fix, and fix it first.**

1. **Real, and larger than claimed.** 37–40 KB per call on every provider, ~48% of the whole durable per-task
   allocation on Postgres. Reproducible, deterministic, three providers.
2. **Buys** −16 to −21 KB/task keeping EF (option 1), −25 to −32 KB/task on raw ADO (option 2). Zero
   round-trips, and time is flat: this is a GC-pressure and tail-latency change, not a throughput one.
3. **Reward vs the stated medium-high risk: worth it, and the risk is lower than the issue assumed.** The
   feared cost was hand-maintained per-provider SQL; the benchmark shows the statement can be generated once
   from the EF model, which is provider-agnostic by construction and cannot drift from the schema (a mapped
   column absent from the value list throws at startup). Recommend **option 1** (parameterized INSERT via
   `ExecuteSqlRawAsync` in the EF Core base): it is one shared code path, keeps EF's parameter and type
   handling, and returns ~65% of the total available win. Revisit option 2 per provider only alongside #18.

### #18 — run the procs/CTEs on a raw ADO command. **Fix, Postgres first. Take the free half immediately.**

1. **Real; magnitude confirmed at the task level, mechanism partly refuted.** The per-call prediction
   (6.4 KB → 1–2 KB) is wrong in both terms: production is 8.67–12.20 KB and the raw ADO floor is ~5.4 KB on
   the networked providers (SQLite's is 1.85 KB, which is where the 1–2 KB figure holds). The delta,
   3.25–6.71 KB per call × 3 calls, gives **−20.13 KB/task on Postgres and −9.75 KB/task on SQL Server** —
   bracketing the issue's predicted 12–15 KB/task.
2. **Buys** −25.7% of the Postgres per-task allocation, −15% on SQL Server. No round-trips, no time change.
3. **Reward vs the stated medium risk: yes on Postgres, marginal on SQL Server.** Recommend splitting it:
   - **now, at no risk** — cache the interpolated SQL string per instance in `PostgresTaskStorage` and
     `SqlServerTaskStorage` (the schema is fixed at construction): −4.17 KB/task on Postgres, −1.23 KB on
     SQL Server, for a change that cannot alter a transaction boundary;
   - **then** the raw `NpgsqlCommand` path on Postgres for the remaining −16 KB/task;
   - SQL Server's remaining −8.5 KB/task is the weakest of the four candidates — do it only if the raw-ADO
     plumbing is already there for #17 option 2.

### #16 — fold `SetQueued` into `Persist`. **Real, and the only throughput lever. Do it after #17/#18.**

1. **Real.** One full round-trip and 12.20 KB (Postgres) / 8.67 KB (SQL Server) / 21.26 KB (SQLite) per task,
   measured as the exact call `WorkerQueue` makes.
2. **Buys** −15.6% allocation on Postgres and −1 of 4 round-trips; modelled throughput ceiling **+33%**
   (2,708 → ~3,630 tasks/s), which is an upper bound, not a forecast. It is the only one of the three that can
   move tasks/sec at all — #17 and #18 leave the round-trip count untouched and their times are flat.
3. **Reward vs the stated medium risk: yes, but sequence it last.** Its risk is categorically different from
   the other two: #17 and #18 change *how* a statement is issued and are pinned by a row-equality check,
   while #16 changes *dispatch and recovery ordering* — the `WaitingQueue` semantics, the full-queue drop
   paths and the `TaskDeliveryRegistry` double-execution defence. #17 + #18 deliver −58% allocation with no
   ordering semantics touched; land those first, then take #16 for the throughput, behind the full
   cross-provider recovery suite.

---

### P-L.5 — #19 BEFORE baseline (SQLite, this tree, unmodified)

Captured on the pristine `23cbc0c` worktree because #19 (skip the explicit transaction in the base relational
`SetStatus` when no audit row is written) is being implemented elsewhere, so this is the last place the BEFORE
state exists. **No comment was posted on #19** — the A/B is only complete once the fix lands and the identical
commands are re-run.

Exact commands from the issue, run with nothing else on the machine:

```bash
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- L8 --storage sqlite --audit none --count 3k --parallelism 1 --producers 1 --warmup 2 --measured 5
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- L8 --storage sqlite --audit full --count 3k --parallelism 1 --producers 1 --warmup 2 --measured 5
```

| | audit none | audit full |
|---|---:|---:|
| Throughput | 26 tasks/s (stdev 6, **CV 24.4%**) | **7 tasks/s** (stdev 0, CV 1.5%) |
| p50 / p90 / p99 | 154.3 / 284.4 / 453.8 ms | 156.2 / 158.1 / 166.1 ms |
| **Allocated** | **112,628.4 B/task** | **156,910.9 B/task** |
| Raw JSON | `benchmarks/results/L8-20260831-161948.json` | `benchmarks/results/L8-20260831-185351.json` |

Notes for whoever runs the AFTER half:

- **Auditing is what costs on SQLite, not the transaction.** `full` runs at 7 tasks/s against `none`'s 26 —
  a 3.7x throughput gap and +44 KB/task — because every status transition adds a `StatusAudit` insert. The
  `full` arm is #19's control (the fix only touches the `createAudit == false` branch) and it should not move.
- **The two arms have opposite noise characters.** `full` is rock steady (CV 1.5%, p999/p50 = 1.4x) and can
  resolve a small effect. `none` is not: the harness flagged `CV > 5%: not steady-state`, per-iteration
  throughput 27 / 24 / 33 / 31 / 15 tasks/s, CV 24.4%. That is the arm #19 actually changes, so **allocation
  (112,628 B/task, deterministic) is the metric this A/B can decide on**; the `none` throughput column cannot
  resolve anything smaller than a large effect at this iteration count.
- If the AFTER run is meant to show the round-trip saving in tasks/sec, give the `none` arm more warmup or
  more measured iterations than the issue's command specifies — or use the per-write cell
  (`A4S --storage sqlite --parallelism 1`) that RESULTS.md P-J/P-K used instead, precisely because L8 on
  SQLite is a single-writer convoy.

For scale, the storage micros above put the SQLite per-task write cost at `Tracked_Persist` 38.63 KB +
3 × 21.26 KB = 102.4 KB, which with the engine layer accounts for ~96% of the 112.6 KB/task measured here.
The micro also brackets #19's own target: the base `SetStatus` (transaction + `ExecuteUpdate`) costs
21.26 KB / 660 µs, and the plain UPDATE it reduces to costs 9.03 KB / 583 µs — an **upper bound** on #19,
since that comparison also drops `ExecuteUpdate`'s translation, not only the `BEGIN`/`COMMIT`.

---

### Priority

1. **#18's free half** (cache the SQL string) — one line per method, no risk, −4.2 KB/task on Postgres.
2. **#17 option 1** (model-generated parameterized INSERT in the EF base) — −16 to −21 KB/task, all providers,
   one shared code path.
3. **#18 proper on Postgres** (raw `NpgsqlCommand`) — −16 KB/task more.
4. **#16** — the throughput lever, behind the recovery suite.
5. **#17 option 2 / #18 on SQL Server** — the remaining ~8–9 KB/task each; only worth it once the raw-ADO
   plumbing exists.

## P-M — Issue #19 landed: the no-audit transaction elision, A/B

Fix in `edf9ad0`; A/B on the issue's reproduce commands (`L8 --storage sqlite --count 3k --parallelism 1`,
both sides back-to-back on a quiet machine, BEFORE on a clean `23cbc0c` worktree):

| Arm | Side | Throughput | p50 | Allocated |
|---|---|---:|---:|---:|
| audit none | before | 16 tasks/s (CV 28.6%) | 155.5 ms | 113,438 B/task |
| audit none | after | 433 tasks/s (CV 2.9%) | 3.87 ms | 108,488 B/task |
| audit full | before | 7 tasks/s (CV 1.5%) | 156.2 ms | 156,910 B/task |
| audit full | after | 7 tasks/s | 156.2 ms | 157,299 B/task |

The explicit transaction held SQLite's write lock across three round-trips and pushed every concurrent
writer into busy-handler backoff (the 155 ms p50 is the backoff, not I/O). A single autocommitted UPDATE
shrinks the lock window to one statement: 27x throughput, p50 down 40x, and the run turns steady. The
audit-full arm keeps its transaction and is byte-flat and time-flat, as intended.

## P-N — The 4.0 storage batch, clean A/B (release measurement)

After a run polluted by an unrelated CPU-hogging container first read as "throughput flat", the
definitive pair ran back-to-back on a quiet machine: BEFORE on a pristine `23cbc0c` worktree, AFTER on
the 4.0 tree (`084a7db`: SQL caching + #17 model-derived INSERT + #16 born-Queued fold). L8 Postgres,
audit none, tiny payload, p16, 4 producers, 5k tasks, warmup 2 / measured 5.

| Side | Throughput | p50 | p90 | p99 | p999 | Allocated |
|---|---:|---:|---:|---:|---:|---:|
| BEFORE (CV 0.8%) | 2,792 tasks/s | 2,140 µs | 2,419 µs | 3,006 µs | 5,587 µs | 80,077 B/task |
| AFTER (CV 2.5%) | 4,694 tasks/s | 1,660 µs | 2,499 µs | 5,726 µs | 10,297 µs | 48,179 B/task |

**+68% throughput, −22% p50, −40% allocations.** The naive 4/3 round-trip model predicted a +33%
ceiling; the fold beat it because the removed SetQueued was not just a round-trip but a contended
write between the producer and the consumers. The tail is higher in absolute terms (p99 3.0 → 5.7 ms)
with 68% more tasks in flight at the same pool and parallelism — the pre-release soak run
characterizes it at matched load. Allocations are deterministic across every run of the day
(48,079-48,179 B/task on three AFTER runs under wildly different CPU conditions).
