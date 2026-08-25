# Fase 4 (#23) — review avversariale

> Gate 0.4 del piano (`review/recurring-occurrences-plan.md` §0, punto 4: «fasi 1, 4, 6: review avversariale
> prima del merge»), sul working tree non committato di #23, base `2b16653` (fase 3 mergiata).
> Quattro round, 2026-08-24 17:02 → 2026-08-25 06:44, più questa passata di chiusura (2026-08-25).
> **Solo review: nessuna modifica al codice sotto esame durante i round.** I fix stanno sotto, uno per finding.
>
> **Esito: nessuna finding aperta di questa review.** Le sei dell'ultimo round — 1 high, 5 medium, tutte
> contro l'albero corrente — sono state riverificate una per una contro il codice, **corrette tutte e sei** e
> pinnate da un test che fallisce senza il fix. Nessuna è stata accettata per iscritto e nessuna è stata
> rinviata.
>
> Restano **tre punti aperti**, elencati in fondo e non chiusi qui: uno chiede una decisione del maintainer
> (fix o ratifica), uno raccoglie i punti ancora aperti del gate di completezza, e uno è il follow-up F6.
> Il merge dipende da quelli, non da altre finding di questa review.

## Come è stata composta (leggere prima delle finding)

Il gate 0.4 rimanda alla skill `adversarial-review`, che fissa un floor: flock di finder su modelli diversi,
triage, scettici refute-by-default, giuria Codex separata, loop finché il giro non è a vuoto. Questa
esecuzione è stata orchestrata da `.claude/workflows/issue23-dev.js`, che implementa quel floor così:

| Stadio del floor | Cosa è stato fatto davvero |
|------------------|----------------------------|
| Finder (flock, modelli diversi) | Per round: lenti Claude (`opus`, effort `high`) sulle superfici della fase + **una lente Codex** read-only (`gpt-5.6-sol`, `model_reasoning_effort=xhigh`, `-s read-only`, `approval_policy=never`). Round 1 aggiunge la lente "minore" |
| Triage | dedup per chiave finding nel workflow; le `low` sono separate dai candidati |
| Verify (scettici, refute-by-default) | **uno** scettico per candidato (`opus`, `xhigh` su critical/high, `high` sul resto), con istruzione esplicita di refutare per default; una refutazione uccide la finding |
| Seconda giuria | non prevista come stadio separato: il round successivo riparte in modalità *focused* sui fix del precedente, che è la giuria di fatto |
| Loop until dry | **4 round**, l'ultimo dei quali ha ancora prodotto sei finding sostanziali |

Due conseguenze da tenere presenti, dichiarate e non nascoste:

1. **In `review/` sopravvive solo la traccia Codex** (`review/orchestrator/codex/f4-r1…f4-r4/`: prompt,
   `events.jsonl`, `last-message.md`). Le finding delle lenti Claude e i verdetti degli scettici vivono nel
   journal del workflow, non qui. Questo documento è quindi ricostruito **dalle tracce Codex** più la verifica
   diretta del codice fatta in questa passata. Conseguenza pratica: un `last-message.md` è una lista di
   **candidati**, non di difetti confermati — fra quella lista e il fixer c'è lo scettico, che ne uccide
   alcuni, e quali non è ricostruibile da qui.
2. **Un solo scettico per finding** è metà del floor della skill (che ne chiede ≥2 con quorum). L'assenza di
   finding su una superficie non è una prova di pulizia.

Esiti dei run Codex, come richiede il protocollo: **4 su 4 `done`** (`last-message.md` non vuoto, nessun item
`error` fatale in `events.jsonl`). Gli `stderr.log` contengono rumore ricorrente dell'ambiente (cache dei
modelli, `SKILL.md` senza frontmatter in `.agents/`, un `exec_command` rifiutato dall'helper nel round 1 e
nella certificazione) che non ha ridotto la copertura: in tutti e quattro i casi la lente ha letto il diff e
ha risposto nel merito.

---

## I quattro round, in breve

