# Recurring Task Scheduler

Refer to the root CLAUDE.md for project-wide rules.

Fluent builder (`Builder/RecurringTaskBuilder.cs`) + occurrence math (`RecurringTask.cs`: `CalculateNextRun`,
`GetNextOccurrence`, `NextOccurrenceStrictlyAfter`). Cron support comes from
[Cronos](https://github.com/HangfireIO/Cronos) — validate expressions on https://crontab.guru/.

## Critical Gotchas

1. **Cron wins over everything.** If `CronInterval` is set, every other interval is ignored
   (`GetNextOccurrence` returns from the cron branch before the cascade). Silent-misconfiguration class.
2. **First-run selection has NO gap buffer.** A `RunNow` / `SpecificRunTime` in the future is honored as-is;
   one in the *recent past* (within **20 seconds**, `current.AddSeconds(-20)`) is honored only if it precedes
   the next grid occurrence; otherwise the grid wins. At `currentRun > 0` the method returns the grid value
   unchanged — there is deliberately no arbitrary gap ("No arbitrary gap required" in-source). Do not
   reintroduce one: removing it was the schedule-drift fix, pinned by
   `RecurringTests/RecurringTaskScheduleDriftTests.cs` (14:00 + 1 h must be exactly 15:00).
3. **Interval cascade order is Month → Week → Day → Hour → Minute → Second**; intervals refine each other
   (`.Every(5).Minutes().AtSecond(30)` = every 5 minutes at the :30 mark). `Intervals/` holds **seven**
   classes — `Second`, `Minute`, `Hour`, `Day`, `Week`, `Month`, `Cron`; `Week` is the one easily forgotten.
4. **Every interval class MUST keep a public parameterless constructor annotated with the STJ
   `[JsonConstructor]`** (System.Text.Json since v3.10; `RecurringTask` is persisted). Without it STJ falls
   back to a parameterized ctor, which flips `Interval` to a different default and stops binding
   `OnDays`/`OnTimes` — a SILENT schedule corruption. `OnDays` also needs a **public setter** on
   `DayInterval`/`WeekInterval`/`MonthInterval`: STJ silently drops a non-public setter on read, losing the
   schedule on recovery. Guarded by
   `Serialization/IntervalSerializationParityTests.Every_interval_keeps_a_public_parameterless_json_constructor`.
5. **`MaxRuns` counts real executions only (Option B accounting).** `CalculateNextRun` returns `null` on the
   single gate `currentRun >= MaxRuns` (or past `RunUntil`). Occurrences skipped to realign after a downtime
   are reported by `CalculateNextValidRun` (`NextRunResult.SkippedCount`) **for logging only** — they do NOT
   consume the budget, so `CurrentRunCount` always equals the number of `RunsAudit` rows and the counter
   advances by exactly 1 per run (there is intentionally no "advance by N" path). Do NOT reintroduce
   `currentRun + skippedCount >= MaxRuns` in `RecurringTaskExtensions`.
6. **Restart revival preserves the stored `NextRunUtc`.** On recovery a future `NextRunUtc` is used as-is; it
   must NOT be fed to `CalculateNextValidRun` as a bare base time, which computes the occurrence strictly
   *after* it — skipping one occurrence per restart and dropping the last one before `RunUntil`.
   Recalculation applies only when `NextRunUtc` is already past. See `Dispatcher.ExecuteDispatch` (recovery
   branch).
7. **Skip-forward realignment is calendar-aware, never flat arithmetic.** `NextOccurrenceStrictlyAfter` is the
   single primitive returning the first real occurrence strictly after `now`, via three paths: cron →
   `GetNextOccurrence` (O(1)); uniform arithmetic grid (`IsUniformGrid()` — no `OnDays`/`OnHours`/Month/Cron,
   ≤1 `OnTimes`, checked on every field regardless of `Interval`) → O(1) jump measured on the steady grid
   (`first→second`, never the irregular first-run gap) and self-verified on-grid, else falling back to the
   walk; everything else → a bounded calendar walk reusing `GetNextOccurrence`.
   - `RunUntil` is applied **once, by the caller**: a top guard returns null when `after >= RunUntil`, and the
     uniform jump's self-verify is skipped at/after `RunUntil`. The walk's cap-hit fallback never returns a
     value `<= after` (a stale past next-run would fire immediately and consume `MaxRuns`).
   - The recovery grace-window decides via `RecurringTask.IsOccurrenceStillCurrent` (calendar-exact), **not**
     `GetMinimumInterval`. Do NOT reintroduce flat `GetMinimumInterval()` arithmetic for skip-forward: it is
     approximate (30 days for Month, the 5-minute default for `DayInterval(Interval=0)` from `OnDays`) and
     lands on invalid days/times. It stays legitimate elsewhere as a rough "≈ one period" heuristic.
   - **Ground truth**: skip-forward must equal stepping `CalculateNextRun(occ, 1)` one occurrence at a time
     until strictly after `now` — pinned by the `RecurringTests/RecurringCalendarSkipForwardTests.cs` property
     test and `RecurringSkipForwardHardeningTests.cs`.

8. **Ask the grid through `IScheduleEvaluator`.** The dispatcher, the worker and the recovery never call the
   occurrence math directly. `ScheduleEvaluator` wraps the pure primitives synchronously today; the async
   shape is for the occurrence provider that arrives later. It also owns the one genuinely new question:
   `NextGridOccurrenceAfter`, the natural successor computed while IGNORING `RunUntil`/`MaxRuns` (on a
   shallow copy — the live definition is never mutated). The recovery grace window needs it because the
   bounded successor returns null both when the slot is still current and when the series simply ended.
   `EnumerateDueSlotsAsync` (V3) answers the other half: which slots the grid already owes at a given now,
   oldest first, from the schedule's cursor. Its `cap` is **mandatory**, not a courtesy — a one-second grid
   left behind by a long downtime owes millions of slots — and it applies neither the run budget nor a
   misfire policy: it reports what the grid owes, and phase 4 decides which of those become occurrences.
9. **`CalculateNextRun`, `GetMinimumInterval` and `CalculateNextValidRun` take an optional `nowUtc`** (P9).
   The scheduling path always passes it; null falls back to the real clock for callers outside it.

10. **A zone moves CALENDAR schedules only, and the classification is derived, never stored**
    (`ScheduleSemantics`, T5). `Elapsed` is exactly the constant-step grids — Second/Minute/Hour intervals with
    no `OnHours`, since `AtMinute`/`AtSecond` only re-phase a constant step. Everything else is `Calendar`: a
    Day, Week or Month interval always snaps to a time of day (`OnTimes` defaults to midnight). A zone on an
    Elapsed schedule is REFUSED by `Validate()`, not ignored, and the check lives there and not in
    `InTimeZone` because the chain has no final shape yet: `Schedule().InTimeZone(z).EveryDay()` is legitimate.
    T5's own wording put `Every(n).Days/Weeks` among the cadences; the classification shipped here overrides
    it, ratified in `review/recurring-occurrences-decisions.md` §3.4 — do not "restore" T5's letter.
    `InTimeZone` is declared again on the day/week/month builder interfaces, returning the SAME builder, so a
    zone named mid-chain does not swallow the refinement after it (`EveryWeek().InTimeZone(z).OnDay(...)`);
    the inherited `IBuildableSchedulerBuilder` overload forwards to it explicitly.
11. **`GoverningZone` is the single gate into the zoned math**: null for Elapsed, for no zone and for plain
    `"UTC"`, all three of which the legacy arithmetic already answers exactly. Everything keys off it —
    `IsUniformGrid` returns false when it is non-null (T8: local midnight is not a constant 24 h step, so the
    O(1) jump would land off-grid) and `CountMissedOccurrences` follows. It is what keeps the zone-less path
    byte-identical.
12. **The zoned walk advances from the NOMINAL wall slot, never from the mapped instant** (T7).
    `WallClock.ToUtc` reports `Consumed` when a slot maps at or before the instant the walk stands on (a slot
    a DST gap collapsed onto an instant already served, or the second reading of a repeated hour) and
    `NextGridOccurrenceInZone` continues from the wall time. Advancing from the shifted instant would skip the
    slots the gap swallowed; returning it would schedule an occurrence in the past.
    - **The slots that fire nothing are counted, and the count is the walk's** (T6, decisions §3.4).
      `WallMapping.CollapsedCount` is 0 out of `ToUtc` — one call sees one slot — and filled in by
      `NextGridOccurrenceInZone`, from its own discards plus `CountSlotsSwallowedByGap` (the later slots a
      gap ate between the nominal one and its exit; reached only for a `Shifted` mapping). It travels on
      `NextRunResult.CollapsedSlotCount`, an `init` property, and `WorkerExecutor` logs it. Logging only: the
      compressed slots ARE the one occurrence, so they spend one run, not one each.
13. **Cronos is the oracle, and cron delegates to it with the zone** (`CronOracleTests`: 400 occurrences,
    five fluent shapes, nine zones, exact equality). If the two ever diverge, align to Cronos. The comparison
    is seeded on the fluent grid's own first occurrence, because day and month intervals advance their period
    BEFORE selecting inside it and occurrence one can legitimately differ — pinned shape by shape. A CUSTOM
    zone cannot reach a schedule (no IANA id, gotcha 14), so its 400-occurrence run is made against the
    mapping and the cascade directly; the reformulation is written into decisions §3.4.
14. **The stored id is IANA, and resolution failure is poison.** `ScheduleTimeZone` takes a Windows or IANA
    spelling and persists the IANA one (a row written on Windows must resolve on a Linux replica); a custom
    `TimeZoneInfo` has none and is refused. An id that stops resolving reaches `Validate()` on every path, so
    recovery poisons the row instead of running it on the wrong clock.
    - **`Validate()` is also where the id is canonicalized**, not just checked — `InTimeZone` is not, because
      the public `Dispatcher.ExecuteDispatch` takes a `RecurringTask` built by hand and never meets a builder.
      Validation runs before the definition is serialized on every path that persists one, so the row gets the
      IANA spelling whichever entry point wrote it (`TimeZoneIdNormalizationTests`,
      `ScheduleTimeZoneIntegrationTests.A_schedule_handed_straight_to_the_dispatcher_is_persisted_with_the_IANA_id`).
      Keep any future normalization there for the same reason.
15. **`AtTime`/`AtTimes` store the `TimeOnly` VERBATIM** (T12): a time of day is read on whatever clock the
    schedule ends up on, so there is nothing to convert. `TimeOnly.ToUniversalTime()` never converted anything
    either — it rebuilt the value from today's UTC date, offset zero — it only dropped the milliseconds; it is
    deprecated in docs and XML-doc, with no `[Obsolete]` (R13), and nothing in the library calls it. The two
    places that apply an `OnTimes` to a date go through `WithTimeOfDay`, not `Adjust(hour, minute, second)`,
    so the grid can land on the precision the builder kept.
16. **`OnHours()` is not a calendar selector, and is not on the fluent API.** It sits on the concrete
    `IntervalSchedulerBuilder` only — `IIntervalSchedulerBuilder` never declared it, so `Schedule().OnHours()`
    does not compile — takes no hours, and builds `EveryHour()`'s cadence. Nothing populates
    `HourInterval.OnHours`, which is reachable from persisted metadata alone. Listing it beside
    `OnDays`/`OnMonths` is what put it in the calendar row of three documents.

Builder and per-interval tests: `test/EverTask.Tests/RecurringTests/Builders/` and `.../Intervals/`.
Time zones: `test/EverTask.Tests/RecurringTests/TimeZones/` (mapping, classification, DST, the Cronos oracle,
skip-forward parity, id normalization) plus `IntegrationTests/ScheduleTimeZoneIntegrationTests.cs` for the
wiring — the zone into the row, back out of it at restart, and into what the handler reads.
