# Recurring exclusions — spec (issue #36)

Status: **APPROVED v1.0** — four Codex adversarial review rounds (same session, full code access).
Round-4 verdict: GO with two changes, both applied (§5.4 live-advance guards: run counted once on
storage-less retries; lost-ownership → `ReparkFromRowAsync`). Codex also confirmed the inline no-persist
normalization sound (deterministic re-derivation; the crash-after-execution window is the existing
at-least-once gap, not a new one). Ready for implementation.
Scope: tranche 1 of #36 (fixed exclusions). Tranche 2 (named calendars) is sketched in §10 and explicitly
deferred to its own issue.

## 1. Goal

The recurring surface has two extremes: the inclusive fluent grid (`OnDays`, `AtTime`, cron — you say when
to run) and `INextOccurrenceProvider` (you compute the grid yourself). The middle is missing: "run on this
grid, EXCEPT these moments". Today that forces a convoluted cron or a full provider, losing the built-in
grid's declared determinism, `SkipOldest` support and uniform-grid detection.

This spec adds **composable fixed exclusions** on top of any built-in grid:

```csharp
.Schedule().Every(1).Days().AtTime(new TimeOnly(8, 0))
    .Except(e => e
        .OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday)
        .OnDates(new DateOnly(2026, 12, 25))
        .Between(maintenanceStart, maintenanceEnd))

.Schedule().Every(4).Hours().ExceptWeekends()   // sugar over Except(e => e.OnDays(Sat, Sun))
```

Declarative, serialized into the schedule definition, deterministic by construction.

## 2. Non-negotiable constraints (from the issue, restated as testable invariants)

1. **An excluded slot never exists.** The exclusion acts inside the occurrence math: the grid never
   produces the slot, so misfire/`CatchUp` never counts it as owed, `MissedCount`/`MissedCountIsExact`
   stay honest, and the `SkipOldest` bisection and the `Halt` breaker operate on the filtered grid.
   This covers the PERSISTED cursor too (§5.5).
2. **Serialization**: exclusions are part of the persisted definition (they survive restarts, recovery and
   replicas). Byte parity: a schedule with no exclusions serializes byte-identically to today.
3. **Determinism**: fixed exclusions keep the grid deterministic. `IsUniformGrid()` answers `false` when
   exclusions are present (a uniform grid with holes is not uniform), and every consumer of that answer
   follows.
4. **Time zones/DST**: day/date exclusions are read on the schedule's exclusion clock (§4.2), with
   phase-3 semantics. Absolute windows (`Between`) are instants and compare in absolute time.
5. **Purely additive**: `Exclusions == null` ⇒ every existing path byte-identical (JSON, arithmetic,
   fast paths, monitoring). No storage schema change at all — the definition lives inside the
   `RecurringTask` JSON blob.
6. **Computational exhaustion is never mathematical exhaustion.** A search budget running out is a typed
   failure (§5.4), NEVER `null`/"series ended": silently finalizing a live series would violate the
   recovery lifecycle invariant.

## 3. Public surface

### 3.1 Builder

New members on `IBuildableSchedulerBuilder`, as **default interface members** throwing
`SchedulerBuilderDefaults.NotImplemented()` (the established post-3.11 pattern, so external builder
implementations keep compiling):

```csharp
IBuildableSchedulerBuilder Except(Action<IExclusionBuilder> configure);
IBuildableSchedulerBuilder ExceptWeekends();
```

Both are **redeclared with `new`** on `IIntervalSchedulerBuilder`, `IHourSchedulerBuilder`,
`IMinuteSchedulerBuilder`, `IDailyTimeSchedulerBuilder`, `IWeeklySchedulerBuilder`,
`IMonthlySchedulerBuilder`, returning the SAME builder type — the `InTimeZone`/`OnMisfire` precedent, so
naming an exclusion mid-chain does not swallow the refinement after it.

`IExclusionBuilder` (new, `EverTask.Abstractions`, flat namespace):

```csharp
public interface IExclusionBuilder
{
    IExclusionBuilder OnDays(params DayOfWeek[] days);      // whole days of the week, exclusion clock
    IExclusionBuilder OnDates(params DateOnly[] dates);     // whole single dates, exclusion clock
    IExclusionBuilder Between(DateTimeOffset from, DateTimeOffset to);  // absolute window, half-open [from, to)
}
```

