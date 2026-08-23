# Fase 1 (#23) — review avversariale

> Gate 4 del piano (`review/recurring-occurrences-plan.md` §0.4), eseguito il 2026-08-23 sul working tree non
> committato di `feature/issue23-durable-occurrences`, base `issue23-baseline` (`c71b5e2`).
> **Solo review: nessuna modifica al codice sotto esame.**
>
> **Esito round 1 (04:40): NON mergeable così com'è.** Tre difetti P1 vanno chiusi prima del merge; gli altri
> sono classificati e possono essere accettati esplicitamente o rinviati alla fase che li rende raggiungibili.
>
> **Esito dopo il round 2 (16:40): MERGEABLE.** I cinque fix ratificati dal maintainer (decisioni §3.1) sono
> stati riverificati uno per uno da una giuria indipendente sull'albero consegnato, le cinque finding rimaste
> `PLAUSIBILE` hanno avuto il secondo parere che mancava (due confutate, tre confermate con analisi di
> raggiungibilità), e i tre difetti che il round 2 ha trovato sono chiusi. **Il verbale del round 2 è in fondo
> a questo file** ed è la parte da leggere per sapere dove sta l'albero adesso: le sezioni sopra descrivono
> l'albero delle 04:40 e restano come sono, per non riscrivere la storia. Evidenza dei gate:
> `review/recurring-occurrences-phase1-verification.md`.

## Come è stata composta (leggere prima delle finding)

La skill `adversarial-review` fissa un floor — flock di finder Claude, triage, ≥2 scettici per candidato con
quorum, giuria Codex separata — che presuppone la possibilità di lanciare agenti. Questa esecuzione è
avvenuta **dentro un singolo agente**, senza fan-out disponibile. La deviazione è dichiarata qui, non
nascosta:

| Stadio del floor | Cosa è stato fatto davvero |
|------------------|----------------------------|
| Finder (flock, modelli diversi) | **3 lenti Codex** read-only, `gpt-5.6-sol` a `model_reasoning_effort=xhigh`, una per superficie disgiunta (A recovery/no-loss, B storage/CAS/migrazioni, C clock/compatibilità) + **una lente propria** dell'agente orchestrante su tutto il diff |
| Triage | fatto a mano dall'agente: dedup per causa radice, riclassificazione di severità |
| Verify (≥2 scettici, refute-by-default) | **NON eseguito come previsto.** Ogni claim è stato riverificato **una volta**, dall'agente, leggendo il sorgente citato. Le finding portano quindi `CONFERMATA` (letta nel codice), `PLAUSIBILE` (ragionamento statico non verificabile senza un DB vivo) o `CONFUTATA` |
| Seconda giuria Codex | non eseguita |
| Loop until dry | non eseguito: un solo round |

Conseguenza da tenere presente: **una finding `PLAUSIBILE` non ha avuto un secondo parere**, e l'assenza di
finding su una superficie non è una prova di pulizia. Le tre lenti hanno però trovato in modo indipendente lo
stesso difetto principale (R1), il che è un segnale di convergenza, non di completezza.

> **Aggiornamento (round 2).** Le tre righe mancanti di questa tabella — verify a più scettici, seconda giuria,
> secondo giro — sono state eseguite il 2026-08-23 pomeriggio sull'albero consegnato. Il verbale è in fondo al
> file; questa tabella resta com'era perché descrive il round 1.

Esiti dei run Codex, come richiede il protocollo: **3 su 3 `done`** (exit 0, `last-message.md` non vuoto,
nessun item `error` in `events.jsonl`). Nessuna lente fallita, nessun buco di copertura da rate limit.

---

## Finding

Severità con la scala del repo: **P0** = perdita dati / deadlock / doppia esecuzione · **P1** = correttezza ·
**P2** = minore · **nit** = pulizia.

### P1 — da chiudere prima del merge

#### R1 · La finalizzazione del Dispatcher rilegge le sue stesse aspettative e annulla il CAS

`src/EverTask/Dispatcher/Dispatcher.cs:602-617` — **CONFERMATA** (trovata dalla lente A e, indipendentemente,
dalla lente propria).

**Invariante rotta (X3):** la finalizzazione è condizionata sui valori da cui la decisione è stata calcolata,
così un `Cancel` o un reschedule linearizzato prima vince e la finalizzazione perde.

**Catena causale:**