| Round | Quando | Esito della lente Codex | Dove |
|-------|--------|--------------------------|------|
| 1 | 2026-08-24 17:02 → 17:20 | 7 high, 2 medium — prima passata piena sul diff di fase 4 | `codex/f4-r1/last-message.md` |
| 2 | 2026-08-25 03:51 → 04:13 | «i difetti originari risultano chiusi nei rispettivi scenari»; 2 high, 2 medium nuovi | `codex/f4-r2/last-message.md` |
| 3 | 2026-08-25 04:44 → 05:08 | «le due correzioni sono parziali»: 1 high, 4 medium | `codex/f4-r3/last-message.md` |
| 4 | 2026-08-25 06:21 → 06:44 | 1 high, 5 medium **contro l'albero consegnato** — nessuna chiusa prima di questa passata | `codex/f4-r4/last-message.md` |

Ciò che i round 1–3 hanno prodotto è già dentro l'albero e, dove ha superato la lettera del piano o delle
decisioni, è **ratificato** in `review/recurring-occurrences-decisions.md` §3.5: i due conteggi di
`CountMissedOccurrences` (bound del chiamante, `RunUntil` escluso anche nel ramo O(1)), l'halt che non viene
ri-parcheggiato, la forma con cui R8 è stato chiuso, la regressione di allocazioni D7 e — riscritta in questa
passata — la riga sull'esito `AlreadyExists`, che descriveva ancora «un run, uno slot» mentre il codice
consegnato cammina l'intero tratto servito dentro un solo run.

---

## Round 4 — le sei finding, la verifica e il fix

Severità con la scala della lente (critical/high/medium/low). «Verifica» è la rilettura fatta in questa
passata contro il codice reale, non il verdetto dello scettico del workflow.

### 1 · [medium] La finalizzazione M6/M14 si perde quando l'ultimo run consentito atterra dopo un walk

`src/EverTask/Scheduler/Occurrences/OccurrenceMaterializer.cs` (`MaterializeAsync`) — **CONFERMATA**.

Il piano nulla il cursore quando lo slot che concede è l'ultimo run che `MaxRuns` permette: la serie deve
chiudersi **nello stesso commit** che crea quell'occorrenza (M6, M14). Se però quello slot ha già una riga, il
run non crea niente e non spende alcun run: cammina alla griglia successiva e scrive lì l'occorrenza ancora
dovuta — ma con il cursore della griglia, non con `null`. Esito: padre `Queued` con un cursore e il budget
speso, chiuso da un run successivo. Era anche **pinnato** da
`DurableOccurrencesIntegrationTests.A_slot_already_served_is_not_mistaken_for_the_last_run_the_budget_allows`,
che asseriva `Status != Completed`: un test che cristallizzava la deviazione.

**Fix.** `DueSlotPlan.RunBudgetEndsSeries` dice **perché** il cursore del piano è nullo — il budget dei run,
non la griglia — e `MaterializeAsync` applica la regola alla **scrittura** invece che allo slot: il null va
alla materializzazione che spende l'ultimo run, ovunque il walk l'abbia messa. Le altre due strade restano
com'erano: la griglia risponde per uno slot raggiunto camminando, e il piano per lo slot che ha nominato.
Distinguere le due sorgenti del null è la parte che conta: un null della griglia è un fatto **sullo slot** e
non può essere spostato su un altro.

**Test.** Lo stesso test, riscritto per pretendere la chiusura in quel commit (`Completed`, `NextRunUtc` nullo,
un solo audit di finalizzazione, e un run successivo che non trova più niente da fare) più
`DueSlotEnumeratorTests.Should_end_the_series_in_the_same_plan_that_spends_its_last_run` (ora asserisce anche
il flag) e `Should_not_claim_a_spent_run_budget_when_it_is_the_grid_that_ends`, che pinna l'altra metà.

### 2 · [high] Un handler che non si è lasciato COSTRUIRE veniva trattato come un handler che non esiste

`src/EverTask/Scheduler/Occurrences/OccurrenceMaterializer.cs` (`BuildOccurrenceFromRowAsync`) — **CONFERMATA**.

La riconciliazione era stata corretta nel round 3 per terminalizzare anche la riga il cui payload si
ricostruisce ma per cui nessuno registra più un `IEverTaskHandler<T>`. Il `catch (Exception)` che la
implementa, però, non distingue: una dipendenza scoped la cui factory lancia una `TimeoutException` durante la
risoluzione produce la stessa eccezione, e l'occorrenza finiva `Failed` — senza uno solo dei retry che la sua
policy promette, con la capacità liberata e la serie che avanza su lavoro che nessun handler ha mai visto.

