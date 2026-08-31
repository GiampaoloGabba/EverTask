# Adversarial review — riscrittura clean-room di HandlerRegistrar + passaggio a MIT

**Target**: worktree `E:\Archivio\Sviluppo\Web\EverTask-mit`, branch `feature/mit-license` (base
`03067c4` = master), modifiche non committate.
**Profilo**: Completo — 3 finder Fable + Codex **Full** (3 run GPT-5.6 Sol @ xhigh, read-only, una per
lente), skeptic Opus @ high, second-jury Codex sul finding contested.
**Lenti** (richieste dall'utente): parità funzionale vs vecchia implementazione, performance, più
correttezza/edge/qualità-test.

## Verdetto

**Nessuna perdita funzionale. Zero P0/P1 su tutti e sei i motori di ricerca.** La parità osservabile
è confermata area per area da entrambi i motori (Codex e Fable, indipendenti): descriptor identici
(servizio, implementazione, lifetime, ordine, semantica TryAdd), first-wins identico, warning G1/G2
con stessi trigger/conteggi/ordine, consumer intatti (wrapper eager, resolver lazy, logging dei
warning), chore di licenza complete senza riferimenti pendenti.

Le uniche differenze osservabili, tutte dichiarate e tutte a favore:

1. **Fix di un crash latente all'avvio** (era il P2 "contratto sotto-dichiarato", ora documentato nel
   CHANGELOG): il vecchio filtro duplicati usava `Type.GetInterface("IEverTaskHandler`+"`"+`1")` — con un
   handler multi-interfaccia più un duplicato su uno dei suoi task, `AddEverTask` crashava con
   `AmbiguousMatchException`. Provato dal vivo: il vecchio codice non riesce nemmeno a scansionare il
   nuovo assembly di test. Il nuovo raggruppamento per identità esatta è immune.
