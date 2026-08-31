# Review avversariale — Recurring Schedule Exclusions (#36)

**Scope del diff**: `c426eb1..0dfd852` (4 commit: definizione/builder/validazione/serializzazione delle
esclusioni, valutazione dentro la griglia delle occorrenze, propagazione nella pipeline + analizzatore ET0010,
documentazione).

**Profilo**: **Rapido** — 4 lenti Opus in parallelo (`grid-math`, `no-loss`, `spec-tests`, `parity-perf`) più un
passaggio **Codex Light**, **round singolo**. Nessun round di completeness, nessuna second-jury (vedi
§ Coverage).

**Verifica**: ogni candidato sopravvissuto al triage è stato passato a 2–3 scettici indipendenti con mandato
*refute-by-default* (l'onere della prova è sul finding: confutare cercando la guardia esistente, non
confermare). Verdetto `confirmed` = nessuno scettico ha confutato; `under-verified` = giuria spaccata;
`refuted` = confutazione unanime.

**Nessun fix è stato applicato.** Il working tree è invariato; le fix in tabella sono proposte, non patch.

**Onestà sull'incertezza**: il profilo Rapido esclude per costruzione il round di completeness — quindi
*non* posso affermare che il diff sia stato coperto esaustivamente, solo che le 4 lenti dichiarate hanno
girato senza fallire. Diverse conferme (`spec-tests-*`) sono **lacune di copertura**, non difetti attivi:
sono marcate come tali. Due finding restano `under-verified` con giuria spaccata e sono riportati per intero
con entrambe le posizioni.

---

## 1. Findings confermati e under-verified

### P0

| ID | file:line | Invariante violato | Scenario | Voti scettici | Fix concisa |
|---|---|---|---|---|---|
| `grid-math-4` | `src/EverTask/Worker/WorkerExecutor.cs:485` | Una run ricorrente **già registrata** sulla riga (`CurrentRunCount` incrementato, `Completed`/`UpdateCurrentRun` scritti) non viene mai riconsegnata dai path di schedule-retry o recovery di EverTask. Il cursore di una riga schedule nomina uno slot **pending**; la grace window di `Dispatcher.IsSlipedOccurrenceStillCurrentAsync` esiste proprio per eseguire uno slot che *non* ha ancora girato. La regola gemella in `src/EverTask/CLAUDE.md` ammette il replay del provider-retry solo perché **nulla era stato scritto**. | Schedule inline con storage, esclusioni e un handler con side effect reale. Lo slot S gira e completa; l'advance esaurisce il budget di ricerca esclusioni (200k, §5.4: raggiungibile per drift delle regole di zona o metadata modificati a mano) e `RecordRunBeforeExclusionRetryAsync` (`:1774-1808`) scrive `CurrentRunCount=N+1`, `Status=Completed`, **`NextRunUtc=S`** — cioè lo slot appena eseguito. La riga è ora byte-indistinguibile da «S è pending e non ha mai girato». Al retry, `RetryScheduleDecisionAsync` scarta `ScheduleRunAlreadyRecorded` prima di `ExecuteDispatch` (`:485-487`; il flag è letto solo a `:490` per il backoff) e il dispatcher rientra nella decisione di recovery con `existingNextRunUtc = S`: **S viene rieseguito**. | **3 voti, 0 confutazioni** (high / medium / high). Il voto `medium` corregge il finding: la repro *headline* non è raggiungibile così com'è scritta, ma la variante con restart lo è; il meccanismo di scrittura è confermato riga per riga da tutti e tre. | Nel ramo con storage, quando la delivery porta `ScheduleRunAlreadyRecorded`, **riprendere l'advance** invece di rientrare nella decisione di recovery: ricostruire l'executor dalla riga con `ExecutionTime = row.NextRunUtc` e chiamare `QueueNextOccourrence(..., countsAsRun: false)` — la stessa forma del ramo storage-less. |

### P1