**Fix.** Dopo una ricostruzione fallita si pone al container la domanda che separa i due casi — *c'è qualcosa
registrato per questo task?* — e solo un «no» è definitivo. Una risoluzione che rilancia è prova del
contrario: qualcosa c'è, ed è la costruzione ad aver fallito. Il ramo transitorio lascia la riga dov'è, non
scrive niente, logga un warning (EventId 1817) e la riprova al run successivo. Il costo di quella domanda si
paga solo sul path già fallito.

**Test.** `DurableOccurrencesIntegrationTests.An_occurrence_whose_handler_only_failed_to_activate_keeps_its_place_in_the_series`:
handler registrato la cui **prima** attivazione lancia (`ActivationFaultGate`, che fallisce dal costruttore
come farebbe una dipendenza vera), riga che resta `Queued` senza `Exception`, nessuna occorrenza creata dietro
di lei, e il run successivo che la riconcilia e la consegna allo scheduler.
Il tetto ai tentativi che questo ramo **non** ha è registrato come follow-up F6.

### 3 · [medium] La capacità veniva liberata su una scrittura che nessuno ha confermato

`src/EverTask/Scheduler/Occurrences/OccurrenceMaterializer.cs` (`ReconcileOccurrencesAsync` /
`FailUnusableOccurrenceAsync`) — **CONFERMATA**.

`ITaskStorage.SetStatus` è best-effort su ogni provider relazionale: `EfCoreTaskStorage` fa rollback, logga
`StatusUpdateFailed` e **ritorna normalmente**. Il materializer faceva `active--` subito dopo averla chiamata,
quindi una scrittura inghiottita liberava lo slot di una riga ancora viva: con `MaxPendingOccurrences = 1` due
occorrenze attive sotto un budget che ne dichiara una, ed entrambe recuperabili ed eseguibili dopo un deploy
che rende di nuovo leggibile la vecchia.

**Fix.** La riga viene riletta e lo slot si libera solo se lo stato è davvero terminale; altrimenti resta
contata attiva e un warning lo dice (EventId 1818). Una lettura in più, e solo sul path raro di una riga
inutilizzabile — che nel caso normale sparisce dalla query non-terminale al run successivo.

**Test.** `DurableOccurrencesIntegrationTests.A_status_write_that_never_landed_does_not_free_the_slot_it_was_meant_to_free`,
con `FaultInjectingTaskStorage.SwallowNext(nameof(SetStatus), 1)`: una valvola nuova che fa **ritornare** la
scrittura senza raggiungere lo store, che è esattamente la forma del guasto (un fault che lancia sarebbe un
altro test — lì il chiamante vede il fallimento).

### 4 · [medium] `MaxAge` più larga dell'intervallo rappresentabile mandava la serie in errore permanente

`src/EverTask/Scheduler/Occurrences/DueSlotEnumerator.cs` (`PlanCatchUpAsync`, `PlanFireOnceAsync`) —
**CONFERMATA**. Già segnalata nel round 2 e non chiusa allora.

`new CatchUpOptions(TimeSpan.MaxValue, 20)` passa la validazione pubblica (che rifiuta solo una finestra non
positiva) e significa «non scartare mai uno slot perché è vecchio». Calcolato alla lettera, `nowUtc - MaxAge`
lancia `ArgumentOutOfRangeException`; il materializer cattura, ri-parcheggia, e il run dopo fallisce nello
stesso punto: la serie non materializza più nulla, per sempre.

**Fix.** Una sottrazione saturante (`AgeCutoff`): una finestra più larga del calendario diventa
`DateTimeOffset.MinValue`, cioè esattamente «nessuna finestra». Vale per entrambe le policy che leggono
`MaxAge`.

**Test.** `DueSlotEnumeratorTests.Should_drop_nothing_when_the_age_window_is_wider_than_the_calendar`, teoria
su `CatchUp` e `FireOnce`, con la premessa esplicita che la superficie pubblica accetta quel valore.

### 5 · [medium] Con capacità zero si pagava comunque la bisezione che chiude il range del misfire

