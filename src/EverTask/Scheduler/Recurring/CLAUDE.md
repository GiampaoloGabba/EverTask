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

Builder and per-interval tests: `test/EverTask.Tests/RecurringTests/Builders/` and `.../Intervals/`.