- All three are **additive and repeatable**; repeated calls union. The three kinds union with each other.
- `Except` may be called more than once on a chain; calls union into one exclusion set.
- `ExceptWeekends()` is pure sugar: exactly `Except(e => e.OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday))`.
- `Except` with a callback that adds nothing throws `InvalidOperationException` at build (an empty
  exclusion is a typo, not a no-op).
- `Between` with `from >= to` throws `ArgumentException` immediately (fail at the call site, where the
  values are in scope).

### 3.2 Persisted shape

New property on `RecurringTask`:

```csharp
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public ScheduleExclusions? Exclusions { get; set; }
```

`ScheduleExclusions` and `ExclusionRange` live **beside `RecurringTask` in the core scheduler namespace**
(the real `MisfireSettings` placement) — they are persisted implementation shapes, not consumer contracts;
only `IExclusionBuilder` sits in Abstractions. FLAT records, no polymorphism:

```csharp
public sealed class ScheduleExclusions
{
    public DayOfWeek[] Days { get; set; } = [];          // public setter + parameterless ctor:
    public DateOnly[] Dates { get; set; } = [];          // gotcha 4 (STJ silently drops selectors
    public ExclusionRange[] Ranges { get; set; } = [];   // without them)
}

public sealed class ExclusionRange
{
    public DateTimeOffset FromUtc { get; set; }   // inclusive
    public DateTimeOffset ToUtc { get; set; }     // exclusive
}
```

**Canonical form, enforced by `Validate()` (which also normalizes, like the IANA canonicalization):**

- `null` arrays (hand-edited JSON) are read as empty; a `null` element inside `Ranges` is corrupt metadata
  (poison path).
- `Days` and `Dates` are sorted and deduplicated; `Ranges` are normalized to offset zero (UTC),
  sorted, and overlapping/adjacent ranges are MERGED. One canonical serialized form per exclusion set,
  and the evaluation cost per probe is predictable.
- An all-empty `ScheduleExclusions` is normalized to `null`, so byte parity holds even for a sloppy
  hand-built caller.
