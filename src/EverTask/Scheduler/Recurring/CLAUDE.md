# Recurring Task Scheduler

## Purpose

Fluent builder API + interval calculation for recurring tasks. Supports second/minute/hour/day/month intervals plus cron expressions.

## Key Components

| Component | Responsibility |
|-----------|----------------|
| `RecurringTask.cs` | Immutable config + scheduling logic (`CalculateNextRun`, `GetNextOccurrence`) |
| `Builder/RecurringTaskBuilder.cs` | Entry point for fluent API |
| Interval classes | `SecondInterval`, `MinuteInterval`, `HourInterval`, `DayInterval`, `MonthInterval`, `CronInterval` |

**Cron Expression Support**: Uses [Cronos](https://github.com/HangfireIO/Cronos) library (validate expressions at https://crontab.guru/).

## Critical Gotchas

### 1. Cron Expression Precedence
**CRITICAL**: If `CronInterval` is set, ALL other intervals (Second/Minute/Hour/Day/Month) are **ignored**.

**Location**: `RecurringTask.GetNextOccurrence()` checks Cron first.

### 2. 30-Second Gap Rule
When first run falls before calculated next interval, a **30-second buffer** is added to prevent overlapping executions.

**Example**: Task starts 10:00:25, interval "every minute" → next run 10:01:30 (not 10:01:00).

**Location**: `RecurringTask.CalculateNextRun()` when `currentRun > 0`.

### 3. Interval Cascade
Multiple intervals refine each other: Month → Day → Hour → Minute → Second.

**Example**: `.Every(5).Minutes().AtSecond(30)` = Every 5 minutes at :30 seconds mark.

### 4. Serialization Requirement (System.Text.Json since v3.9)
**ALL interval classes MUST keep a public parameterless constructor annotated with the STJ
`[System.Text.Json.Serialization.JsonConstructor]`** (RecurringTask is persisted to DB). STJ then builds the
instance via the no-arg ctor and sets properties via their setters. Removing the parameterless ctor makes STJ
fall back to a parameterized ctor, which flips `Interval` to a different default and stops binding
`OnDays`/`OnTimes` — a SILENT schedule corruption.

**`OnDays` must have a PUBLIC setter** on `DayInterval`/`WeekInterval` (like `MonthInterval`): STJ silently
drops a non-public setter on read, losing the OnDays schedule on recovery.

**Check / guard**: `public XInterval() { }` with the STJ `[JsonConstructor]` in every interval class — pinned
by the build-time guard `IntervalSerializationParityTests.Every_interval_keeps_a_public_parameterless_json_constructor`.

### 5. Stop Conditions
Tasks auto-stop when:
- `MaxRuns` reached (`.MaxRuns(10)` stops after **10 real executions**)
- `RunUntil` exceeded (`.RunUntil(endDate)` stops after date)

**Location**: `RecurringTask.CalculateNextRun()` returns `null` to signal termination (the single `MaxRuns` gate, `currentRun >= MaxRuns`).

**`MaxRuns` counts real executions only (Option B accounting).** Occurrences skipped to realign the schedule after a downtime are reported by `CalculateNextValidRun` (`NextRunResult.SkippedCount`) **for logging only** — they do NOT consume the `MaxRuns` budget. So `CurrentRunCount` always equals the number of `RunsAudit` rows, and `UpdateCurrentRun` / `CompleteRecurringRun` advance the counter by exactly 1 per run (there is intentionally no "advance by N" path). Do not reintroduce `currentRun + skippedCount >= MaxRuns` in `RecurringTaskExtensions`.

### 6. Restart Revival Preserves Stored NextRunUtc
On startup recovery a recurring task is re-dispatched with `isRecovery: true`. When the stored `NextRunUtc` is still in the **future** it is used **as-is** as the next occurrence — it must NOT be fed to `CalculateNextValidRun` as a bare base time, which computes the occurrence strictly *after* it, skipping one occurrence per restart (and dropping the last occurrence before `RunUntil`). Recalculation (skip-forward) applies only when `NextRunUtc` is in the past. See `Dispatcher.ExecuteDispatch` (recovery branch) and the `RunUntil`/`NextRunUtc`-preservation tests in `QueueResilienceIntegrationTests` / `SqlServerRecoveryIntegrationTests`.

### 7. Skip-Forward Realignment is Calendar-Aware (NOT flat-interval math)
After a downtime, `CalculateNextValidRun` realigns past missed occurrences via the single
`RecurringTask.NextOccurrenceStrictlyAfter(anchor, after)` primitive — the calendar-aware generalization of
the cron path. It computes the **first real occurrence strictly after `now`**:
- **cron** → `GetNextOccurrence(now)` (Cronos, O(1));
- **uniform arithmetic grid** (`IsUniformGrid()`: no calendar structure — no `OnDays`/`OnHours`/Month/Cron, ≤1
  `OnTimes` — checked on **every field regardless of `Interval`** because `OnDays` rides on `DayInterval(Interval=0)`;
  one or MORE pure-cadence fields, e.g. `Minute+Second`) → O(1) jump. The step is measured from the **steady grid**
  (`first→second`, never the irregular `anchor→first` first-run gap) and the candidate is **self-verified** on-grid
  (else it falls back to the walk);
- **everything else** (`OnDays`, `OnHours`, `MonthInterval`, multi-`OnTimes`, non-constant combinations) → a bounded
  calendar walk reusing `GetNextOccurrence`. These schedules are always coarse, so the walk is cheap.

**RunUntil is applied ONCE by the caller** (`NextOccurrenceStrictlyAfter`): a top guard returns null when
`after >= RunUntil`, and the uniform jump's self-verify is **skipped at/after RunUntil** (else `GetNextOccurrence`
would null the candidate and force a needless walk to the cap). The walk's cap-hit fallback **never returns a value
`<= after`** (a stale past next-run would be scheduled immediately and consume `MaxRuns`). Recovery's grace-window
(`Dispatcher`) decides via `RecurringTask.IsOccurrenceStillCurrent` (calendar-exact), **not** `GetMinimumInterval`,
which is wrong for OnDays/Month/Week. These are the round-2 hardening invariants — see
`RecurringSkipForwardHardeningTests` and `review/recurring-skipforward-deep-review.md`.

