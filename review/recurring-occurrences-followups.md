# Follow-up esterni a #23 — registro

> Aperto il 2026-08-23 alla chiusura della **fase 1** di [#23](https://github.com/GiampaoloGabba/EverTask/issues/23).
> Qui stanno i punti che le review di #23 hanno trovato **fuori dallo scope dell'issue**: preesistenti, o su
> path che #23 non può toccare. Le voci che restano **dentro** #23 non stanno qui: sono nella disposizione
> `review/recurring-occurrences-decisions.md` §3.2 e, quando il fix appartiene a una fase successiva, nel
> piano di quella fase.
>
> **Tutte le voci sono state aperte come issue il 2026-08-26**, alla chiusura della fase 7 (piano §7, riga
> «Issue»). La colonna Stato porta il numero; il testo qui sotto resta la traccia in albero di ciascuna, ed è
> ciò da cui il corpo dell'issue è stato scritto.

| # | Titolo dell'issue | Origine | Stato |
|---|--------------------|---------|-------|
| F1 | `SQLite: normalize DateTimeOffset values to UTC at the public storage entry points` | Review fase 1, R13 | [#37](https://github.com/GiampaoloGabba/EverTask/issues/37) |
| F2 | `SetRecurringTaskPoisoned swallows its own errors: the recovery summary reports a terminalization that never happened` | Review fase 1, R10 | [#38](https://github.com/GiampaoloGabba/EverTask/issues/38) |
| F3 | `Recovery: the per-page Task.WhenAll barrier makes idle queues wait for a slow delivery` | Review fase 1, sezione «Confutate» | [#39](https://github.com/GiampaoloGabba/EverTask/issues/39) |
| F4 | `Epic: distributed execution — cross-host lease/claim` | Decisioni #23, M17-A / D5 | [#31](https://github.com/GiampaoloGabba/EverTask/issues/31) |
| F5 | `MemoryLeakRegressionTests: two tests share the probe's static counters and dirty each other` | Certificazione fase 2 (suite completa, net10.0) | [#40](https://github.com/GiampaoloGabba/EverTask/issues/40) |
| F6 | `Durable occurrences: rebuilding a row has no attempt ceiling, unlike recovery` | Review avversariale fase 4, round 4 (finding 2) | [#41](https://github.com/GiampaoloGabba/EverTask/issues/41) |
| F7 | `Monitoring API: management endpoints (requeue, resume, cancel) behind an authorization model of their own` | Fase 7, punto aperto del piano §7 chiuso su «no» | [#42](https://github.com/GiampaoloGabba/EverTask/issues/42) |
| F8 | `BackwardCompatibilityScheduleDriftTests: the CurrentRunCount assertion races the write that produces it` | Suite completa della fase 7 (net8.0, 1 run su 2) | [#43](https://github.com/GiampaoloGabba/EverTask/issues/43) |
| F9 | `Monitoring API: page the two audit trails of the task detail` | Review fase 7, round 2 (finding #2), per la sola metà rimasta aperta | [#44](https://github.com/GiampaoloGabba/EverTask/issues/44) |
| F10 | `SqlServerEfCoreTaskStorageTests: the deadlock-victim test fails when the engine does not collide under load` | Suite completa della fase 7, chiusura dei gap (net9.0 e net10.0), per la sola metà rimasta aperta | [#45](https://github.com/GiampaoloGabba/EverTask/issues/45) |

---

## F1 — SQLite: normalizzare a UTC i `DateTimeOffset` all'ingresso pubblico dello storage

**Origine.** `review/recurring-occurrences-phase1-adversarial-review.md`, R13 (confermata dal secondo
scettico nel round 2). Disposizione: decisioni §3.2, accettata per iscritto in fase 1.

**Il fatto.** SQLite tiene un `DateTimeOffset` come TEXT con l'offset scritto dentro, quindi l'uguaglianza è
sensibile alla rappresentazione: `10:00+02:00` e `08:00+00:00` sono lo stesso istante e due stringhe diverse.
Le tre operazioni compare-and-swap che confrontano il cursore (`MaterializeOccurrence`,
`TrySetRecurringSeriesCompleted`, `TryHaltSchedule`) e ogni confronto di slot passano da quella uguaglianza.
È la stessa limitazione che costringe `SqliteTaskStorage` a filtrare `RunUntil` client-side.

**Perché non in #23.** La pipeline di dispatch normalizza sempre a UTC, quindi la riga anomala non nasce da
lì: serve una scrittura che passi da `Persist` o `UpdateTask` con un offset diverso da zero — API storage
pubblica, cioè **path legacy**. P1 e il gate 3 della fase 1 vietano di cambiarlo.

**Direzione.** Normalizzare a UTC all'ingresso di `Persist` / `UpdateTask` (e nel `SqliteTaskStorage` in
particolare), oppure dare a SQLite un confronto di cursore identity-preserving nelle tre CAS. La prima è il
fix vero e vale per tutti i provider; la seconda cura solo il sintomo.

**Test da portare con il fix.** Una riga scritta con offset `+02:00` e riletta/confrontata con lo stesso
istante espresso in UTC: le tre CAS devono combaciare.

---

## F2 — `SetRecurringTaskPoisoned` inghiotte i propri errori: il sommario della recovery mente

**Origine.** Review fase 1, R10. **Preesistente**: la fase 1 aggiunge un call site, non il comportamento.

**Il fatto.** `EfCoreTaskStorage.SetRecurringTaskPoisoned` cattura e logga la propria eccezione senza
rilanciarla. `WorkerService` incrementa comunque `permanentFailures` e logga la riga come avvelenata, mentre
la riga resta recuperabile e ripete lo stesso ciclo a ogni riavvio. La direzione è sicura — nessuna riga si
perde — ma il sommario dichiara una terminalizzazione che non è avvenuta.

**Direzione.** O la scrittura rilancia e il chiamante conta il fallimento per quello che è, o il chiamante
verifica l'esito prima di dichiarare la riga avvelenata. La seconda non richiede di cambiare la famiglia di
rethrow dello storage.

---

## F3 — Recovery: la barriera `Task.WhenAll` per pagina fa aspettare le altre code

**Origine.** Review fase 1, sezione «Confutate»: **non** una regressione della fase 1 (la barriera per pagina
c'era identica prima del diff; il raggruppamento per coda è solo stato spostato dentro `RecoverWaveAsync`),
ma un limite reale.

**Il fatto.** `ProcessPendingAsync` aspetta il completamento di **tutta** la pagina prima di chiedere la
successiva. Il fan-out per coda impedisce a una coda satura di occupare gli slot delle altre *dentro* la
pagina, ma non impedisce a una consegna lenta di ritardare la pagina successiva, e quindi la recovery delle
righe di code completamente idle.

**Direzione.** Una pipeline: leggere la pagina successiva mentre la corrente è in volo, con un tetto di
pagine in volo. Attenzione all'interazione con la barriera M7 (figli prima dei padri), che oggi si appoggia
al fatto che la paginazione è finita quando parte la seconda ondata.

---

## F4 — Distributed execution lease (epic)

**Origine.** Decisioni #23, M17 opzione A / D5: 4.0 spedisce il contratto **single-active-host**, e la
distribuzione diventa un'epic separata che la attiva per **tutto** EverTask, non solo per le occorrenze.
Lo sketch (colonne `ExecutionLeaseOwner/Epoch/ExpiresAtUtc`, `TryClaimExecution` che **sostituisce**
`SetInProgress` con fencing epoch, claim dopo il gate rate-limit, heartbeat, predicato recovery lease-aware,
reaper delle lease scadute, clock del DB per le scadenze) è in M17, con i seam da preservare.

Il piano §7 la elenca già fra le issue da aprire alla release; è qui per tenere un solo elenco.

---

## F5 — `MemoryLeakRegressionTests`: due test condividono i contatori statici della probe

**Origine.** Suite completa della certificazione della **fase 2**: `EverTask.Tests` su **net10.0** ha riportato
un fallimento (`1385/1386`), net8.0 e net9.0 verdi. Il test è
`MemoryLeakRegressionTests.Should_resolve_and_dispose_fresh_handler_per_execution_for_immediate_tasks`, che la
fase 2 **non tocca**.

**Preesistente, verificato.** Lo stesso fallimento si riproduce identico sulla baseline pre-fase-2 (worktree
staccato su `0c5d77a`, quindi senza una riga del diff della fase 2) eseguendo i due test come coppia:

```
dotnet test test/EverTask.Tests/EverTask.Tests.csproj -c Release -f net10.0 \
  --filter "FullyQualifiedName~MemoryLeakRegressionTests.Should_dispose_dispatch_time_metadata_handler_when_dispatching_immediate_task|FullyQualifiedName~MemoryLeakRegressionTests.Should_resolve_and_dispose_fresh_handler_per_execution_for_immediate_tasks"
```

**Non è quindi una regressione della fase 2**, ed è la ragione per cui non è stato corretto qui: la
correzione tocca un test di regressione **pinnato** senza avere un difetto di prodotto da correggere.

**Il fatto.** I due test condividono i contatori **statici** di `TestTaskMem2DisposeProbeHandler`
(`Created`/`Disposed`/`Executed`) e si coordinano solo con `Reset()`. Il primo
(`Should_dispose_dispatch_time_metadata_handler_when_dispatching_immediate_task`) dispaccia un task immediato
su un host **mai avviato**, quindi lascia dietro di sé una consegna che nessun consumer ritira. Quando i due
girano in sequenza, il secondo osserva **`Created=3, Disposed=3`** invece di `2/2` — una risoluzione
dispatch-time in più, che arriva da un `Dispatcher.ExecuteDispatchCore` che non è il suo (verificato con
stack trace sulla costruzione della probe) — e l'attesa `Disposed == 2` scade.

**Perché non si vede sempre.** Dipende dall'ordine in cui xUnit esegue i test della classe, che cambia con il
layout dell'assembly: una ricompilazione ha rimesso la classe verde su net10.0 senza toccare né il test né il
prodotto. È un flake latente, non un fallimento deterministico — il che lo rende peggiore, non migliore: si
ripresenterà a caso su qualsiasi fase futura.

**Si è ripresentato**, come previsto: alla chiusura dei gap della fase 7 (2026-08-26) ha fallito su **net10.0**
in una delle tre esecuzioni complete della soluzione, verde nelle altre due e su tutti gli altri TFM.

**Direzione.** Togliere lo stato condiviso invece di allargare i timeout: dare al test dispatch-time una
propria coppia task+handler probe (i contatori statici smettono di incrociarsi), oppure far asserire a
entrambi i **delta** rispetto a uno snapshot iniziale invece dei valori assoluti. La prima è più semplice e
non cambia una sola asserzione delle due esistenti.

---

## F6 — Occorrenze: la ricostruzione di una riga non ha un tetto di tentativi

**Origine.** Review avversariale della **fase 4**, round 4, finding 2
(`review/recurring-occurrences-phase4-adversarial-review.md`). Nasce **dal fix** di quella finding, non dal
codice che la precedeva: è il costo che il fix ha scelto di pagare.

**Il fatto.** La riconciliazione distingue ora due esiti di una ricostruzione fallita: l'handler non è
registrato (verdetto definitivo ⇒ `Failed`) oppure l'handler c'è e **non si è lasciato costruire** (fallimento
transitorio ⇒ l'occorrenza resta dov'è e il run successivo riprova). La seconda strada non ha un tetto: un
handler registrato il cui costruttore lancia **sempre** — una dipendenza scoped configurata male, non una
indisponibilità momentanea — tiene la sua occorrenza non-terminale per tutta la vita del processo, e con il
budget di default (`MaxPendingOccurrences = 1`) la serie non materializza più nulla. Ogni run lascia un
warning (EventId 1817), quindi la situazione è visibile, ma nessuna scrittura la chiude.

**Perché la scelta è questa.** L'alternativa — terminalizzare — è ciò che il round 4 ha classificato high: un
timeout di una connection factory finiva l'occorrenza `Failed` senza uno solo dei retry che la sua policy
promette, e il lavoro tornava solo con un requeue amministrativo. Fra «perdere lavoro per un guasto che
passa» e «fermare una serie finché qualcuno guarda i log» la seconda è l'unica conservativa.

**Direzione.** Un contatore di tentativi di ricostruzione per riga, con la stessa forma del contatore L18
della recovery (`IncrementRecoveryFailure` / `ClearRecoveryFailure`, poison terminale alla soglia): dopo N run
consecutivi in cui la riga non si lascia ricostruire, il verdetto diventa definitivo e l'occorrenza va
`Failed` come le altre. La colonna esiste già ed è per riga, quindi non serve nuova superficie storage.

**Test da portare con il fix.** Un handler che non si attiva mai: N run, poi la riga è `Failed` e la serie
riparte; e uno che si attiva al secondo tentativo, che deve restare non-terminale e poi essere riconsegnato
allo scheduler (il test che oggi pinna il ramo transitorio,
`DurableOccurrencesIntegrationTests.An_occurrence_whose_handler_only_failed_to_activate_keeps_its_place_in_the_series`).

---

## F7 — Monitoring API: endpoint di gestione dietro un'autorizzazione propria

**Origine.** Piano §7, riga «Monitor API»: `POST /tasks/{id}/requeue` «dietro l'auth esistente (**aperto**:
l'API oggi è read-only)». La fase 7 ha chiuso il punto su **no** e l'ha ratificato (decisioni §3.8).

**Perché no in 4.0.** L'unica autenticazione che l'API ha è la coppia utente/password del dashboard, cioè una
credenziale di **sola lettura** condivisa da chiunque guardi la dashboard. Un requeue rimette in esecuzione un
handler con effetti collaterali; farlo passare per quella credenziale significa decidere la policy di
autorizzazione al posto del consumatore. La strada raccomandata esiste già ed è `ITaskScheduleManager`,
chiamato dall'applicazione dietro la sua autorizzazione — è ciò che il sample mostra.

**Cosa servirebbe per dire sì.** Non un endpoint: un modello di autorizzazione. Un secondo ruolo (lettura vs
operazione) o un hook `Func<HttpContext, Task<bool>>` che l'host popola, più una scelta esplicita su CSRF per
le chiamate che partono dalla SPA, più la decisione se `CancelSchedule` e `ResumeSchedule` seguano la stessa
strada (sono le altre due operazioni che un operatore vuole davvero dalla dashboard: un catch-up in halt si
rilascia solo così). Aprire il solo requeue senza quel modello è la parte facile della domanda.

**Direzione.** Un'issue che parte dall'autorizzazione, non dagli endpoint, e che li abilita tutti e tre
insieme dietro un ruolo separato — con `EnableManagementEndpoints` spento di default, così un host che
aggiorna non guadagna una superficie di scrittura senza averla chiesta.

---

## F8 — `BackwardCompatibilityScheduleDriftTests`: l'asserzione su `CurrentRunCount` corre contro la scrittura che la produce

**Origine.** Suite completa della **fase 7**: `EverTask.Tests` su **net8.0** ha riportato un fallimento
(`2014/2015`) in una delle due esecuzioni complete, con net9.0 e net10.0 verdi **nello stesso run** e tutte e
tre verdi nel run precedente sugli stessi binari. Il test è
`BackwardCompatibilityScheduleDriftTests.Old_Serialized_Recurring_Task_Should_Deserialize_And_Reschedule`, che
la fase 7 **non tocca** (l'unica modifica a `src/EverTask` della fase è un attributo `InternalsVisibleTo`).

**Il fatto, e non è un timeout.** L'attesa è

```csharp
await TaskWaitHelper.WaitForConditionAsync(
    () => StateManager.GetCounter(nameof(TestTaskRecurringSeconds)) >= 1, timeoutMs: 5000);
```

e `WaitForConditionAsync` **lancia** allo scadere, quindi il fallimento non è la scadenza: la condizione è
stata soddisfatta. Il contatore però è incrementato **dentro `Handle`**, mentre `CurrentRunCount` è scritto
dal worker **dopo** che l'handler è tornato (`QueueNextOccourrence` → `CompleteRecurringRun`), che è un
round-trip di storage più in là. La lettura successiva (`Storage.GetAll()`) può quindi cadere fra i due, e
`updatedTask.CurrentRunCount` vale ancora `0`. Sotto carico — tre TFM in parallelo, più le suite storage con
i container — quella finestra si allarga.

**Perché non qui.** È un difetto del **test**, sul path inline legacy, e la sua asserzione è esattamente ciò
che il gate 3 del piano di #23 vieta di adattare («le suite legacy passano **senza modifiche**»). La classe è
già passata dall'audit dei flaky (issue #35, chiusa): quel giro ha reso deterministiche le ATTESE, non questa
asserzione.

**Non si è ripresentato** nelle tre esecuzioni complete della chiusura dei gap della fase 7 (2026-08-26),
verdi su tutti e tre i TFM: come F5, è una race latente e non un fallimento deterministico.

**Direzione.** Attendere il fatto che si asserisce invece di un fatto che lo precede:
`TaskWaitHelper.WaitForRecurringRunsAsync(storage, taskId, 1)` — che esiste già ed è esattamente questo —
oppure una `WaitForConditionAsync` sul `CurrentRunCount` della riga. Nessuna asserzione cambia significato:
cambia solo il momento in cui viene letta. Vale per entrambi i test della classe che leggono il contatore
della riga dopo aver atteso quello dell'handler.

---

## F9 — I due trail di audit del dettaglio si leggono interi, senza paginazione

**Origine.** Round 2 della review di fase 7, finding [medium] #2, per la parte che il **round 3** ha lasciato
aperta. La premessa del finding — `Services/TaskRunTiming` e i quattro altri lettori si appoggiavano a
`row.StatusAudits`/`row.RunsAudits`, che nessuna lettura di storage popola (non esiste un solo `.Include(`
sotto `src/`) — è **chiusa**: il round 2 ha spostato lo start del run su `ITaskStorage.GetLastRunStarts`, e il
round 3 ha spostato gli altri quattro su `ITaskStorage.GetStatusAudits` / `GetRunsAudits`
(`GET /tasks/{id}/status-audit`, `/runs-audit`, i blocchi `statusAudits`/`runsAudits` del dettaglio) e la
media della overview su `TaskRunTiming.AverageMeasuredDurationMs` (decisioni §«round 3», con i test di
contratto sui quattro provider, in memoria, e `API/Services/RelationalAuditReadTests` su storage relazionale
vera). **Nulla di quel finding resta da fare.**

**Cosa resta.** La **forma** delle due letture nuove: `GetStatusAudits(taskId)` e `GetRunsAudits(taskId)`
rispondono la storia **intera** della riga, e i due endpoint la servono intera. È la stessa asimmetria che
`GetOccurrencesPage` ha già risolto dall'altra parte del dettaglio: un'occorrenza si pagina, le sue
transizioni no. Per una riga ricorrente longeva le due tabelle crescono quanto la vita della serie — una
transizione per stato per run — quindi aprire il dettaglio di uno schedule anziano trasferisce l'intero
storico per mostrarne le prime venti righe.

**Perché non in #23.** La correzione non è un difetto da chiudere ma **superficie di storage nuova**: due
overload paginati (`skip`/`take` + totale), i loro default sopra `Get`, l'override indicizzato per provider
sull'indice `(QueuedTaskId)` che entrambe le tabelle già hanno, i controlli di pagina nella UI (come
`OccurrencesTab` e `ExecutionLogsTab`) e i test di contratto sui cinque store. Nessuna riga della review la
chiede, e la fase 7 è lo sweep finale: aggiungere due membri pubblici di `ITaskStorage` in più, alla vigilia
di una release, per una lentezza che nessuno ha misurato, è esattamente ciò che il gate 3 esiste per fermare.

**Direzione.** Un overload paginato per ciascuna delle due letture, con la firma esistente conservata (P6:
i due membri sono DIM già scritti, e la release 4.0.0 li congela), i due endpoint che accettano
`skip`/`take` come già fa `/execution-logs`, e la UI che pagina i due tab del dettaglio.

**Test da portare con il fix.** Sui quattro provider più la memoria: venti transizioni su una riga, una
pagina di cinque, il totale che resta venti, e l'ordine newest-first invariato attraverso le pagine.

---

## F10 — `SqlServerEfCoreTaskStorageTests`: la collisione del deadlock non è garantita sotto carico

**Origine.** Suite completa della **fase 7**, durante la chiusura dei gap di completezza (2026-08-26): una
esecuzione su tre ha riportato un fallimento su **net9.0**
(`SqlServerEfCoreTaskStorageTests.Should_rerun_a_read_that_sql_server_picked_as_the_deadlock_victim`), con
net8.0 e net10.0 verdi nella stessa run. Si è ripresentato nel **round 2** della stessa chiusura, su
**net10.0**, unico fallimento di tutta la corsa. Il test non è toccato da #23.

**Il fatto, ed è per progetto.** Il test **costruisce** la collisione invece di simularla — tre cicli di
`RetrievePending` contro quattro di `SetStatus`, più due `Get` che fanno da vittime estranee al ciclo — e la
prova che la run abbia davvero colliso è l'EventId 2025 dello storage:

```csharp
_storageLog.Count(RereadAfterDeadlockEventId).ShouldBeGreaterThan(0,
    "two minutes of pressure and nothing collided, so this run proves nothing about what happens " +
    "when something does");
```

Quell'asserzione è **giusta**: senza di essa una run in cui nulla è colliso passerebbe a vuoto, ed è scritto
in `test/EverTask.Tests.Storage/CLAUDE.md`. Ma scegliere una vittima resta una decisione del motore: sotto una
`dotnet test` di tutta la soluzione — tre TFM in parallelo, quindici host che parlano allo stesso container
SQL Server — i lock possono serializzarsi e il ciclo non formarsi mai. Il test allora fallisce dicendo
esattamente la verità («nulla è colliso»), che però non è un difetto del prodotto.

**La metà già chiusa, il 2026-08-26.** Il **budget del loop** era la parte pilotabile dal test ed è stata
corretta nella fase 7 stessa: la pressione girava finché `StopOnceItHasDeadlocked` non contava **due**
rieseguite entro **20 secondi**, e il tratto fra la prima collisione e la seconda è dove finiva quasi tutta
l'attesa. Oggi il loop si ferma alla **prima** riesecuzione — che è tutta la tesi del test e l'unica cosa che
l'asserzione legge — e la finestra è di **due minuti**, dimensionata per una corsa solution-wide invece che
per il test da solo. Entrambe sono nel working tree e scritte in `test/EverTask.Tests.Storage/CLAUDE.md`
(«the test stops at the FIRST one», «its 2-minute budget is not a performance expectation»). Su questo lato
non resta nulla.

**Cosa resta.** Rendere la **collisione** affidabile. Un budget più largo alza la probabilità, non la
certezza: finché la vittima è una scelta del motore, una corsa in cui i lock si serializzano resta possibile,
ed è la forma in cui il test è fallito di nuovo dopo la correzione (round 2, net10.0). Resta aperta anche la
**diagnosi**: un run senza collisione fallisce oggi sull'asserzione finale, che a chi legge il report si
presenta come uno storage che non riesegue le proprie letture.

**Perché non qui.** È un difetto del **test**, su un path che #23 non tocca, e la correzione è una scelta di
progetto sul come provocare la collisione — non una riga da cambiare in fretta alla vigilia di una release.

**Direzione.** Rendere il ciclo deterministico invece di ammorbidire l'asserzione: alzare la pressione (più
cicli concorrenti) oppure fissare quale sessione viene uccisa con `SET DEADLOCK_PRIORITY`, così la vittima non
dipende dall'euristica del motore. E far fallire il test con la diagnosi giusta — «non è stato possibile
provocare una collisione in N secondi» — invece che con l'asserzione finale. Restare sul principio che una run
senza collisione **non passa**.