2. **Wording del warning G2** (dichiarato): ora nomina esplicitamente la scelta dello scanner.
3. **Delta interno-only**: il double-scan della stessa assembly non emette più il vecchio warning
   nonsense "using 'H', ignoring [H]" (percorso irraggiungibile dall'API pubblica, che deduplica).

## Performance (lente dedicata)

- **Benchmark misurato** (1000 iter × 5 round interleaved, mediana, stesso output — 193 descriptor):
  OLD 1064.25 µs/op, 699,630 B/op → **NEW 74.09 µs/op (-93.0%), 80,494 B/op (-88.5%)**.
- Analisi asintotica (Codex): il nuovo vince su ogni forma reale (molti contratti, ereditarietà
  profonda, molte interfacce, assembly enormi, open generics patologici). Unico caso in cui il
  vecchio vincerebbe: migliaia di handler duplicati per lo stesso task (guardia `List.Contains`
  O(D²), break-even ≈ D>1000, costo ~50-100 ms una tantum a D=10.000) — accettato come non-difetto,
  commento nel codice già documenta l'assunzione.
- Hot path runtime non toccati (la registrazione è solo composition-time); i 3 file con header
  rimossi hanno diff di soli commenti (verificato da 2 motori).

## Findings e disposizioni

| # | Finding | Sev | Fonti | Verdetto | Disposizione |
|---|---------|-----|-------|----------|--------------|
| D | Warning G2 identifica i tipi con `.Name` semplice (ambiguo su collisioni di nome / generici; cita l'ordine per assembly senza stampare assembly) | P3 | Codex-edge + Fable-edge | Skeptic 1-1 (contested) → **jury Codex: FOLLOW-UP** | Ereditato verbatim da master, G1 già usa FullName, nessun contratto consumer. Follow-up suggerito: formatter amichevole + nome assembly. Non in questa PR. |
| A | Guardia anti-duplicato O(D²) | perf-P2→nit | Codex-perf + Fable-perf | Accettato come non-difetto | Solo patologico (D>1000 duplicati per UN task). Eventuale HashSet oltre soglia se mai diventasse reale. |
| E | Test RTLE non provava l'atomicità | test-quality | Codex-edge + Fable-edge | **FIXED** | Ora scansiona [assembly buona, rotta] e asserisce zero descriptor. |
| F | Double-scan non pinnava la guardia `Duplicates.Contains` | test-quality | Codex-edge + Fable-edge | **FIXED** | Il test ora asserisce warning Triplicate unico e loser elencati una volta. |
| I | Test "lazy executor" non toccava `GetOrResolveHandler` | test-quality | Fable-edge | **FIXED** | Ora costruisce un `TaskHandlerExecutor` lazy con l'AQN del loser e risolve dal percorso reale. |
| J | Flag `isHandler` senza fixture negativo | test-quality | Fable-edge | **FIXED** | Aggiunto `OpenGenericNotAHandler<T>` + assert di silenzio. |
| K | Contratto di parità sotto-dichiarava il fix del crash | P2 (docs) | Fable-parity + Codex-parity | **FIXED (docs)** | CHANGELOG qualificato; deviazione dichiarata qui e in design-decisions.md. |
| M | Base del worktree stale (master~1) | P2 (ops) | Fable-perf | **FIXED** | Fast-forward a `03067c4` (il commit toccava solo 3 file di test non modificati da noi). |
| B | Probe `isOpen` per ogni tipo concreto | nit | Codex-perf + Fable-perf | Dropped | 2 letture di flag per tipo, sotto la soglia di misurabilità; lazy-compute complicherebbe il loop. |
| C | Self-binding ritentato per contratto | nit | Codex-perf | Dropped | Parità col vecchio; TryAdd no-op. |
| G | Fixture "patologici" nell'assembly di test condivisa | test-quality | Codex-edge | Dropped (dichiarato) | Prassi già esistente del progetto (`TestTaskHanlderDuplicate`); grep conferma che nessun test esistente se ne accorge. |
| H | `RegisterTasksFromAssembly(null!)` → NRE tardivo | nit | Codex-edge | Follow-up | Pre-esistente, boundary di configurazione, fuori scope. |
| N | Assembly Reflection.Emit non-collectible | nit | Codex-perf + Fable-perf | Accettato | O(5) fisse per run del testhost. |

## Attacchi di test-quality FALLITI (i test sono solidi)

`InDefinedTypesOrder` non è tautologico (deriva l'ordine indipendentemente; reverse/sort/last-wins
fallirebbero); l'override emesso via Reflection.Emit non può essere silenziosamente vacuo (base
astratta → `CreateType()` esploderebbe); nessuna trappola Shouldly/expression-lambda; nessuna race
con il parallelismo xUnit; `Mock<Assembly>` valido su net8/9/10; i rename in AssemblyResolutionTests
non hanno perso copertura load-bearing; nessun test esistente si rompe per i nuovi fixture.

## Verifica finale

- 42/42 verdi sulle suite di registrazione (incl. i 4 test rafforzati post-review).
- Suite completa Docker-free su net8/net9/net10: core 1235×3, storage, monitoring — verde (i 2 fail
  iniziali del monitoring erano il `wwwroot` della dashboard non buildato nel worktree fresco).
- Gate finale rilanciato sulla base aggiornata `03067c4` (esito nel messaggio di chiusura).

## Coverage

Sei flussi di find, tutti completati senza errori: Codex xhigh × 3 lenti (parity 6.5KB, perf 6.8KB,
edge 7.3KB di referto) + finder Fable × 3 (stesse lenti, 321k token). Skeptic: 2 × Opus @ high sul
solo difetto sopravvissuto (letture scoped). Second-jury Codex sul contested. Nessuna lente fallita,
nessun risultato perso, nessun cap silenzioso sui finder. Non coperto (dichiarato dai motori): tipo
con `GetInterfaces()` che lancia, `TypeBuilder` non finalizzato, collisioni di nomi generici/nested
nel testo dei warning (parte del follow-up D), elementi null nella lista assembly (pre-esistente, H).
Nessuna opinione legale formale sul clean-room (review tecnica, non parere di provenienza).

## Addendum — follow-up implementati nella stessa branch (su richiesta utente)

Dopo la chiusura della review, D, H e i due analyzer sono stati implementati qui:

- **D**: warning G2 con identità namespace-qualificate + assembly per ogni handler, nomi generici
  espansi al livello che li dichiara (`Outer<Int32>.InnerTask`), formatter con catena dei
  declaring type e partizione dell'arità.
- **H**: `RegisterTasksFromAssembly/ies` validano al boundary (`ArgumentNullException` /
  `ArgumentException` per elemento null, atomico, ParamName pinnato dai test).
- **Analyzer ET0011/ET0012** (`HandlerRegistrationAnalyzer`, categoria `EverTask.Registration`):
  specchio compile-time dei warning G1/G2 — open-generic handler mai registrabile (ET0011, anche
  nested in container generico) e handler duplicati per lo stesso contratto chiuso nella stessa
  compilation (ET0012, compilation-end, identità CSharpErrorMessageFormat, ordine deterministico,
  generated code incluso). ET0010 resta riservato (#34). Cross-assembly resta warning a runtime.

**Review incrementale Codex (xhigh, read-only)** sul delta: 1 medium (generated code escluso dai
flag) + 3 low (ordine non deterministico multi-contratto, collisioni di nome in
MinimallyQualifiedFormat, formatter nested-generic) — **tutti e quattro fixati** insieme ai
test-gap segnalati (partial handler, same-leaf-name su namespace diversi, nested-in-generic,
nested-generic G2, ParamName + atomicità del test null). Aree dichiarate pulite da Codex:
concorrenza dell'analyzer, comparer, tag CompilationEnd, release tracking, eligibilità struct,
AllInterfaces via base di metadata, null-validation.

Verifica: 44/44 registrar/resolution, 57/57 analyzer, gate completo Docker-free verde su
net8/net9/net10.

## Prossimo passo

Nessun follow-up aperto. Per trasformare il report in un piano operativo:
`/review-to-plan E:\Archivio\Sviluppo\Web\EverTask-mit\review\mit-relicense\adversarial-review.md`
