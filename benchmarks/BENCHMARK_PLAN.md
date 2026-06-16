# EverTask — Piano di benchmark "sotto carico" (v2)

Documento di specifica. Obiettivo: capire **davvero** quanto è performante EverTask, in modo serio e
ripetibile (throughput, latenza, scaling, saturazione, costo reale della persistenza), perché le app
reali girano sulla configurazione **durabile su DB** — quindi (a) misurare con onestà come si comporta
nella config che si usa davvero, e (b) far emergere slowdown nascosti che la code review non trova.
Un'eventuale pubblicazione è secondaria: prima viene sapere la verità.

> **Target primario = profilo onesto (production, durabile su DB).** È la config che le app reali usano.
> Il profilo *engine* (In-Memory, motore puro) resta come **diagnostica**: serve a sapere quanto overhead
> aggiunge il DB rispetto al soffitto del motore — non è il numero che conta per l'uso reale.

> **v2** = v1 riscritto dopo una review avversariale a 4 lenti indipendenti (completezza, validità
> metodologica, intent-check, + Codex come voce di modello diverso). Le quattro voci hanno converso
> sugli stessi difetti centrali — segnale forte che sono reali. Diff principale vs v1:
> - **Doppio headline etichettato**: *engine ceiling* (in vitro) **e** *production* (durabile su DB).
> - **Load generator open-loop** per la latenza (v1 era closed-loop → coordinated omission).
> - **Latenza a 3 timestamp** (separare reattività channel dalla scrittura di stato).
> - **Lifecycle storage completo** (3 scritture/task, non solo il `Persist` del dispatch).
> - **Baseline-ancora** (raw `Channel<T>`, `Task.Run`, polling-loop) per dare un metro ai numeri.
> - **Nuovi scenari**: recovery/backlog, failure/retry storm, payload sweep, sospetti slowdown.
> - **Hardening**: padded counters, `GetTotalAllocatedBytes`, warm-until-stable, affinità CCD, GC come
>   processi separati, anti-bias nell'ordine di esecuzione.
>
> Scope: load test su **In-Memory, SQLite, SqlServer (Docker), Postgres (Docker)**. **Nessun** confronto
> diretto vs Hangfire/Quartz — la claim relativa si sostiene con le baseline-ancora interne (§4).

---

## 0. Principio guida: due categorie, e due claim distinte

**Due categorie di misura** (invariato da v1, è corretto):

| Categoria | Cosa misura | Strumento |
|---|---|---|
| **Micro-benchmark [B]** | costo *per operazione* (dispatch, serializzazione, enqueue, GCRA, callback) — ns, byte/op | **BenchmarkDotNet** |
| **Load / end-to-end [L]** | *throughput* a completamento, latenza, scaling, saturazione, costo persistenza, percentili | **harness console custom** |

**Due numeri che il piano NON deve confondere** (questo è il cuore della correzione v2):

- **Numero "engine" (diagnostico)** → *in vitro*: In-Memory, raw `Channel<T>` come tetto. Dice quanto è
  veloce il motore in assenza di DB. Utile per capire dove finisce il motore e dove inizia l'overhead di
  persistenza — ma non è la config che le app reali usano.
- **Numero "production" (quello che conta per le tue app)** → governato dal fatto che `Dispatch()`
  **awaita `Persist` (storage write sincrona) prima di ritornare**, e che il ciclo di vita di un task fa
  **3 scritture sincrone** (`Persist` + `SetInProgress` + `SetCompleted`, 4+ con audit `Full`). In un
  controller ASP.NET il tempo di risposta HTTP include un round-trip al DB. È **questo** il numero che
  determina come si comportano le tue app.

Ogni numero deve dichiarare a quale dei due appartiene. L'headline è **doppio ed etichettato**: *engine*
(diagnostico) accanto a *production* (durabile su DB, lifecycle completo, audit on) — con **production
come primario**.

> Perché non usare In-Memory come numero di riferimento: è *senza durabilità*, quindi misura un sistema
> diverso da quello che le tue app eseguono. Va benissimo come diagnostica del motore, non come il numero
> che descrive il comportamento reale.