| ID | file:line | Invariante violato | Scenario | Voti scettici | Fix concisa |
|---|---|---|---|---|---|
| `no-loss-1` <br>*(confirmed)* | `src/EverTask/Scheduler/Recurring/ScheduleEvaluator.cs:60` | Spec §2.6 / gotcha 20 (`Scheduler/Recurring/CLAUDE.md`): l'**esaurimento computazionale non è mai esaurimento matematico** — un budget speso non si riporta come «la serie è finita». Spec §5.5 vuole la normalizzazione «budget-guarded come la porta»: una ricerca che non sa rispondere deve **sollevare**, non restituire `null`. Più l'invariante di lifecycle: ogni task persistito è eseguito o lasciato in uno stato che `RetrievePending` recupera. | `RecurringTask.FirstOccurrenceOnOrAfter` (`RecurringTask.cs:1010-1024`) cammina al massimo `MaxBackfillProbeSteps = 64` e chiude con `return slot is { } value && value >= instant ? value : null;`: **il cap-hit è indistinguibile dal «la griglia non ha nulla»**. `NormalizeCursorAsync` (`:60-70`) inoltra il `null` invariato e entrambi i chiamanti lo *finalizzano*: `Dispatcher.cs:1019-1027` → `RecurringRunDecision(null, true)` → `FinalizeExhaustedSeriesAsync` (`Completed`, cursore azzerato); `DueSlotEnumerator.cs:94`. Una serie viva con una finestra di esclusione lunga viene chiusa in silenzio. | **2 voti, 0 confutazioni** (medium, high). Il voto `medium` segnala che lo scenario *illustrativo* del finding (cadenza mensile) è aritmeticamente sbagliato; il voto `high` ha **riprodotto il difetto in un test xUnit temporaneo** (poi rimosso, tree pulito) con `WeekInterval(1, Mon..Fri)`. Il difetto regge, la repro corretta è quella del secondo voto. | Rendere distinguibile la resa: far sollevare a `FirstOccurrenceOnOrAfter` una `ExclusionSearchBudgetExceededException` (o una failure tipizzata sorella) quando esce sul cap di step invece che perché la griglia ha risposto `null`, così il routing §5.4 già esistente la gestisce. |
| `spec-tests-8` <br>*(lacuna di copertura)* | `src/EverTask/Worker/WorkerExecutor.cs:1795` | Spec §8 nomina **tre** varianti: «il catch di budget nel live-advance registra la run (varianti versioned, unversioned e storage-less) prima del park di backoff». Ogni variante è una scrittura distinta che deve atterrare **prima** del park, o la run già eseguita sparisce da `CurrentRunCount`/audit mentre il cursore resta. | Nessun test raggiunge le righe `1793-1806`. Sostituendo il cursore ritenuto con un `null` in stile `result.NextRun` nel ramo unversioned (o invertendo `markCompleted`), la riga viene scritta con `NextRunUtc = null`: la serie durable/inline è **finalizzata da un fallimento di valutazione transitorio**, esattamente ciò che §5.4 vieta — e la suite resta verde. Enumerati i 16 file di test che toccano le esclusioni: solo due entrano nel live-advance con budget esaurito (`OccurrenceProviderIntegrationTests.cs:1032`, storage-less; `RescheduleIntegrationTests.cs:1217`, versioned/`taskKey`). | **2 voti, 0 confutazioni** (high, high). Entrambi hanno enumerato tutte le vie per raggiungere il catch (`ExclusionSearchBudgetExceededException` ha un solo sito di lancio, `RecurringTask.cs:708`) e non hanno trovato copertura né una mutazione intercettata altrove. | Un test per ramo residuo: uno schedule versioned (task-keyed, indirizzabile via `ITaskScheduleManager`) e uno unversioned, entrambi con definizione filtrata «vuota per sempre», asserendo dopo l'advance che `CurrentRunCount` è cresciuto di esattamente 1 e che `NextRunUtc` è ancora lo slot eseguito. |
| `no-loss-0` <br>**under-verified** | `src/EverTask/Scheduler/Recurring/RecurringTask.cs:722` | Stesso invariante di `no-loss-1`, più il contratto documentato di `TryJumpUniformGrid` (`:1279-1285`, `:1324-1332`): un `null` significa «questa non è davvero una griglia costante, ricadi sul walk», **mai** «non c'è occorrenza». | `AdvanceBasePastExcludedRegion` restituisce la risposta del jump alla lettera; un `null` esce dal loop `candidate is { } current` (`:693`) e ritorna `null` (`:713`), che ogni consumatore legge come fine serie. | **1 refuted (high) / 1 confermato (medium) — giuria spaccata.** Il confutatore sostiene che una griglia che passa `IsUniformGrid()` non può fallire il self-verify a `:1331`, quindi il trigger non esiste. Il voto contrario **ha costruito la sequenza concreta**: `HourInterval{Interval=1, OnMinute=30}` + `MinuteInterval{Interval=45}` + `SecondInterval{Interval=20}` passa tutti i gate `:1248-1277` e **non** è a passo costante. Il finding sopravvive con la sua repro originale sbagliata e una repro sostitutiva plausibile ma non eseguita. **Da verificare con un test prima di trattarlo come difetto.** | Distinguere i due `null`: dare a `TryJumpUniformGrid` una forma try che riporti «non verificabile» separatamente da «nessuna occorrenza»; oppure, più economico, in `AdvanceBasePastExcludedRegion` ricadere sull'advance ancorato slot-per-slot quando il jump risponde `null`. |

