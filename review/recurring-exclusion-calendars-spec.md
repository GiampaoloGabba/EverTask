# Named exclusion calendars — spec (issue #36, tranche 2)

Status: **APPROVED v1.0** — two Codex adversarial review rounds (same session as the tranche-1 spec, full
code access). Round-2 verdict: GO with listed changes, all applied — snapshot created and registered by
`AddEverTask` immediately after the configuration callback (never a lazy freeze), the 16-name cap
enforced inside the one shared resolution primitive before any lookup, `BackfillFrom` scoped to NEW
series (reschedule never rewinds), and an edit that invalidates the resolved union poisons at recovery.
Round 2 declared further review diminishing returns. Ready for implementation.
Builds ON TOP of the approved `review/recurring-exclusions-spec.md` (v1.0, implemented and certified):
everything there stays; this document only adds the named-calendar source of exclusions. Terms
(exclusion clock, region exit, budget, canonical form) are inherited, not restated.

## 1. Goal

A holiday/blackout calendar defined ONCE at the host and reused by name across schedules — the Quartz
calendar shape, EverTask-style:

```csharp
// host registration (config time)
services.AddEverTask(opt => opt
    .AddScheduleCalendar("it-holidays", cal => cal
        .OnDates(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 6), new DateOnly(2026, 12, 25))
        .OnDates(Pasquetta2026, Ferragosto2026))
    .AddScheduleCalendar("maintenance", cal => cal
        .Between(maintenanceStart, maintenanceEnd)))

// schedule side
.Schedule().EveryDay().AtTime(new TimeOnly(8, 0)).ExceptCalendar("it-holidays")
.Schedule().Every(4).Hours().ExceptWeekends().ExceptCalendar("it-holidays", "maintenance")
```

Adding next year's holidays is ONE host-config edit; no schedule row changes, no redeploy of definitions.

## 2. Scope decision: STATIC registrations only (v2 of the feature, v1 of calendars)

A calendar is a **declarative, config-time registration**: a name bound to an exclusion SET built with the
same `IExclusionBuilder` shapes tranche 1 shipped (days, dates, absolute ranges). No dynamic source in this
tranche: a calendar whose dates come from a database or service is exactly an `INextOccurrenceProvider`
concern (determinism must be DECLARED, failures must park, I/O must be async) and stays out — the issue
itself flags dynamic as "falls under the custom-provider constraints". The persisted shape (names) does not
change if a dynamic source ever arrives; only the registry entry would.

## 3. Public surface

### 3.1 Host registration

```csharp
EverTaskServiceConfiguration AddScheduleCalendar(string name, Action<IExclusionBuilder> configure)
```

- Name: non-empty after TRIMMING, case-sensitive, max 100 chars. ONE canonicalization rule everywhere —
  registration, `ExceptCalendar` arguments and persisted `Calendars` elements are all trimmed before
  length check, sort, dedup and lookup — so no pair of surfaces can disagree about the same name.
  Duplicate registration of the same (trimmed) name throws at host build (`InvalidOperationException`) —
  silent last-wins would make two hosts disagree quietly.
- The callback uses the EXISTING `IExclusionBuilder` (no new builder type). An empty callback throws, like
  `Except`.
- The built set is validated and CANONICALIZED at registration with tranche 1's exact rules (sorted/deduped
  days and dates, UTC-normalized merged ranges, 1000-entry cap per calendar, all-7-days refusal).
- Registrations collect in the `EverTaskServiceConfiguration` during the `AddEverTask` callback.
  **Immediately after the configuration callback returns, `AddEverTask` creates the deep immutable
  calendar snapshot (cloned arrays, immutable dictionary) and registers THAT snapshot instance**; neither
  the registry nor its DI registration retains `EverTaskServiceConfiguration`, so a later mutation of the
  configuration object cannot affect it — even before the first service resolution (DI singletons are
  lazy; the freeze must not wait for one). That frozen snapshot is what makes a calendar deterministic
  within a process lifetime (§5.3).

### 3.2 Builder

```csharp
IBuildableSchedulerBuilder ExceptCalendar(params string[] names)
```