- **Size cap**: `Dates.Length + Ranges.Length <= 1000` after normalization, refused by `Validate()`
  above it. The cap bounds the definition's size and the per-probe exclusion work; it does NOT prevent
  search-budget exhaustion (§5.4 owns that contract). (Bigger calendars are tranche 2's problem, by
  name, or a provider's.)
- `DateOnly` round-trips natively under STJ on net8+ (ISO `yyyy-MM-dd`), matching `EverTaskJson`'s
  defaults; pinned by a golden-JSON test.
- No storage/EF change: `RecurringTask` is stored as JSON. No migration, no provider work.

## 4. Semantics

### 4.1 What "excluded" means

A candidate slot (a nominal occurrence instant the unfiltered grid would produce) is **excluded** iff any
of:

- its date, read on the exclusion clock (§4.2), falls on a `Days` entry;
- that same local date equals a `Dates` entry;
- its instant `t` satisfies `FromUtc <= t < ToUtc` for any `Ranges` entry (absolute comparison, no clock).

An excluded slot is **not generated**: it is not a run, not a missed slot, not a skipped slot, not an
audit row, not a monitoring event. It has no observable existence anywhere — logs included. A DST
`CollapsedCount` belonging to an excluded candidate dies with it: only the occurrence ultimately returned
reports its own collapse count, never an accumulation from discarded candidates.

**First-run overrides are not grid slots.** `RunNow`, `RunAt`/`SpecificRunTime` and `RunDelayed` win or
lose by the existing first-run selection exactly as today; exclusions filter recurring GRID slots only.
(Documented; a user who wants the first run filtered has asked for a grid slot, not an override.)

### 4.2 The exclusion clock

- `Days`/`Dates` are calendar questions and read the **exclusion clock**: the PERSISTED `TimeZoneId`,
  else UTC — the evaluator never consults host configuration, or two replicas would read one row on two
  clocks. The host's configured default schedule zone participates the way it does today: stamped into
  `TimeZoneId` at new-definition ingress (build/dispatch, before `Validate()`), never re-applied at
  recovery or evaluation. `ScheduleTimeZone.ApplyDefault` is extended to stamp a schedule carrying
  calendar exclusions (`Days`/`Dates` non-empty) exactly as it stamps a `Calendar` schedule — otherwise
  `Every(4).Hours().ExceptWeekends()` on a host configured for Europe/Rome would silently read UTC
  weekends. `ApplyDefault` must tolerate `null` arrays (it can run before `Validate()` canonicalizes a
  hand-built definition).
- **`Validate()`'s Elapsed-zone refusal is relaxed**: a zone on an otherwise-Elapsed schedule becomes legal
  when the exclusions carry a calendar component (`Days` or `Dates` non-empty) — the zone then governs the
  exclusion clock and nothing else. `Semantics`/`GoverningZone` are UNCHANGED (still derived from the
  inclusion shape alone), so the base-grid arithmetic keeps its legacy path: an every-4-hours cadence walks
  in UTC exactly as today, and only the exclusion predicate reads Rome's calendar.
  - A zone on an Elapsed schedule whose exclusions are `Ranges`-only stays refused (the zone would still
    govern nothing).
- **ET0010 follows, and the fix is structural**: the analyzer currently inspects only the chain BEFORE
  `InTimeZone`, assuming nothing later can make an Elapsed chain zone-sensitive — `Except`/`ExceptWeekends`
  invalidates that assumption (`EveryHour().InTimeZone(z).ExceptWeekends()` is legal). The analyzer must
  consider the completed chain: any `Except`/`ExceptWeekends` call anywhere in it suppresses the
  diagnostic (conservative, keeps the zero-false-positive bar; the runtime check stays authoritative).
- DST notes: a local date's boundaries map through the zone's real offsets; a slot inside a DST-repeated
  hour is excluded iff the local date says so on that pass (both passes share the date, so no ambiguity for
  whole-day exclusions). No new wall-mapping machinery: the predicate reads an instant, it never maps a
  wall time forward.

### 4.3 Interaction with the rest of the definition

| Combined with | Rule |
|---|---|
| Cron | Allowed. Cronos answers, then the filter applies (anchored re-ask, §5.2 — Cronos jumps are cheap and phase-free). |
| Any built-in interval | Allowed — that is the feature. |
| `INextOccurrenceProvider` | **Refused by `Validate()`** (v1): a provider computes its own calendar and can apply its own exclusions; post-filtering would add async region skipping, provider failures inside the filter loop and new parking semantics. Revisit on demand. |
| `MaxRuns` | Unaffected: excluded slots never existed, so they spend nothing. |
| `RunUntil` | Unchanged, still exclusive, still applied where it is today. |
| Misfire `Skip`/`FireOnce`/`CatchUp` | Automatic: counts and windows are computed on the filtered grid. |
| `SkipOldest` | Works: the grid stays deterministic; the bisection probes the filtered grid. |
| `BackfillFrom` | Works: the cursor lands on the first non-excluded slot ≥ the requested instant. |
| Durable occurrences | Works: the materializer enumerates the filtered grid. |
| `RescheduleMode.RebaseFromCursor` | **Refused when either definition carries exclusions** (v1), `RecalculateFromNow` named in the refusal — the established "refuses more than it accepts" posture. Rationale: the ordinal-preserving rebase counts positions, and an exclusion added or removed before the cursor shifts every ordinal, silently skipping or replaying a slot. An ordinal-on-the-base-grid mapping is specifiable but is not bought in v1. |
| Inline (non-durable) schedules | Works: exclusions do not require durability. |
| First-run overrides (`RunNow`/`RunAt`/`RunDelayed`) | Not filtered (§4.1). |

## 5. Evaluator integration

### 5.1 The single door, with its anchor

The filter lives at the ONE place every grid primitive asks:
`RecurringTask.GetNextOccurrence(current, out collapsedSlots)` (cron branch included). The filtered
answer is computed as an **anchored advance**:

```
candidate = <unfiltered answer strictly after `current`>   // cron | UTC cascade | zoned walk
while candidate != null && IsExcluded(candidate):
    candidate = <next unfiltered occurrence, advanced FROM candidate>   // anchored: phase preserved
    (budget: §5.4)
return candidate   // RunUntil gate unchanged, applied where it is today
```

The advance is always FROM the last unfiltered candidate — a genuine base-grid occurrence — never from an
arbitrary instant like a region boundary: re-asking the cascade from an off-grid instant re-anchors a
cadence (an every-4-hours grid at 00:00/04:00/08:00 re-asked from 05:00 answers 09:00 and the phase is
lost). Region exits are only ever used as a JUMP TARGET on a uniform base grid (§5.2), where tick
arithmetic from a real occurrence preserves phase by construction; and the search there is for the first
base slot **on or after** the exit, never strictly after it — with half-open ranges the slot AT `ToUtc`
is valid.

Because `NextOccurrenceStrictlyAfter`, `FirstOccurrenceOnOrAfter`, `CountMissedOccurrences`,
`NextGridOccurrenceAfter`, `EnumerateDueSlotsAsync` and the zoned walk all route through this door, the
filtered grid is what everything downstream sees. The door is NOT the only source of slots, though — the
persisted cursor enters the due set directly; §5.5 closes that hole.

### 5.2 Crossing an excluded region

Three regimes, chosen by the BASE grid (the definition with `Exclusions` cleared — a shallow copy, the
`Unbounded()` precedent). The region exit is the FARTHEST exit among the exclusions covering the
candidate: the next local midnight after an excluded day/date (mapped through the zone with the existing
`WallClock` rules), `ToUtc` for a range.

- **Uniform base grid** (`Base().IsUniformGrid()` — the fine-grained cadences, where slot-by-slot
  crossing of a weekend costs 172,800 probes on a per-second grid):
  `TryJumpUniformGrid(anchor: candidate, after: exit - 1 tick)` on the base copy — the first base slot on
  or after the exit, in O(1), phase-anchored on a real occurrence.
- **Cron**: Cronos is phase-free, so the filter asks it directly for the first occurrence on or after the
  exit (`GetNextOccurrence(exit - 1 tick, zone)`), O(1) per region. Cron never walks an excluded region
  slot by slot — an every-minute cron crossing a 30-day window must cost ~1 probe, not 43,200.
- **Non-uniform, non-cron base grid** (calendar selectors, multi-`OnTimes`, zoned): advance slot by slot
  through the door's anchored loop — the phase of these grids travels with the instant the cascade
  advances from, so an off-grid jump target is not available. Their density is calendar-bounded (a
  handful of slots per day), so crossing even a decades-long window stays within the budget (§5.4).

In every regime, a landed slot may itself be excluded (consecutive regions); the loop continues.
`DateOnly.MaxValue` as an excluded day ends the search as "no further occurrence" (the genuine end of
representable time). A RANGE ending at `DateTimeOffset.MaxValue` is different: the half-open contract
makes an occurrence exactly AT `MaxValue` valid, so the search ends only after checking whether a base
slot falls exactly there.

### 5.3 Emptiness that is provable is refused statically

`Validate()` refuses `Days` covering all 7 days of the week — every slot lands on some weekday, so the
filtered grid is provably empty. That is the ONLY static emptiness refusal: the earlier idea of refusing
an inclusion day-selector that is a subset of the excluded days is UNSOUND on composite definitions (the
cascade is Month → Week → Day → …, and a later component can move the final date off the selected day),
so it is dropped. Everything else is the budget's job.

### 5.4 The search budget is a typed failure, never an ending

The filter loop carries a budget: `MaxExclusionSearchIterations = 200,000` **discarded excluded
candidates** per door call (that is the unit — one loop iteration; the oracle calls inside a single
`TryJumpUniformGrid` or Cronos probe are not counted). With §5.2's jump regimes, a walk only pays per
slot on calendar-density grids, so the budget covers decades-long windows on daily grids (36,500
candidates for a century) with an order of magnitude to spare. It is NOT claimed unreachable: a
definition can exhaust it — a cadence whose every candidate is excluded for ever
(`Every(7).Days()` anchored on a Saturday plus `Except(Saturday)`: a truly empty filtered grid the §5.3
static check cannot see), or an absurd density × duration product. Exhaustion is therefore an explicit,
documented contract: EverTask refuses to evaluate the definition, it never guesses.

On exhaustion the door throws `ExclusionSearchBudgetExceededException` (internal; it carries the instant
the search stood on — the pure occurrence math has no schedule identity, so the evaluator/caller layers
attach `ScheduleIdentity` when they wrap or log it). Routing uses the EXISTING per-path failure
machinery — no new storage operation is invented:

- **New-definition ingress** (dispatch, `Reschedule`/`ReevaluateSchedule`/`ResumeSchedule`, backfill):
  the exception surfaces to the caller and NOTHING persists. This is the gate that makes runtime
  occurrences of the exception rare by construction: every exclusion set enters through here.
- **Startup recovery**: the exception is a recoverable evaluation failure and takes the existing
  bounded-attempt route (`RecoveryDispatchFailureCount` → terminal poison after the cap), which already
  owns its conditional writes. Never the immediate-poison path and never "series over".
- **Materializer**: the existing generic-failure handling — re-park with backoff
  (`ReParkAfterFailureAsync`); a restart lands the row in recovery, where the bounded route decides.
- **Live advance**: an EXPLICIT catch for the typed exception in `QueueNextOccourrence`'s evaluation —
  the run already executed, so it is recorded FIRST, with the cursor RETAINED (the next one is exactly
  what could not be computed): versioned storage through the existing re-aim/CAS advance logic,
  unversioned storage through its ordinary run update, a storage-less schedule by incrementing the
  in-memory counter. Then the row is parked with a doubling backoff, provider-deferral style (a
  schedule-retry delivery, no grid evaluation at park time); each retry re-evaluates and re-parks until
  a restart routes it through recovery's bounded counter. Two guards on that catch:
  - **Run counted once**: an exclusion-budget retry carries a "run already recorded" marker; the
    storage-less retry resumes from `ScheduleRetryFromUtc` WITHOUT incrementing the in-memory run count
    again (`countsAsRun: false` or equivalent — the existing no-storage retry resumes with
    `countsAsRun: true` and would double-count, ending a `MaxRuns = 2` series after one real
    execution), and repeated exhaustion preserves the marker.
  - **Lost ownership**: when the versioned advance returns `OwnsNextOccurrence == false` — the run was
    recorded against a row a reschedule took over — the exclusion-budget retry is NOT parked; the catch
    calls `ReparkFromRowAsync` with the returned row and returns, per the existing replaced-definition
    liveness invariant.
  Reachable only through zone-rule drift or hand-edited metadata — ingress validated everything else.
- It must never be swallowed into a `null`: `SkipOldest`'s bisection monotonicity, the `Halt` breaker's
  backlog count and the recovery grace window all read `null` as mathematics, not as fatigue.

### 5.5 The persisted cursor can be excluded — normalize it on read

The cursor enters the due set WITHOUT passing the door: `EnumerateDueSlotsAsync` includes it as the first
owed slot, `CountMissedOccurrences` counts its anchor as 1, and the skip-plan can materialize it directly.
A cursor written under one definition can be excluded under the current one (a reschedule that added an
exclusion, hand-replaced metadata, a zone-rule change).

Two-sided rule:

- **Writers**: every path that computes a GRID cursor (dispatch, backfill, reschedule/
  `RecalculateFromNow`, advance, materializer) computes it through the filtered door, so no EverTask
  writer persists an excluded grid cursor. Pinned by tests. (A pending first-run override is not a grid
  cursor — see the exemption below.)
- **Readers (defensive)**: a new evaluator primitive, `NormalizeCursorAsync`, answers "the first
  non-excluded slot at or ON this instant" via the current grid's INCLUSIVE `FirstOccurrenceOnOrAfter`
  (never `NextOccurrenceStrictlyAfter(cursor, cursor)` — after hand-edited metadata or a zone-rule change
  the stored cursor may not be a base-grid occurrence at all, so it cannot serve as a phase anchor).
  Budget-guarded like the door. Its call sites are the two places the cursor enters a decision without
  passing the door:
  - `Dispatcher.DecideRecurringRunAsync`, BEFORE the durable / preserved-future-cursor / grace branches;
  - the top of `DueSlotEnumerator.PlanAsync`.
  The normalized value is what the plan sees. Persistence splits by path:
  - **Durable planner** (`DueSlotEnumerator.PlanAsync`): the normalized cursor is persisted through the
    EXISTING cursor-only CAS (`TryAdvanceScheduleCursor` — no run counted, no audit) even when the plan
    materializes nothing (future / `WindowFull`), so later passes do not re-normalize for ever.
  - **Inline recovery** (`DecideRecurringRunAsync`): normalization is NOT persisted — recovery
    deliberately skips `UpdateTask`, no cursor-only CAS exists for inline rows, and a new `ITaskStorage`
    member is not bought for a hand-edited-metadata corner. The normalized instant is what gets
    SCHEDULED; the derivation is deterministic, so each pass re-derives the same value at bounded cost,
    and the first real advance rewrites the row anyway.
  Normalization is not a run, not a skip, not an event: the excluded slot never existed.
- **First-run exemption — by the cursor's PROVENANCE, not by configuration**: an override merely present
  on the definition proves nothing (a stale `SpecificRunTime` loses to the grid and the stored cursor is
  then a grid cursor). The exemption applies only when the stored cursor IS the override actually
  selected: for `SpecificRunTime`, cursor equality with its UTC instant is required; `RunNow` and
  `InitialDelay` at `CurrentRunCount == 0` are inherently selected (their instants always win at
  dispatch). Any other cursor at run count zero is a grid cursor and is normalized.
  `RunAt(saturday).Then().EveryDay().ExceptWeekends()` recovered after a crash still runs its Saturday
  override: the user named that instant explicitly, it is not a grid slot (§4.1).

### 5.6 Fast paths

- `IsUniformGrid()` returns **false** when `Exclusions != null` (holes ⇒ not a fixed progression), and
  `CountsMissedInConstantTime()` follows.
- **Skip-forward keeps its O(1)-ish contract on fine grids** through §5.2's uniform-base jump: in
  `NextOccurrenceStrictlyAfter`, a definition whose base is uniform jumps on the base copy and filter-fixes
  the candidate (excluded → region exit → re-jump). Cost O(regions crossed), preserving gotcha 7 for
  high-frequency grids; a per-second grid after a month of downtime never walks millions of steps.
- `CountMissedOccurrences` with exclusions always WALKS (the filtered door), honouring the caller's cap as
  today. Decision counts are capped by `MaxOccurrences`-sized caps and stay cheap; the uncapped diagnostic
  count falls back to `MaxSkipCountIterations` and reports through the existing `IsExact` machinery — a
  truncated number is already never presented as a total. No closed-form region subtraction in v1
  (complexity not bought by any decision path).

### 5.7 Perf invariant (the D7 of this issue)

`Exclusions == null` short-circuits before anything else in every new check. The A/B gate: the P-K cells
(A4W, LRA in-memory, LRA SQLite, A4S SQLite) must show no measurable regression for schedules without
exclusions, and the golden-JSON suite must show byte-identical serialization.

## 6. Validation summary (all in `RecurringTask.Validate()` unless stated)

| Rule | Where | Outcome |
|---|---|---|
| `Between(from, to)` with `from >= to` | builder, immediately | `ArgumentException` |
| `Except(e => { })` adding nothing | build | `InvalidOperationException` |
| `null` arrays on a hand-built `ScheduleExclusions` | `Validate()` | read as empty |
| All-empty after normalization | `Validate()` | normalized to `null`, no error |
| Days/dates sorted+deduped; ranges UTC-normalized, sorted, merged | `Validate()` | canonical form (normalization, like the IANA id) |
| `Dates + Ranges` entry count > 1000 after normalization | `Validate()` | `InvalidOperationException` |
| `Days` = all 7 days | `Validate()` | `InvalidOperationException` |
| Exclusions beside a `Provider` | `Validate()` | `InvalidOperationException` |
| Range with `FromUtc >= ToUtc`, `null` range element, out-of-range `DayOfWeek` (hand-edited JSON) | `Validate()` | `ArgumentException` — corrupt metadata, poison path |
| Zone + Elapsed + calendar exclusions | `Validate()` | now LEGAL (§4.2); default zone stamps it too |
| Zone + Elapsed + ranges-only exclusions | `Validate()` | still refused |
| Search budget exhausted | evaluator | typed exception (§5.4): ingress refusal / recovery bounded-attempts→poison / materializer-advance re-park |

## 7. Observability

- `ToString()` (persisted as `RecurringInfo`, shown by the dashboard) appends a human-readable clause,
  **bounded**: day names in full (`except Saturday - Sunday`), dates and ranges summarized past a small
  count (`except 12 dates, 3 windows`) rather than embedding a thousand entries in every row and event.
  Bounds render on the schedule clock (the `OnScheduleClock` rule).
- No new DTO fields, no new events: excluded slots have nothing to report (§4.1), and the task detail
  already exposes the full serialized definition JSON for anyone who needs the exact set.

## 8. Testing plan

- **Builder**: each exclusion kind, unions, repeatability, `ExceptWeekends` equivalence, empty-callback
  refusal, mid-chain placement on every builder interface (the redeclaration matrix), both call orders
  around `InTimeZone`.
- **Grid math** (`RecurringTests/`): filtered next-occurrence across kinds; **phase preservation across a
  region on a cadence grid** (every-4-hours anchored 00:00, window `[04:00, 05:00)` → next is 08:00, and
  a slot exactly AT `ToUtc` fires); consecutive excluded days; exclusion at series start; exclusion
  covering `RunUntil`'s tail; uniform-base jump + filter-fix vs the walked reference (downtime crossing
  several weekends); collapsed-count of a discarded DST candidate not reported; `DateOnly.MaxValue` /
  end-of-representable-time exits.