---

## 1. Struttura della suite

```
benchmarks/
├── EverTask.Benchmarks/            # ESISTENTE — micro-benchmark BDN (restano) + nuovi [B] (§5)
├── EverTask.LoadHarness/           # NUOVO — harness load/throughput (console)
│   ├── Program.cs                  # CLI: scenario, --storage, --parallelism, --capacity, --fullmode,
│   │                               #      --count, --rate (open-loop λ), --payload, --audit, --error-rate,
│   │                               #      --gc, --affinity, --duration (sustained), --pool
│   ├── Infra/
│   │   ├── PaddedCounters.cs        # contatori per-worker cache-line-padded (NO single Interlocked)
│   │   ├── CompletionGate.cs        # somma i padded counters; TCS armato a N
│   │   ├── OpenLoopGenerator.cs     # generatore arrivi a rate fisso λ con planned-arrival timestamp
│   │   ├── ClosedLoopDriver.cs      # driver closed-loop CONCORRENTE (P thread), non sequenziale
│   │   ├── LatencyRecorder.cs       # HdrHistogram + recordWithExpectedInterval (coordinated-omission)
│   │   ├── ThreeStageTimers.cs      # enqueued → dequeued → handler-invoked (hook nel worker)
│   │   ├── AllocationMeter.cs       # GC.GetTotalAllocatedBytes(precise) delta per run (solo questo)
│   │   ├── HostFactory.cs           # host EverTask per ogni combinazione storage/config
│   │   ├── StorageMatrix.cs         # In-Memory / SQLite(WAL) / SqlServer / Postgres (Testcontainers)
│   │   └── EnvReport.cs             # dump ambiente: GC, JIT/PGO, timer res, affinity, pool, DB config
│   ├── Baselines/                   # §4 — ancore: RawChannel, BareTaskRun, NaivePollingDispatcher
│   ├── Tasks/                        # task + handler: noop, cpu, io, throwing, disposable, payload-sized
│   └── Scenarios/                    # uno per scenario (§5)
└── BENCHMARK_PLAN.md / RESULTS.md / LOAD_RESULTS.md
```

- `EverTask.LoadHarness` resta **fuori da `EverTask.slnx`** → mai in CI. Solo esecuzione locale manuale.
- SqlServer/Postgres via **Testcontainers** (Docker disponibile su questa macchina).
- Target `net9.0`, allineato a `Directory.Build.props` dei benchmark.

---

## 2. Infrastruttura comune (la parte che rende i numeri validi)

### 2.1 Completion gate SENZA contention artificiale
Il gate non deve essere uno strumento che misura sé stesso. Su un 7950X **dual-CCD**, un singolo
`Interlocked.Decrement` martellato da 32 worker misura il ping-pong della cache line cross-CCD, non il
motore → falserebbe sia il throughput sia la curva di scaling (knee a 16 = artefatto NUMA).

```csharp
// ogni worker incrementa il PROPRIO contatore padded (no cache-line sharing)
sealed class PaddedCounters { readonly PaddedLong[] _perWorker; /* indicizzato per worker-id */ }
// il gate somma i contatori solo raramente / a fine run; TCS armato quando la somma raggiunge N
```
Inoltre: **micro [B] che misura il floor del gate stesso** (solo 32 thread che incrementano, senza
EverTask) → si dichiara/sottrae l'overhead dello strumento.

### 2.2 Load generation: open-loop (default per la latenza) + closed-loop concorrente
v1 era **closed-loop sequenziale** (`for { await Dispatch }`): con `FullMode=Wait` il dispatch blocca a
coda piena → il produttore non immette finché un worker non è libero → la latenza misurata è **per
costruzione ~zero** (coordinated omission). Inaccettabile per la metrica-vetrina.

- **OpenLoopGenerator** (per la latenza, L-lat): immette a rate fisso `λ` da una schedule pianificata
  `plannedArrival[i] = t0 + i/λ`, su thread separati dai worker, **senza** che il completamento di un
  task condizioni l'immissione del successivo. λ è variabile indipendente: si traccia la **curva
  latenza-vs-λ fino al knee** (saturazione). Un "p99 = X ms" senza il λ associato non è difendibile.