- Same DIM + per-builder `new` redeclaration pattern as `Except`/`ExceptWeekends` (all six interfaces).
- Additive and repeatable; unions with inline `Except`/`ExceptWeekends`.
- Empty array or an empty/whitespace name throws `ArgumentException` at the call site; names are trimmed
  (§3.1's one rule).
- **A schedule may reference at most 16 calendars** (after dedup) — refused by `Validate()` above it. The
  bound exists so the resolved union stays bounded (§5.4); nobody names a thousand calendars on one
  schedule on purpose.

### 3.3 Persisted shape

`ScheduleExclusions` gains one member:

```csharp
public string[] Calendars { get; set; } = [];
```

- Additive JSON member, three distinct serialization contracts (all pinned by tests): a row with NO
  exclusions stays byte-identical, before and after; a tranche-1-shaped exclusion JSON (no `Calendars`
  member) READS correctly; its rewrite intentionally adopts the new canonical shape with `"Calendars":[]`
  (legal only because the exclusion shape is unreleased — both tranches ship in 4.0.0). Sorted +
  deduplicated + trimmed by `Validate()` (canonical form), 100-char bound enforced on persisted elements
  too.
- Only the NAMES are persisted, never the dates — the calendar's content lives in host config, which is
  the point: the row keeps meaning "whatever it-holidays means on the host that evaluates it".
- `ValidateExclusions()` no longer normalizes to `null` when `Calendars` is non-empty (a calendars-only
  exclusion set is legal); the null-array leniency covers `Calendars` too; a `null`/empty/whitespace
  element inside it is corrupt metadata (poison path, like a null range).

## 4. Semantics

### 4.1 Resolution is by name, at evaluation time, through the registry

`IsExcluded(slot)` consults the union of the inline set and every named calendar's set, resolved through
the registry. Since the registry is immutable per process, resolution is a dictionary hit and the merged
view can be cached per evaluation pass (an implementation concern; the contract is: one door call sees ONE
consistent union).

**Editing a calendar is FORWARD-ONLY, and applies from the next process start.** An edit changes the
evaluator's answers from the current cursor onward — it never rewinds a cursor, never revokes an already
materialized occurrence, never filters a first-run override, never clears a standing `Halt`, and the
persisted exclusion-retry marker soundly resumes its already-recorded advance against the new union.
Concretely:

- Within one process lifetime the frozen registry snapshot makes every count, bisection and plan
  internally consistent — `SkipOldest`'s monotonicity and the `Halt` threshold never see a mid-flight
  change (§5.3).
- A WIDENED calendar (more excluded) may normalize the current cursor forward past a now-excluded slot;
  occurrences already materialized for such slots are NOT revoked — they were created under the grid that
  owed them and recovery handles them independently, which is at-least-once doing its job.
- A NARROWED calendar (less excluded) exposes only the newly unexcluded slots AT OR AFTER the standing
  cursor: a slot behind the cursor is gone for good — normalization is inclusive and forward-only, and
  "re-owing the past" is not a thing any cursor machinery can express. Replaying slots behind an existing
  cursor requires dispatching a NEW durable series with `BackfillFrom`: `Reschedule`,
  `ReevaluateSchedule` and a redispatch of an existing keyed row never rewind that row (the dispatcher
  consults `BackfillFromUtc` only when there is no existing cursor).
- **An edit can also INVALIDATE a schedule**: a widened calendar can push an existing schedule's resolved
  union past the 1000-entry cap, or cover all seven days. On the next restart the contextual recovery
  validation POISONS that row (the ordinary corrupt-metadata verdict), rather than evaluating it — the
  forward-only promise holds only for edits that leave the definition valid, and the docs say so.
- The docs state it in one sentence: "a calendar edit applies from the next host start and only looking
  forward: future slots follow the new calendar, nothing already passed, materialized or halted changes".

### 4.2 The clock

Calendar `Days`/`Dates` entries are read on the SCHEDULE's exclusion clock (persisted `TimeZoneId` else
UTC), not on any clock of the calendar's own: "it-holidays" used by a Rome schedule and a UTC schedule
excludes each schedule's own local dates. `Ranges` stay absolute. A calendar with days/dates counts as a
calendar exclusion for the Elapsed-zone relaxation and for `ApplyDefault` — but statically the definition
only knows the NAME, so:

- `HasCalendarExclusions()` answers true when `Calendars` is non-empty (conservative: a named calendar MAY
  carry days/dates; a zone on `Every(4).Hours().ExceptCalendar("x")` is therefore legal even when "x" turns
  out to be ranges-only — the zone then governs nothing, which is harmless, unlike the reverse refusal).
- ET0010 already suppresses on any `Except*` call; `ExceptCalendar` joins the suppression set.

### 4.3 Unregistered names — the provider-key verdicts, verbatim

- **Validation travels as ONE context**: `Validate` gains an internal `ScheduleValidationContext` carrying
  BOTH registries (providers + calendars) instead of a second ambiguous registry overload. Every path that
  rebuilds a row into something that may PARK or EXECUTE a schedule passes it: startup recovery
  (`WorkerService`), `RetryScheduleDecisionAsync`, the materializer's run and its repark builder, and
  `WorkerExecutor.ReparkFromRowAsync` — the last one today deserializes without validating and parks the
  stored cursor directly, which would let a row naming an unknown calendar execute once before any
  evaluator lookup throws.