1. Il ramo recovery decide che la serie è esaurita, usando il cursore `C`, lo stato `Queued`, la versione `V`
   letti dalla pagina di recovery.
2. Un `Cancel` concorrente porta lo stato a `Cancelled`. `SetStatus` **non azzera `NextRunUtc`**
   (`EfCoreTaskStorage.cs:289-295` esclude esplicitamente `Cancelled` dalle transizioni terminali), quindi
   `C` e `V` restano quelli di prima.
3. `FinalizeExhaustedSeriesAsync` fa un `Get` **dopo** la decisione e passa lo stato appena letto come
   `expectedStatus`.
4. Il CAS trova `(C, Cancelled, V)` e combacia: scrive `Completed` sopra il `Cancelled` che l'utente ha
   chiesto.

Il commento in loco ragiona sulla finestra sbagliata: dice che «un cambiamento fra la lettura e la scrittura
fa perdere il compare-and-swap, che è l'esito corretto». Vero — ma la finestra pericolosa è fra la
**decisione** e la lettura, e lì il cambiamento viene *assorbito* nell'aspettativa.

**Severità: P1 oggi, P0 dalla fase 5.** Oggi l'unico writer concorrente è `Cancel`, e il danno è uno stato
terminale sbagliato più un audit `Cancelled → Completed`: brutto, non distruttivo. Quando arriva
`ITaskScheduleManager`, un `Reschedule` che alza la versione lasciando lo stesso cursore (allungare `RunUntil`
è esattamente questo caso) viene letto come aspettativa e la serie rischedulata viene chiusa con lavoro ancora
davanti: **perdita**.

**Fix indicato:** portare stato e versione osservati al momento della decisione fino al CAS (`DispatchRowMetadata`
li trasporta già quasi tutti: ha `ScheduleVersion`, manca lo stato), e non rileggerli mai dopo. La sorella in
`WorkerService.FinalizeRecurringSeriesAsync` è già corretta proprio perché usa la riga della pagina di
recovery.

#### R2 · La finalizzazione della recovery ignora la capability e avvelena gli storage custom compatibili

`src/EverTask/Worker/WorkerService.cs` (`FinalizeRecurringSeriesAsync`) vs `ITaskStorage.cs:386-391` —
**CONFERMATA** (lente A).

**Invariante rotta:** «Uno storage senza il compare-and-swap mantiene la scrittura incondizionata storica»
(decisioni X3, e il `<remarks>` del DIM dice la stessa cosa).

**Catena causale:**

1. Uno storage custom partecipa alla recovery X3 ma lascia `SupportsScheduleVersioning == false` — cioè
   esattamente lo scenario che il default interface member esiste per servire.
2. La recovery incontra una riga di categoria (ii) e chiama `TrySetRecurringSeriesCompleted` **senza
   controllare la capability**.
3. Il DIM di default lancia `NotSupportedException`.
4. Il `catch` di `FinalizeRecurringSeriesAsync` lo tratta come fallimento L18: incrementa il contatore e, dopo
   N riavvii, chiama `SetRecurringTaskPoisoned` — una fine di serie legittima diventa una riga `Failed`.

Il Dispatcher ha il ramo giusto (`FinalizeExhaustedSeriesAsync` controlla `SupportsScheduleVersioning` e
ricade su `SetRecurringSeriesCompleted`); la recovery no. Due call site sorelle, due comportamenti.

**Fix indicato:** rispecchiare il ramo del Dispatcher in `WorkerService`.

#### R3 · Un `OccurrenceMode` non definito ricade in silenzio su `Inline`

`src/EverTask/Scheduler/Recurring/RecurringTask.cs:41-50`, `src/EverTask/Serialization/TolerantEnumConverter.cs:36-60`
— **CONFERMATA** (lente C).

**Invariante rotta:** il converter tollerante è deliberatamente severo sui numeri fuori range **perché**
delega altrove il controllo dei valori definiti. Il suo commento lo dice testualmente: «Defined-value
enforcement for recurring schedules is handled upstream by `RecurringTask.Validate()` (B2)».
`RecurringTask.Validate()` valida i sette intervalli e **non guarda `OccurrenceMode`**.

