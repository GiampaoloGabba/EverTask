# Fase 1 (#23) — verbale di verifica

> Rifatto il **2026-08-23, sera**, sull'albero consegnato, dopo che i cinque fix ratificati (decisioni §3.1),
> il round 2 della review avversariale e poi la chiusura dei quattordici gap di completezza hanno cambiato
> l'albero rispetto alla versione delle 04:40. Ogni stesura precedente di questo file misurava un albero che
> non esiste più: i conteggi, l'inventario del gate 3 e i numeri del benchmark qui sotto sono quelli
> dell'albero che si consegna. I due giri di chiusura dei gap hanno una sezione ciascuno in fondo.
>
> Decisioni: `review/recurring-occurrences-decisions.md` (prevalgono). Piano:
> `review/recurring-occurrences-plan.md`, gate comuni nel §0. Questo file è l'evidenza in-tree dei gate
> **1, 2, 3, 5, 6, 7**; il gate 4 ha il suo rapporto separato in
> `review/recurring-occurrences-phase1-adversarial-review.md`, chiuso verde dal round 2 in fondo a quel file.
>
> **Tutti e sette i gate sono verdi.**

Base: tag `issue23-baseline` (`c71b5e2`), master prima della fase 1. Oggetto: il working tree non committato
del branch `feature/issue23-durable-occurrences`.

---

## Gate 1 — build a 0 warning

```
dotnet build EverTask.slnx -c Release
→ Avvisi: 0   Errori: 0
```

net8.0 / net9.0 / net10.0, warnings-as-errors attivo.

## Gate 2 — suite completa, Testcontainers inclusi

```
dotnet test EverTask.slnx -c Release      # nessun filtro: le gambe container girano
EXITCODE=0
```

(Corsa finale, sull'albero esatto che si consegna, dopo la chiusura dei gap del **terzo** giro di
certificazione del 2026-08-23 sera tardi. I conteggi sotto sono quelli di quella corsa.)

| Assembly | net8.0 | net9.0 | net10.0 |
|----------|-------:|-------:|--------:|
| `EverTask.Tests` | 1349 | 1349 | 1349 |
| `EverTask.Tests.Storage` | 593 | 790 | 794 |
| `EverTask.Tests.Monitoring` | 178 (+4 skip) | 182 (+4 skip) | 182 (+4 skip) |
| `EverTask.Tests.Logging` | 10 | 10 | 10 |
| `EverTask.Analyzers.Tests` | — | 44 | — |

**6840 test passati, 0 falliti, 12 saltati.** I 4×3 skip di Monitoring sono preesistenti e non riguardano
lo storage. Rispetto alla stesura precedente (6726) sono +114, di cui **14 del terzo giro**: il pin della
finalizzazione senza run, uno per TFM sul negozio in memoria e uno per provider nella suite condivisa (tre su
net8, quattro sugli altri due). Gli altri 100 erano già in albero — il secondo giro li aveva scritti senza
riaggiornare questa tabella.

**Una intermittenza osservata, e non nascosta.** Nella prima delle due corse complete di questa sessione,
`LazyModeIntegrationTests.Should_use_lazy_mode_for_infrequent_recurring_tasks` è fallito su net10 (e solo lì:
net8 e net9 verdi nella stessa corsa). Non è riproducibile: verde nella corsa completa successiva, verde in
isolamento, verde in sei esecuzioni consecutive della sua classe. Il test aspetta il primo run con un timeout
di **1 secondo** (`TestEnvironment.GetTimeout(1000, 3000)`) e conta le dispose dopo un `Task.Delay(100)` fisso,
su contatori statici — con cinque host di test in parallelo su tre TFM è un limite di tempo, non un
invariante. Non tocca nulla di ciò che la fase 1 cambia: storage in memoria, nessun orologio iniettato,
`ShouldUseLazyResolution` invariato. Resta segnalato come debito di stabilità del test, non come esito. Le due
corse complete del terzo giro sono verdi anche su quel test.

Le tre asimmetrie sono le stesse di sempre: MySQL gira su net9/net10 (593 vs 790), gli snapshot SQL delle
migration sono `#if NET10_0`, uno per provider (790 vs 794), gli analyzer girano solo su net9.