- **ClosedLoopDriver** (per il throughput max, L-thr): P thread che dispatchano in parallelo e
  ri-immettono appena completano → misura il *soffitto* di throughput, non la latenza.
- A coda piena con `Wait`, registrare anche la **dispatch stall latency** (quanto `WriteAsync` ha
  bloccato) come metrica esplicita, così la coda nascosta diventa visibile invece di sparire.

### 2.3 Latenza a 3 timestamp (separa reattività channel dalla scrittura di stato)
Tra dequeue e handler il worker fa `SetInProgress` (**storage write**; su DB è un round-trip awaited).
Misurare un solo intervallo dispatch→handler mescola reattività del channel e costo di persistenza.

Registrare **tre** istanti per ogni task (`Stopwatch.GetTimestamp()`):
1. `enqueued` (ritorno di `WriteAsync` nel dispatch) → 2. `dequeued` (worker estrae dal channel)
→ 3. `handlerInvoked` (dopo `SetInProgress` + `OnStarted`).
- **Reattività channel pura** = `dequeued − enqueued` → qui si difende la tesi anti-polling.
- **Latenza fino all'handler** = `handlerInvoked − enqueued` → etichettata "include una storage write",
  riportata **per-storage**.
- Più la **dispatch-call latency** = `enqueued − plannedArrival` (open-loop), che è ciò che il request
  thread subisce.

Registrare `Stopwatch.Frequency` nell'output. (Su singola macchina QPC è monotono e coerente tra
CCD → confronto cross-thread valido, nessuna correzione di skew necessaria.)

### 2.4 HdrHistogram con correzione coordinated-omission
Package `HdrHistogram` (NuGet, da aggiungere a `Directory.Packages.props`). Memoria fissa, registrazione
concorrente dai worker, percentili accurati senza conservare ogni campione. **Usare
`RecordValueWithExpectedInterval(value, expectedInterval = 1/λ)`** nel caso open-loop: re-inserisce i
campioni "mancanti" quando un arrivo è ritardato oltre l'intervallo atteso → corregge l'omissione.
HdrHistogram da solo *non* risolve l'omissione: la risolve **alimentarlo con la planned-arrival** (§2.2).

### 2.5 Allocazioni: solo `GetTotalAllocatedBytes`
Il lavoro alloca su thread diversi da quello dell'harness (serializzazione sul produttore, esecuzione +
`SetInProgress` sui worker). `GC.GetAllocatedBytesForCurrentThread` vedrebbe **solo** il dispatch →
byte/task sottostimato di una frazione enorme. Per il totale per-run usare **esclusivamente**
`GC.GetTotalAllocatedBytes(precise: true)`, delta inizio/fine, diviso N. `GetAllocatedBytesForCurrentThread`
solo nei micro-BDN single-thread.

> **Timing E allocazioni, ma a due granularità.** Il load harness misura *entrambi*: timing end-to-end
> **e** allocazioni **aggregate per-run** (byte/task). Non dà però il costo *per-operazione in isolamento*
> (es. i byte di un singolo `Schedule()`): quello viene dal track **[B] BenchmarkDotNet con
> `[MemoryDiagnoser]`**. Le due misure sono complementari — il load dice "quanto alloca il sistema per
> task", il micro dice "quanto alloca quel metodo". Per le classi su hot-path (scheduler, gate, wrapper)
> servono entrambe.

### 2.6 Warmup, JIT/PGO, GC, ambiente
- **Warm-until-stable**, non un conteggio fisso: scartare iterazioni finché il CV inter-iterazione di
  throughput **e** p99 non scende sotto ~5%. (Il "1-2 warmup" di v1 non garantisce steady-state con
  Tiered JIT + Dynamic PGO di .NET 9.)
- Dichiarare `DOTNET_TieredCompilation`, `TieredPGO`, `ReadyToRun`, min threadpool. Misurare anche con
  `TieredCompilation=0` per numeri stabili/conservativi, oltre che ON (produzione reale).