**Catena causale:** una riga con `"OccurrenceMode": 2` (scritta a mano, da un peer più recente, o da una
corruzione) deserializza in `(OccurrenceMode)2`. `Validate()` tace, quindi la recovery non segnala errore di
schedule. `RecoveredTask.IsDurableSchedule` confronta solo con `Durable`, ritorna `false`, e il padre viene
recuperato lungo il path inline — cioè con la semantica sbagliata invece di essere avvelenato in modo pulito,
che è la scelta fail-safe già adottata ovunque nel codebase per i metadati di schedule corrotti.

**Severità: P1.** Non raggiungibile *oggi* — nessun writer scrive un valore diverso da 0 — ma la promessa
scritta nel converter non è mantenuta e il costo del fix è una riga.

**Fix indicato:** `Enum.IsDefined` su `OccurrenceMode` dentro `RecurringTask.Validate()`.

### P2 — da decidere, non bloccanti

| # | Finding | Dove | Verdetto |
|---|---------|------|----------|
| R4 | `ToQueuedTask` conserva un calcolo autonomo del primo `NextRunUtc` sul wall clock invece del `InvalidOperationException` difensivo che il piano §1.1 prescrive | `TaskHandlerExecutor.cs:285-295` | **CONFERMATA** — deviazione dal piano, dichiarata in loco e motivata (lanciare romperebbe un chiamante diretto dell'extension pubblica). Il ramo è irraggiungibile dai path della libreria. Serve una decisione esplicita del maintainer: accettare la deviazione o applicare il piano |
| R5 | SQLite perde il `DEFAULT 0` di `ScheduleVersion`: l'`ALTER TABLE` lo mette, la ricostruzione della tabella (imposta da FK + check) lo omette. La radice è che il modello condiviso non configura `HasDefaultValue(0)`, quindi nessuno dei quattro snapshot lo ha | `MigrationSnapshots/Sqlite.AddDurableOccurrences.sql:32`, `TaskStoreEfDbContext.cs` | **CONFERMATA** contro lo snapshot committato. Impatto reale ma stretto: un binario vecchio che inserisce senza la colonna su un DB già migrato. Il fix richiede una **nuova** migration (le esistenti sono congelate) |
| R6 | Gli audit di `CancelSchedule` derivano da una lettura, non dalla scrittura. Tre varianti: (a) il padre viene aggiornato e auditato senza guardia `Status != Cancelled`, quindi un secondo cancel produce un audit senza transizione; (b) EF rilegge i candidati `Cancelled` e attribuisce a sé anche quelli cancellati da un altro writer; (c) la proc MySQL fa `INSERT … SELECT` degli audit **prima** dell'`UPDATE` | `EfCoreTaskStorage.cs:947-1012`, `MemoryTaskStorage.cs:521-539`, `MySql/Migrations/…:190-203`, `PostgresTaskStorage.cs:320` | (a) **CONFERMATA** (e coerente col comportamento incondizionato storico di `SetStatus`). (b) e (c) **PLAUSIBILI**: dipendono dal livello di isolamento — sotto REPEATABLE READ l'`INSERT … SELECT` di InnoDB prende lock condivisi che chiudono (c). Una causa radice sola: l'insieme auditato non viene dalla `UPDATE` |
| R7 | `CancelSchedule` non tocca le occorrenze `ServiceStopped`, mentre `TrySetQueuedIfRecoverable` le accetta: un'occorrenza può essere rimessa in coda ed eseguita dopo che il suo schedule è stato cancellato | tutte e cinque le implementazioni | **CONFERMATA come comportamento** — ma l'implementazione è **fedele alla decisione M15**, che elenca `WaitingQueue/Queued/Pending`. Il buco è nella decisione, non nel codice. Nessun impatto in fase 1 (le occorrenze nascono in fase 4) |
| R8 | La seconda passata di recovery bufferizza **ogni** schedule durevole di **ogni** pagina in una sola lista, con payload e definizione deserializzati, e non ne rilascia nessuno fino a fine paginazione. Il commento assume «una manciata per host»: è un'assunzione, non un limite | `WorkerService.cs:225,262,279` | **CONFERMATA** come fatto di codice. Zero impatto in fase 1 (nessuna riga può essere `Durable` finché la fase 4 non introduce il builder). Il fix pulito è una seconda scansione keyset con lo stesso cutoff, non una lista |
| R9 | Finalizzazione riuscita e `ClearRecoveryFailure` sono due scritture dentro lo stesso `try`: se la seconda lancia, il `catch` incrementa L18 e può avvelenare una riga già `Completed` | `WorkerService.cs` (`FinalizeRecurringSeriesAsync`) | **CONFERMATA** come forma del codice. Finestra stretta: serve un errore del DB e un contatore già > 0 |
| R10 | `SetRecurringTaskPoisoned` inghiotte i propri errori; `WorkerService` conta comunque `permanentFailures` e logga la riga come avvelenata, mentre resta recuperabile e ripete il ciclo a ogni riavvio | `EfCoreTaskStorage.cs:705-710`, `WorkerService.cs` | **CONFERMATA**, ma **preesistente**: la fase 1 aggiunge un call site, non il comportamento. Direzione sicura (la riga non si perde), sommario disonesto |
| R11 | `TrySetRecurringSeriesCompleted` e `TryHaltSchedule` accettano un cursore atteso nullo; EF lo traduce in `NextRunUtc IS NULL`, che combacia proprio con le righe finalizzate e avvelenate. `MaterializeOccurrence` si difende esplicitamente da questo caso, queste due no | `EfCoreTaskStorage.cs:907,1116`, `MemoryTaskStorage.cs:495,594` | **PLAUSIBILE / latente**: nessun chiamante in-tree passa null (entrambi i call site hanno un cursore non nullo per costruzione). Resta l'asimmetria dentro la stessa classe |
| R12 | Cosa finisce davvero nella riga occorrenza dipende dal provider: le proc SqlServer/MySql e la CTE Postgres impongono la forma M4 (`WaitingQueue`, contatori a zero, `TaskKey` nullo, `ScheduleVersion` = quella del padre), EF e Memory persistono l'oggetto ricevuto | `EfCoreTaskStorage.cs:831`, `MemoryTaskStorage.cs:476`, `PostgresTaskStorage.cs:252`, proc SqlServer/MySql | **CONFERMATA** leggendo le cinque implementazioni. Nessun test di contratto la fissa oggi, perché i test costruiscono già la forma canonica. Da normalizzare in codice condiviso prima che la fase 4 ci costruisca sopra |
| R13 | SQLite tiene i `DateTimeOffset` come TEXT con l'offset, quindi l'uguaglianza di cursore e slot è sensibile alla rappresentazione: un cursore legacy `10:00+02:00` non combacia con `08:00+00:00` normalizzato | `SqliteTaskStoreContextModelSnapshot.cs:55,86` | **PLAUSIBILE** — tratto preesistente del provider (è la stessa limitazione che costringe a filtrare `RunUntil` client-side). Tutte le scritture in-tree normalizzano a UTC, quindi serve una riga scritta da fuori |
| R14 | La CTE singola di `CancelSchedule` su Postgres può non vedere un figlio committato da un materializer concorrente: lo snapshot dello statement è preso prima che l'insert diventi visibile, e la ri-verifica di READ COMMITTED riguarda la riga padre bloccata, non l'insieme dei figli | `PostgresTaskStorage.cs:320-340` | **PLAUSIBILE** — ragionamento statico, non verificato contro un DB vivo. Impatto in fase 4 |
| R15 | `OccurrenceRetentionDays` pota le occorrenze terminali senza rispettare la guardia sulla retention dei log che `CleanupCompletedTasks` rispetta: i `TaskExecutionLog` di un'occorrenza vengono cancellati a cascata prima della loro finestra | `EfCoreTaskStorage.cs:1593-1604`, `AuditCleanupHostedService.cs:153-157` | **CONFERMATA** (lente propria). O si allinea alla passata sorella, o la differenza va scritta in `docs/configuration-reference.md`, dove oggi non c'è |

### Confutate

| Claim | Perché cade |
|-------|-------------|
| «Le stored procedure emulano il CAS con una SELECT precedente» (lente B, dichiarata P1 su 5 operazioni) | **CONFUTATA.** Entrambe le procedure leggono la riga schedule con `WITH (UPDLOCK, HOLDLOCK)` (SQL Server) e `FOR UPDATE` (MySQL) **dentro la transazione**: il lock è tenuto fino al commit, quindi un secondo materializer si blocca sulla propria SELECT e, quando riparte, rilegge versione e cursore già avanzati e ritorna `CursorMoved`. La `UPDATE` successiva su `WHERE Id = …` è protetta da quel lock. La regola «il CAS sta nella WHERE» vale dove non c'è lock — cioè nel path EF, dove infatti è rispettata |
| «`ScheduleVersion` va in overflow a `int.MaxValue` e reintroduce ABA» (lente B) | Vero aritmeticamente, irrilevante nella pratica: servono 2³¹ reschedule dello stesso schedule. Registrata come **nit**, non come difetto |
| «Una `Persist` concorrente può far emergere la violazione di unique come eccezione su SQL Server» (lente B) | **Non raggiungibile in-tree**: l'unico writer di righe occorrenza è il materializer, e il suo `IF EXISTS` gira sotto il lock del padre. Resta una nota di robustezza per il giorno in cui qualcosa scriverà figli da fuori |
| «Una coda bloccata affama le pagine successive delle altre code» (lente A) | **Preesistente**, non introdotta dalla fase 1: la barriera `Task.WhenAll` per pagina c'era già identica prima del diff (il raggruppamento per coda è solo stato spostato dentro `RecoverWaveAsync`). Fuori scope per una review di fase 1, ma vale come issue a sé |

---

## Aree lette e trovate pulite

Sono elencate perché «nessuna finding» non deve leggersi come «non guardato»:

- **I quattro cloni del predicato di recovery concordano.** Canonico su `QueuedTask`, mirror EF-traducibile,
  lista inline SQLite, store in memoria. L'indice parziale Postgres `IX_QueuedTasks_Recovery`
  (`Status IN (5) OR (IsRecurring AND NextRunUtc IS NOT NULL)`) è un **soprainsieme** della nuova unione:
  la categoria (ii) richiede `IsRecurring && NextRunUtc != null`, coperta dal secondo disgiunto. Nessuna
  nuova migration serviva, e il fatto è stato verificato leggendo la migration `Initial`.
- **La composizione `OrElse` produce un albero traducibile** (parameter rebinding, non `Expression.Invoke`), e
  `TrySetQueuedIfRecoverable` applica solo la metà "da eseguire".
- **Le due categorie hanno la precedenza giusta.** Dove si sovrappongono (`NextRunUtc >= RunUntil` con
  `RunUntil >= now`), la finalizzazione vince, che è l'esito corretto: lo slot pendente è oltre il confine.
- **La race dei due scheduler è corretta.** Il waiter del segnale è creato una volta sola e sopravvive alla
  sconfitta contro il delay — abbandonarlo gli farebbe consumare in silenzio la `Release` successiva; il timer
  perdente viene cancellato e osservato, il CTS collegato disposto. Nessun wake-up perso, nessun timer
  orfano, nessuna eccezione non osservata.
- **Compatibilità pubblica.** Nessun parametro posizionale appeso a un record pubblico (le proprietà nuove
  sono `init` nel body), nessun parametro opzionale appeso a un metodo pubblico esistente: i costruttori
  pre-P9 di `WorkerService`, `WorkerExecutor`, `PeriodicTimerScheduler`, `ShardedScheduler` e la vecchia
  `ToQueuedTask()` restano come **overload reali**, e `DispatchRowMetadata` viaggia su un membro *internal*.
- **JSON legacy byte-identico.** `OccurrenceMode.Inline == 0` più `WhenWritingDefault` non cambia né ordine né
  byte; la scrittura numerica degli enum e le letture legacy restano intatte.
- **Atomicità della materializzazione**: inserimento figlio, avanzamento cursore ed eventuale finalizzazione
  commitano insieme su tutte e cinque le implementazioni, e l'ordine di classificazione degli esiti è lo
  stesso ovunque.
- **`Remove` cancella le occorrenze nella stessa transazione** (la FK Restrict non lascerebbe alternative), e
  i `Down()` rimuovono le quattro procedure nuove senza toccare quelle vecchie.
- **Disciplina delle delivery invariata**: un solo `End` per delivery, `DuplicateInProcess` non acquisisce
  ownership, la recovery lo tratta idempotentemente. Nessun nuovo path di doppia esecuzione in-process.
- **Inventario dei `UtcNow` residui**: a parte R4, quelli rimasti sono fallback delle firme legacy, timestamp
  di audit/log/eventi, attese di retry e TTL di housekeeping — cioè le aree che P9 esclude esplicitamente.

## Coverage

**Cosa è stato sondato:** l'intero diff non committato (52 file modificati, 3364 righe aggiunte, più 15 file
nuovi), con tre lenti Codex disgiunte a `xhigh` e una lettura propria mirata su recovery, storage, scheduler,
DI e superficie pubblica.