### P2

| ID | file:line | Invariante violato | Scenario | Voti scettici | Fix concisa |
|---|---|---|---|---|---|
| `no-loss-2` | `src/EverTask/Worker/WorkerExecutor.cs:1810` | `src/EverTask/CLAUDE.md`: «un re-park **fallito** è un **evento** di errore, non solo una riga di log, perché dietro non c'è nessun poller; tre siti lo dicono in modo identico — `Dispatcher.ParkProviderRetryAsync`, `OccurrenceMaterializer.ReParkAfterFailureAsync`, `WorkerExecutor.DeferScheduleForProvider`». | `DeferScheduleForExclusionAsync` (`:1810-1833`) passa a `ProviderRetryParker` semplici callback `[LoggerMessage]` (`ExclusionSearchDeferred` 1244 / `ExclusionSearchRetryParkFailed` 1245) dove il gemello strutturale `DeferScheduleForProviderAsync` (`:1738-1772`) passa `RegisterEvent`. `ParkAsync` inghiotte l'eccezione dopo aver invocato `registrationFailed` e i chiamanti scartano l'esito: **nessun evento esce da nessuna via**. Riga `Completed`/`Queued` con cursore passato stantio, nessuna registrazione nello scheduler, e il recovery è solo allo startup. | **2 voti, 0 confutazioni** (high, high). | Instradare entrambi i callback via `RegisterEvent` come fa `DeferScheduleForProviderAsync` (Warning per il deferral con `StandingInstant`/failures/`retryAt`, Error per il park fallito) e dare al rifiuto un report proprio in stile `ScheduleReparkRefused` invece di riusare `NextOccurrenceRefusedBySuccess…`. |
| `spec-tests-9` <br>*(lacuna di copertura)* | `src/EverTask/Dispatcher/Dispatcher.cs:1019` | Spec §5.5/§8: «il recovery inline schedula l'istante normalizzato **senza scrivere**, e lo ri-deriva identico al passaggio successivo». La normalizzazione deve avvenire **prima** dei rami durable / preserved-future-cursor / grace, e non deve essere persistita. | Cancellando l'intero blocco `:1019-1029`, o riordinandolo dopo l'early return `if (recurring.IsDurable && existingNextRunUtc.HasValue)`, **tutti i test passano**. Una riga inline il cui cursore memorizzato è diventato escluso (un reschedule che aggiunge `ExceptWeekends`, un cambio di regole di zona, metadata sostituiti a mano) viene parcheggiata ed eseguita **allo slot escluso** al riavvio. `NormalizeCursorAsync` ha 3 call site; ogni test che asserisce la normalizzazione punta al planner durable o al manager, mai a `DecideRecurringRunAsync`. | **2 voti, 0 confutazioni** (high, high). Il secondo nota che il blocco non emette log né evento: **solo un'asserzione sull'istante schedulato può osservarlo**. | Test di recovery inline (non-durable): riga ricorrente `Queued` con `DailyAtNoonExceptWeekends()`, `CurrentRunCount >= 1` e cursore su un sabato; dopo il restart asserire che la delivery scatta al lunedì successivo a mezzogiorno **e** che `NextRunUtc` della riga è ancora il sabato. |
| `spec-tests-10` <br>*(lacuna di copertura)* | `test/EverTask.Tests/RecurringTests/RecurringExclusionMathTests.cs:156` | Spec §5.2: «il cron non cammina mai una regione esclusa slot per slot — un cron ogni-minuto che attraversa una finestra di 30 giorni deve costare ~1 probe, non 43.200». La claim è una claim di **costo**, ed è esattamente ciò per cui esiste il ramo cron in `AdvanceBasePastExcludedRegion`. | Il test asserisce solo il valore di uscita: un refactor che elimina o riordina il ramo cron (che oggi sta dopo il check `IsUniformGrid()` ed è raggiunto solo perché il cron forza `IsUniformGrid()` a false) lascia la suite verde mentre ogni chiamata alla porta costa 43.200 chiamate a Cronos — e `CountMissedOccurrences` chiama la porta ripetutamente. | **2 voti, 0 confutazioni** (medium, high). Il secondo ha costruito il mutante end-to-end e sopravvive. Nessun difetto attivo: è copertura. | Contare i probe: asserire su un conteggio di iterazioni, o usare una regione abbastanza ampia da far superare il budget (e quindi sollevare) a un walk per-slot. |
| `spec-tests-12` <br>*(difetto attivo, latente)* | `analyzers/EverTask.Analyzers/ScheduleTimeZoneAnalyzer.cs:172` | Spec §4.2: «l'analizzatore deve considerare la catena completa: qualunque `Except`/`ExceptWeekends` ovunque nella catena sopprime la diagnostica (conservativo, mantiene la soglia zero-falsi-positivi)». La metà *forward* di `HasExclusionInCompletedChain` copre le chiamate che vengono **dopo** `InTimeZone`. | In `TryGetOuterInvocation` (`:166-185`) il loop di risalita (`:171-172`) lascia `current` **sul** nodo di conversione più esterno, mentre `:177` confronta `ReferenceEquals(Unwrap(outer.Instance), current)`: `Unwrap` toglie le conversioni e restituisce l'invocation interna, quindi il confronto **non può mai essere vero** in presenza di una conversione. La camminata forward si ferma al primo nodo di conversione → ET0010 falso positivo su una catena che *ha* l'esclusione. La camminata backward (`:144-151`) è coerente perché fa `Unwrap` su entrambi i lati. | **2 voti, 0 confutazioni** (high, medium). Il primo ha **verificato empiricamente** con una sonda temporanea contro l'analizzatore reale: lo scenario *dichiarato* dal finding (interfaccia base senza `new`) **non** riproduce, ma il difetto meccanico è reale. Severità sovrastimata nel finding originale: oggi latente, nessuna catena pubblica lo attiva. | Un token: `ReferenceEquals(Unwrap(outer.Instance), Unwrap(current))` — oppure tenere l'operando pre-walk come identità confrontata (`ReferenceEquals(Unwrap(outer.Instance), operation)`). |
| `parity-perf-14` <br>**under-verified** | `src/EverTask/Scheduler/Recurring/RecurringTask.cs:693` | Gotcha 7: la primitiva di skip-forward «non deve mai restituire un valore `<= after`»; ogni walk del file porta un bail esplicito di no-progress (`:973`, `:1017`). Il loop di advance ancorato introdotto da #36 non ne ha nessuno. | Cron `0 * * * *` con `InTimeZone(z)` ed `Except(OnDays(Saturday))` su una zona la cui fall-back rende `IsAmbiguousTime(midnight)` vera: `exit` può risolversi al **primo** passaggio della mezzanotte, quindi `exit <= candidate`, e il ramo cron calcola lo slot successivo da `exit.AddTicks(-1)` come istante assoluto, senza mai confrontarlo con `candidate`/`current`. | **1 refuted (high) / 1 confermato (medium) — giuria spaccata.** Il confutatore: gli altri due regimi (uniform e non-cron) sono forward-per-costruzione, quindi il buco è solo nel ramo cron ed è irraggiungibile. Il voto contrario **ha verificato `exit <= candidate` contro una zona reale** (`America/St_Johns`, fall-back alle 00:30) ma concede che la severità e la raggiungibilità end-to-end sono sovrastimate. **Non trattarlo come difetto senza una repro eseguita.** | Aggiungere lo stesso bail difensivo dei walk gemelli dopo `AdvanceBasePastExcludedRegion`: `if (candidate is { } next && next <= current) return null;` — o meglio, clampare dentro `AdvanceBasePastExcludedRegion` ricadendo su `GetNextBaseOccurrence(baseGrid, candidate, ...)` quando l'uscita non è strettamente avanti. |