**Do NOT reintroduce flat `GetMinimumInterval()` arithmetic for skip-forward** — it returns an approximate flat
value (30 days for Month, the 5-minute default for `DayInterval(Interval=0)` from `OnDays`, the granular field
for combos) that **diverges on uneven schedules** and lands on invalid days/times (F8). `GetMinimumInterval` is
still legitimately used elsewhere as a rough "≈ one period" heuristic (Dispatcher recovery grace-window,
lazy-resolution threshold) — that is fine; only skip-forward must stay calendar-exact.

**Invariant**: skip-forward must equal the result of stepping the normal path (`CalculateNextRun(occ, 1)`) one
occurrence at a time until strictly after `now`. The `RecurringCalendarSkipForwardTests` ground-truth property
test pins exactly this. Skipped occurrences remain **logging-only** (Option B — see gotcha #5).

## 🔗 Test Coverage

**When modifying interval calculation**:
- Critical test: `test/EverTask.Tests/RecurringTests/RecurringTaskScheduleDriftTests.cs` (validates gap rule)
- Update: `test/EverTask.Tests/RecurringTests/Intervals/`

**When modifying fluent builder**:
- Update: `test/EverTask.Tests/RecurringTests/Builders/`
- Chain tests: `test/EverTask.Tests/RecurringTests/Builders/Chains/`

**When adding new interval type**:
- Add parameterless constructor
- Add calculation tests in `test/EverTask.Tests/RecurringTests/Intervals/`
- Add builder tests in `test/EverTask.Tests/RecurringTests/Builders/`
