# Adversarial review — Calendari di esclusione (#36, tranche 2)

| | |
|---|---|
| **Scope** | Diff calendari #36 tranche 2 — `5579959..d68e15d` |
| **Profilo** | Rapido — 4 lenti Opus + Codex Light, round singolo |
| **Lenti** | `rebuild-validation`, `lifetime-seam`, `spec-tests`, `parity-perf` (+ passata Codex Light) |
| **Esito** | 1 finding **confermato** (P2), 6 **refutati** (1 P1, 5 P2), 0 under-verified |
| **Fix applicati** | **Nessuno** — il report è solo diagnostico |

Riferimenti normativi usati come oracolo: `review/recurring-exclusion-calendars-spec.md` (§3.1, §4.1, §4.3, §5.2, §5.3, §5.4, §7),
`src/EverTask/Scheduler/Recurring/CLAUDE.md` (gotcha 18/20), `CLAUDE.md` di root (contratto XML sulla superficie pubblica).

---

## 1. Findings confermati

### P2

| # | File:line | Invariante | Scenario | Voti | Fix (sintesi) |
|---|---|---|---|---|---|
| 1 | `src/EverTask/Worker/WorkerExecutor.cs:2011` | spec §4.3 + §7 — *ogni* percorso di row-rebuild (recovery, schedule retry, materializer, **repark**) deve rifiutare/avvelenare un nome di calendario sconosciuto invece di eseguire una volta. §4.3 nomina esplicitamente `ReparkFromRowAsync` come «l'ultimo che oggi deserializza senza validare». | Rimuovendo il `ScheduleValidationContext` passato a `RecoveredTaskFactory.FromRow` (arg. **opzionale**, `RecoveredTaskFactory.cs:58` → la rimozione **compila**) e riportando la creazione dello scope sotto il null-guard, **tutta la tranche resta verde**: una riga che nomina `"removed-holidays"` viene riparcheggiata, `scheduler.TrySchedule` riesce e lo schedule spara il cursore memorizzato una volta con il calendario mai risolto. | 2/2 skeptic *non refutato*, confidence **high** + **high** | Tre test sul pattern `MemoryTaskStorage` già usato dalle suite reschedule/durable: (a) riga ricorrente inline con calendario non registrato + advance con CAS persa → assert che la riga **non** venga registrata sullo scheduler e che sia emesso `ScheduleReparkFromRowFailed`; (b) stesso guard sul percorso di schedule-retry (`ScheduleRetryAbandoned`); (c) stesso guard sul walk del materializer. |

**Evidenza raccolta dai due skeptic (concorde):**

- Il wiring non è retto da alcun contratto: `RecoveredTaskFactory.FromRow(row, ScheduleValidationContext? validationContext = null)` —
  parametro opzionale, quindi la regressione è silenziosa e compilante.