---

## 2. Clustering per root cause

### Cluster A — «il `null` sovraccarico»: esaurimento computazionale letto come fine della serie
**Membri**: `no-loss-1` (P1, confirmed) · `no-loss-0` (P1, under-verified) · `parity-perf-14` (P2, under-verified)

Tre finding, **una sola causa a monte**: nella famiglia di primitive della griglia (`FirstOccurrenceOnOrAfter`,
`TryJumpUniformGrid`, `AdvanceBasePastExcludedRegion`) il valore `null` porta oggi **tre** significati diversi
— «la matematica non ha occorrenze», «ho finito il budget/gli step», «non posso verificare questa forma,
ricadi sul walk» — e tutti i consumatori a valle lo collassano nel primo. Spec §2.6 e la gotcha 20 vietano
esattamente questo collasso, ma lo vietano *per contratto*, non *per tipo*.

- **Fix a monte (raccomandata)**: rendere la resa distinguibile nel tipo di ritorno delle primitive — una
  failure tipizzata (`ExclusionSearchBudgetExceededException` o sorella) per il give-up computazionale, e una
  forma try separata per il «non verificabile» del jump. Il routing §5.4 esiste già e assorbe la failure
  tipizzata; i tre finding chiudono insieme, e la classe di bug si chiude per ogni futura primitiva.
