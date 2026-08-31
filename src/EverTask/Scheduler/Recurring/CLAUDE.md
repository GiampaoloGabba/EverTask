# Recurring Task Scheduler

Refer to the root CLAUDE.md for project-wide rules.

Fluent builder (`Builder/RecurringTaskBuilder.cs`) + occurrence math (`RecurringTask.cs`). Cron comes from
[Cronos](https://github.com/HangfireIO/Cronos) — validate expressions on https://crontab.guru/.

## Critical Gotchas

1. **Cron wins over everything.** With `CronInterval` set every other interval is ignored — a silent misconfiguration,
   not an error.
2. **First-run selection has NO gap buffer.** A future `RunNow`/`SpecificRunTime` is honored as-is; one in the recent
   past (within 20 s) only if it precedes the next grid occurrence; from `currentRun > 0` the grid value is returned
   unchanged. Never reintroduce a gap — removing it was the schedule-drift fix.
3. **Cascade order is Month → Week → Day → Hour → Minute → Second**, and intervals refine each other
   (`.Every(5).Minutes().AtSecond(30)`). `Intervals/` holds SEVEN classes; `Week` is the one easily forgotten.
4. **Every interval class keeps a public parameterless ctor with `[JsonConstructor]`**, and `OnDays` a PUBLIC setter
   on `DayInterval`/`WeekInterval`/`MonthInterval`. Without them STJ binds a parameterized ctor (wrong `Interval`
   default) or drops the selector on read — a silent schedule corruption on recovery.
5. **`MaxRuns` counts real executions only**: the series ends on `currentRun >= MaxRuns` or past `RunUntil` and
   nothing else. Slots skipped to realign after a downtime travel in `NextRunResult.SkippedCount` for LOGGING only, so
   `CurrentRunCount` equals the `RunsAudit` rows and moves +1 per run. Never add `+ skippedCount`.
6. **Restart revival preserves the stored `NextRunUtc`.** A future value is used as-is; feeding it to
   `CalculateNextValidRun` as a base time computes the occurrence strictly AFTER it, skipping one per restart.
   Recalculate only when it is already past.
7. **Skip-forward is calendar-aware, never flat arithmetic.** `NextOccurrenceStrictlyAfter` is the single primitive
   for "first occurrence strictly after `now`": cron → `GetNextOccurrence`; a uniform arithmetic grid
   (`IsUniformGrid()`) → an O(1) jump measured on the steady grid and self-verified on-grid, else the walk; anything
   else → a bounded calendar walk. It must equal stepping `CalculateNextRun(occ, 1)` until strictly after `now`, and
   never return a value `<= after` (a stale past next-run fires at once and spends a run). `RunUntil` is applied ONCE,
   by the caller, and is EXCLUSIVE — `CountMissedOccurrences` must agree, since `DueSlotEnumerator` compares that
   count against `MaxOccurrences` and an off-by-one trips the `Halt` breaker, which never releases itself.
   `GetMinimumInterval()` arithmetic is banned here (approximate, it lands on invalid days/times); the recovery grace
   window uses the natural calendar successor.
8. **Ask the grid through `IScheduleEvaluator`** — dispatcher, worker and recovery never call the occurrence math
   directly. `NextGridOccurrenceAfter` is the natural successor with `RunUntil`/`MaxRuns` IGNORED (shallow copy —
   never mutate the live definition); the grace window needs it because the bounded successor is null both for a
   still-current slot and for a finished series. `EnumerateDueSlotsAsync`: see `src/EverTask/CLAUDE.md`.
9. **`CalculateNextRun`, `GetMinimumInterval` and `CalculateNextValidRun` take an optional `nowUtc`**: the scheduling
   path always passes it, null falls back to the real clock.
10. **A zone moves CALENDAR schedules only, and the classification is derived, never stored** (`ScheduleSemantics`).
    `Elapsed` is exactly the constant-step grids — Second/Minute/Hour with no `OnHours`; everything else is
    `Calendar`. A zone on an Elapsed schedule is REFUSED by `Validate()`, not ignored, and the check lives there and
    not in `InTimeZone` because the chain has no final shape yet. `InTimeZone` is redeclared on the day/week/month
    builders returning the SAME builder, so a zone named mid-chain does not swallow the refinement after it
    (`EveryWeek().InTimeZone(z).OnDay(...)`).
11. **`GoverningZone` is the single gate into the zoned math**: null for Elapsed, for no zone and for plain `"UTC"`,
    which the legacy arithmetic answers exactly. `IsUniformGrid` is false when it is non-null (local midnight is not a
    constant 24 h step) and `CountMissedOccurrences` follows — keeping the zone-less path byte-identical.
12. **The zoned walk advances from the NOMINAL wall slot, never from the mapped instant.** `WallClock.ToUtc` reports
    `Consumed` for a slot mapping at or before the instant the walk stands on (a DST gap, the repeated hour read
    twice) and `NextGridOccurrenceInZone` continues from the wall time: advancing from the shifted instant skips the
    swallowed slots, returning it schedules an occurrence in the past. The WALK counts them
    (`WallMapping.CollapsedCount` + `CountSlotsSwallowedByGap`, onto `NextRunResult.CollapsedSlotCount`) — logging
    only: they ARE one occurrence, and spend one run, not one each.
13. **Cronos is the oracle**, and cron delegates to it with the zone; if the two diverge, align to Cronos.
14. **The stored id is IANA, and resolution failure is poison.** `ScheduleTimeZone` accepts a Windows or IANA spelling
    and persists IANA (a row written on Windows must resolve on a Linux replica); a custom `TimeZoneInfo` has none and
    is refused. An id that stops resolving reaches `Validate()` on every path, so recovery poisons the row instead of
    running it on the wrong clock. `Validate()`, not `InTimeZone`, is also where the id is CANONICALIZED — a
    hand-built `RecurringTask` never meets a builder. Keep normalization there.
15. **`AtTime`/`AtTimes` store the `TimeOnly` VERBATIM** — a time of day is read on whatever clock the schedule ends
    up on. Applying an `OnTimes` to a date goes through `WithTimeOfDay`, never `Adjust(h, m, s)`, so the grid keeps
    the precision the builder kept.
16. **`OnHours()` is not a calendar selector and is not on the fluent API.** It sits on the concrete
    `IntervalSchedulerBuilder` only (`Schedule().OnHours()` does not compile), takes no hours and builds
    `EveryHour()`'s cadence; nothing populates `HourInterval.OnHours`, reachable from persisted metadata alone.
17. **A misfire policy and the occurrence mode are ONE decision.** `FireOnce`/`CatchUp` replay missed work and a
    replay needs a durable row per slot, so the builder sets `OccurrenceMode.Durable` with them and `Validate()`
    REFUSES the pair on an inline definition instead of promoting it silently; `BackfillFrom` follows the rule.
    `MisfireSettings` is the persisted flat shape (one record for all three policies — a polymorphic member would need
    a declared alias set to round-trip); `CatchUpOptions`/`FireOnceOptions` are what a caller sees, and both are
    `[JsonIgnore(WhenWritingNull)]`, keeping earlier schedules byte-identical. Behaviour lives in
    `Scheduler/Occurrences/`; this namespace owns the definition, its validation and `FirstOccurrenceOnOrAfter` — the
    one question answered inclusively, and it probes BACKWARD by a PERIOD, never by a second: one `GetMinimumInterval`
    anchored on the instant, doubled while the grid still answers past it, and calendar MONTHS for a month cadence
    (`ProbeStepsByMonths`), since a flat 30 days lands on a different PHASE.
18. **A cursor belongs to the grid that produced it, so a reschedule never reuses it literally** (`ScheduleRebase`).
    What carries over is the nominal PERIOD — `RecurringTask.PeriodKind` (`SchedulePeriodKind`: coarsest selector
    wins, cron has none, a plain cadence is the instant itself), read on the OLD definition's clock — plus the
    cursor's POSITION inside it; the new cursor is the NEW definition's occurrence at that position, from
    `FirstOccurrenceOnOrAfter` on the new clock. It REFUSES more than it accepts (a different cadence or selector, a
    cron on either side, a period with no slot) — each would skip or replay a period of work; `RecalculateFromNow` is
    the fallback named in every refusal.
    - **The POSITION matters as soon as a period holds two slots**, or the rebase rewinds onto a slot already run and
      spends one more of `MaxRuns`. It is counted on the old grid with the bounds IGNORED
      (`FirstGridOccurrenceOnOrAfter`): a `RunUntil` inside the period truncates it into that same rewind. A cursor
      past every slot clamps to the LAST position; a period with fewer slots than the position reached is refused like
      an empty one. The week boundary is Sunday.
    - **A week cadence naming no day, and EVERY month cadence, have a period of ONE DAY** whatever `PeriodKind` says:
      both step their period first and only then apply a selector walking FORWARD from the day handed in, so the
      weekday or day of the month is the grid's PHASE and lives on the CURSOR (`OnDays(1, 15)` fires ONCE a month).
      Those two place the slot directly: the cursor's own wall DAY, at the new `OnTimes` entry in the cursor's own
      POSITION among the old ones, or at the cursor's own time when `OnTimes` is empty.
19. **A provider REPLACES the grid; it never refines one** (`ProviderSettings`). `RecurringTask.Provider` carries the
    registration KEY and an opaque config string — never a type name, which a rename would orphan — and is
    `[JsonIgnore(WhenWritingNull)]`. `Validate` refuses it beside any interval or cron, since a second silent winner
    (gotcha 1) would make a schedule mean something nobody wrote. Such a definition is `Calendar` (the zone id travels
    to the provider), its `PeriodKind` is `None` (so `ScheduleRebase` refuses it like cron) and `IsUniformGrid` is
    false. Async grid, throwing `GetNextOccurrence`, `PlanNextRun`/`SelectNextRun` split: see
    `src/EverTask/CLAUDE.md`.

20. **An excluded slot never exists** (`ScheduleExclusions`, #36). The filter sits inside `GetNextOccurrence` as
    an ANCHORED loop: it advances from the last real base-grid occurrence, never from a region boundary —
    re-asking the cascade from an off-grid instant re-anchors a cadence. A region exit is a jump TARGET only
    where phase-free (uniform `Base()` copy via `TryJumpUniformGrid`, cron via Cronos), with on-or-after
    semantics (a slot exactly at a range's exclusive `ToUtc` is valid). `IsUniformGrid()` is false with
    exclusions; skip-forward jumps the exclusions-cleared `Base()` copy and filter-fixes. Day/date exclusions
    read the persisted `TimeZoneId` (legal on an Elapsed grid when they need it; the default zone stamps at
    ingress only, never at evaluation). Spending the 200k-candidate budget throws
    `ExclusionSearchBudgetExceededException`, NEVER null — "no occurrence" is mathematics, a spent budget is
    not: ingress refuses, recovery takes the bounded-attempt poison route, the live advance records the run
    FIRST — cursor retained, in ONE commit with the retry marker (`RecordRecurringRunForExclusionRetry`,
    `ScheduleRuntimeInfo.ExclusionAdvanceRetry`, keyed on cursor+runCount so it self-invalidates) — and the
    retry/restart RESUMES THE ADVANCE from that cursor, never re-deciding it as pending work
    (`ScheduleRunAlreadyRecorded` in-process, the marker across restarts; a lost versioned advance goes to
    `ReparkFromRowAsync`). `FirstOccurrenceOnOrAfter` throws the same typed failure on its probe cap when
    exclusions are present — a cap-hit `null` read as "grid over" silently finalized a live series. A stored
    cursor is normalized via `NormalizeCursorAsync`
    (inclusive; a pending first-run override is exempt by PROVENANCE — `SpecificRunTime` only on cursor
    equality); the durable planner persists the normalized cursor through the cursor-only CAS, inline
    recovery re-derives and never writes. `ScheduleRebase` refuses exclusions on either side.

Tests (`test/EverTask.Tests/`): `RecurringTests/` — `Builders/`, `Intervals/`, `TimeZones/` (mapping, DST,
`CronOracleTests`, id normalization), `RecurringTaskScheduleDriftTests`, `RecurringCalendarSkipForwardTests`,
`RecurringSkipForwardHardeningTests`, `BackfillCursorTests`, `ScheduleRebaseTests`,
`RecurringExclusionMathTests`, `ScheduleExclusionValidationTests`, `TimeZones/ExclusionTimeZoneTests`, plus
`Serialization/IntervalSerializationParityTests` and `IntegrationTests/ScheduleTimeZoneIntegrationTests.cs`.