**Le gambe container hanno girato.** `docker info` risponde (29.5.3, engine Linux, `docker-desktop`) e le
immagini sono in locale (`mcr.microsoft.com/mssql/server:2022-latest`, `postgres:16-alpine`,
`mariadb:10.11`, `testcontainers/ryuk`). `Ignorati: 0` su `EverTask.Tests.Storage` è la controprova: i
`ConditionalFact` legati a Docker avrebbero saltato invece di passare. In più, prima della corsa completa, i
tre provider container sono stati eseguiti anche uno per uno su net10 — SqlServer 153, Postgres 181,
MySql 181, tutti verdi.

## Gate 3 — path legacy invariato

L'inventario completo, non un riassunto. Sotto `test/`, i file **preesistenti** che il diff tocca sono undici,
e nessuno cambia un'asserzione:

| File preesistente | Righe +/- | Cos'è |
|-------------------|----------:|-------|
| `EfCore/EfCoreTaskStorageTestsBase.cs` | +1118 / -0 | Solo aggiunte: la regione occorrenze durevoli, la sezione recovery esecuzione-vs-finalizzazione, le due coppie astratte di SQL per la fault injection |
| `SqLiteEfCoreTaskStorageTests.cs` | +123 / -1 | Aggiunte (schema dal catalogo, default `ScheduleVersion`, snapshot SQL, DDL dei due fault); la riga tolta è una `using` sostituita |
| `SqlServerEfCoreTaskStorageTests.cs` | +126 / -0 | Aggiunte |
| `PostgresEfCoreTaskStorageTests.cs` | +199 / -0 | Aggiunte |
| `MySqlEfCoreTaskStorageTests.cs` | +104 / -0 | Aggiunte |
| `TestHelpers/FakeTimeProvider.cs` | +175 / -4 | **Helper**, non un test: i timer virtuali (`CreateTimer`, `SetUtcNow`, `WaitForPendingTimersAsync`). `GetUtcNow`/`Advance` conservano la firma e il significato che avevano |
| `GlobalUsings.cs` | +5 / -0 | `InternalsVisibleTo("EverTask.Tests.Storage")`, perché il nuovo `RecoveryHarness` restituisce l'internal `WorkerService` |
| `MemoryStorageRecoveryFilterTests.cs` | +80 / -0 | Aggiunte: i test dedicati di X3 (l'eccezione ammessa dal gate) |
| `WorkerExecutorMonitoringTests.cs` | +102 / -0 | Aggiunte |
| `RecurringTests/Intervals/IntervalValidationTests.cs` | +25 / -0 | Aggiunte: `OccurrenceMode` fuori range (R3) |
| `IntegrationTests/RunUntilPastNextRunRecoveryReproTests.cs` | +11 / -2 | **Cablaggio**, non asserzioni: il test riceve `clock: new FakeTimeProvider(...)` e semina da `Clock.GetUtcNow()`. Lo chiede il piano §1 |
| `WorkerServiceRecoveryPoisonTests.cs` | +102 / -16 | **Firme dei mock**, non asserzioni: `RetrievePending` e `ExecuteDispatch` hanno l'overload nuovo che il core ora chiama |
| `TestHelpers/IsolatedIntegrationTestBase.cs` | +33 / -2 | Helper: il parametro `clock` su entrambe le overload e la proprietà `Clock`. Lo chiede il piano §1.2 |

Rispetto alla stesura precedente di questo verbale, quattro file preesistenti sono stati **riportati
byte-identici** in questa sessione, perché li aveva toccati una passata di cleanup e non la fase 1:
`BackwardCompatibilityScheduleDriftTests.cs`, `MultiQueue/QueueFullBehaviorTests.cs`,
`MultiQueue/QueueParallelismTests.cs`, `SignalR/SignalRTaskMonitorTests.cs` (conversioni a `new()` e tre
letterali riscritti come raw string). Oggi `git diff` su quei quattro è vuoto.

### La modifica che non c'è più, e la misura che lo giustifica

`BackwardCompatibilityScheduleDriftTests.Legacy_Task_Without_ScheduledExecutionUtc_Should_Still_Work`
aspettava, nella versione precedente, che `CurrentRunCount >= 1` prima di leggere la riga, per chiudere una
race fra il contatore alzato dentro l'handler e la scrittura durevole che segue. La modifica era motivata da
una race dichiarata preesistente, senza una misura a sostegno. La misura ora esiste, e dice che la modifica
non serve:

| Albero | Test | Esecuzioni | Falliti |
|--------|------|-----------:|--------:|
| `issue23-baseline` (`c71b5e2`), test originale | `Legacy_Task_Without_ScheduledExecutionUtc_Should_Still_Work` | 30 | 0 |
| albero consegnato, test **ripristinato** all'originale | idem | 30 | 0 |

(net10.0, Release, invocazioni separate, esito preso dall'exit code.) Più le tre corse complete della suite
in questa sessione. Il test è tornato al suo testo originale.

## Gate 4 — review avversariale

Verde. Il rapporto è `review/recurring-occurrences-phase1-adversarial-review.md`: il round 1 (04:40) aveva
chiuso con «non mergeable» e tre P1; il **round 2**, in fondo allo stesso file, riverifica sull'albero
consegnato i cinque fix ratificati, dà il secondo parere alle cinque finding rimaste `PLAUSIBILE` (due
confutate, tre confermate con analisi di raggiungibilità), chiude i tre difetti che ha trovato (R3 incompleto,
R11, un P2 di logging) e lascia per iscritto le due che richiedono una decisione del maintainer (R6a/R6b,
R13). Due lenti Codex indipendenti, read-only, `xhigh`, entrambe classificate `done`.

**Chiusura dei punti rimasti aperti.** Il round 2 lasciava sei punti «da accettare per iscritto o da portare
nel piano della fase 4»: R6a/R6b, R7, R8, R10, R12, R13. Ognuno ha ora una destinazione scritta in
`review/recurring-occurrences-decisions.md` §3.2 — R6a accettata, R12 **chiusa in fase 1** (forma canonica
`ApplyOccurrenceContract`, pinnata sui quattro provider), R6b/R7/R8 portate nel piano §4.3 con il lavoro da
fare, R10 e R13 accettate per la fase 1 e girate al registro `review/recurring-occurrences-followups.md`
(F2, F1) insieme alla barriera per pagina della recovery (F3) e all'epic della lease (F4). Il piano §7
elenca le quattro issue da aprire alla release.

## Gate 5 — regola anti-stale e passata `humanizer`

**Anti-stale.** Le superfici di configurazione pubbliche nuove della fase 1 sono **due**, non una, e per
entrambe i tre posti obbligati sono aggiornati nello stesso diff (`docs/configuration-cheatsheet.md`,
`docs/configuration-reference.md`, `plugins/evertask/skills/integrate-evertask/`):

| Superficie | Cheatsheet | Reference | Skill |
|------------|------------|-----------|-------|
| `AuditRetentionPolicy.OccurrenceRetentionDays` | riga nella tabella "Audit Retention / Cleanup" | paragrafo "Occurrence retention" | `references/03-storage.md` |
| **Il seam dell'orologio di scheduling** (`TryAddSingleton(TimeProvider.System)`, e tutta la pipeline segue un `TimeProvider` registrato dall'utente) | nota sotto la tabella "Service Configuration", accanto a quella della retention | `### The Scheduling Clock (TimeProvider)`, in fondo a "Service Configuration" | `references/01-setup.md` |

La seconda riga era il buco: non è un metodo del builder ma una registrazione DI, e la stesura precedente di
questo verbale la contava fuori dalla regola («l'unica opzione pubblica nuova»), il che rendeva invisibile il
fatto che il cheatsheet e la reference non la nominassero affatto. È una scelta che l'utente fa e che cambia
il comportamento di tutto lo scheduling: la regola vale. La superficie storage è
cambiata, quindi anche `.claude/skills/new-relational-storage-provider/` **e** il mirror
`.agents/skills/new-relational-storage-provider/`: il delta applicato ai due è identico (verificato
confrontando i due diff; i due file divergevano già a `HEAD` sui nomi `CLAUDE.md`/`AGENTS.md` e su due
paragrafi, drift preesistente e fuori dallo scope della fase 1).

**Humanizer.** La passata è stata rifatta ancora (tool `Skill`, `humanizer`) sui due file toccati per il seam
dell'orologio: `docs/configuration-cheatsheet.md` e `docs/configuration-reference.md`. Ha tolto i due
trattini lunghi che le sezioni nuove avevano introdotto — la reference ne conteneva **uno** in tutto il file
prima della fase 1, il cheatsheet **uno**, e riportarli a quel conteggio è la firma giusta per queste pagine.
Nient'altro da riscrivere: le sezioni nuove seguono la struttura delle sorelle (`**Signature:**`,
`**Default:**`, `**Examples:**`, `**Notes:**`) e la nota del cheatsheet ricalca quella della retention.

La passata precedente, in questa stessa sessione, riguardava tre file riscritti *dopo* quella ancora prima:

| File | Esito di questa passata |
|------|-------------------------|
| `docs/configuration-cheatsheet.md` | riletto; la riga `OccurrenceRetentionDays` resta com'è — il grassetto su una parola sola rispecchia la riga sorella di `DeleteCompletedTasksAfterRetention`, non è enfasi meccanica |
| `docs/configuration-reference.md` | **riscritto** il paragrafo "Occurrence retention": tre frasi di fila incernierate su due punti, e un grassetto su una parola sola dove la sezione sorella ne mette uno su una clausola intera |
| `docs/storage/custom-storage.md` | **riscritte** sei occorrenze di trattino lungo nelle sezioni nuove. Il file non ne conteneva **nessuno** prima della fase 1: sei introdotti in un colpo sono la firma sbagliata, non lo stile della pagina |
| `docs/storage/{sql-server,postgres,mysql,sqlite}-storage.md` | invariati dalla passata precedente, riletti, nessuna riscrittura necessaria |

Il paragrafo su colonne e vincoli, quasi identico su tre pagine di provider, resta volutamente duplicato:
sono pagine di riferimento indipendenti, ognuna deve reggersi da sola.

Il terzo giro non ha toccato nessun file sotto `docs/` — le sue modifiche sono `Directory.Build.props`, due
`CLAUDE.md`, il README della fixture, `packages.md` della skill di integrazione, il piano, questo verbale e
due file di test — quindi nessuna nuova passata `humanizer` era dovuta. La versione nella skill dice ora
«4.0.0, non ancora rilasciata; l'ultima pubblicata è 3.11.0»: un consumatore esterno continua a prendere la
pubblicata.

## Gate 6 — integration-first, niente mock truccati

Le operazioni nuove sono pinnate su parti reali: `EfCoreTaskStorageTestsBase` gira su quattro motori veri
(SQLite su file, SQL Server, PostgreSQL e MariaDB via Testcontainers), gli integration test costruiscono un
`IHost` reale, e il decoratore `FaultInjectingTaskStorage` avvolge uno storage **vero** invece di simularlo.

La fault injection dell'atomicità merita una nota, perché è l'unico punto dove la scelta era fra un test
comodo e un test vero. Il piano chiede un'eccezione dopo l'INSERT dell'occorrenza e prima dell'avanzamento
del cursore. Le quattro implementazioni arrivano lì per strade diverse — la base EF avanza e poi inserisce,
le due procedure inseriscono e poi avanzano, Postgres fa tutto in una CTE — quindi «dopo l'insert» non è lo
stesso punto per tutte. Il guasto è iniettato dove **su tutte e quattro** viene dopo l'INSERT del figlio: un
trigger che fa fallire l'inserimento dello `StatusAudit`, che la base EF stanca dietro l'occorrenza nello
stesso `SaveChanges`, le due procedure scrivono come ultima istruzione e la CTE Postgres come ultimo ramo. La
materializzazione che chiude la serie è la forma che ha quella scrittura. Il test verifica poi che la stessa
materializzazione riesca a trigger rimosso, così il verde non può venire da uno schedule mai eleggibile.

## Gate 7 / D7 — benchmark prima/dopo

I numeri stanno in `benchmarks/RESULTS.md` §P-J e sono stati **rimisurati sull'albero consegnato**
(2026-08-23), dopo i fix ratificati e il round 2: checkout alternati base/patched, 3 ripetizioni, mediane.

| Cella | Δ throughput | Δ alloc/task |
|-------|-------------:|-------------:|
| A4W — dispatch + execute, motore reale | +0,2% | +7,7% |
| LRA in-memory — avanzamento ricorrente | +0,6% | +0,1% |
| LRA SQLite — avanzamento su tabella allargata | -0,5% | +1,3% |
| A4S SQLite p1 — le 3 scritture di lifecycle | +1,2% | +6,7% |

**Nessuna regressione di throughput**: tutto entro ±1,2%, e le due celle stabili (dispersione ≤ 0,7% sulle
sei corse) sono quelle da leggere. L'allocazione cresce, ed è reale: segue le tre colonne nuove di
`QueuedTasks`, la collection di navigazione `Occurrences` e le cinque proprietà nuove di
`TaskHandlerExecutor`. Nessuna delle due è un round-trip in più, che è la formulazione di D7.

---

## Gap chiusi nel terzo giro di certificazione (2026-08-23, sera tardi)

Tre gap, uno per certificatore. Cosa è stato fatto, e dove.

| Gap segnalato | Cosa è stato fatto |
|---------------|--------------------|
| X3: l'albero pinna la forma d'audit **opposta** a quella scritta nelle decisioni («una finalizzazione senza run produce un audit `Queued → Completed`»), senza ratifica scritta | La ratifica esiste: decisioni §3.2, «Ratifiche del maintainer — ciclo 3», dichiara corretta la forma consegnata (un solo audit `Completed`, nessun `Queued` fantasma, perché `WorkerService.FinalizeRecurringSeriesAsync` finalizza **prima** di ogni re-dispatch) e supera il testo di X3 su quel punto. Restava la contraddizione nel piano: la voce della lista test §1 ora dice la stessa cosa e rimanda alla ratifica |
| X6: la fixture di consumer compatibility non veniva caricata contro un assembly 4.0 — `Directory.Build.props` era ancora a 3.11.0, quindi il controllo era 3.11 contro 3.11 | `Directory.Build.props` → **4.0.0** (bump anticipato alla fase 1, decisioni §3.2 ciclo 3). Ora il DLL compilato contro `3.11.0-issue23baseline` esegue davvero contro assembly `4.0.0.0`, e `An_assembly_compiled_against_the_baseline_still_binds_to_the_current_ones` **asserisce la distanza**: la major di riferimento deve essere inferiore a quella caricata, così un bump dimenticato fa cadere il test invece di svuotarlo. Aggiornati il README della fixture, `CLAUDE.md` radice, `test/EverTask.Tests/CLAUDE.md`, `packages.md` della skill di integrazione e la riga Release del piano §7 (il bump non è più lavoro della fase 7) |
| X3: nessun test fissava `LastExecutionUtc = now` su una finalizzazione che non materializza alcuna run | `Finalizing_a_series_should_stamp_LastExecutionUtc_although_nothing_ran` nella suite condivisa (quattro provider) e `Should_stamp_LastExecutionUtc_on_a_finalization_that_ran_nothing` sul negozio in memoria: cinque implementazioni. La riga parte con `LastExecutionUtc` di tre giorni prima e `CurrentRunCount = 4`; dopo entrambe le scritture terminali — l'incondizionata e il compare-and-swap — il valore è quello della finalizzazione, il contatore è fermo e non c'è alcun `RunsAudit`. L'effetto era già in `docs/storage/custom-storage.md`, con la conseguenza sulla retention: ora è anche pinnato |

## Gap chiusi nel secondo giro di certificazione (2026-08-23, sera)

I certificatori di completezza hanno riletto l'albero e questo verbale, e hanno trovato quattordici buchi.
Uno per riga, con cosa è stato fatto.

| Gap segnalato | Cosa è stato fatto |
|---------------|--------------------|
| Gate 4: sei punti del round 2 (R6a/R6b, R7, R8, R10, R12, R13) senza destinazione scritta | Disposizione per iscritto di ognuno in decisioni §3.2, con la ragione e la destinazione |
| Gate 4: R7/R8/R12 senza nota nel piano di fase 4 | Nuovo §4.3 del piano con il lavoro da fare per R6b, R7 e R8; R12 dichiarata **chiusa in fase 1** (`ApplyOccurrenceContract` + test sui quattro provider), quindi non rinviata |
| Gate 4: i follow-up «da girare a issue separate» senza traccia in albero | Nuovo registro `review/recurring-occurrences-followups.md` (F1 R13/SQLite, F2 R10, F3 barriera per pagina, F4 epic lease), richiamato dal piano §7 e dalle decisioni §3.2. Le issue GitHub restano da aprire: il registro è la loro traccia |
| Gate 5: il seam dell'orologio non era nel cheatsheet né nella reference | `docs/configuration-cheatsheet.md` (nota sotto "Service Configuration") e `docs/configuration-reference.md` (`### The Scheduling Clock (TimeProvider)`), più la tabella del gate 5 qui sopra che ora conta **due** superfici invece di una. Passata `humanizer` rifatta sui due file |
| X2: `EfCoreTaskStorage` dichiarava le due capability incondizionatamente mentre ogni operazione richiede un provider relazionale | Le due proprietà rispondono dal provider (risolto una volta e memorizzato): su EF Core InMemory sono `false`, così il rifiuto resta al dispatch. Pinnato da `EfCoreNonRelationalCapabilityTests` (tre test) e, sul versante positivo, da `A_relational_provider_advertises_both_durable_occurrence_capabilities` sui quattro provider. Aggiornati CHANGELOG, `EfCore/CLAUDE.md` e le due copie della skill `new-relational-storage-provider` |
| P9: `FakeTimeProvider` non implementava `CreateTimer`, quindi i `Task.Delay(…, timeProvider)` restavano su timer reali | `FakeTimeProvider` ha ora timer **virtuali**: `Advance` li fa scattare in ordine, con l'orologio fermo sull'istante di ognuno mentre gira la callback. I due test scheduler e quello del parking lot aspettano che il delay sia **armato** (`WaitForPendingTimersAsync`) e poi muovono solo l'orologio: niente più polling reale né segnale di risveglio a fare da sostituto |
| M7: nessun test della barriera a due passate su più pagine | `RecoveryDurableScheduleBarrierTests`: 242 righe vere in `MemoryTaskStorage`, due schedule durevoli sulla **prima** pagina e le loro occorrenze fino alla terza, `WorkerService` reale, e l'ordine delle `ExecuteDispatch` come osservabile. Più il controllo: una serie ricorrente inline **non** viene trattenuta |
| X3: scenario mensile assente (c'era solo `DayInterval(30)`) | `A_monthly_occurrence_scheduled_before_an_elapsed_RunUntil_is_still_executed`: `MonthInterval(1) OnDay 10`, slot il 10 maggio, confine il 15, riavvio il 20, successore naturale il 10 giugno. Su orologio iniettato, perché "un mese" non è un numero fisso di giorni |
| X3: finalizzazione per `MaxRuns` non provata end-to-end | `A_series_whose_run_budget_is_spent_is_finalized_without_running_anything`: cursore **nel futuro**, handler mai raggiunto, unico audit `Completed`, nessun `RunsAudit`, `CurrentRunCount` invariato |
| P9: nessun test storage con `FakeTimeProvider` sui quattro provider | `The_recovery_predicates_should_follow_the_injected_scheduling_clock` in `EfCoreTaskStorageTestsBase`: stessa riga, due posizioni di un orologio finto, e il predicato la classifica diversamente |
| L18 sulla finalizzazione provato solo su `MemoryTaskStorage` | `A_failing_recovery_finalization_should_be_counted_and_cleared_by_a_later_success`, nella suite condivisa: `FaultInjectingTaskStorage` sopra il provider vero e il `WorkerService` reale, via il nuovo helper `TestHelpers/RecoveryHarness` |
| Parità dell'evaluator senza `WeekInterval`, `MonthInterval` e intervalli combinati | Cinque forme in più nella `TheoryData` condivisa (settimanale, mensile, trimestrale con `OnFirst`, due combinazioni di campi intervallo), che alimentano sia la parità sia `EnumerateDueSlotsAsync` |
| Golden JSON senza uno schedule davvero composito | Due golden nuovi: tutti e sei i campi intervallo valorizzati insieme, e le cadenze combinate con configurazione di primo run e limiti. Assemblati dai frammenti già pinnati dai golden a intervallo singolo |
| `AlreadyExists` concorrente non realizzato alla lettera e non ratificato | Ratifica in decisioni §3.2 (perché due writer sullo stesso cursore ottengono `Created`+`CursorMoved`) **e** il test concorrente vero: `…_should_report_AlreadyExists_to_both_callers_racing_a_taken_slot` |
| Finestra di fault injection prescritta assente e non ratificata come sostituzione | Seconda coppia di SQL astratti (`InstallScheduleAdvanceFaultSql`) e `…_should_roll_everything_back_when_the_schedule_advance_faults`: il guasto cade fra insert e avanzamento sulle tre implementazioni che inseriscono per prime. Ratifica del limite della base EF in decisioni §3.2 |

## Gap chiusi nel primo giro (2026-08-23, pomeriggio)

| Gap segnalato | Cosa è stato fatto |
|---------------|--------------------|
| Gate 4 senza record verde per l'albero consegnato | Round 2 della review: riverifica dei cinque fix ratificati sull'albero consegnato, verdetto in fondo a `…-adversarial-review.md` |
| Gate 4, quality floor: verify a più scettici, seconda giuria, loop non eseguiti | Due lenti Codex indipendenti (`xhigh`, read-only): secondo scettico sulle cinque `PLAUSIBILE` e re-review post-fix con giro di completezza. Entrambe `done` |
| Cinque finding `PLAUSIBILE` senza secondo parere | R6c e R14 **confutate**; R6b, R11, R13 confermate con analisi di raggiungibilità; R11 chiusa con fix e test |
| Gate 5, docs riscritte dopo la passata `humanizer` | Passata rifatta sui tre file cambiati; due riscritti |
| Gate 2, evidenza stantia (1292/528/707/711, «il gate 4 non è verde») | Questo verbale, sulla corsa delle 15:07Z dell'albero consegnato |
| Gate 3, evidenza stantia (dichiarava un solo file preesistente toccato) | L'inventario completo qui sopra, e quattro file riportati byte-identici |
| Gate 3, la modifica a `BackwardCompatibilityScheduleDriftTests` senza misura | Modifica **rimossa**; 30 corse per albero a sostegno |
| Gate 7 / D7, numeri di un albero precedente ai fix | A/B rifatto sull'albero consegnato, `RESULTS.md` §P-J aggiornato |
| V3: `IScheduleEvaluator.EnumerateDueSlotsAsync` assente | Aggiunto a interfaccia e implementazione, con `cap` obbligatorio, più sette test (undici casi) in `ScheduleEvaluatorTests`, fra cui il confronto con la griglia percorsa a mano su tutte le forme di schedule |
| Fault injection che non provava il rollback di un figlio già inserito | Il trigger sullo `StatusAudit` descritto nel gate 6, un test condiviso sui quattro provider |
| Doppia materializzazione concorrente: secondo esito non verificato | Ora l'esito del perdente è nominato: `CursorMoved`, con la spiegazione di perché non può essere `AlreadyExists` |
| `RequeueTerminal`: audit mai letti | Il test confronta `StatusAudit` e `RunsAudit` prima e dopo: la traccia sopravvive, il requeue aggiunge la propria transizione e nessun run |
| Matrice §8, riga P9: gamba **limiter** non pinnata | `RateLimiting/RateLimiterDeterministicClockTests`: limiter, gate e parking lot risolti da un container `AddEverTask` reale con orologio iniettato |
| Matrice §8, riga P9: gamba **worker** non pinnata | `DeterministicSchedulingClockTests.The_worker_computes_the_next_occurrence_on_the_injected_clock`: il cursore scritto dopo il run è quello dell'orologio iniettato |

### Dettaglio: la gamba limiter della riga P9

Ogni test di rate limiting costruiva limiter, gate e parking lot a mano passandogli un orologio finto: prova
che leggono l'orologio che **ricevono**, non quale orologio il container dia loro. Ma due dei tre sono creati
da factory scritte a mano in `AddEverTask`, che passano il `TimeProvider` esplicitamente: togliere quelle due
righe lasciava la suite verde. I tre test nuovi li risolvono dal container e guardano un istante che
l'orologio reale non può produrre: lo slot del limiter a `now + 4s` sul 2016, il re-park del gate su quello
slot senza che il pavimento del "past slot" lo sostituisca, la pausa del parking lot che finisce quando
avanza l'orologio finto e non dieci minuti dopo.

Che il pin morda è stato verificato togliendo davvero i due `sp.GetRequiredService<TimeProvider>()` dalle
factory: build a 0 errori e **tutti e tre i test rossi**. Il file è stato poi ripristinato byte-identico
(confrontato con la copia presa prima) e i tre test tornano verdi.

### Dettaglio: la gamba worker della riga P9

`QueueNextOccourrence` chiede all'evaluator la prossima occorrenza passandogli due istanti: lo slot appena
eseguito e il proprio "adesso". Il secondo decide se la risposta è presa così com'è o riallineata come
occorrenza persa. Con l'orologio reale, lo slot 2016 appena calcolato sembra vecchio di un decennio e la
serie salta a oggi; con quello iniettato il cursore scritto è `FrozenNow + 1 minuto`. Il test guarda proprio
quel valore, dopo aver aspettato la metà durevole del run.