- **GC come processi separati**, non switch a runtime: una run Server e una Workstation. Parametri
  espliciti: `gcServer`, `gcConcurrent`, `GCHeapCount` (provare 32=logici e 16=fisici), `GCRetainVM=1`
  per gli steady-state. Riportare Gen0/1/2 **+ LOH + % tempo-in-GC**.
- **Affinità CCD** sul 7950X (2× CCD da 8 core, L3 separata): curve di scaling con (a) affinità a un
  solo CCD per P≤8 (scaling pulito intra-CCD) e (b) full-machine per P=16/32, etichettando il salto
  cross-CCD. Riportare power plan. Punti curva: 1/2/4/8/16/24/32.
- **DB**: schema pre-migrato, connection pool **pre-aperto** (warmup che apre ≥`parallelism` connessioni
  e tocca tutte le query path: Persist/SetInProgress/SetCompleted/RetrievePending), `Min/Max Pool Size`
  dichiarati, SQLite in **WAL** con path su disco reale dichiarato, durability/fsync del container
  dichiarati. `EnvReport` dumpa tutto questo per ogni run.

---

## 3. Metriche raccolte (per ogni run)

- **Throughput**: task/sec a completamento di tutti gli N (closed-loop concorrente).
- **Latenza** (tre segmenti di §2.3): dispatch-call, reattività-channel, fino-all'handler — p50/p90/p99/p999/max.
- **Rapporto p999/p50** come metrica **di prima classe**: una p999 che esplode con p50 piatta =
  GC pause / lock convoy / thread-pool starvation nascosti. Flag se supera una soglia.
- **Round-trip storage per task** (esplicito): 1 (dispatch) + esecuzione + audit → conteggio reale.
- **Allocazioni**: byte/task (`GetTotalAllocatedBytes`), Gen0/1/2, LOH, % tempo-in-GC.
- **Outcome counts** (sempre): completed / failed / retried / dropped / rejected / deferred — un numero
  di throughput senza i drop non è interpretabile.
- **Threading**: completed work items, lock contention (micro [B] `[ThreadingDiagnoser]`).
- **Storage delta**: throughput/latenza vs baseline In-Memory **e** vs raw-Channel (§4).
- **Output grezzo**: JSON per run + deviazione/CI + dump completo della config (§2.6) in `LOAD_RESULTS.md`.

---

## 4. Baseline-ancora (danno il METRO ai numeri assoluti)

"Performante" è relativo: rispetto a cosa? Senza ancora, "1M task/sec" non dice se è un buon numero o se
stai lasciando performance sul tavolo. Queste baseline sono **tetti teorici interni** che ti dicono
quanto sei lontano dal massimo teorico — è così che capisci *davvero* se il motore è efficiente.

- **A1 — raw `Channel<T>` + worker pool** (stesso handler no-op, nessun EverTask): il **soffitto teorico**
  del motore. Ti dice: *"EverTask aggiunge X% di overhead sopra un Channel grezzo"* → se X è piccolo, il
  motore è efficiente; se è grande, c'è grasso da tagliare.
- **A2 — `Task.Run(noop)` nudo**: il **pavimento di latenza** del thread-pool — quanto sei vicino al
  minimo fisico del runtime.
- **A3 — polling-loop ingenuo** (un dispatcher che fa `SELECT pending` ogni 1s su DB e accoda): quantifica
  quanto vale l'architettura push-via-Channel rispetto al polling. È un riferimento tecnico per te, non
  uno slogan — serve a capire se il design paga davvero.
- **A4 — storage-only / worker-only**: isolano i due lati (solo le 3 scritture su DB senza motore; solo
  il motore senza persistenza) per attribuire correttamente i colli di bottiglia.

---

## 5. Catalogo benchmark

Legenda: **[L]** load harness, **[B]** BenchmarkDotNet. Headline marcati ★.

### TIER 0 — Ancore/baseline (§4)
**A1 raw Channel · A2 bare Task.Run · A3 naive polling · A4 storage-only/worker-only.** [L]/[B].
Eseguiti **per primi**: senza il metro, i numeri EverTask non sono interpretabili.