- **Emptiness & budget**: all-7-days static refusal; the empty-forever cadence
  (`Every(7).Days()` on a Saturday anchor + `Except(Saturday)`) refused at dispatch by the budget; the
  composite Week→Day shape that the dropped subset-check would have wrongly refused, evaluating
  correctly; an every-minute cron crossing a 30-day window in O(regions) (the cron jump); recovery
  routing the typed exception through the bounded-attempt counter to poison, never to finalize;
  materializer/advance re-parking on it.
- **Counting**: `CountMissedOccurrences` on filtered grids vs a brute-force reference; `MissedCount`
  honesty through a `CatchUp` replay spanning excluded regions (excluded slots neither counted nor
  materialized); `SkipOldest` bisection on a filtered grid; `Halt` threshold unaffected by excluded slots.
- **Cursor normalization (§5.5)**: a stored cursor made excluded by a definition change is normalized
  forward before planning — not counted, not materialized, no event; the durable planner persists the
  normalized cursor via the cursor-only CAS even when the plan materializes nothing (future /
  `WindowFull`); inline recovery schedules the normalized instant without writing, and re-derives
  identically on the next pass; the pending first-run override is NOT normalized when the cursor's
  provenance says so (`RunAt` on an excluded day survives recovery; a STALE `SpecificRunTime` that lost
  to the grid does not exempt the grid cursor); the live-advance budget catch records the run (versioned
  and unversioned and storage-less variants) before the backoff park; every grid-cursor writer lands on
  non-excluded slots.