- `rg` su `test/` non trova alcuna occorrenza di `ScheduleReparkFromRowFailed` / `ScheduleReparkedFromRow` / `ScheduleRetryAbandoned`.
- Gli unici due punti in cui un test costruisce una definizione che nomina un calendario **assente dal registry** sono
  `RecoveredTaskFactoryTests.cs:197-217` (il contesto lo passa **il test stesso**: dimostra che l'helper valida, non che un chiamante gli passi il contesto)
  e `WorkerServiceRecoveryPoisonTests.cs:91-165` (solo percorso di recovery via `RecoveryHarness.CreateRecoveryService(...).ProcessPendingAsync()`).
- Tutti gli altri test con calendari registrano il nome (`DurableOccurrencesIntegrationTests.cs:141,176,197,210`; il test exclusion-retry di `RescheduleIntegrationTests`)
  oppure coprono solo ingress/recovery (`DispatcherTests.cs:140`, `ScheduleManagementValidationTests.cs:230`).

**Natura del finding:** è un buco di *pinning*, non un difetto di runtime — il codice spedito è corretto, ma il guard più
delicato dei quattro (l'unico che la spec ha dovuto nominare a mano perché mancante prima della tranche) non è trattenuto
da nessun test. Rischio: regressione futura a costo zero di compilazione, con esecuzione singola non voluta di uno slot escluso.

### Under-verified

Nessuno. Tutti e 7 i finding trattenuti hanno chiuso con due voti concordi.

---

## 2. Findings refutati

| # | Titolo | Sev. dichiarata | File:line | Motivo della refutazione | Voti |
|---|---|---|---|---|---|
| R1 | Risoluzione calendari pagata **per probe** della griglia e non per pass: planner e materializer chiamano l'evaluator in loop | P1 | `src/EverTask/Scheduler/Recurring/ScheduleEvaluator.cs:132` | **Invariante non violato.** Spec §5.2 (`recurring-exclusion-calendars-spec.md:196-204`) dice «una risoluzione per *top-level evaluator call* — **non per door probe**», e *door probe* è termine tecnico del codebase: i candidati iterati dentro `RecurringTask.FilterExcludedCandidates` (`RecurringTask.cs:641-655`, budget `MaxExclusionSearchIterations` = 200k). Il codice risolve esattamente **una volta per metodo pubblico dell'evaluator** e le door probe girano sull'unione già appiattita. Il finding ridefinisce «top-level call» come «pass» e poi contesta la propria riscrittura. Lo scenario proposto (`CatchUp(maxAge:30d, maxOccurrences:500).SkipOldest()`, outage di 3 settimane, ~900 date + ~100 finestre) non riproduce le magnitudini dichiarate. | 2/2 refuted, **high** + **high** |
| R2 | I fallimenti di registrazione calendario non nominano né il calendario né il concetto giusto, e contraddicono il contratto XML di `AddScheduleCalendar` | P2 | `src/EverTask/MicrosoftExtensionsDI/EverTaskServiceConfiguration.cs:517` | **Guard preesistente.** Lo scenario di punta (bounds di `Between` invertiti → messaggio «An exclusion range start must be earlier than its end. (Parameter 'Exclusions')») **non può accadere**: `ExclusionBuilder.Between` (`ExclusionBuilder.cs:26-34`) lancia già `ArgumentException("The exclusion window start must be earlier than its end.", nameof(from))` **sul call-site**, dentro la callback dell'operatore, con `paramName` reale e stack-top sulla riga `.Between` scritta dall'host. `IExclusionBuilder` è l'unica superficie esposta da `AddScheduleCalendar` (unico overload, `EverTaskServiceConfiguration.cs:498`, nessun overload su `ScheduleExclusions` in `src/`), quindi non esiste altro modo di aggiungere un range. Gran parte dell'evidenza citata è irraggiungibile dall'entry point accusato. | 2/2 refuted, **high** + **high** |
| R3 | Lo snapshot congelato dei calendari è registrato **prima** dell'abort guard di `AddEverTask`, e vince first-wins via `TryAddSingleton` | P2 | `src/EverTask/MicrosoftExtensionsDI/ServiceCollectionExtensions.cs:47` | **Fatti meccanici verificati** (righe 47-48 registrano lo snapshot prima del guard assembly a 50-53; è l'unica registrazione che precede il guard; nessun consumer riconcilia lo snapshot con una configurazione successiva). Cade la **premessa di sfruttabilità**: l'unica sequenza fallimentare richiede che l'host **catturi** l'`ArgumentException("No assemblies found to scan...")` che `AddEverTask` documenta nel proprio `<exception>` come misconfigurazione fatale del composition root, e **poi** re-invochi `AddEverTask` sulla stessa `IServiceCollection`. Non è un difetto raggiungibile per uso corretto. | 2/2 refuted, **high** + **medium** |
| R4 | Il rifiuto di esclusioni inline in `ScheduleRebase` è stato **sostituito** dalla forma calendario, perdendo copertura certificata di tranche 1 | P2 | `test/EverTask.Tests/RecurringTests/ScheduleRebaseTests.cs:27` | **Nessuna copertura persa.** `ScheduleExclusions` è un unico container sealed piatto (Days/Dates/Ranges/Calendars) e `RequireSameShape` (`ScheduleRebase.cs:229`) è un puro null-test su quel container (`current.Exclusions != null \|\| replacement.Exclusions != null`): `Calendars = ["holidays"]` e `Days = [Sunday]` sono lo **stesso oggetto**, lo stesso non-null, lo **stesso ramo**, lo stesso messaggio asserito, su entrambi i lati della `[InlineData(true/false)]` rimasta intatta. Copertura di riga, ramo e comportamentale identiche; nessun repro costruibile sul codice attuale (lo ammette il finding stesso). Resta valido solo come *suggerimento* di hardening. | 2/2 refuted, **medium** + **high** |
| R5 | Il test forward-only sul «calendario ristretto» non restringe nulla: è una tautologia che non può fallire | P2 | `test/EverTask.Tests/RecurringTests/RecurringExclusionMathTests.cs:65` | **Refutato su tre fronti indipendenti.** (a) Lo scenario di falsificazione è autolesionista: la riga 80 è `afterCursor.ShouldBe(Utc(2026,12,26,0))`, un'uguaglianza esatta su un ritorno a valore singolo — un'implementazione che «ri-deve» 12-25 ritorna 12-25 e il test **fallisce**. (b) Il mutante concreto proposto (ancorare la probe a `cursor - period`, ritornare la prima risposta) non produce alcun rewind su questi dati: `FirstOccurrenceAfterBackfillProbe` (`RecurringTask.cs:993-1030`) arretra esattamente un `GetMinimumInterval`. (c) Il test **è** falsificabile in almeno tre modi (normalizzazione strettamente-successiva; griglia a giorni senza backward probe; `NormalizeCursorAsync` che perda `ResolveCalendars` → `FilterExcludedCandidates` a `RecurringTask.cs:636-641` **lancia** su `Calendars` non risolti). | 2/2 refuted, **high** + **medium** |
| R6 | Il normalizer estratto alloca a ogni `Validate()` di uno schedule senza calendari, dove tranche 1 non allocava | P2 | `src/EverTask/Scheduler/Recurring/ScheduleExclusionNormalizer.cs:22` | **Guard preesistente + baseline sbagliata.** `RecurringTask.ValidateExclusions` (`RecurringTask.cs:221-240`) apre con `if (Exclusions is not { } exclusions) return;` e la chiamata a `ScheduleExclusionNormalizer.Normalize(exclusions)` sta alla riga 232, **dopo** l'early return; `RecoveredTaskFactory.cs:85-94` chiama `Validate` solo se `!string.IsNullOrEmpty(row.RecurringTask)`. Lo scenario dichiarato (backlog di 200k righe dove «ognuna» paga ~4 gen0 in più) è quindi **falso**: righe senza esclusioni — e righe non ricorrenti — colpiscono lo stesso early return di tranche 1. Anche l'affermazione di parity («tranche 1 non allocava nulla») è factualmente errata. Il fix suggerito (`if (names.Length == 0) return names;`) resta una micro-ottimizzazione legittima ma non una regressione. | 2/2 refuted, **high** + **high** |

---

## 3. Clustering per root cause

### C1 — Pinning dei quattro guard di row-rebuild (§4.3) — **1 confermato, 2 refutati**

Finding: **#1 (confermato)**, R4, R5.

La tranche 2 estende la validazione del rebuild a tutti e quattro i percorsi ma la suite li pinna in modo **asimmetrico**:
recovery e ingress sono coperti, repark / schedule-retry / materializer no. Le altre due segnalazioni della stessa lente
(R4: swap in-place del pin su `ScheduleRebase`; R5: test forward-only sul narrowing) puntano allo stesso sospetto — «i test
seguono la forma nuova e lasciano scoperta quella vecchia» — ma solo #1 sopravvive alla verifica, perché è l'unico dove
esiste davvero un ramo di produzione senza alcun test che lo trattenga. R4 e R5 muoiono sul fatto che il guard sottostante
è *shape-blind* (un null-test su un container sealed) e che l'asserzione contestata è un'uguaglianza esatta.

**Root cause del cluster:** la validazione è stata centralizzata in `RecoveredTaskFactory` con un parametro **opzionale**
(`ScheduleValidationContext? = null`). Un contratto opzionale non genera errore di compilazione quando un chiamante smette
di passarlo, e i test seguono i percorsi storici (recovery) invece dei percorsi nuovi. Il fix strutturale, oltre ai tre test,
sarebbe rendere il contesto obbligatorio dove il rebuild è di produzione.

### C2 — Superficie di registrazione al composition root — **2 refutati**

Finding: R2, R3.

Entrambi attaccano `AddScheduleCalendar` / `ServiceCollectionExtensions` da direzioni opposte (qualità del messaggio d'errore;
ordine di pubblicazione dello snapshot). Entrambi cadono per lo stesso motivo: **la superficie è più stretta di quanto le lenti
abbiano assunto**. `IExclusionBuilder` è l'unico ingresso e valida sul call-site (R2); la `ArgumentException` del guard assembly
è documentata e fatale, non catturabile in un flusso corretto (R3). Nessun cambiamento richiesto; R3 resta un *hardening*
a costo zero (spostare le righe 45-48 dopo il guard) qualora si voglia eliminare l'ordine fragile a prescindere dalla sfruttabilità.

### C3 — Modello di costo della risoluzione calendari — **2 refutati (di cui l'unico P1)**

Finding: R1 (P1), R6.

Entrambi contestano il costo introdotto dal layer calendari e entrambi sbagliano il **baseline**. R1 riscrive la definizione
di «top-level evaluator call» della spec §5.2 in «pass» e poi contesta la violazione della propria riscrittura; R6 asserisce
un baseline di tranche 1 a zero allocazioni che non esiste, ignorando l'early return che rende il normalizer irraggiungibile
per gli schedule senza esclusioni. Segnale utile per il futuro: la spec §5.2 va letta con il glossario del codebase
(*door probe* = candidato dentro `FilterExcludedCandidates`, budget 200k), non con l'intuizione.

---

## 4. Coverage

**Lenti fallite:** *nessuna*. Le 4 lenti Opus (`rebuild-validation`, `lifetime-seam`, `spec-tests`, `parity-perf`) hanno tutte
completato, più la passata Codex Light.

**Triage:** `10 → kept 7, dropped 0 (declared), dups 3, failed batches 0`.
Nessun finding scartato per merito in triage: le 3 rimozioni sono duplicati collassati sul rappresentante più forte
(due varianti del cluster C3 sul costo per-probe, una del cluster C1 sul pinning del rebuild).

**Limiti dichiarati del profilo Rapido:**

- **Nessun completeness round.** Non è stata eseguita la passata che cerca *ciò che nessuna lente ha guardato*: aree fuori
  dalle 4 lenti (per esempio serializzazione/round-trip dei nomi di calendario su storage, comportamento cross-provider EF,
  superficie del dashboard/monitoring) non sono state coperte da questo run.
- **Nessun second-jury.** Ogni finding ha chiuso con **2 voti** di skeptic; non è stata convocata una seconda giuria sui
  verdetti di confidence `medium` (R3 e R5 hanno un voto `medium` ciascuno, R4 ne ha uno `medium` — restano refutati
  ma con margine di dubbio più ampio degli altri).
- **Round singolo.** Nessuna iterazione di follow-up sui refutati; i suggerimenti di hardening emersi da R3 (ordine dello
  snapshot), R4 (matrice di forme sul rebase) e R6 (early return su `NormalizeCalendarNames`) non sono stati riesaminati.

**Fix applicati:** nessuno. Nessun file di `src/` o `test/` è stato modificato da questa review.

---

## 5. Raw

Il verdetto completo, voto per voto (reasoning integrale degli skeptic, scenari e fix non troncati), è in:

```
E:/Archivio/Sviluppo/Web/EverTask/review/recurring-calendars-adversarial-review.raw.json
```