- **Mitigazioni per-sito (fallback)**: guardia `next <= current` dopo l'advance in `FilterExcludedCandidates`;
  fallback allo slot-by-slot anchored quando il jump risponde `null`. Chiudono `no-loss-0` e `parity-perf-14`
  ma **non** `no-loss-1`, che vive sul lato `ScheduleEvaluator`.

**Attenzione**: due dei tre membri sono `under-verified` con giuria spaccata. Il cluster **non** giustifica da
solo un refactor delle primitive; `no-loss-1` sì, ed è già stato riprodotto.

### Cluster B — il path di retry §5.4 non è un vero gemello del provider-retry
**Membri**: `grid-math-4` (P0) · `no-loss-2` (P2) · `spec-tests-8` (P1, copertura)

`DeferScheduleForExclusionAsync` + `RecordRunBeforeExclusionRetryAsync` sono stati scritti *sul modello* di
`DeferScheduleForProviderAsync` ma divergono in tre punti, e le tre divergenze sono lo stesso errore: il nuovo
path **scrive** prima di parcheggiare (mentre il provider-retry non scrive nulla, ed è per questo che il suo
replay è lecito), **non pubblica eventi** dove il gemello li pubblica, e **non è coperto** su nessuna delle sue
varianti di scrittura.

- **Fix a monte**: riallineare il path di esclusione al gemello — riprendere l'advance invece di rientrare
  nella decisione di recovery quando `ScheduleRunAlreadyRecorded` è presente (chiude il P0), e instradare
  entrambi i callback via `RegisterEvent` (chiude `no-loss-2`). I test proposti per `spec-tests-8` sono gli
  stessi che servono a verificare la fix del P0: **un solo intervento, una sola batteria di test**.
- Trattarli come tre fix indipendenti è il modo per lasciarne uno indietro.

### Cluster C — rami senza osservabile
**Membri**: `spec-tests-9` (P2) · `spec-tests-10` (P2)

Causa comune: due rami del diff non producono **nessun** side effect osservabile (né log, né evento, né
scrittura) — l'unico osservabile è l'**istante schedulato** in un caso e il **costo** nell'altro. Nessuno dei
due può essere coperto con le asserzioni idiomatiche del repo, che sono asserzioni di valore su riga
persistita. Non c'è una fix a monte: sono due test da scrivere, ognuno con la propria tecnica (asserzione
sull'istante di delivery; conteggio probe o budget stretto).

### Cluster D — isolato
`spec-tests-12` (ET0010, un token). Nessuna relazione con gli altri; unico membro, unica fix.

---

## 3. Findings confutati (con il motivo della confutazione)