### TIER 1 — Headline doppio (production primario + engine diagnostico)

- **★★ L-dispatch-prod (PRODUCTION — il numero primario)** [L] — **il numero che le tue app vivono**:
  latenza di `await Dispatch()` sul thread chiamante, **per-storage (SQLite/Postgres/SqlServer)**, sotto
  carico concorrente di request. p50/p99/p999. Qui In-Memory µs diventa DB ms — ed è la realtà delle app.
- **★★ L8-headline (PRODUCTION)** [L] — throughput end-to-end **lifecycle completo** (3 scritture) su
  storage durabile + audit `Full` (la config reale). È il "quanto regge davvero" che ti interessa.
- **★ L-thr (engine, diagnostico)** [L] — throughput motore puro: handler no-op, In-Memory, audit None,
  closed-loop concorrente. `--count` 100k/1M × `--parallelism` 1..32. Output: task/sec **e % overhead
  vs A1 raw Channel**. Dice quanto è efficiente il motore e quanto costa il DB rispetto a lui.
- **★ L-lat (engine, open-loop)** [L] — latenza dispatch→start a rate λ, In-Memory. Tre segmenti (§2.3).
  Curva latenza-vs-λ fino al knee. `dequeued−enqueued` vs A2/A3 = quanto vale il push-via-Channel.
- **B1** [B] `[MemoryDiagnoser]` — costo per-dispatch scomposto: serializzazione Newtonsoft / persist /
  enqueue / dispatch completo. ns/op + byte/op + Gen0. **Con payload sweep** (vedi L-payload).

### TIER 2 — Stress + caccia agli slowdown nascosti (l'obiettivo b)

- **L3 — backpressure / coda piena** [L] — `--fullmode` Wait/DropWrite/DropOldest × `--capacity`.
  Dispatch stall latency (Wait), task persi (Drop*), throughput steady-state. **Outcome counts obbligatori.**
- **L4 — saturazione (corretto)** [L] — produttore **open-loop che NON await-a il backpressure**
  (`_ = Dispatch(...)`), altrimenti con `Wait` l'accumulo è impossibile by-design (tautologia). L'accumulo
  va cercato nel **`ConcurrentPriorityQueue` dello scheduler e nel parking-lot del rate-limiter** (non
  bounded), NON nel channel (bounded). Memoria nel tempo + crescita code interne.
- **L5 — contention** [L]+[B] `[ThreadingDiagnoser]` — dispatch concorrente da P thread; **include
  hot-key taskKey**: cardinalità chiave 1 (tutte collidono → serializzano su un `SemaphoreSlim` con una
  storage read nella sezione critica) vs N-distinte. Throughput, p99, `GetByTaskKey`/sec.
