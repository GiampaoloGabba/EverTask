# Follow-up esterni a #23 — registro

> Aperto il 2026-08-23 alla chiusura della **fase 1** di [#23](https://github.com/GiampaoloGabba/EverTask/issues/23).
> Qui stanno i punti che le review della fase 1 hanno trovato **fuori dallo scope di #23**: preesistenti, o su
> path che #23 non può toccare. Ognuno è già scritto come issue da aprire; finché l'issue non esiste, questa è
> la sua traccia in albero. Le voci che restano **dentro** #23 non stanno qui: sono nella disposizione
> `review/recurring-occurrences-decisions.md` §3.2 e, quando il fix appartiene a una fase successiva, nel
> piano di quella fase.

| # | Titolo dell'issue da aprire | Origine | Stato |
|---|------------------------------|---------|-------|
| F1 | `SQLite: normalizzare a UTC i DateTimeOffset all'ingresso pubblico dello storage` | Review fase 1, R13 | da aprire |
| F2 | `SetRecurringTaskPoisoned inghiotte i propri errori: il sommario della recovery mente` | Review fase 1, R10 | da aprire |
| F3 | `Recovery: la barriera Task.WhenAll per pagina fa aspettare le altre code` | Review fase 1, sezione «Confutate» | da aprire |
| F4 | `Distributed execution lease` (epic) | Decisioni #23, M17-A / D5 | prevista dal piano §7, da aprire alla release |
| F5 | `MemoryLeakRegressionTests: due test condividono i contatori statici della probe e si sporcano a vicenda` | Certificazione fase 2 (suite completa, net10.0) | da aprire |
| F6 | `Occorrenze: la ricostruzione di una riga non ha un tetto di tentativi, come invece ce l'ha la recovery` | Review avversariale fase 4, round 4 (finding 2) | da aprire |

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