| ID | Sev. proposta | Motivo della confutazione | Voti |
|---|---|---|---|
| `spec-tests-7` | P1 | «Test truccato» non regge: `RecoveryHarness.CreateRecoveryService` costruisce il **vero** `WorkerService` interno su un **vero** `MemoryTaskStorage` (non un `Mock<ITaskStorage>` — contrasto col test vicino a `:104`); solo la *sorgente* dell'eccezione è mockata. Il test discrimina davvero i due esiti contrapposti dall'invariante (riga `Queued` con cursore **ritenuto** dopo il primo fallimento, `Failed` al cap, mai `Completed` con cursore nullo). | 2 refuted (medium, high) |
| `grid-math-5` | P2 | L'NRE esiste chiamando `CalculateNextRun` su una definizione costruita a mano con `Days = null!`, ma **nessun path raggiungibile** ci arriva: ogni sito che accetta una definizione costruita a mano o deserializzata valida prima (`Dispatcher.cs:393`, `TaskScheduleManager.cs:367`, `RecoveredTaskFactory.cs:93` — l'unico `Deserialize<RecurringTask>` che alimenta l'esecuzione) e `ValidateExclusions` (`:233-235`) ripara i null in place. L'altro deserializzatore (`Monitor.Api/Services/TaskScheduleFacts.cs:121`) non valuta mai la griglia. | 2 refuted (high, high) |
| `no-loss-3` | P2 | Il costo per-probe **è già limitato per design**: `ValidateExclusions` (`:285-289`) rifiuta `Dates.Length + Ranges.Length > 1000` — un cap **combinato**, quindi il «~2000 confronti per probe» del finding è impossibile (tetto: 1000). E la repro headline è irrealizzabile: `Every(1).Minutes()` è una base uniform, quindi prende il jump O(1) su `TryJumpBaseUniformGrid` — una finestra di mesi costa ~1 iterazione, esattamente ciò che §5.2 impone. | 2 refuted (high, high) |
| `spec-tests-11` | P2 | Il meccanismo descritto non esiste: `FindNthSlotFromEndAsync` (`DueSlotEnumerator.cs:704-772`) **non interpola** — è una bisezione a midpoint puro sull'asse dei tick (`:717`). Richiede solo la monotonia di «quanti slot dopo t», che vale per qualunque insieme di istanti: rimuovere slot allarga i plateau, non rompe la correttezza. `MaxBisectionSteps = 64` copre l'intero range dei tick indipendentemente dalla griglia. | 2 refuted (high, high) |
| `parity-perf-18` | P2 | L'invariante citato è **mal citato**. Spec §5.7 (D7) recita testualmente: «`Exclusions == null` corto-circuita prima di tutto in ogni nuovo check. Il gate A/B: le celle P-K … non devono mostrare regressioni misurabili **per schedule senza esclusioni**». D7 è esplicitamente limitato al path a esclusioni nulle — quello che il finding stesso ammette essere corto-circuitato. L'allocazione `Base()` per-step è reale ma non viola l'invariante invocato. | 2 refuted (high, high) |

---

## 4. Coverage

**Lenti fallite: [nessuna].** Tutte e 4 le lenti Opus (`grid-math`, `no-loss`, `spec-tests`, `parity-perf`)
hanno completato e prodotto candidati; il passaggio **Codex Light** ha girato in sola lettura. Nessun batch di
verifica fallito.

**Triage**: **19 candidati → kept 14, dropped 0 (dichiarati), duplicati 5, batch falliti 0.**
Nessun candidato è stato scartato per giudizio di merito: i 5 rimossi erano duplicati esatti di finding già
presenti nel ledger. I 14 sopravvissuti sono tutti stati verificati.

**Esito della verifica**: 7 `confirmed`, 2 `under-verified` (giuria spaccata: `no-loss-0`, `parity-perf-14`),
5 `refuted`.

**Limiti dichiarati del profilo Rapido**:
- **Nessun round di completeness** — non è stata fatta una seconda passata per stimare cosa le lenti hanno
  mancato. La copertura del diff **non** è dimostrata, solo dichiarata per lente.
- **Nessuna second-jury** — i due `under-verified` restano con giuria 1-1; il profilo Rapido non prevede il
  tie-break con una terza giuria. Sono riportati con entrambe le posizioni e **non** vanno trattati come
  difetti confermati senza una repro eseguita.
- **P2 non-difetto senza scettici**: **nessuno**. Ogni finding kept è passato per almeno 2 scettici; non ci
  sono osservazioni di stile o nit non verificati in questo report.

**Attribuzione Codex**: il ledger traccia la lente d'origine ma non separa i contributi Codex Light da quelli
delle 4 lenti Opus dopo il merge del triage. Non posso quindi quantificare onestamente quanti dei 14 kept
provengano dal passaggio Codex.

**Note sui verdetti che correggono i finding**: tre `confirmed` sono stati confermati *con correzioni*
sostanziali dagli scettici (`grid-math-4`: repro headline non raggiungibile, variante con restart sì;
`no-loss-1`: scenario illustrativo aritmeticamente errato, repro corretta eseguita dal secondo scettico;
`spec-tests-12`: scenario dichiarato non riproduce, meccanismo reale ma latente e severità sovrastimata). Le
correzioni sono riportate nella colonna «Voti scettici» e vanno lette prima di scrivere la fix.

---

Ledger grezzo completo (voti, ragionamenti integrali degli scettici, campi non riportati qui):
`E:/Archivio/Sviluppo/Web/EverTask/review/recurring-exclusions-adversarial-review.raw.json`