- **Dispatch / every `ITaskScheduleManager` write**: the context check refuses with the name in the
  message (`ArgumentException`) — nothing persists. `Validate()` without a context skips the check, like
  the provider-key check it sits beside.
- **Recovery**: a row naming a calendar this host does not register is corrupt-for-this-host metadata and
  takes the terminal poison route (the unresolvable-time-zone / unregistered-provider-key precedent). The
  poison reason names the calendar.
- **Evaluation reached without resolution** (defensive): the filtered door throws a typed
  `ArgumentException` naming the calendar when unresolved names reach it; it must never silently evaluate
  as "no exclusions" — a host missing its holiday config would run through every holiday without a sound.
- **Deliberately registry-free surfaces**, preserved and pinned by tests: `ToString()` reports the
  persisted names only; `TaskScheduleFacts` (Monitor.Api) deserializes metadata and never resolves;
  `ScheduleRebase` already refuses any non-null `Exclusions` before touching the math;
  `GetMinimumInterval` answers base-cadence metadata, not a filtered-grid question.

### 4.4 Interaction rules (deltas over the tranche-1 matrix)

| Combined with | Rule |
|---|---|
| Inline `Except`/`ExceptWeekends` | Union. |
| `INextOccurrenceProvider` | Still refused (the whole `Exclusions` object is). |
| `RebaseFromCursor` | Still refused (exclusions on either side, calendars included). |
| `SkipOldest` | Allowed — the union is deterministic per process (§4.1, §5.3). |
| Misfire / durable / backfill / normalization / budget | Unchanged: they consume the filtered door, which now filters on the union. |
| Serialization byte parity | Rows without calendars are untouched; `Calendars` is omitted-when-empty ONLY via canonicalization keeping the array present-but-empty inside an existing `Exclusions` object — see §5.1. |

## 5. Implementation notes

### 5.1 Serialization detail