- **L-slowdown-mem** [L] — **`MemoryTaskStorage.Get` è O(n) sotto lock globale**, eseguito a ogni
  re-schedule recurring → degrada man mano che la tabella cresce. Mantieni la tabella a 10k/100k righe,
  misura il throughput di re-schedule **in funzione della dimensione tabella**. (Sospetto slowdown #1.)
- **L-overhead-isolation** [B] — isola il costo per-task pagato **sempre**: `IsBlacklisted` (fino a 4×/task)
  + `_inFlightTasks.TryAdd/Remove` + `TaskDeliveryRegistry`, con blacklist vuota vs grande. Attribuisce
  l'overhead che L-thr ingloba.
- **L-cancel-storm** [L] — dispatch N con handler lento, cancella una frazione in volo: cancel latency
  p50/p99 (CTS dispose + scheduler heap removal + storage `SetCancelledByUser`) + throughput dei sopravvissuti.

- **★ SCHED-VS — scheduler standard vs sharded, head-to-head (DECISION-DRIVING: si può buttare lo standard?)**
  Drawback reali dello sharded (dal codice `ShardedScheduler.cs`): `shardCount` default = `max(4, ProcessorCount)`
  = **32 sul 7950X** → 32 background loop + 32 `SemaphoreSlim` + 32 priority queue + ~300B/shard, *anche per
  pochi task*. Distribuzione hash-based su `PersistenceId`. Il vantaggio esiste solo quando il singolo
  `ConcurrentPriorityQueue` (lock unico) dello standard diventa il collo. Servono **due misure complementari**:
  - **B-sched** [B] `[MemoryDiagnoser]` — *isolato*: throughput + ns/op + **byte/op** di `Schedule` /
    `TryUnschedule` / dequeue, Periodic vs Sharded(shardCount 4/8/16/32), a **1 / 100 / 10k / 100k**
    registrazioni vive. Trova il **costo fisso** dello sharded a basso volume e il **crossover**.
  - **L-sched** [L] — *end-to-end*: drift di scheduling p50/p99 + throughput di re-schedule recurring +
    **memoria + thread count**, a basso carico (10-100 task) **e** alto carico (100k+ scheduled, >10k
    `Schedule`/sec). Entrambi gli scheduler, stesso harness.
  - **Output**: curva di crossover + tabella per regime. Esiti: (a) Sharded ≥ Periodic *ovunque* (incluso
    basso volume su latenza/memoria/thread) → candidato a rimuovere lo standard; (b) Periodic vince a basso
    volume (probabile per i 32 loop) → servono entrambi, **documenta la soglia di switch**; (c) pari →
    Periodic default semplice, Sharded opt-in dichiarato. Assorbe L7 (drift) e parte di B3 (contention PQ).

### TIER 3 — Lifecycle / durabilità completa (dove crolla la claim "in vitro")

- **★ L8 — lifecycle storage COMPLETO per storage** [L] — non "ripeti L1": misura **tutte le 3 scritture**
  (`Persist`+`SetInProgress`+`SetCompleted`) end-to-end su In-Memory→SQLite→SqlServer→Postgres. Metrica:
  throughput/latenza assoluti + **round-trip/task** + delta vs In-Memory + delta vs A4 storage-only.
  È il numero "production" reale.
- **L-audit — write amplification** [L] — L8 ripetuto con audit **Full/Minimal/ErrorsOnly/None** (non solo
  Full|None: i livelli intermedi hanno semantica diversa). Righe `StatusAudit` scritte/task. `Full` è il
  **config di default reale** (le tue app probabilmente lo useranno), non un'opzione marginale.

- **L-logger — persistent proxy logger** [L] — impatto del logger persistente (`WithPersistentLogger`,
  `PersistentLoggerOptions`: `SetMinimumLevel`, `SetMaxLogsPerTask`) che scrive i `TaskExecutionLog` nel DB
  via `SaveExecutionLogsAsync`. Handler che emette K righe di log (es. 0 / 3 / 10), **logger on vs off**, su
  ciascuno storage DB. Metrica: delta di throughput/latenza + **righe log scritte/task** + byte/task. Feature
  secondaria, ma vale sapere *quanto* costa davvero: è una scrittura DB extra per esecuzione, quindi
  candidata a essere un costo nascosto non banale quando attiva. Misurarla isolata (non confonderla con
  l'audit) e riportare il costo marginale per riga di log.
- **L-recovery — backlog drain a freddo** [L] — *il differenziatore headline della libreria.* Pre-seed
  10k/100k/1M righe recoverable (stati misti: Queued/WaitingQueue/InProgress/recurring), kill, restart →
  tempo di startup, tempo a primo task, **tempo a drain**, rows/sec, p99 dispatch→start durante recovery,
  memoria. Su tutti e 4 gli storage.
- **L-failure — retry/timeout storm** [L] — `--error-rate` 0/10/50/100%. Handler che fallisce/timeout →
  retry default `LinearRetryPolicy(3, 500ms)` + scritture stato extra + OnError + (ri-acquisizione gate se
  `ThrottleRetries`). Variante run-once per isolare il costo del **failure path** dal delay di retry.
  Throughput + write amplification + p99. (Success-path bias di v1 = ogni numero era ottimistico.)

### TIER 4 — Feature & superfici residue

- **L-payload** [L]+[B] — asse `--payload` tiny / 1KB / 64KB / nested-polymorphic (la ragione di Newtonsoft).
  Dispatch latency, byte/op, dimensione riga DB, p99, pressione LOH. (B1 con un solo payload triviale non
  rappresenta nulla.)
- **L6/B2 — rate limiting** — overhead del GCRA gate a vuoto (deve essere ~0) vs throttling attivo + re-park;
  cardinalità chiavi 10 vs 10k; Burst; Overflow Wait vs Discard; esaurimento `MaxParkedTasks`.
- **L7 — scheduled drift** [L] — *confluito in SCHED-VS* (Tier 2): drift `scheduleTime`→esecuzione,
  Periodic vs Sharded. **Caveat timer**: lo scheduler usa `UtcNow` + `SemaphoreSlim.WaitAsync` → drift
  ≤16ms è **floor della piattaforma** (tick ~15.6ms), non imprecisione di EverTask; eventualmente forzare
  il timer a 1ms per isolare il contributo dello scheduler.
- **B3 — recurring per occorrenza** — `CalculateNextRun` + re-schedule; cron vs fluent. La contention del
  `ConcurrentPriorityQueue` (1k/10k/100k recurring vivi, enqueue/dequeue + `Remove(previous)` O(n)) è
  coperta da **B-sched** in SCHED-VS — qui resta solo il costo di calcolo dell'occorrenza.
- **L-multiqueue** [L] — 2-3 named queue, carico asimmetrico (una satura, una idle): una coda satura
  degrada l'altra? Costo di `FallbackToDefault` vs Wait vs ThrowException; interferenza su
  `TaskDeliveryRegistry`/parking-lot condivisi.
- **L-resolution** [B]/[L] — `UseLazyHandlerResolution` on/off; handler no-dep / scoped-dep / disposable /
  con logger; delayed <30m vs ≥30m, recurring <5m vs ≥5m (cambiano modalità). Costo DI + reflection
  `SetLogCapture.Invoke` per task.
- **L-dispose** [B] — handler no-op vs `IAsyncDisposable` con DisposeAsync triviale: costo dispose/task.
- **L-monitoring** [L] — alto throughput + 1 subscriber lento: impatto su throughput + `MonitoringDroppedEvents`
  + work items (il cap semaforo droppa over-cap). Realistico per SignalR.
- **B-coldstart** [B] — prima dispatch di un tipo mai visto (Expression compile + cache miss) vs warm.
- **B-callbacks** [B] — handler con vs senza override lifecycle: costo del reflection `Invoke` (boxing ValueTask).

---

## 6. Trappole metodologiche (riassunto operativo)

Già integrate negli scenari sopra; qui la checklist da rispettare in ogni run:
1. **Open-loop + planned-arrival** per ogni latenza (no coordinated omission).
2. **3 timestamp**, mai un solo intervallo dispatch→handler.
3. **Padded per-worker counters**, mai un singolo Interlocked condiviso.
4. **`GetTotalAllocatedBytes`** per il totale, mai per-thread.
5. **Warm-until-stable** (CV<5%), JIT/PGO/GC dichiarati, GC come processi separati.
6. **Affinità/topologia CCD** dichiarata; curve etichettate sul salto cross-CCD.
7. **Outcome counts** (dropped/rejected/deferred) sempre riportati.
8. **DB**: pool pre-aperto, schema migrato, WAL/durability/fsync dichiarati; Testcontainers etichettato
   "costo relativo in ambiente containerizzato", **non** "produzione reale".
9. **p999/p50** come segnale di slowdown nascosto.
10. **Timer ~15.6ms** = floor della piattaforma in L7, non imprecisione di EverTask.

---

## 7. Output e reporting

- JSON grezzo per run sotto `benchmarks/results/` (per analisi successiva).
- `benchmarks/LOAD_RESULTS.md`: ambiente completo (§2.6), tabelle per scenario, **deviazione/CI**,
  outcome counts, p999/p50, **doppio headline etichettato** (engine vs production), e i delta vs ancore.
- Confini espliciti della claim: ogni numero dichiara storage, audit, λ/parallelism, GC, payload.

---

## 8. Ordine di esecuzione (anti-bias: sospetti PRIMA della vetrina)

v1 metteva i numeri-vetrina per primi e i sospetti slowdown per ultimi — esaurito tempo/energia sui
numeri belli, lo storage delta rischiava di essere fatto in fretta. v2 inverte:

1. **Tier 0 ancore** (A1-A4) — danno il metro a tutto il resto.
2. **Sospetti slowdown** — L-slowdown-mem, L5 hot-key, L-overhead-isolation, L8 lifecycle completo,
   L-failure, L-recovery. *Qui si cercano i problemi, con il modello teorico atteso come oracolo
   (deviazione dalla linearità = slowdown).*
3. **Stress** — L3, L4, L-cancel-storm, L-multiqueue, L-monitoring.
4. **Feature** — L6/B2, L7, B3, L-payload, L-resolution, L-dispose, B-coldstart, B-callbacks.
5. **Headline per ultimo** — L-dispatch-prod e L8-headline (production, primari), poi L-thr, L-lat, B1
   (engine, diagnostici): a quel punto sai dove sono i limiti e i numeri sono onesti, non ottimistici.
6. **Sostenibilità lunga** (`--duration` minuti, payload realistico) per scoprire crescita Gen2/LOH che
   un run da 1M task brevi non vede.

---

## 9. Decisioni prese

1. **Percentili → `HdrHistogram`** con `RecordValueWithExpectedInterval` (§2.4). Da aggiungere a
   `Directory.Packages.props`.
2. **Driver → console custom**; open-loop + closed-loop concorrente (§2.2). BDN solo per i micro [B].
3. **Solo reportistica, solo locale.** Nessun gate CI, nessun run automatico.
4. **`--count 1M` confermato** (In-Memory 1M; DB anche a 100k per iterare in fretta).
5. **Headline doppio etichettato, production primario** (v2): *production* (durabile su DB, lifecycle
   completo, audit on) è il numero che conta per le app reali; *engine* (In-Memory + % overhead su raw
   Channel) resta come diagnostica del motore. Mai confondere i due.
6. **Scope comprensivo** (v2): tutte le correzioni metodologiche + tutti gli scenari nuovi (recovery,
   failure storm, payload, lifecycle completo, ancore, sospetti slowdown). Suite ~2× v1.
7. **Scheduler standard vs sharded** (v2): head-to-head decision-driving (SCHED-VS), load + micro, basso e
   alto volume, con analisi di crossover per decidere se lo standard è ridondante.

### Confine di scope (cosa NON sta in questo piano)
Questo è il piano **load/end-to-end di sistema**, più i micro [B] che rispondono a domande puntuali
(incluso SCHED-VS, perché guida una decisione architetturale *ora*). Una **suite micro hot-path esaustiva
per-metodo** (regression-guard di ogni classe sul percorso caldo, in isolamento: dispatcher, gate, wrapper,
serializer, blacklist, registry…) ha scopo e cadenza diversi — cresce col codice e vive accanto a
`EverTask.Benchmarks`/`RESULTS.md`. Va in un **doc companion separato** (es. `BENCHMARK_MICRO_PLAN.md`),
da redigere dopo. Qui pianto solo il seme decision-driving (SCHED-VS); il catalogo per-metodo resta fuori.

## 10. Provenienza della review (per tracciabilità)

v2 deriva da una review avversariale a 4 lenti indipendenti sul piano v1. Difetti critici emersi e
corretti: coordinated omission (L2), latenza che mescola channel+storage write, L8 che misurava 1
scrittura su 3, headline In-Memory non difendibile, assenza di ancore per la claim relativa, completion
gate auto-contendente, allocazioni per-thread, warmup insufficiente, saturazione tautologica con Wait,
ordine confirmation-first. Scenari aggiunti: recovery/backlog, failure/retry storm, payload sweep,
multi-queue, cancel storm, In-Memory O(n), hot-key serialization, lazy/eager, dispose, monitoring a carico.
