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