- **Zones/DST** (`RecurringTests/TimeZones/`): local-date exclusion vs UTC instant crossing midnight;
  exclusion clock on an Elapsed grid with a zone; the DEFAULT schedule zone governing exclusions on an
  Elapsed grid; DST-repeated hour on an excluded/non-excluded date; a gap-removed midnight as region exit.
- **Serialization**: golden JSON for a schedule with exclusions (canonical form: sorted, merged, UTC
  offsets); byte parity for one without; round-trip through `EverTaskJson`; `null`-array leniency and
  corrupt-element poison.
- **Rebase**: refused with exclusions on either side, `RecalculateFromNow` named; `RecalculateFromNow`
  landing on the filtered grid.
- **Durable end-to-end** (`IntegrationTests/`): a durable CatchUp schedule with `ExceptWeekends` down
  across a weekend — replay materializes weekday slots only; backfill landing inside an excluded region
  starts at the first slot after it.
- **Recovery**: skip-forward realignment over an excluded region; grace window when the stored slot's
  natural successor falls in an excluded region.
- **Analyzer**: ET0010 not reporting when `Except`/`ExceptWeekends` appears anywhere in the completed
  chain (both orders), still reporting on genuinely Elapsed-only chains; analyzer release-docs updated
  (RS2002 lockstep rule).