**Cosa è rimasto fuori, e va detto:**

- **Il verify a due scettici non è stato eseguito.** Ogni claim ha una sola riverifica. Le finding marcate
  `PLAUSIBILE` non hanno un secondo parere e nessuna è stata riprodotta con un test.
- **Nessuna finding è stata riprodotta a runtime.** Le interleaving descritte (R1, R6b/c, R14) sono
  ricostruzioni statiche; R14 in particolare dipende dal comportamento reale di PostgreSQL sotto
  READ COMMITTED e non è stata verificata contro un database vivo.
- **Niente build, lint o test durante la review** (regola read-only della skill). La suite completa è stata
  eseguita separatamente ed è verde — vedi `review/recurring-occurrences-phase1-verification.md`. Il fatto che
  sia verde non contraddice nessuna finding qui: tutte descrivono race o scenari che i test attuali non
  coprono.
- **Non esaminati:** UI di monitoring, DTO dell'API, i pacchetti baseline sotto `nupkg/` a livello di IL, la
  correttezza delle docs oltre alle due incoerenze citate (R15), i benchmark.
- **Seconda giuria Codex e round di completezza:** non eseguiti.

---

## Cosa fare adesso

1. **Bloccanti:** R1, R2, R3. Sono tre fix piccoli e circoscritti; R1 e R2 stanno nello stesso cluster di
   causa radice — *«l'aspettativa del compare-and-swap deve venire dalla decisione, e la capability va
   controllata nello stesso modo nei due call site sorelle»*.