`src/EverTask/Scheduler/Occurrences/DueSlotEnumerator.cs` (`PlanCatchUpAsync`, `PlanFireOnceAsync`) —
**CONFERMATA**.

`LastEligibleSlotAsync` trova lo slot più recente del backlog bisecando l'asse degli istanti, e ogni sonda
cammina la griglia: su una griglia zonata è O(backlog) per sonda. Quel valore finisce **solo** nel misfire
stampato sulle righe che il piano concede — e un piano con la finestra piena non ne concede nessuna. Ogni
retry operativo di uno schedule in attesa che la sua unica occorrenza finisca pagava quindi decine di walk per
produrre un piano vuoto, tenendo intanto il gate per-schedule e un permesso del budget globale.

**Fix.** Il misfire si costruisce solo quando ci sono righe da stamparlo sopra (`slots.Count > 0`), e
`PlanFireOnceAsync` esce subito quando non può creare niente **e** non ha una finestra d'età da applicare —
gli unici due esiti possibili in quel caso sono già decisi.

**Test.** `DueSlotEnumeratorTests.Should_not_probe_a_backlog_for_a_plan_that_may_create_nothing`, che conta le
domande fatte alla griglia con un decoratore dell'evaluator reale: **una** con la finestra piena (il conteggio
che È la decisione), oltre dieci con la finestra libera — la seconda metà è la premessa, cioè la prova che la
bisezione esiste davvero e che il primo piano l'ha saltata.

### 6 · [medium] `Cancel` leggeva un errore come «nessuna occorrenza» e chiudeva solo il padre

`src/EverTask/Dispatcher/Dispatcher.cs` (`Cancel`, `OwnsPendingOccurrencesAsync`) — **CONFERMATA**.

Le due domande che `Cancel` fa sulle occorrenze sono **letture**, e degradavano qualunque errore a `false`. La
prima è solo una classificazione (la seconda la copre), ma la seconda è l'ultima parola: con un
`GetOccurrences` che fallisce, il cancel finiva normalmente avendo scritto solo il padre, e l'occorrenza che
non ha potuto vedere restava non-terminale — coperta unicamente dalla blacklist, le cui entry scadono dopo
circa un'ora, dopo di che la riga può essere eseguita come parte di una serie che l'utente aveva cancellato
(M15).

**Fix.** `OwnsPendingOccurrencesAsync` ritorna `bool?` e riporta il fallimento invece di deciderlo: `null`
lascia passare la prima classificazione alla scrittura semplice (dove la seconda domanda la copre) e fa
**cascare** la seconda. Il cascade su uno schedule senza occorrenze è la scrittura del padre, cioè quella che
sarebbe stata fatta comunque.

**Test.** `DurableOccurrencesIntegrationTests.A_cancel_whose_occurrence_lookup_fails_cascades_instead_of_calling_the_series_terminal`:
`GetOccurrences` che fallisce sempre, un'occorrenza `Queued` sotto lo schedule, e la pretesa che finisca
`Cancelled` insieme al padre.

---

## Evidenza dei gate (fase 4)

