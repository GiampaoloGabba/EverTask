# Piano di implementazione — Occorrenze durevoli, misfire, timezone e schedule dinamici (issue #23)

> **Stato: v1.0 — APPROVATO dal maintainer (2026-08-22)**; GO di Codex al round 5. Documento file-per-file, ancorato al codice reale
> (riferimenti `file:riga` verificati il 2026-08-21 su `master` @ `0557beb`). Le scelte bloccate stanno in
> `review/recurring-occurrences-decisions.md` (che **prevale**). Mirror strutturale di `review/mysql-provider-plan.md`.

---

## 0. Executive summary

**Tesi:** «Il catch-up first-class non è un cursore più furbo: è una riga per occorrenza. Tutto il resto di
EverTask (one-shot, recovery, retry, audit, rate limiting, monitoring) già sa gestire una riga.»

**7 fasi** in sequenza (una sub-issue di #23 + una PR ciascuna), tutte sotto la release **4.0.0** (la major è
imposta dal refactor Abstractions `62d2010` già su master; le modifiche di #23 restano additive — decisioni
v1.1/X6).
Ordine riorganizzato dopo il round 1: **prima gli invarianti** (evaluator, clock, compatibilità, storage CAS),
poi i consumatori (contesto, zona, occorrenze), **per ultimo** il provider.

| Fase | Contenuto | Dipende da | Rischio |
|------|-----------|------------|---------|
| 1 | Invarianti: `IScheduleEvaluator` behavior-neutral (+ successore naturale per il grace), clock deterministico dello scheduling (P9), compatibilità JSON/record, factory executor-da-riga, migrazione unica, DIM storage atomici + capability, recovery X3 (esecuzione vs finalizzazione) | — | medio-alto (tocca 4 provider + hot path) |
| 2 | Execution context (ambient accessor, `Context` sulla base class) | 1 | basso |
| 3 | Timezone (`ScheduleSemantics`, `WallClock`, `InTimeZone`, default globale, Cronos con zona, docs) | 1 | medio (matematica) |
| 4 | Occorrenze durevoli + misfire (materializer, kick, riconciliazione, cancel atomico, requeue, retention, eventi) | 1, 2 | **alto** |
| 5 | `ITaskScheduleManager` (`Reschedule`, `ReevaluateSchedule`, `RequeueFailedOccurrence`, `CancelSchedule`) | 1, 4 | medio |
| 6 | `INextOccurrenceProvider` | 3, 4, 5 | alto |
| 7 | Monitoring API + UI React, sweep docs finale, samples, CHANGELOG, release | tutte | basso ma ampio |

**Gate comuni** (bloccanti prima del merge di ogni fase):
1. `dotnet build EverTask.slnx -c Release` a **0 warning** su net8/net9/net10;
2. `dotnet test EverTask.slnx -c Release` **verde**, inclusi i Testcontainers (SQL Server, Postgres, MariaDB) — Docker è disponibile, mai saltarli;
3. path legacy **byte-identico**: suite `RecurringTests/` e integration esistenti verdi **senza modifiche**; golden-byte del JSON legacy verde. Le eccezioni ammesse sono **solo** quelle ratificate per iscritto nelle decisioni, ognuna con i propri test dedicati: **X3** (filtro recoverable e finalizzazione, fase 1) e **`EagerHandlerOwnership`** (rilascio dello scope del handler eager su ogni uscita da `DoWorkGuarded`, fase 2 — decisioni §3.3). Una deviazione non ratificata blocca il gate: si ratifica o si sposta fuori dalla fase;
4. fasi 1, 4, 6: review avversariale (skill `adversarial-review`) prima del merge;
5. regola anti-stale: ogni opzione pubblica introdotta in una fase aggiorna **nella stessa PR** `docs/configuration-cheatsheet.md`, `docs/configuration-reference.md`, `plugins/evertask/skills/integrate-evertask/` (+ tipi TS della UI se cambiano i DTO API); ogni modifica alla superficie storage aggiorna nella stessa PR la skill `new-relational-storage-provider` in `.claude/skills/` **e** nel mirror `.agents/skills/`. La fase 7 è solo lo sweep finale, non il posto dove rimandare le docs. **Ogni documento in `docs/` toccato o creato passa dalla skill `humanizer` prima del commit**;
6. test **integration-first** (D6): parti reali ovunque possibile (host reale, storage reale, Testcontainers), unit solo dove ha senso; mai mock rigged;
7. performance (D7): nuove operazioni storage al livello di ottimizzazione degli esistenti per provider fin dalla prima PR; nessun round-trip aggiunto ai hot path; benchmark `LoadHarness` prima/dopo per le fasi 1 e 4 (nessuna regressione su dispatch/execute/recurring advance).

---

## 1. FASE 1 — Invarianti (zero cambi di comportamento, eccetto X3)

### 1.1 `IScheduleEvaluator` (refactor behavior-neutral, primo commit)

| File | Modifica |
|------|----------|
| `src/EverTask/Scheduler/Recurring/IScheduleEvaluator.cs` + `ScheduleEvaluator.cs` (nuovi, internal) | `NextAfterAsync(def, anchor, after, ct)`, `CountMissedAsync(def, anchor, after, cap, ct)` (**bounded**: si ferma a `cap + 1`), `IsOccurrenceStillCurrentAsync(def, occ, now, ct)`, `CalculateNextValidRunAsync(...)`, **`NextGridOccurrenceAfterAsync(def, occ, ignoreTerminationBounds: true)`** (successore naturale ignorando `RunUntil`/`MaxRuns`, per il grace X3). Builtin ⇒ wrapper sincrono (`ValueTask` completata) delle primitive attuali (`NextOccurrenceStrictlyAfter` `RecurringTask.cs:213`, `CountMissedOccurrences` `:277`, `IsOccurrenceStillCurrent` `:266`, `CalculateNextValidRun` `RecurringTaskExtensions.cs:31`). |
| `src/EverTask/Dispatcher/Dispatcher.cs:275-351`, `src/EverTask/Worker/WorkerExecutor.cs:977-1041`, `src/EverTask/Handler/TaskHandlerExecutor.cs:272-283` | Tutti i call site passano dall'evaluator. `ToQueuedTask` non calcola più il primo `NextRunUtc`: lo riceve sempre dal Dispatcher (il ramo `ExecutionTime == null` diventa un `InvalidOperationException` difensivo). |

**Gate:** intera suite verde senza toccare un test.

### 1.2 Clock deterministico dello scheduling (P9)