2. **Da decidere prima del merge, anche solo per accettarli per iscritto:** R4 (deviazione dal piano §1.1),
   R5 (serve una migration nuova, quindi va deciso ora o mai), R15 (una riga di docs o un allineamento).
3. **Rinviabili con una nota nel piano di fase 4:** R7, R8, R12, R14 — tutti diventano raggiungibili solo
   quando il materializer esiste.
4. **Da girare a issue separate:** la barriera per pagina della recovery (confutata come regressione, reale
   come limite preesistente) e R13.

Passo successivo consigliato:

```
/review-to-plan E:\Archivio\Sviluppo\Web\EverTask\review\recurring-occurrences-phase1-adversarial-review.md
```

---

# Round 2 — chiusura del gate 4 (2026-08-23, pomeriggio)

Il round 1 si è fermato con tre P1 aperti e con tre stadi del floor dichiarati non eseguiti. Nel frattempo i
cinque fix ratificati dal maintainer (decisioni §3.1: R1, R2, R3, R5, R15) sono atterrati nell'albero, e
niente li aveva riletti. Questo round fa le due cose insieme: **riverifica i fix** e **dà alle finding
`PLAUSIBILE` il secondo parere che non avevano**.

## Come è stato composto

Due lenti Codex indipendenti, entrambe read-only, entrambe su **l'albero consegnato** (working tree non
committato sopra `issue23-baseline`), `gpt-5.6-sol` a `model_reasoning_effort=xhigh`:

| Lente | Mandato | Thread |
|-------|---------|--------|
| A — secondo scettico | Le cinque finding `PLAUSIBILE` (R6b, R6c, R11, R13, R14), **confutando per default**: una finding sopravvive solo con il `file:riga` che la rende vera. Più un giro libero sulle stesse superfici | `01a02f03-9150-7e20-bbbe-7cba86a0398b` |
| B — re-review post-fix | I cinque fix ratificati, uno per uno: corretto / incompleto / sbagliato / regressione. Più la caccia ai difetti **introdotti dai fix** e il giro "finché non viene asciutto" | `01a02f03-a07e-70a1-a7bf-218875dddced` |

Esito dei run, come richiede il protocollo: **2 su 2 `done`** (exit 0, `turn.completed`, `last-message.md`
non vuoto, zero item `error` in `events.jsonl`).

Quello che il round 2 **non** è: le finding restano ragionamenti statici, nessuna è stata riprodotta a
runtime, e le due lenti hanno letto lo stesso albero con lo stesso modello. È un secondo parere reale, non
tre giurie indipendenti.

## I cinque fix ratificati

| Fix | Verdetto lente B | Dove |
|-----|------------------|------|
| R1 — la finalizzazione del Dispatcher porta l'aspettativa dalla decisione | **CORRETTO.** Stato, cursore e versione arrivano dalla pagina di recovery via `DispatchRowMetadata` e nessun path li rilegge | `Dispatcher.cs:307-315,371-375,624-637`, `RecoveredTaskFactory.cs:39-40`, `WorkerService.cs:390-393` |
| R2 — la recovery controlla la capability come il Dispatcher | **CORRETTO**, e il reset del contatore L18 sta fuori dal `try` della scrittura terminale | `WorkerService.cs:486-504,538-556` |
| R3 — `Validate()` rifiuta un `OccurrenceMode` non definito | **INCOMPLETO** (vedi sotto) | `RecurringTask.cs:42-60` |
| R5 — `HasDefaultValue(0)` nel modello condiviso | **CORRETTO** su tutti e quattro i provider, snapshot del modello e SQL committato inclusi; `ValueGeneratedNever` è il compagno giusto | `TaskStoreEfDbContext.cs:93-102`, le 4 migration, i 4 snapshot, `MigrationSnapshots/*.sql:6` (SQLite anche `:32`, la tabella ricostruita) |
| R15 — la potatura delle occorrenze rispetta la finestra dei log | **CORRETTO**: una sola guardia condivisa, calcolata dal servizio di cleanup e passata a entrambe le passate, e gli override MySQL/SQLite la conservano | `AuditCleanupHostedService.cs:147-187`, `EfCoreTaskStorage.cs:1646-1691`, `MySqlTaskStorage.cs:203-235`, `SqliteTaskStorage.cs:129-154` |