| Gate | Esito |
|------|-------|
| 0.1 — `dotnet build EverTask.slnx -c Release` | **0 warning, 0 errori**, net8.0 + net9.0 + net10.0 (2026-08-25, dopo l'ultima modifica di sorgente) |
| 0.2 — `dotnet test EverTask.slnx -c Release` (suite completa, Testcontainers inclusi) | **verde**, 0 fallimenti, ricatturata il 2026-08-25 **dopo l'ultima modifica di questo albero** (la cattura precedente era del 2026-08-24 e non copriva i sorgenti toccati dopo): `EverTask.Tests` 1801/1801 su net8, net9 e net10; `EverTask.Tests.Storage` 614 (net8, senza MySQL) / 815 (net9) / 819 (net10) con Testcontainers reali — SQL Server 2022, Postgres 16, MariaDB 10.11, `docker info` verificato prima del run; `EverTask.Tests.Monitoring` 178/182 (net8) e 182/186 (net9, net10, 4 skip SignalR); analyzer 44 × 3; logging 10 × 3 |
| 0.3 — path legacy | nessuna asserzione di un test preesistente è stata cambiata da questa passata; l'unico test modificato è di fase 4 (`A_slot_already_served_…`), che pinnava la deviazione chiusa dalla finding 1 |
| 0.4 — questa review | chiusa da questo documento |
| 0.5 — anti-stale | nessuna opzione pubblica nuova; `docs/recurring-tasks/durable-occurrences.md` aggiornata (ricostruzione transitoria vs definitiva) e passata dalla skill `humanizer`; nessuna modifica alla superficie storage, quindi le due skill `new-relational-storage-provider` restano com'erano |
| 0.6 — integration-first | i quattro test nuovi girano su host reale + storage reale (tre) e sull'evaluator reale (uno); nessun mock rigged. La valvola `SwallowNext` decora uno storage vero, non lo sostituisce |
| 0.7 — performance | i fix 3 e 5 **tolgono** lavoro (una bisezione per retry a finestra piena) o ne aggiungono su un path già fallito (una lettura per riga inutilizzabile); nessun round-trip nuovo sui hot path |

## Punti aperti che questa passata NON chiude

Elencati perché il gate 0.3 chiede che una deviazione sia ratificata o spostata, non taciuta.

1. **`MaxOccurrences` viene valutato senza guardare il budget residuo di `MaxRuns`** — candidato del round 1
   (finding 2), che nell'albero **non risulta chiuso**: o lo scettico l'ha refutato (i verdetti non sono in
   `review/`, vedi sopra) o non è mai stato portato. Misurato su questo albero: griglia al minuto,
   `MaxRuns = 10` con `CurrentRunCount = 9`, backlog di 100 slot, `MaxOccurrences = 5`, `Halt` ⇒ il piano
   risponde `StopReason = Halted`, `DetectedAtLeast = 101`, zero slot. La serie aveva un solo run da spendere:
   crearlo e completarsi è l'altra lettura possibile, e nessuna delle due è scritta nelle decisioni (M10
   definisce il cap sugli slot **eleggibili**, M14 dice che `MaxRuns` conta le materializzazioni).
   **Serve una decisione del maintainer**: fix (l'overflow guarda il budget residuo) o ratifica in §3.5
   (l'episodio si valuta sulla griglia, indipendentemente da quanti run restano). Non è stato toccato qui
   perché cambiarlo sposta la semantica di una policy pubblica sulla base di una lettura, non di una regola
   scritta.
2. **Punti del certificatore di completezza Codex** (`review/orchestrator/codex/cert-f4/last-message.md`,
   2026-08-24 23:13) ancora aperti in albero: M1 (`MisfireThreshold` non entra nel planner — o si implementa o
   si ratifica che è **solo osservazione**, come dicono oggi cheatsheet, reference, `docs/task-creation.md` e
   la skill); la perdita da `SkipOldest` riportata con la causa di `MaxAge` («outside the misfire window»); il
   test mancante di uno scheduler custom con `SupportsScheduleInspection == false`; i test negativi delle
   validazioni pubbliche (`MaxAge <= 0`, `MaxOccurrences < 1`, `MaxPendingOccurrences < 1`,
   `SetMaterializationConcurrency(< 1)`, `SetBacklogRetryInterval(< 1 s)`). Il quinto punto di quella lista
   (XML-doc di `SetBacklogRetryInterval` contro la ratifica dell'halt) risulta **chiuso**. Appartengono al
   gate di completezza, non a questo.
3. **F6** — la ricostruzione di una riga occorrenza non ha un tetto di tentativi: conseguenza scelta del fix
   della finding 2, registrata in `review/recurring-occurrences-followups.md`.

### Esito dei punti aperti (2026-08-25, ciclo di completamento + ratifica del maintainer)

1. **Fix**: `Halt` scatta solo se `remainingRuns > MaxOccurrences` (`DueSlotEnumerator.PlanCatchUpAsync`); una
   serie con un run residuo lo spende e chiude. Registrato in decisions §3.5.
2. **Chiusi tutti**: M1 implementato (`SetMisfireThreshold` nel planner, con la metà «episodio di più slot
   sempre riportato» ratificata), perdita `SkipOldest` con reason code proprio (`SlotLossReason`), test dello
   scheduler senza `SupportsScheduleInspection`, test negativi delle validazioni pubbliche.
3. **F6** resta in `review/recurring-occurrences-followups.md`.