## 9. Documentation (anti-stale rule: all in the same PR)

- `docs/configuration-cheatsheet.md` (one row per new builder member), `docs/configuration-reference.md`,
  `plugins/evertask/skills/integrate-evertask/`.
- `docs/recurring-tasks.md` (+ the fluent-interval page): an "Excluding moments" section with the two
  snippets from §1 and the §4.3 matrix in prose.
- `CHANGELOG.md` under 4.0.0 Added; `README.md` one bullet in Key Features.
- XML docs on the new public surface (contract + exceptions), `AnalyzerReleases.Unshipped.md` if ET0010's
  behavior note changes its shipped description.
- Module `CLAUDE.md` (`Scheduler/Recurring`): one new gotcha stating §5's invariants (anchored door,
  region jump on uniform base only, budget = typed failure never null, cursor normalization,
  `IsUniformGrid` false).
- `ConsumerCompatibilityTests` fixture untouched by design (new members are DIMs and a nullable property);
  verified, not assumed.

## 10. Tranche 2 — named calendars (DEFERRED, own issue)

Sketch only, to keep tranche 1's shapes compatible with it:

- `ExceptCalendar(string name)` would add a `Calendars: string[]` member to `ScheduleExclusions` — an
  additive JSON member, so tranche 1 rows stay readable forever.