### R3 era incompleto — chiuso

`Validate()` è corretto, e la recovery lo chiama davvero (`RecoveredTaskFactory`, su ogni riga). Ma
**`Validate()` non era chiamato sul path di dispatch**, che la decisione T10 elenca per primo («invocato su
tutti i path: build/dispatch, recovery, `Reschedule`»). `ExecuteDispatch` è pubblico e prende un
`RecurringTask` costruito dal chiamante: una definizione con `OccurrenceMode` fuori range arrivava intatta a
`IsScheduleOnly`, che confronta solo con `Durable`, e la riga girava sul path inline invece di essere
avvelenata. **Fix:** `recurring?.Validate()` all'ingresso di `ExecuteDispatchCore`
(`Dispatcher.cs:192-199`); pinnato da `DispatcherTests.Should_reject_a_schedule_whose_occurrence_mode_is_not_a_defined_value`
e `..._with_a_corrupt_interval_at_dispatch`.

La seconda parte della finding — «una serie esaurita viene finalizzata prima della guardia di poison, quindi
uno `ScheduleError` non avvelena» — è **respinta**: la precedenza è voluta e il commento in loco lo dice già
(`WorkerService.cs:322-327`). Chiudere una serie non richiede di saper leggere la sua definizione, e
`Completed` è lo stato vero di una serie finita; avvelenarla la marcherebbe `Failed`, che non è quello che è
successo.

## Le cinque finding senza secondo parere

| # | Verdetto lente A | Sintesi |
|---|------------------|---------|
| R6b | **CONFERMATA**, non raggiungibile sui provider spediti | L'insieme auditato dal `CancelSchedule` della base EF viene da una rilettura, quindi sotto READ COMMITTED può attribuirsi figli cancellati da un altro writer. SQLite eredita la base ma serializza i writer; SqlServer, Postgres e MySQL sovrascrivono il metodo. Resta un difetto **del contratto della base**, quindi di un provider custom futuro |
| R6c | **CONFUTATA** | Sotto InnoDB REPEATABLE READ l'`INSERT … SELECT` della proc MySQL è una lettura con lock che tiene i next-key lock fino al commit: un writer concorrente o è escluso o aspetta, e un materializer non può inserire un figlio fantasma perché prende prima il padre `FOR UPDATE`. Il commento in loco è valido; sarebbe troppo largo se qualcuno portasse la sessione a READ COMMITTED |
| R11 | **CONFERMATA** | Un cursore atteso nullo si traduce in `NextRunUtc IS NULL` e combacia esattamente con le righe finalizzate e avvelenate. Nessun chiamante in-tree passa null, ma i due membri sono pubblici. **Chiusa** (sotto) |
| R13 | **CONFERMATA**, fuori dalla pipeline di dispatch | Il confronto di cursore su SQLite è testuale. La lente ha anche corretto il round 1 su un punto: «tutte le scritture in-tree normalizzano» è troppo largo — `Persist` e `UpdateTask` scrivono l'entità che ricevono, quindi la riga anomala si può creare dall'API storage pubblica, non solo con SQL grezzo |
| R14 | **CONFUTATA** | `CancelSchedule` su Postgres non è più una CTE sola: prende il padre `FOR UPDATE` in uno statement separato, e solo dopo il secondo statement prende il suo snapshot READ COMMITTED. Un figlio committato mentre il cancel aspettava è visibile (`PostgresTaskStorage.cs:321-369`) |

