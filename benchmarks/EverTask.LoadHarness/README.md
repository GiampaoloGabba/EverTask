# EverTask.LoadHarness

Console harness for the **load / end-to-end** measurements in [`../BENCHMARK_PLAN.md`](../BENCHMARK_PLAN.md).
Kept **out of `EverTask.slnx`** — it never runs in CI. Run it locally.

## Run

```bash
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- <scenario> [--key value ...]
```

Scenarios available today (**Tier 0 — anchors**):

| Id | What | Storage? | Notes |
|----|------|----------|-------|
| `A1` | raw `Channel<T>` + worker pool ceiling | no | engine ceiling; read EverTask throughput as "% overhead above A1" |
| `A2` | bare `Task.Run(noop)` floor | no | thread-pool latency floor; EverTask can't beat it |
| `A3` | naive DB-polling dispatcher | yes | anti-polling reference: pickup latency ≈ poll-interval/2 |
| `A4S` | storage-only: 3 writes/task | yes | isolates persistence cost (Persist+SetInProgress+SetCompleted) |
| `A4W` | worker-only: engine over `NullTaskStorage` | no | isolates engine cost (no persistence) |
| `L8` | full lifecycle throughput through the engine | yes | **production primary**: Persist+InProgress+handler+Completed per `--storage` |
| `LDP` | dispatch-call latency on the caller thread | yes | perceived latency: `await Dispatch()` incl. the sync write |
| `tier0` | A1, A2, A4W (no DB) | no | quick run, no Docker |
| `anchors` | the five Tier-0 anchors (A4S/A3 honour `--storage`) | mixed | full Tier-0 |
| `tier1` | L8, LDP (honour `--storage` + `--audit`) | yes | production headline pair |

Common knobs (defaults in `Infra/RunConfig.cs`):

```
--count 1m          tasks per iteration (accepts 1_000_000 / 1m / 100k)
--parallelism 16    consumer/worker count (default = logical cores)
--producers 4       producer count for A1/A4W (multi-writer like EverTask; 1 = single-producer ref)
--capacity 2000     bounded channel capacity
--storage inmemory  inmemory | sqlite | sqlserver | postgres   (A3/A4S/L8/LDP; sqlserver/postgres need Docker)
--poll-interval 1000   A3 polling period in ms
--audit full        L8/LDP audit level: none | minimal | errorsonly | full (full = engine default, heavy)
--warmup 3          discarded iterations (JIT/PGO + thread-pool ramp-up)
--measured 7        measured iterations
--out benchmarks/results   JSON report directory
```

Examples:

```bash
# engine ceiling, full run
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A1 --count 1m --parallelism 16 --producers 8 --warmup 3 --measured 7
# durable write cost on SQLite — single writer, so parallelism 1 for a clean per-write number
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage sqlite --parallelism 1 --count 50k
# anti-polling reference at a 1s interval
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A3 --storage sqlite --count 20k --poll-interval 1000
```

### Usage notes / gotchas
- **SQLite is single-writer.** Run `A4S --storage sqlite` with `--parallelism 1` for a clean per-write
  cost; higher parallelism measures the write-lock convoy (real, but noisy).
- **`A4S --storage inmemory` degrades across iterations** — `MemoryTaskStorage` looks up by id with an
  O(n) scan under a global lock, so the number reflects the L-slowdown-mem accumulation, not a write floor.
  The real A4S signal is the relational providers.
- **Tiny smoke runs are noisy** (watch the CV warning). For citable numbers use a high `--count` and full
  `--warmup 3 --measured 7`, ideally with process affinity pinned.

## Output

Console summary + a JSON report per run under `benchmarks/results/` (gitignored). Each report carries
the full environment (GC mode, timer resolution, cores, tiering, affinity) so numbers are interpretable
later. A **CV > 5%** warning means the run isn't steady-state — raise `--warmup`/`--measured` or pin
affinity before trusting it. The **p999/p50** ratio is printed as a tail-divergence signal.

### GC mode (Server vs Workstation)
Switch per-run via environment (separate processes, never a runtime switch — see plan §2.6):

```bash
# Windows PowerShell
$env:DOTNET_gcServer=1; dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- tier0
```

The harness prints which GC mode is active in the environment header.

## Status

- ✅ **Tier 0 complete** — A1, A2, A3, A4S, A4W + common infra (padded counters, completion gate,
  HdrHistogram latency with coordinated-omission support, allocation meter, env report, JSON reporting),
  the storage matrix (`StorageMatrix`/`StorageProvisioner`) and the EverTask host (`HostFactory`).
- ✅ **Tier 1 (production headline)** — L8 (full lifecycle throughput) + LDP (dispatch latency), per
  `--storage` and `--audit`. Validated end-to-end on **In-Memory, SQLite, and Postgres (Docker)**;
  SqlServer is wired (same path as Postgres) but not yet exercised here.
- ⏳ **Next**: open-loop L-thr/L-lat, the slowdown suspects (L-audit, in-memory O(n), hot-key) and
  SCHED-VS — all reuse the existing infra.

### Indicative findings from smoke runs (7950X, .NET 9, Workstation GC — NOT citable, high CV)
- Engine alone (A4W, NullStorage): ~380k/s, ~4.5 KB/task. The engine is not the bottleneck.
- In-Memory storage (L8 inmemory): ~3k/s — `MemoryTaskStorage` does an O(n) lookup under a global lock
  per status write; collapses under concurrency + accumulation (the L-slowdown-mem signal).
- SQLite (single writer, L8 p1): ~222/s audit none, ~50/s audit full → audit ≈ 4.4×; does NOT scale with
  parallelism (write-serialized).
- Postgres (L8, audit none): p1 ~676/s → p16 ~2127/s (~3.1× scaling); latency 2.8s → 2.7ms. Parallelism
  pays on a real concurrent-writer DB.
- ~256 KB/task allocated on the relational path (3–4 EF Core DbContexts/task) — an optimization target.