`Calendars = []` inside a non-null `ScheduleExclusions` serializes as an empty array (the tranche-1 golden
JSON gains the member — a WRITE-side change for rows that already carry exclusions). This is a 4.0.0-only
concern: tranche 1 has never shipped, both tranches land in the same release, so the golden tests are
UPDATED rather than dual-shaped, and no released row exists in the old exclusion shape. (This is the one
place being pre-release simplifies the design; state it in the golden test's comment.)

### 5.2 The evaluator: the RESOLVED CLONE handoff

- The registry reaches the pure math by RESOLUTION, never by injection or ambient state. At each
  top-level evaluator call, `ScheduleEvaluator` builds an **internal evaluation clone** of the
  definition: `Exclusions` replaced with the canonical FLATTENED union (inline + every named calendar's
  set, merged and re-canonicalized), `Calendars` emptied. All existing pure math — the filtered door,
  `Base()`, `Unbounded()`, the region exits — then runs UNCHANGED on the clone: no new field on
  `RecurringTask`, no mutable resolution state, nothing for the shallow `MemberwiseClone` copies to clear,
  no reference-keyed cache to leak or serve stale entries (`RecurringTask` and its arrays are mutable
  persisted objects; a cross-call cache keyed on them is unsafe by construction).
- **No cross-call cache in v1**: resolution is one dictionary hit per name plus a bounded merge (§5.4
  caps), per top-level evaluator call — not per door probe. The union cap makes that cost flat.
- **Resolution is ONE shared primitive** used by both `Validate(context)` and the evaluator: it trims and
  deduplicates the names, refuses more than 16 BEFORE any registry lookup, then resolves and merges.
  Validation discards the resolved set (it only judges it); evaluation installs it on the transient clone.
  A bypass path can therefore never perform unbounded lookups.
- A definition whose `Calendars` is non-empty reaching the filtered door WITHOUT having been resolved
  throws (§4.3 defensive) — the door checks `Calendars.Length == 0` before evaluating, which also keeps
  the `Exclusions == null` fast path unchanged.
- `TryGetExclusionRegionExit` and every region-exit rule are untouched: a calendar contributes the same
  three shapes through the flattened union.

### 5.3 Determinism statement (for SkipOldest and the counts)

The union is a pure function of (definition, registry snapshot). The snapshot is frozen per process. Therefore
every question the grid answers is stable within a process — the property `SkipOldest`'s bisection and the
episode-bounded counts rely on. Across processes, stability follows config parity, the same contract every
multi-host deployment already signs for handlers and providers; the single-active-host limit (D5) makes
the cross-host case moot for durable schedules today.

### 5.4 Validation summary (deltas)

| Rule | Where | Outcome |
|---|---|---|
| Duplicate calendar name at registration | host build | `InvalidOperationException` |
| Empty/oversized (>1000 entries) calendar at registration | host build | `InvalidOperationException` |
| `ExceptCalendar()` with no/blank names | builder, immediately | `ArgumentException` |
| More than 16 calendar references after dedup | `Validate()` | `InvalidOperationException` |
| Unknown name, with the validation context in reach | `Validate(context)` at ingress and every row-rebuild path (§4.3) | `ArgumentException`, nothing persists |
| Unknown name at recovery | recovery | terminal poison, reason names the calendar |
| `null`/blank element in persisted `Calendars`; element over 100 chars | `Validate()` | `ArgumentException` (poison path) |
| `Calendars` trimmed+sorted+deduped | `Validate()` | canonical form |
| RESOLVED union (inline + calendars, merged) over 1000 dates+ranges | `Validate(context)` and the evaluator's resolution step | `InvalidOperationException` — the per-calendar cap alone does not bound the merge |
| Union covering all 7 days (inline + calendars) | `Validate(context)` AND the evaluator's resolution step | `InvalidOperationException` (the O(1)-provable emptiness is refused, never left to the search budget) |

## 6. Observability

`ToString()` appends the names verbatim (they are short and bounded): `except calendar it-holidays`,
`except calendars it-holidays, maintenance` — beside the existing inline clause. Nothing else new: an
excluded slot still does not exist.

## 7. Testing plan (deltas)

- Registration: duplicate/empty/oversized refusals; canonicalization of the registered set; the frozen
  snapshot (mutating the configuration object after host build changes nothing).
- Forward-only edits: narrowing exposes only the newly unexcluded slots at or after the standing cursor
  (a slot behind it stays gone); widening with an already materialized occurrence does not revoke it; an
  edit does not clear a standing halt; an edit with a live exclusion-retry marker resumes the recorded
  advance against the new union; an edit that makes the resolved union invalid (cap or all-7-days)
  poisons the row at recovery instead of evaluating it.
- Caps: 17th calendar reference refused; resolved union over 1000 entries refused at ingress AND at the
  evaluator's resolution step.
- Registry-free surfaces pinned: `ToString`, `TaskScheduleFacts`, `ScheduleRebase`, `GetMinimumInterval`
  never resolve; every direct-math evaluation of an unresolved calendared definition throws.
- Row-rebuild validation: each §4.3 path (recovery, schedule retry, materializer, repark) refuses/poisons
  an unknown name instead of executing once.
- Consumer compatibility for the new DIMs (`ExceptCalendar`, `AddScheduleCalendar`).
- Builder: `ExceptCalendar` matrix (mid-chain on all six interfaces, unions with inline, repeatability,
  blank-name refusal).
- Grid: a calendar-only exclusion filters like an inline one (shared paths — one representative per shape,
  not a re-run of the tranche-1 math suite); union semantics (slot excluded by calendar OR inline);
  region exits from calendar entries; zone: one calendar, two schedules in two zones, different excluded
  instants.
- Unknown name: ingress refusal (nothing persisted), recovery poison, defensive evaluation throw.
- Determinism: SkipOldest bisection over a calendar-heavy filtered grid vs brute force.
- Serialization: golden JSON updated with `Calendars`; tranche-1-shaped row (no `Calendars` member in the
  JSON) still reads (null-array leniency); calendars-only exclusions not normalized to null; byte parity
  for rows with NO exclusions unchanged.
- End-to-end: durable CatchUp schedule with `ExceptCalendar("holidays")` down across a holiday — no replay
  of the holiday slot; the same schedule after a REGISTRY change (restart with a widened calendar) owes
  fewer slots, with a narrowed one re-owing inside the caps.
- ET0010: `ExceptCalendar` suppresses, both orders.

## 8. Documentation (same anti-stale rule)

cheatsheet + configuration-reference (`AddScheduleCalendar`, `ExceptCalendar`), integrate-evertask skill
(bump again), recurring docs "Excluding moments" extension, README showcase snippet gains the calendar
line, CHANGELOG 4.0.0 Added, Recurring CLAUDE.md gotcha 20 extension (one sentence: names resolved at
evaluation via the immutable registry; unknown name = poison, never silent).

## 9. Decisions ratified in review round 1

1. Calendar edits are FORWARD-ONLY (never rewind a cursor, revoke a materialized occurrence, filter an
   override, or clear a halt); `BackfillFrom` stays the explicit replay tool.
2. Golden JSON updated in place; the three serialization contracts of §3.3 are pinned separately.
3. Resolution = the evaluator's resolved clone (flattened union, `Calendars` emptied); no registry down
   the call chain, no ambient state, no cross-call cache; unresolved evaluation throws.
4. Zone-on-names asymmetry accepted (harmless, future-proofs a calendar later gaining dates).
5. Emptiness of the merged union is refused at validation AND at resolution — never left to the budget.
6. Frozen deep registry snapshot post-configuration; `ScheduleValidationContext` carries providers +
   calendars into every row-rebuild path.