### R11 chiusa

I due membri condizionali accettavano un cursore atteso nullo mentre `MaterializeOccurrence`, nella stessa
classe, lo rifiuta esplicitamente. Ora lo rifiutano tutti e tre, con la stessa motivazione scritta nel
contratto (`ITaskStorage.cs`, `<remarks>` di `TrySetRecurringSeriesCompleted` e `TryHaltSchedule`):
`EfCoreTaskStorage.cs` e `MemoryTaskStorage.cs`, pinnati da
`EfCoreTaskStorageTestsBase.The_conditional_schedule_writes_should_refuse_a_null_expected_cursor` (quattro
provider) e da `MemoryStorageScheduleCasTests.Should_refuse_a_null_expected_cursor_on_both_conditional_schedule_writes`.
Nessun chiamante in-tree passava null, quindi il comportamento osservabile non cambia per nessuno.

### R6b e R13 restano aperte, per iscritto

Nessuna delle due è raggiungibile in fase 1 dall'uso normale della libreria, e la correzione di entrambe è una
scelta di design che le decisioni non coprono:

- **R6b** chiede o una transazione serializable con retry nella base EF, o l'obbligo per un provider con
  writer concorrenti di derivare l'insieme auditato da `OUTPUT`/`RETURNING`. Riguarda il contratto che la
  skill `new-relational-storage-provider` scaffolda, e diventa raggiungibile in fase 4.
- **R13** chiede o un path di cursore identity-preserving per SQLite nelle tre operazioni CAS, o la
  normalizzazione a UTC all'ingresso pubblico dello storage. Il secondo è il fix vero e tocca `Persist` /
  `UpdateTask`, cioè il path legacy — esattamente ciò che la fase 1 non può cambiare senza una decisione.

Entrambe vanno al maintainer come i P2 del round 1: da accettare per iscritto o da mettere nel piano della
fase 4.

## Difetti introdotti dai fix

La lente B ne ha trovato **uno**, P2, ed è chiuso: il messaggio di log del reset fallito del contatore L18
prometteva che «il contatore viene azzerato alla prossima recovery riuscita», mentre dopo una finalizzazione
riuscita la riga è `Completed` con cursore nullo e non combacia più con nessuno dei due predicati di recovery —
nessuna prossima recovery la leggerà. Messaggio corretto in `WorkerServiceLog.cs:144-147` (EventId invariato).

Nessun P0, nessun P1.

## Giro di completezza

Entrambe le lenti hanno chiuso asciutte sul resto del diff: la lente A ha riletto le quattro copie del
predicato di recovery e le ha trovate allineate (canonico su `QueuedTask`, mirror EF, lista inline SQLite,
indice parziale Postgres come soprainsieme, Memory che chiama i predicati direttamente); la lente B non ha
trovato nulla fuori dal cluster dei cinque fix che il round 1 non avesse già catalogato.

## Stato del gate 4

Tutti i P1 sono chiusi: R1, R2 e R3 con fix (R3 completato in questo round), più R11, promossa da `PLAUSIBILE`
a confermata e chiusa qui.

Dei P2: R4 è una **deviazione ratificata** dal maintainer, R5 e R15 sono fix ratificati e verificati, R9 è
chiuso dal fix di R2 (il reset del contatore sta fuori dal `try`), R6c e R14 sono **confutati**. Restano
aperti **R6a/R6b, R7, R8, R10, R12, R13** — nessuno raggiungibile in fase 1 dall'uso normale della libreria
(R10 è per giunta preesistente), tutti da accettare per iscritto o da portare nel piano della fase 4.

**Il gate 4 è verde.**