- Host-level registration (`opt.AddScheduleCalendar(name, ...)`) resolves the name at evaluation time,
  like provider keys: the NAME is persisted, never the dates.
- The hard problems that make it a separate design: a calendar edited between runs changes the past
  (misfire recounts differ across restarts — determinism per row is lost); an unregistered name at
  recovery (poison vs park); whether a static date-list calendar can honestly declare determinism and keep
  `SkipOldest` (the provider precedent says: declared, not inferred). None of this blocks tranche 1.

## 11. Design decisions ratified in review rounds 1-2

1. Elapsed grids may carry a zone when calendar exclusions exist. The exclusion clock at EVALUATION time
   is the persisted `TimeZoneId`, else UTC; the host default participates only by being stamped at
   ingress (`ApplyDefault`, extended to calendar-excluded Elapsed schedules). ET0010 inspects the
   completed chain.
2. `Between` stays half-open `[from, to)`; the region search is on-or-after the exit, never strictly
   after.
3. Provider + exclusions refused in v1.
4. Budget exhaustion (200,000 discarded candidates per door call) is a typed failure and a DOCUMENTED
   contract, not claimed unreachable: ingress refuses before persisting; recovery takes the existing
   bounded-attempt route to poison; the materializer re-parks via its generic-failure handling; the live
   advance gets an EXPLICIT catch — run recorded first with the cursor retained (versioned CAS /
   unversioned run update / in-memory counter), then a provider-style doubling-backoff park. Cron
   region-jumps via Cronos, so only calendar-density grids ever pay per slot. No N-horizon emptiness
   probe (absence in a window proves nothing for sparse grids).
5. `RebaseFromCursor` refused when exclusions participate (either side), v1.
6. No structured monitoring DTO; `RecurringInfo` clause, bounded summary for large sets.
7. Cursor normalization is an explicit evaluator primitive (`NormalizeCursorAsync`, inclusive
   `FirstOccurrenceOnOrAfter`, never anchored on the possibly-off-grid stored cursor), called at
   `DecideRecurringRunAsync` and `DueSlotEnumerator.PlanAsync`. The durable planner persists the
   normalized cursor via the existing cursor-only CAS even on plans that materialize nothing; inline
   recovery re-derives deterministically and never writes. The exemption for a pending first-run
   override goes by the cursor's provenance: `SpecificRunTime` only on cursor equality with its UTC
   instant; `RunNow`/`InitialDelay` inherently selected at run count zero.