| File | Modifica |
|------|----------|
| `ServiceCollectionExtensions.cs` | `TryAddSingleton(TimeProvider.System)`. |
| `Dispatcher.cs` (`:147,281,293,341,420,454,529`), `WorkerExecutor.cs` (`:975,999,1230`), `WorkerService.cs` (`:217` + predicati recovery), `RecurringTask.cs:63,124` (via parametro `now` passato dall'evaluator) | Ogni `DateTimeOffset.UtcNow` ⇒ `timeProvider.GetUtcNow()`. |
| `PeriodicTimerScheduler.cs:146-203`, `ShardedScheduler.cs:148-238` | Gli scheduler dormono oggi con `SemaphoreSlim.WaitAsync(timeout)` (clock reale): l'attesa diventa una **race tra il segnale di wake-up e `Task.Delay(delay, timeProvider, ct)`** (cancellando il perdente) — o un'astrazione `IWakeableDelay`; i due-check usano `timeProvider.GetUtcNow()`. |
| `Scheduler/Recurring/Builder/*.cs` (`RecurringTaskBuilder.cs:7-10,33-42`, `DailyTimeSchedulerBuilder.cs:38-45`, `HourSchedulerBuilder.cs:16-23`, …) | `RunNow` e le validazioni `RunUntil` leggono `UtcNow`: il `TimeProvider` è passato ai builder interni dal Dispatcher (`Dispatcher.cs:96-98`) — oppure la validazione temporale si sposta nel Dispatcher/evaluator. |
| `ServiceCollectionExtensions.cs:65-75`, `RateLimiting/RateLimitGate.cs:127-140,272-280`, `RateLimitParkingLot.cs:97-103` | La factory passa il `TimeProvider` DI a `InMemoryKeyedRateLimiter`; gate e parking lot leggono lo stesso provider (oggi `UtcNow`/`Task.Delay` reali). |
| Fuori dominio (documentato) | `IRetryPolicy`/`LinearRetryPolicy` (`Task.Delay` proprio, API pubblica), audit, logging. I test con `FakeTimeProvider` non si aspettano che `Advance()` completi i delay di retry (retry testati con delay nulli/brevi o policy fake). |
| `test/EverTask.Tests/TestHelpers/FakeTimeProvider.cs` | Riuso; helper `IsolatedIntegrationTestBase` accetta un `TimeProvider`. |

### 1.3 Compatibilità JSON e record pubblici

| File | Modifica |
|------|----------|
| `src/EverTask/Monitoring/EverTaskEventData.cs:3-43` | Proprietà `init` nel body: `Guid? ParentTaskId`, `DateTimeOffset? ScheduledAtUtc`, `int? ScheduleVersion`. Valorizzate in `FromExecutor` (`:14`) e in `WorkerExecutor.CreateEventDataCached` (`:1201-1239`). |
| `src/EverTask/Scheduler/Recurring/RecurringTask.cs` + `src/EverTask.Abstractions/OccurrenceMode.cs` (scaffold) | `OccurrenceMode { Inline = 0, Durable = 1 }` e `RecurringTask.OccurrenceMode` (default `Inline`, `[JsonIgnore(WhenWritingDefault)]`, golden-byte): nessun builder né comportamento in fase 1 — serve solo a `IsScheduleOnly` e alla recovery a due passate. |
| `src/EverTask/Handler/TaskHandlerExecutor.cs:19-39,177-224` | Proprietà `init`: `Guid? ParentTaskId`, `string? RuntimeInfo`, `int? RunNumber`, `int ScheduleVersion`, `DateTimeOffset? NominalSlotUtc`. `ToLazy()` ⇒ `this with { Handler = null, HandlerCallback = null, HandlerErrorCallback = null, HandlerStartedCallback = null, HandlerCompletedCallback = null, HandlerScope = null, HandlerTypeName = HandlerTypeName ?? TypeNameCache.GetAssemblyQualifiedName(Handler!.GetType()) }`. `IsScheduleOnly => RecurringTask?.OccurrenceMode == Durable`. |
| `test/EverTask.Tests/Serialization/` (nuovi) | `RecurringTaskGoldenJsonTests`: fixture JSON 3.11 per ogni interval e per schedule compositi ⇒ deserializzazione + riserializzazione **byte-identica**. `ConsumerCompatibilityTests`: fixture compilata contro la **baseline `issue23-baseline`** (progetto di test separato che referenzia i pacchetti baseline buildati da master pre-#23, feed locale `nupkg/`) caricata con 4.0 per builder, `ITaskDispatcher`, `EverTaskEventData`, `TaskHandlerExecutor` — dimostra che #23 è additiva (X6 rev. v1.1; il pacchetto pubblico 3.11 non è utilizzabile: il refactor `62d2010` ne rompe già il load). |

### 1.4 Factory executor-da-riga (recovery e materializer)

| File | Modifica |
|------|----------|
| `src/EverTask/Worker/RecoveredTaskFactory.cs` (nuovo, internal) | `FromRow(QueuedTask row)` ⇒ payload deserializzato, `RecurringTask` validato, `TaskKey`, `QueueName`, `AuditLevel`, `ParentTaskId`, `RuntimeInfo`, `ScheduleVersion`, `CurrentRunCount` — un solo punto invece degli argomenti sparsi di `WorkerService.cs:373-388`. `Dispatcher.CreateCachedWrapper` ⇒ `internal static`. |
| `src/EverTask/Worker/WorkerService.cs:203-270,285-478` | `ProcessRecoveredTaskAsync` usa la factory; passa tutti i metadati al re-dispatch; le righe "solo da finalizzare" (X3) vanno a `TrySetRecurringSeriesCompleted` (condizionato) senza `ExecuteDispatch`. **Due passate**: prima tutte le righe non schedule-only (one-shot, figli, recurring inline), poi — dopo `Task.WhenAll` — i padri durevoli (M7: barriera "figli prima dei padri"). `nowUtc` dal `TimeProvider` passato a `RetrievePending`/`TrySetQueuedIfRecoverable`. |
| `src/EverTask/Dispatcher/Dispatcher.cs:287-327` | Ordine del ramo recovery: **finalizzazione** (slot `>= RunUntil` o `MaxRuns` raggiunto) → **grace** con il successore naturale (`NextGridOccurrenceAfterAsync`: `> now` ⇒ esegui; `<= now` ⇒ finalizza; non calcolabile ⇒ niente grace) → skip-forward. Oggi il grace precede la finalizzazione e `following == null` vale "corrente per sempre" (`RecurringTask.cs:266-269`). |

### 1.5 Migrazione unica e storage

| File | Modifica |
|------|----------|
| `src/EverTask/Storage/QueuedTask.cs` | `+ Guid? ParentTaskId`, `+ string? RuntimeInfo`, `+ int ScheduleVersion`; navigazione `QueuedTask? Parent` / `ICollection<QueuedTask> Occurrences` (per la FK). `IsRecoverable(now)` (`:55-64`) ⇒ **X3**: due predicati, `IsRecoverableForExecution(now)` = `IsRecoverableStatus && !MaxRunsExhausted && (RunUntil == null || RunUntil >= now || (IsRecurring && NextRunUtc != null && RunUntil != null && NextRunUtc < RunUntil))` (status e `MaxRuns` in AND davanti a tutto; la stessa espressione raggruppata nelle 4 copie) e `IsRecurringSeriesToFinalize(now)` (`IsRecurring && NextRunUtc != null && Status` **non terminale** `&& (NextRunUtc >= RunUntil \|\| CurrentRunCount >= MaxRuns)`); `RetrievePending(nowUtc, …)` ritorna l'unione e la riga porta il motivo; `now` è sempre un parametro (P9). |
| `src/Storage/EverTask.Storage.EfCore/TaskStoreEfDbContext.cs:16-114` | FK self `ParentTaskId → Id` `OnDelete(DeleteBehavior.Restrict)`; `HasIndex(ParentTaskId, ScheduledExecutionUtc).IsUnique().HasDatabaseName("UX_QueuedTasks_Occurrence")` (nome stabile per riconoscere la violazione); `HasIndex(ParentTaskId)`; check `CK_QueuedTasks_OccurrenceSlot` (`ParentTaskId IS NULL OR ScheduledExecutionUtc IS NOT NULL`) via `ToTable(t => t.HasCheckConstraint(...))`; `ScheduleVersion` default 0. |
| `src/Storage/EverTask.Storage.EfCore/EfCoreTaskStorage.cs` | `RecoverableQuery(now)` (`:24-36`): **X3** (espressione raggruppata); override degli overload `RetrievePending(nowUtc, …)`/`TrySetQueuedIfRecoverable(nowUtc, …)` (`:80,116-158`: oggi leggono `UtcNow`); le firme legacy delegano agli overload con `UtcNow`. Nuove operazioni atomiche (pattern `CompleteRecurringRun` `:470-533`): `MaterializeOccurrence` (CAS su `ScheduleVersion`+`NextRunUtc`, insert figlio copiando le colonne, avanzamento saturante, `Completed`+cursore nullo nello stesso commit quando `newCursor == null`; violazione unique riconosciuta dal nome `UX_QueuedTasks_Occurrence` ⇒ `AlreadyExists`), `TrySetRecurringSeriesCompleted(id, expectedCursor, expectedStatus, expectedVersion)` (condizionato; `SetRecurringSeriesCompleted` resta per il path legacy), `CancelSchedule`, `RequeueTerminal`, `TryRequeueStaleOccurrence(childId, expectedStatus)`, `UpdateSchedule`, `TryHaltSchedule(parentId, expectedVersion, expectedCursor, expectedStatus, runtimeInfo)`; overload CAS di `UpdateCurrentRun`/`CompleteRecurringRun`; `CountActiveOccurrences(parentId)`, `GetOccurrences`. `Remove` (`:711-733`): figli nella stessa transazione. Nuova passata `CleanupTerminalOccurrences` (M16). |
| `src/Storage/EverTask.Storage.SqlServer/` | Migrazione `AddDurableOccurrences` (colonne, FK, indici, check, proc `usp_MaterializeOccurrence`, `usp_CancelSchedule`, nuove versioni CAS di `usp_UpdateCurrentRun`/`usp_CompleteRecurringRun`; `Down()` ripristina — `SqlServer/CLAUDE.md:98`); hand-edit schema-aware (`CLAUDE.md:113-117`). Override in `SqlServerTaskStorage.cs`. |
| `src/Storage/EverTask.Storage.Postgres/` | Migrazione (schema-aware, `Postgres/CLAUDE.md:49-54`); CTE scrivibili per le nuove operazioni e per i CAS (`PostgresTaskStorage.cs:49-208`). |
| `src/Storage/EverTask.Storage.MySql/` | Migrazione + proc (`DROP`/`CREATE` separati, `suppressTransaction: true`, precedente `20260629214027`); `AuditLevel.None` continua a delegare alla base. |
| `src/Storage/EverTask.Storage.Sqlite/` | Migrazione; `SqliteTaskStorage.cs:42-59` (copia client-side del filtro): **X3**. FK attive (`PRAGMA foreign_keys` è on per EF). |
| `src/EverTask/Storage/MemoryTaskStorage.cs` | Tutte le operazioni sotto `_pendingTasksLock`; unique e FK replicati a mano (`:42-47`); `:66` **X3** con `nowUtc` parametrico (`:62-66,99` oggi `UtcNow`). Idem `SqliteTaskStorage.cs:36-42,80`. |
| `src/EverTask/Scheduler/IScheduler.cs:34-46` | `+ bool SupportsScheduleInspection => false` (DIM); `true` nei built-in, che implementano davvero `IsScheduled`. |
| `src/EverTask/Storage/ITaskStorage.cs` | DIM: atomiche **e** overload CAS ⇒ `NotSupportedException`; letture ⇒ default via `Get`; overload `RetrievePending(nowUtc, …)`/`TrySetQueuedIfRecoverable(nowUtc, …)` come **DIM che delegano alle firme legacy** (`:45` astratta, `:74-80` DIM — entrambe intatte, così gli override dei custom storage non vengono bypassati); il core chiama sempre gli overload; i built-in li sovrascrivono con `nowUtc`; capability `SupportsDurableOccurrences => false`, `SupportsScheduleVersioning => false` (override `true` in EfCore base e Memory). Il path legacy continua a chiamare i metodi non-CAS attuali. `AuditRetentionPolicy.OccurrenceRetentionDays`. |
| Docs (stessa PR) | `docs/storage/custom-storage.md:15-55` (nuovi DIM), `docs/storage/{sql-server,postgres,mysql,sqlite}-storage.md` (colonne/indici/FK), `docs/configuration-cheatsheet.md:74-93` e `configuration-reference.md:211-333` (`OccurrenceRetentionDays`), skill `references/03-storage.md:139-155`, `src/Storage/*/CLAUDE.md`. |

**Test (fase 1):**
- `EfCoreTaskStorageTestsBase` (4 provider): atomicità di `MaterializeOccurrence` con **fault injection** (eccezione dopo l'insert e prima dell'avanzamento ⇒ rollback totale), `AlreadyExists` su doppia materializzazione concorrente (stesso slot, due task), `CursorMoved`/`VersionMismatch`, `ParentInactive`, finalizzazione nello stesso commit (`newCursor == null` ⇒ `Completed` + cursore nullo + figlio presente), `CancelSchedule` atomico, `RequeueTerminal` conserva id/audit, CAS su `UpdateCurrentRun`/`CompleteRecurringRun` (0 righe ⇒ `VersionMismatch`), FK Restrict (`Remove(padre)` con figli ⇒ cancellati nella stessa transazione), check constraint, `CleanupTerminalOccurrences` per stato; schema test per provider (indici/constraint via catalogo, precedente `SqlServerEfCoreTaskStorageTests.cs:80-90`); snapshot SQL delle migration.
- Recovery X3: `MemoryStorageRecoveryFilterTests` + sezione recovery di `EfCoreTaskStorageTestsBase` (4 provider, SQL tradotto verificato): `NextRunUtc < RunUntil < now` ⇒ recuperata **per esecuzione** (grace se il successore naturale è futuro, altrimenti finalizzata); `NextRunUtc >= RunUntil` o `CurrentRunCount >= MaxRuns` con cursore non nullo ⇒ recuperata **per finalizzazione** (nessun handler, un solo audit `Completed` senza `Queued` fantasma — forma ratificata nelle decisioni §3.2, ciclo 3, che supera il «audit `Queued → Completed`» di X3); righe `Cancelled` mai selezionate; `TrySetRecurringSeriesCompleted` **perde** il CAS se un `Cancel` si è linearizzato prima (status resta `Cancelled`); grace infinito eliminato (slot vecchio di mesi con successore passato ⇒ finalizzazione, non esecuzione; mensile con successore futuro ⇒ esecuzione); finalizzazione fallita ⇒ L18 incrementato ⇒ successo ⇒ azzerato; i predicati usano il `nowUtc` passato (test con `FakeTimeProvider` su tutti e 4 i provider).
- `RecurringTests/` invariati; golden JSON; consumer compatibility; `FakeTimeProvider` cablato in un test di recovery esistente senza cambiarne le asserzioni; scheduler con clock finto (`TimerSchedulerTests`, `ShardedSchedulerTests`).

---

## 2. FASE 2 — Execution context

| File | Modifica |
|------|----------|
| `src/EverTask.Abstractions/ITaskExecutionContext.cs`, `MisfireInfo.cs`, `ITaskExecutionContextAccessor.cs` (nuovi) | C1. |
| `src/EverTask.Abstractions/IEverTaskHandler.cs` | `+ void SetExecutionContext(ITaskExecutionContext context) { }` (DIM no-op). |
| `src/EverTask.Abstractions/EverTaskHandler.cs` | `protected ITaskExecutionContext Context` con backing field nullable e getter che lancia `InvalidOperationException` se letto prima dell'iniezione (warnings-as-errors: niente `null!`); implementazione esplicita del DIM. |
| `src/EverTask/Worker/TaskExecutionContext.cs`, `AmbientTaskExecutionContextAccessor.cs` (nuovi, internal) | Contesto mutabile internamente (`Attempt`), immutabile per l'handler; accessor singleton `AsyncLocal` (C3). |
| `src/EverTask/Worker/WorkerExecutor.cs` | `DoWorkCore` dopo `CreateLogCapture` (`:285-301`): costruisce il contesto (slot = `task.NominalSlotUtc ?? RuntimeInfo.SlotUtc ?? ExecutionTime` secondo C4; `Misfire.Kind = Late` se `now − slot > MisfireThreshold`), lo assegna all'accessor, lo inietta via delegate cachato (`HandlerOptionsCache` `:38-44`), **prima di `OnStarted`**; `Attempt` aggiornato nel retry loop (`:623-670`); accessor azzerato nel `finally`. C1 chiede `Attempt` = «l'ultimo tentativo eseguito» in `OnError`: il contatore avanza **solo** quando un tentativo è ammesso dentro l'handler e l'annuncio di `OnRetry` (il tentativo che *sta per* partire) è riportato indietro all'uscita di `ExecuteTask`, perché quel retry può ancora non partire mai (cancel nella callback o durante il delay, gate `ThrottleRetries` che lo rimanda). **Conseguenza di C2 sul path della reiezione terminale del rate limit** (`HandleRateLimitRejectionAsync`, l'unica callback che non passa da `DoWorkCore`): riceve le stesse due iniezioni prima di `OnError`, altrimenti la callback leggerebbe un `Context` non iniettato (che lancia) o un `Logger` null, e `ExecuteCallback` inghiottirebbe entrambi. La log capture di quel path **non** è persistita: l'unica scrittura di storage ammessa in un ciclo di reiezione resta lo stato `Failed`. |
| `src/EverTask/Worker/WorkerExecutor.cs` — `EagerHandlerOwnership` (**eccezione al gate 3, ratificata — decisioni §3.3**) | Il rilascio dello scope EverTask-owned del handler eager smette di vivere nel solo `finally` di `DoWorkCore` e diventa un claim per-delivery rilasciato **una volta sola** su qualunque uscita: i due siti ordinati (`DoWorkCore` e la reiezione terminale, **prima** che la serie pianifichi l'occorrenza successiva) restano dove sono, il `finally` di `DoWork` copre tutte le altre uscite di `DoWorkGuarded` (i due drop da blacklist, il deferral rate-limit, il re-park in-flight, lo skip di delivery duplicata, l'attesa al gate cancellata dallo shutdown), dove oggi lo scope non veniva rilasciato mai. Correzione di un leak reale: su quei path `DisposeAsyncCore` del handler e le sue dipendenze scoped non giravano. |
| `src/EverTask/MicrosoftExtensionsDI/EverTaskServiceConfiguration.cs` | `SetMisfireThreshold(TimeSpan)` (default 5 s; **non** tocca la tolleranza legacy di 1 s). |
| Docs (stessa PR) | `docs/task-creation.md` (sezione "Execution context"), cheatsheet `:177-189` (Handler Properties) e `:16-29`, reference `:1768+` e `###SetMisfireThreshold`, skill `references/02-*`/`05-scheduling.md`. |

**Test:** `ExecutionContextIntegrationTests` — valori in `Handle` e in tutti i callback per one-shot immediato/ritardato/recurring; `Attempt` coerente nei retry e in `OnRetry`/`OnError`; accessor letto da un servizio scoped iniettato in un handler **eager** (il caso C3) e in uno lazy; `RunNumber` dopo riavvio; rate-limit deferral di un one-shot ⇒ `ScheduledAtUtc` invariato (slot nominale) mentre il reserved slot cambia; reiezione terminale ⇒ `OnError` legge `Context` e `Logger` (`A_terminal_rate_limit_rejection_still_gives_OnError_its_context_and_logger`) e riporta l'ultimo tentativo davvero eseguito, non quello che il gate ha rimandato.
`EagerHandlerScopeReleaseTests` (host reale, storage Memory, handler tutti eager, scheduler reale strumentato) — una uscita per test: drop da blacklist, deferral del gate, re-park in-flight, skip di delivery duplicata, e la reiezione terminale di un'occorrenza, dove ciò che si pinna è l'**ordine** (scope rilasciato prima che l'occorrenza successiva arrivi allo scheduler). Ogni test conta gli scope creati e quelli disposti: un rilascio mancante lascia il conteggio scoperto.

---

## 3. FASE 3 — Timezone

| File | Modifica |
|------|----------|
| `src/EverTask/Scheduler/Recurring/ScheduleSemantics.cs` (nuovo) | Classificazione T5 (`Elapsed`/`Calendar`) calcolata da `RecurringTask`. |
| `src/EverTask/Scheduler/Recurring/WallClock.cs` (nuovo, internal static) | `ToWall(utc, zone)`; `ToUtc(wallNominal, zone, notBeforeUtc) → WallMapping { Utc, Consumed, CollapsedCount }` con l'algoritmo T6/T7 (bracket 48 h + binary search; ambiguo ⇒ primo passaggio; `Consumed` se `<= notBeforeUtc`). |
| `src/EverTask/Scheduler/Recurring/RecurringTask.cs` | `+ string? TimeZoneId` (`[JsonIgnore(WhenWritingNull)]`), `[JsonIgnore] Zone` lazy. `GetNextOccurrence(current)` (`:164-185`): Elapsed o zona UTC ⇒ path attuale **intoccato**; Calendar con zona ⇒ loop `wall = ToWall(current)` → cascata intervalli esistente su wall → `ToUtc(...)`; `Consumed` ⇒ riparte dallo slot nominale. Cron ⇒ `CronInterval.GetNextOccurrence(current, Zone)`. `IsUniformGrid()` (`:327`): T8. `Validate()`: zona. `ToString()`: suffisso. |
| `src/EverTask/Scheduler/Recurring/Intervals/CronInterval.cs:62-63` | Overload con zona. |
| `src/EverTask.Abstractions/IRecurringTaskBuilder.cs` + `src/EverTask/Scheduler/Recurring/Builder/*.cs` | `InTimeZone(TimeZoneInfo)`/`InTimeZone(string)` come DIM (T3) implementati dai builder interni; `AtTime/AtTimes` salvano il `TimeOnly` verbatim; `InTimeZone` su Elapsed ⇒ `InvalidOperationException`. |
| `src/EverTask/MicrosoftExtensionsDI/EverTaskServiceConfiguration.cs` + `Dispatcher.Dispatch(task, builder, …)` | `SetDefaultScheduleTimeZone(TimeZoneInfo)`; applicato solo ai Calendar privi di zona, prima della serializzazione. |
| Docs (stessa PR) | `docs/recurring-tasks/best-practices.md:77-98` e `docs/task-dispatching.md:152-166` (rimozione dei pattern errati), nuova `docs/recurring-tasks/time-zones.md` (semantica Elapsed/Calendar, DST con esempio Italia 02:00), `fluent-api.md`, `cron-expressions.md`, cheatsheet `:203-215`, reference `:1822+`, skill `references/05-scheduling.md`, `src/EverTask/Scheduler/Recurring/CLAUDE.md` (gotcha zona/griglia uniforme). |

**Test (funzioni pure, deterministici):** `CronOracleTests` (zone `Europe/Rome`, `America/New_York`, `Australia/Sydney`, `Asia/Kolkata`, `Asia/Kathmandu`, `Australia/Lord_Howe`, `Pacific/Chatham`, `America/Sao_Paulo` + zona custom con regole EU): 400 occorrenze **dalla seconda** uguali a Cronos, prima occorrenza testata a parte; `DstTransitionTests` (02:00/02:30 Roma spring-forward ⇒ 01:00Z; fall-back una volta per Calendar, due per `Every(30).Minutes`; `01:45 + 30 min = 03:15` locale; mezzanotte attraverso entrambe; più `AtTimes` collassati nel gap ⇒ una occorrenza; gap a mezz'ora Lord Howe; `RunUntil`/`MaxRuns` attraverso la DST); `SkipForwardWithZoneTests` (property test == walk; `IsUniformGrid` per semantica); `TimeZoneIdNormalizationTests`; `ScheduleSemanticsTests` (ogni combinazione del builder classificata); serializzazione (assente ⇒ UTC; golden).

---

## 4. FASE 4 — Occorrenze durevoli + misfire

### 4.1 Definizione e builder

| File | Modifica |
|------|----------|
| `src/EverTask.Abstractions/` (nuovi) | `MisfirePolicy`, `CatchUpOverflowPolicy { Halt, SkipOldest }`, `CatchUpOptions { MaxAge, MaxOccurrences, OverflowPolicy = Halt, MaxPendingOccurrences = 1 }`, `FireOnceOptions { MaxAge? }`, `IMisfirePolicyBuilder` (`Skip()`, `FireOnce(FireOnceOptions?)`, `CatchUp(CatchUpOptions)`). Validazione per costruzione (cap > 0, `MaxPendingOccurrences >= 1`). |
| `src/EverTask/Scheduler/Recurring/RecurringTask.cs` | `OccurrenceMode` (scaffold di fase 1) diventa effettivo; `+ MisfireSettings? Misfire`, `+ DateTimeOffset? BackfillFromUtc` (ignore-when-null). `Validate()`: `FireOnce`/`CatchUp` ⇒ `Durable`; cap obbligatori; `Inline` ⇒ solo `Skip`. |
| Builder | `OnMisfire(Action<IMisfirePolicyBuilder>)`, `WithDurableOccurrences()`, `BackfillFrom(DateTimeOffset)` come DIM (T3). |
| `src/EverTask/Scheduler/Occurrences/DueSlotEnumerator.cs` (nuovo, internal) | `EnumerateDueSlots(def, cursor, now, policy, activeCount)` via evaluator: `Skip` ⇒ al più lo slot corrente; `FireOnce` ⇒ l'ultimo dovuto + range; `CatchUp` ⇒ (1) salta gli slot `< now − MaxAge` (conteggio, evento); (2) conta gli eleggibili in modo **bounded** fino a `MaxOccurrences + 1` (`DetectedAtLeast`, `IsExact`): se eccede ⇒ `Halt` (nessuna materializzazione; lo stato halted è **persistito** dal materializer con `TryHaltSchedule` CAS su versione+cursore+status) o `SkipOldest` (punto d'inizio delle ultime `MaxOccurrences` trovato per **bisezione sull'istante**, ogni sonda = conteggio forward bounded; vale anche per i provider forward-only **deterministici** — gli altri sono rifiutati al dispatch); (3) emette dal più vecchio al massimo `MaxPendingOccurrences − activeCount` slot; `RunUntil` esclusivo; `MaxRuns`. Ritorna anche il motivo di stop (`WindowFull`, `Halted`, `Exhausted`) per il ri-park operativo. |

### 4.2 Materializer, worker, dispatcher

| File | Modifica |
|------|----------|
| `src/EverTask/Scheduler/Occurrences/OccurrenceMaterializer.cs` (nuovo, internal, singleton) | M7/M9/M10: `RunAsync(parentId, parentExecutor?, ct)`; lock per-padre; `SemaphoreSlim` globale (`MaterializationConcurrency`); legge la riga; `ParentInactive`/terminale ⇒ esce; **halted** (`RuntimeInfo.Halted`) ⇒ solo evento rate-limited + ri-park operativo; **figli non-terminali** (`GetOccurrences(parentId, nonTerminal)`): quelli né in delivery (`deliveryRegistry.IsDelivering`) né `IsScheduled` sono **stale** ⇒ `TryRequeueStaleOccurrence(id, expectedStatus)` (CAS) ⇒ se riuscito `scheduler.Schedule(child, slot)`; in ogni caso contano come **attivi** (senza `SupportsScheduleInspection` tutti i non-terminali contano, nessuna riconciliazione, warning una tantum); `EnumerateDueSlots(..., activeCount)`; per ogni slot `MaterializeOccurrence` (M6) ⇒ `Created` ⇒ executor figlio lazy via `CreateCachedWrapper(...).Handle(task, slot, recurring: null, …, existingTaskId: childId)` con `ParentTaskId/RuntimeInfo/RunNumber/ScheduleVersion/NominalSlotUtc` ⇒ `scheduler.Schedule(child, slot)`; `AlreadyExists`/`CursorMoved` ⇒ rilegge; eventi; ri-park del padre al prossimo slot futuro **oppure**, se lo stop è `WindowFull`/`Halted`, a `now + BacklogRetryInterval` (liveness indipendente dal kick); nulla se finalizzato. Tutte le eccezioni loggate, mai propagate al chiamante del kick. |
| `src/EverTask/Worker/WorkerExecutor.cs` | `DoWorkGuarded` (`:121-206`): dopo blacklist (`:125`), prima del gate (`:128`): `if (task.IsScheduleOnly) { await materializer.RunAsync(task.PersistenceId, task, ct); return; }`. `DoWork` `finally` (`:111-117`): `if (task.ParentTaskId is { } p) await materializer.KickAsync(p)` in `try/catch` **prima** di `deliveryRegistry.End` (unico End, disciplina invariata). |
| `src/EverTask/Handler/TaskHandlerWrapper.cs:131-171` | `ExtractRateLimit`: nessuna policy/key/warning per `IsScheduleOnly`. `ResolveQueueName` (`:177-188`): il figlio riceve la coda effettiva del padre (M4). |
| `src/EverTask/Dispatcher/Dispatcher.cs` | Dispatch di un padre `Durable`: cursore iniziale (+`BackfillFromUtc`), `NotSupportedException` se `!SupportsDurableOccurrences`. Recovery (`:269-334`): `Durable` ⇒ niente grace/skip-forward: parcheggia il padre a `max(now, NextRunUtc)` (il materializer gestisce tutto). `Cancel` (`:105-160`): padre durevole ⇒ blacklist + unschedule + `CancelSchedule` (M15). `ShouldUseLazyResolution`: figli sempre lazy (M4). |
| `src/EverTask/MicrosoftExtensionsDI/EverTaskServiceConfiguration.cs` | `SetMaterializationConcurrency(int)`, `SetBacklogRetryInterval(TimeSpan)` (default 1 min, **validato** `> 0` e ≥ granularità dello scheduler). |
| `src/EverTask/RateLimiting/CLAUDE.md:52-57,103-104` | Invarianti rinegoziate (additive). |
| Docs (stessa PR) | Nuova `docs/recurring-tasks/durable-occurrences.md` (modello, policy, `MaxAge`/`MaxOccurrences`/`OverflowPolicy`/`MaxPendingOccurrences`, at-least-once, contratto P8, retention), `docs/resilience*.md` (contratto misfire), **`docs/scalability.md:58-65` riscritto** (oggi promette distribuzione multi-istanza che il codice non garantisce; P8 + rimando all'epic lease), cheatsheet `:203-215`, reference `:1822+`, skill `references/05-scheduling.md` (wizard: "ancorato al calendario? ⇒ `InTimeZone` + `OnMisfire(CatchUp)`"), `src/EverTask/CLAUDE.md`, `Scheduler/Recurring/CLAUDE.md`. |

### 4.3 Debito della review di fase 1 che diventa raggiungibile qui

Tre punti della review avversariale della fase 1 sono stati rinviati **a questa fase**, perché è qui che il
materializer, il cancel e il carico durevole esistono davvero. La disposizione per iscritto di ognuno è in
`review/recurring-occurrences-decisions.md` §3.2; questa è la parte che va **fatta**. Vanno chiusi dentro la
fase 4, non dopo: la review di fase 4 (gate 4) li ritroverebbe con dei chiamanti veri davanti.

| Punto | Cosa fare in fase 4 | Dove |
|-------|---------------------|------|
| **R6b** — l'insieme auditato dal `CancelSchedule` della base EF viene da una rilettura, quindi sotto READ COMMITTED può attribuirsi figli cancellati da un altro writer | Decidere il contratto della **base**: transazione serializable con retry, oppure obbligo — scritto nel contratto e nella skill — di derivare l'insieme auditato da `OUTPUT`/`RETURNING` per un provider con writer concorrenti. I tre provider ottimizzati lo fanno già; SQLite eredita la base e serializza i writer, quindi il difetto riguarda un provider custom futuro. Aggiornare `.claude/skills/new-relational-storage-provider/` **e** il mirror `.agents/` nella stessa PR | `EfCoreTaskStorage.CancelSchedule`, `docs/storage/custom-storage.md`, le due skill |
| **R7** — `CancelSchedule` non cancella le occorrenze `ServiceStopped`, che `TrySetQueuedIfRecoverable` invece accetta: un'occorrenza può tornare in coda ed essere eseguita dopo che il suo schedule è stato cancellato | Correggere **M15** (il buco è nella decisione, non nel codice: l'implementazione è fedele all'elenco `WaitingQueue/Queued/Pending`) e allineare le cinque implementazioni, così l'insieme cancellato è il complemento esatto di quello che la recovery rimette in coda. Test: schedule cancellato con un figlio `ServiceStopped` ⇒ il figlio non riparte al riavvio | decisioni M15, le 5 `CancelSchedule`, `EfCoreTaskStorageTestsBase` |
| **R8** — la seconda passata di recovery bufferizza in una sola lista ogni schedule durevole di **ogni** pagina, con payload e definizione deserializzati | Sostituire la lista con una **seconda scansione keyset** sullo stesso cutoff, filtrata sugli schedule durevoli. Da fare quando esiste un carico durevole vero da misurare (`LoadHarness`, gate 7). La barriera «figli prima dei padri» resta la stessa: cambia solo come la seconda ondata trova le sue righe | `WorkerService.ProcessPendingAsync`, `RecoveryDurableScheduleBarrierTests` (che pinna la barriera e deve restare verde) |

Chiusi invece **in fase 1**, e da non riaprire qui: R12 (forma canonica della riga occorrenza,
`QueuedTask.ApplyOccurrenceContract`, pinnata sui quattro provider) e R14 (confutata: il `CancelSchedule`
Postgres prende il padre `FOR UPDATE` in uno statement separato). Fuori da #23, in
`review/recurring-occurrences-followups.md`: R10 e R13.

**Test:**
- Unit: `DueSlotEnumeratorTests` (tutte le policy × `MaxAge` × `MaxOccurrences`/`Halt`/`SkipOldest` (conteggio bounded: una griglia al secondo con `MaxAge` 92 giorni non enumera milioni di slot) × `MaxPendingOccurrences` × `RunUntil` × `MaxRuns` × backfill × zona/DST nel backlog); `MisfireInfoTests`.
- Integration (`IsolatedIntegrationTestBase` + `FakeTimeProvider`; storage Memory e SQLite): `DurableOccurrencesIntegrationTests` — ogni occorrenza è una riga figlio; il padre non esegue mai l'handler e **non consuma budget rate-limit** (`RateLimitingIntegrationTests` esteso); `MaxPendingOccurrences` 1 vs n (`TestTaskStateManager.WereExecutedInParallel`); finestra piena ⇒ nessuna perdita, ripresa al kick **e**, a kick perso (fault injection), al retry operativo; due figli paralleli con status write inghiottiti ⇒ rilevati stale, il padre non si blocca; `Halt` ⇒ evento critico con `DetectedAtLeast/IsExact`, nessuna materializzazione, stato persistito (sopravvive al riavvio e all'aging di `MaxAge`), sblocco solo con `ResumeSchedule`/`Reschedule`; `SkipOldest` ⇒ esattamente i più recenti (bisezione verificata contro l'enumerazione completa su griglie piccole); recovery a due passate ⇒ nessuna violazione di `MaxPendingOccurrences = 1` con padre e figlio recuperati insieme; figlio stale `Queued`/`InProgress` ⇒ riconciliato via CAS e consegnato senza riavvio; figlio `Failed` non blocca; `Cancel(padre)` cascade atomico; `MaxRuns` con figli cancellati/falliti/requeued; `RunUntil` superato ⇒ finalizzazione; coda custom conservata; entrambi gli scheduler (incluso sharded).
- Fault injection (`FaultInjectingStorage` decorator sui test): eccezione dopo l'insert del figlio e prima dell'avanzamento; commit riuscito ma `Schedule` fallito (⇒ riconciliazione senza riavvio); side effect dell'handler e crash prima del completion write (⇒ seconda esecuzione documentata, at-least-once); cursore nullo e finalizzazione nello stesso commit; `Cancel` del padre durante l'insert; kick che lancia ⇒ `TaskDeliveryRegistry` comunque rilasciato.
- Recovery: `CatchUpRecoveryIntegrationTests` (SQLite reale) — padre seminato con cursore N slot nel passato ⇒ esattamente gli slot entro `MaxAge`, dal più vecchio, finestra rispettata; riavvio a metà ⇒ nessun duplicato; `FireOnce` ⇒ un figlio con range; `Skip` ⇒ comportamento odierno; `RunUntil` scaduto nel downtime ⇒ replay degli slot `< RunUntil` + finalizzazione (X3); molti padri ⇒ budget globale rispettato.
- Multi-host (Testcontainers SQL Server, due `IHost`): una sola riga per slot; **e** un test che documenta il contratto P8 (senza lease due host possono eseguire lo stesso figlio: asserzione esplicita del limite, da invertire nell'epic lease).
- Retention: `AuditCleanupRetentionTests` — `OccurrenceRetentionDays` per `Completed/Failed/Cancelled`, con e senza audit/log; padre intatto; nessuna ri-materializzazione.

---

## 5. FASE 5 — `ITaskScheduleManager`

| File | Modifica |
|------|----------|
| `src/EverTask.Abstractions/ITaskScheduleManager.cs`, `RescheduleMode.cs` (nuovi) | S2. |
| `src/EverTask/Dispatcher/TaskScheduleManager.cs` (nuovo) | `NotSupportedException` se `!SupportsScheduleVersioning`. Sotto il lock per-taskKey del Dispatcher (`:481-491`): `GetByTaskKey` ⇒ deve essere `IsRecurring`; nuova definizione (default zona; `Validate()`); cursore per `mode` (M18: `RecalculateFromNow` ⇒ prima occorrenza dopo `now` + `BacklogDiscarded` se durevole con backlog; `RebaseFromCursor` ⇒ solo definizioni builtin con lo stesso `SchedulePeriodKind` e `ScheduleSemantics` — cron/provider rifiutati — via `TryRebaseFromNominalPeriod`, che fallisce senza sconfinare se il periodo non ha slot); azzera `RuntimeInfo.Halted`; `UpdateSchedule(...)` (versione +1); `GateInvalidation`; ri-park tramite **sostituzione latest-wins** di `Schedule()` (nessun `TryUnschedule` preventivo: niente finestra senza registrazione) — `Inline`: executor da `RecoveredTaskFactory`; `Durable`: `materializer.RunAsync`; **solo dopo** che `Schedule()` è tornato senza eccezione `ScheduleVersionRegistry.Publish(taskId, version)` (altrimenti evento `ReparkFailed`, nessuna pubblicazione). `ReevaluateSchedule` = `Reschedule` con la definizione corrente. `ResumeSchedule` = azzera l'halt + rivaluta. `RequeueFailedOccurrence`: `RequeueTerminal` + consegna allo scheduler. `CancelSchedule(taskKey)` (rimuove l'entry del registry). Evento `ScheduleRescheduled`. |
| `src/EverTask/Worker/WorkerExecutor.cs:121-126,943-1036` | `DoWorkGuarded`: subito dopo il blacklist check, un executor inline con `ScheduleVersion` inferiore a quella pubblicata viene scartato **solo se** `ScheduleVersionRegistry.TryGetLatest(taskId)` ha un'entry (assenza = nessun lower bound: mai scartare un executor recuperato al riavvio) (S4). `QueueNextOccourrence`: overload CAS quando lo storage supporta il versioning; `VersionMismatch` ⇒ rilegge e ricalcola (S1/S3). Entry del registry rimosse quando la serie termina. |
| Docs (stessa PR) | `docs/recurring-tasks/managing-tasks.md`, `idempotent-registration.md` (limite S4), cheatsheet (`## Dispatch Parameters`/nuova sezione Schedule management), reference, skill `references/05-scheduling.md:96`, `templates/RecurringRegistrar.md`, samples (endpoint admin "cambia ora"). |

**Test:** `RescheduleIntegrationTests` — padre parcheggiato (vecchio slot non scatta, nuovo sì, versione +1); `InProgress` inline (completion con `VersionMismatch` ⇒ nuova definizione); durevole con backlog (`RecalculateFromNow` scarta con evento; `RebaseFromCursor` conserva il giorno logico anche cambiando zona — caso Rome → Kiritimati — e cambiando solo l'ora); forma incompatibile ⇒ `InvalidOperationException`; taskKey inesistente/one-shot ⇒ eccezione; concorrenza `Reschedule` vs `Dispatch` vs completion (CAS); S4: executor già in coda scartato dal registry; riavvio con registry vuoto ⇒ executor recuperato **non** scartato; ri-park fallito ⇒ nessuna pubblicazione, il vecchio executor esegue una volta e l'avanzamento applica la nuova definizione; `ResumeSchedule` sblocca un `Halt` (e torna in `Halt` se ancora oltre il cap); cron/provider con `RebaseFromCursor` ⇒ `InvalidOperationException`; periodo senza slot ⇒ eccezione senza sconfinare; storage senza `SupportsScheduleVersioning` ⇒ `NotSupportedException`.

---

## 6. FASE 6 — `INextOccurrenceProvider`

| File | Modifica |
|------|----------|
| `src/EverTask.Abstractions/INextOccurrenceProvider.cs`, `NextOccurrenceRequest.cs` (nuovi) | V1 (incluso `IsDeterministic` DIM, default `false`: richiesto da `SkipOldest`). |
| `src/EverTask/Scheduler/Occurrences/OccurrenceProviderRegistry.cs` (nuovo) + `EverTaskServiceBuilder.AddOccurrenceProvider<T>(key)` | V2; risoluzione in scope per chiamata; provider sincrono-contrattuale (`> AfterUtc`, altrimenti eccezione). |
| `src/EverTask/Scheduler/Recurring/RecurringTask.cs` | `+ ProviderSettings? Provider { Key, Config }`; `Validate()` con registry (chiave registrata); esclusività. |
| `ScheduleEvaluator.cs` | Ramo provider per tutte le primitive (`CountMissedAsync` con cap, `EnumerateDueSlots` bounded). |
| `WorkerExecutor`/`Materializer`/`Dispatcher` | V4: eccezione del provider ⇒ ri-park a `now + backoff` (stato invariato), evento warning con contatore; chiave sconosciuta ⇒ `ArgumentException` al dispatch / poison in recovery. `SetOccurrenceProviderRetry(Action<...>)`. |
| Builder | `Schedule().UseOccurrenceProvider(key, config?)` (DIM). |
| Docs (stessa PR) | Nuova `docs/recurring-tasks/occurrence-providers.md`, cheatsheet, reference, skill, sample `BusinessDaysProvider`. |

**Test:** provider fake: sequenza; `null` ⇒ completata; zona/`RunNumber` nel request; risultato `<= AfterUtc` ⇒ errore; chiave non registrata (dispatch + recovery); eccezione transitoria ⇒ ri-park con backoff, nessun `Failed`, ripresa, **contatore L18 non incrementato**; transient failure seguita da crash ⇒ la recovery ritenta (V4); `ReevaluateSchedule`; provider + `CatchUp` durevole (`Halt` e `SkipOldest` per bisezione con provider forward-only); provider + skip-forward dopo downtime (cap conteggio); provider lento/cancellato; `RebaseFromCursor` rifiutato.

---

## 7. FASE 7 — Monitoring, API, UI, sweep docs, release

**Timing docs**: le docs si scrivono **nella fase che introduce la feature** (mai prima dell'implementazione — documentare API non ancora esistenti creerebbe docs false; mai rimandate alla fase 7). La fase 7 aggiunge solo: sweep di coerenza, `README.md` (paragrafo breve nella feature list + link), `docs/index.md`, sezione nuova `docs/recurring-tasks/` già creata nelle fasi 3–6. Tutto ciò che tocca `docs/` passa da `humanizer` (gate 5).

| Area | File | Modifica |
|------|------|----------|
| Monitor API | `src/Monitoring/EverTask.Monitor.Api/DTOs/Tasks/{TaskListDto,TaskDetailDto,TaskFilter,TaskCountsDto}.cs`, `DTOs/Dashboard/OverviewDto.cs`, `Services/TaskQueryService.cs:88-154`, `Controllers/TasksController.cs` | Campi `ParentTaskId`, `OccurrenceMode`, `MisfirePolicy`, `TimeZoneId`, `ScheduleVersion`, `NominalSlotUtc`; filtro `parentTaskId`/`onlyOccurrences`/`onlyCatchUp`; `GET /tasks/{id}/occurrences`; overview con backlog **per stato** (pending/active/failed/skipped) e lag; `TaskCountsDto.Occurrences`; `POST /tasks/{id}/requeue` dietro l'auth esistente (**aperto**: l'API oggi è read-only). |
| UI React | `src/Monitoring/EverTask.Monitor.Api/UI/src/types/{task,dashboard}.types.ts` («Must match backend DTOs exactly»), viste lista/dettaglio/overview | Tipi speculari; elenco occorrenze nel dettaglio del padre; badge catch-up/late; backlog nell'overview. Build con `pnpm`, verifica embedding `wwwroot`. |
| Eventi | `docs/monitoring-events.md:57-86` (già stale: manca `ExecutionLogs`), `docs/monitoring-api-reference.md:89-245` | Proprietà nuove; messaggi `OccurrenceMaterialized`, `OccurrenceSkipped`, `CatchUpStarted/Completed`, `BacklogDiscarded`, `ScheduleRescheduled`, `ProviderEvaluationFailed`. |
| Sweep docs | `docs/recurring-tasks.md`, `docs/recurring-tasks/overview.md`, `docs/index.md`, `README.md`, `docs/configuration-reference.md` ToC `:12`, `docs/architecture.md` | Coerenza finale. |
| README | `README.md` | Paragrafo breve nella feature list (durable occurrences, time zones, misfire policies, runtime reschedule) + link a `docs/recurring-tasks/durable-occurrences.md` e `time-zones.md`; via `humanizer`. |
| Skill di progetto | `.claude/skills/new-relational-storage-provider/` **e** il mirror `.agents/skills/new-relational-storage-provider/` (tenuti identici) | La skill scaffolda un nuovo provider relazionale: dopo la fase 1 DEVE includere le nuove colonne (`ParentTaskId`+FK/unique/check, `RuntimeInfo`, `ScheduleVersion`), le operazioni atomiche (`MaterializeOccurrence`, `TrySetRecurringSeriesCompleted`, `TryRequeueStaleOccurrence`, `TryHaltSchedule`, `CancelSchedule`, `RequeueTerminal`, `UpdateSchedule`, overload CAS e `nowUtc`), le due capability e la matrice di verifica per-database aggiornata — altrimenti un provider futuro nascerebbe senza il supporto occorrenze. Aggiornata **nella fase 1** (colonne/ops) e ritoccata nella fase 4 (semantiche); qui in fase 7 solo verifica finale di coerenza. |
| Plugin | `plugins/evertask/skills/integrate-evertask/` (`SKILL.md` + `references/01-setup.md`, `03-storage.md`, `05-scheduling.md`, `07-monitoring-logging.md`, `templates/RecurringRegistrar.md`) | Già coperto dalla regola anti-stale per fase; in fase 7 verifica finale: wizard decision points (calendario⇒`InTimeZone`+`OnMisfire(CatchUp)`; ora runtime⇒`Reschedule`/provider), template registrar aggiornato. |
| Release | `CHANGELOG.md` (`## [Unreleased]` → 4.0.0, includendo la voce breaking del refactor Abstractions già presente in Unreleased; precedenti `:273,:303-304`) — `Directory.Build.props` è già a 4.0.0 dalla fase 1 (decisioni §3.2, ciclo 3: la fixture di consumer compatibility deve caricare il 3.11-compiled contro assembly 4.0), CLAUDE.md locali finali, `test/EverTask.Tests/CLAUDE.md` (`FakeTimeProvider`, oracolo Cronos, fault injection) | |
| Issue | Commento su #23, 7 sub-issue, chiusura alla release; aprire le issue del registro `review/recurring-occurrences-followups.md` (F1 SQLite/UTC, F2 `SetRecurringTaskPoisoned`, F3 barriera per pagina della recovery) e l'epic F4 "distributed execution lease" (M17-A). | |

---

## 8. Matrice di test trasversale

| Proprietà | Dove è pinnata |
|-----------|----------------|
| Default byte-identico (P1) | Suite legacy senza modifiche; golden JSON; consumer compatibility fixture |
| X3 (unico cambio legacy) | Test recovery-filter dedicati (Memory + 4 provider) |
| Skip-forward == walk, anche con zona | `RecurringCalendarSkipForwardTests` + variante zona |
| Fluent API == Cronos (dalla 2ª occorrenza) | `CronOracleTests` |
| DST: gap/overlap/monotonia/collasso/mezz'ora | `DstTransitionTests` |
| Materializza + avanza + finalizza atomico; CAS | `EfCoreTaskStorageTestsBase` (4 provider) + Memory + fault injection |
| Una riga per slot con 2 host; limite P8 esplicito | Testcontainers multi-host |
| Padre: nessuna esecuzione, nessun budget rate-limit | `DurableOccurrencesIntegrationTests`, `RateLimitingIntegrationTests` |
| Backlog mai bloccato da `Failed`/stale; `MaxPendingOccurrences`; `Halt`/`SkipOldest`; liveness via retry operativo | idem + fault injection |
| Catch-up dopo downtime, entro `MaxAge`, dal più vecchio; riavvio ⇒ nessun duplicato; riconciliazione senza riavvio | `CatchUpRecoveryIntegrationTests` + fault injection |
| `Reschedule` per le occorrenze non fired; rebase del periodo nominale; CAS vs completion; registry S4 | `RescheduleIntegrationTests` |
| Provider: transitorio ≠ corrotto; crash dopo transient | test fase 6 |
| Contesto in `Handle`/callback, eager e lazy, `Attempt`, slot nominale vs reserved | `ExecutionContextIntegrationTests` |
| Eventi/record compatibili | `MonitoringWireFormatTests`, consumer compatibility |
| Clock deterministico dello scheduling (P9) | scheduler + worker + dispatcher + builder + limiter con `FakeTimeProvider`; retry fuori dominio |

## 9. Open items / mustVerifyAtImpl

- Nome stabile del constraint unique su tutti i provider (EF `HasDatabaseName`) e mapping della violazione per provider.
- `WallClock.ToUtc`: validare contro Cronos su tutte le zone dell'oracolo; se diverge, allineare a Cronos.
- Carico della materializzazione all'avvio (`LoadHarness`) con budget globale; baseline `LoadHarness` PRIMA della fase 1 e confronto dopo le fasi 1 e 4 (D7).
- `Task.Delay(delay, timeProvider, ct)` disponibile da .NET 8: verificare per ogni TFM; progettare la race segnale/delay senza leak di `Task.Delay` pendenti.
- Rebase M18: `SchedulePeriodKind` per ogni forma builtin (istante per Elapsed; giorno per Day/OnDays; settimana per Week; mese per Month; **nessuno** per cron/provider ⇒ rebase rifiutato) e matrice di compatibilità di forma.
- Bisezione `SkipOldest`: dimostrare il bound (numero di sonde × `cap + 1` conteggi); con provider non deterministici la policy è rifiutata al dispatch (`IsDeterministic == false`).
- Proc SqlServer/MySql: compatibilità delle nuove versioni con righe vecchie (`ScheduleVersion` 0, colonne NULL).
- UI: build `pnpm` e embedding `wwwroot` nel pacchetto.
- Consumer compatibility fixture: come buildare i pacchetti baseline da `issue23-baseline` e referenziarli in un progetto di test (feed locale `nupkg/`).
