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
| `LRA` | recurring advance: next-occurrence + `UpdateCurrentRun` | yes | the schedule-advance axis, driven directly (a live grid is paced at 1 s and would measure the clock) |
| `tier0` | A1, A2, A4W (no DB) | no | quick run, no Docker |
| `anchors` | the five Tier-0 anchors (A4S/A3 honour `--storage`) | mixed | full Tier-0 |
| `tier1` | L8, LDP (honour `--storage` + `--audit`) | yes | production headline pair |
| `recurring` | LRA (honours `--storage`) | yes | schedule-advance parity gate |

Common knobs (defaults in `Infra/RunConfig.cs`):

```
--count 1m          tasks per iteration (accepts 1_000_000 / 1m / 100k)
--parallelism 16    consumer/worker count (default = logical cores)
--producers 4       producer count for A1/A4W (multi-writer like EverTask; 1 = single-producer ref)
--capacity 2000     bounded channel capacity
--storage inmemory  inmemory | sqlite | sqlserver | postgres   (A3/A4S/L8/LDP/LRA; sqlserver/postgres need Docker)
--poll-interval 1000   A3 polling period in ms
--audit full        L8/LDP audit level: none | minimal | errorsonly | full (full = engine default, heavy)
--payload none      task body size for L8/A4W: none (tiny/primitives) | 1k | 64k | <n>[k] (sizes the
                    serialized payload — the axis where Newtonsoft→STJ pays off on the durable path)
--warmup 3          discarded iterations (JIT/PGO + thread-pool ramp-up)
--measured 7        measured iterations
--out benchmarks/results   JSON report directory
--log Warning       minimum level for EverTask's OWN logging: Trace | Debug | Information | Warning |
                    Error | Critical | None. Warning is what the harness always pinned, so numbers taken
                    before this knob existed stay comparable
--sink none         what consumes the log records: none (no provider — MEL drops them, the historical
                    wiring) | render (calls the formatter and discards the string: a console/file sink
                    minus the I/O) | enumerate (reads the event id and walks the state's key/value pairs:
                    a structured sink — Serilog/Seq — minus the I/O). The fake sink allocates nothing of
                    its own; what it does allocate is what the real sink would (`Infra/NullSinkLoggerProvider.cs`)
```

Both apply to the engine scenarios (`HostFactory`) and the storage-only ones (`StorageMatrix`), and both are
echoed in the run header, the result block and the JSON report, so an A/B output says which logging it ran
with. A typo in either value aborts the run instead of silently falling back to the default.

Examples:

```bash
# engine ceiling, full run
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A1 --count 1m --parallelism 16 --producers 8 --warmup 3 --measured 7
# durable write cost on SQLite — single writer, so parallelism 1 for a clean per-write number
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage sqlite --parallelism 1 --count 50k
# anti-polling reference at a 1s interval
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A3 --storage sqlite --count 20k --poll-interval 1000
```

### A/B recipe for issue #32 (cost of EverTask's own logging)

`--log`/`--sink` exist to A/B the `[LoggerMessage]` migration. The classic `logger.LogX(...)` extensions
build a `FormattedLogValues` and box every argument into an `object[]` **before** the level is checked, so
they cost something even at a level that is off; the generated methods check `IsEnabled` first. Three cells
cover the range:

| Cell | Knobs | What it isolates |
|------|-------|------------------|
| level off | `--log Warning` | today's default: what a disabled call site still costs (`--sink none` is implied) |
| rendering sink | `--log Information --sink render` | level on, message rendered: a console/file sink |
| structured sink | `--log Debug --sink enumerate` | a Serilog/Seq sink at the noisiest level |

Two scenarios: A4W (engine logging: dispatcher, worker, executor, no DB) and A4S `--storage postgres`
(storage logging, the per-write `SetStatus` line; needs Docker).

```bash
# one cell = one command; run all six per checkout
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4W --count 500k --warmup 3 --measured 7 --log Warning
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4W --count 500k --warmup 3 --measured 7 --log Information --sink render
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4W --count 500k --warmup 3 --measured 7 --log Debug --sink enumerate
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage postgres --parallelism 16 --count 20k --warmup 3 --measured 7 --log Warning
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage postgres --parallelism 16 --count 20k --warmup 3 --measured 7 --log Information --sink render
dotnet run -c Release --project benchmarks/EverTask.LoadHarness -- A4S --storage postgres --parallelism 16 --count 20k --warmup 3 --measured 7 --log Debug --sink enumerate
```

How to run and read it:

- Alternate the checkouts (base, patched, base, patched), three runs of each cell per checkout, on an idle
  machine. A whole base block followed by a whole patched block bakes thermal and background drift into
  the delta.
- Compare the median tasks/s and the median bytes/task of each cell's three runs, and report the CV the
  harness prints: above 5% the cell isn't steady-state and its delta isn't citable.
- `BytesPerTask` is comparable only within the same `--log`/`--sink` pair. The sink's own rendering and
  state boxing are part of the number by design (they are what a real sink costs), so never compare a
  `--sink render` run against a `--sink none` one.
- Everything else must match between checkouts, GC mode included (`DOTNET_gcServer`), and the two checkouts
  must be built with the same SDK.

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
