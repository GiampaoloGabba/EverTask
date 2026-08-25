# 05: Scheduling: delayed, scheduled, recurring

All schedules execute in **UTC** unless the schedule names a zone with `InTimeZone` (see #time-zones).
`RunAt` always takes an absolute instant: build its `DateTimeOffset` with `zone.GetUtcOffset(localDateTime)`,
never with `zone.BaseUtcOffset` (the standard offset, wrong for half the year).

## One-shot

```csharp
await dispatcher.Dispatch(task, TimeSpan.FromMinutes(30));            // relative
await dispatcher.Dispatch(task, new DateTimeOffset(2026,1,1,9,0,0, TimeSpan.Zero)); // absolute (past → immediate)
```

## Recurring: fluent builder

```csharp
await dispatcher.Dispatch(task, r => r.Schedule().EveryDay().AtTime(new TimeOnly(3,0)), taskKey: "daily-cleanup");
```

### Entry points (`IRecurringTaskBuilder`)

| Method | Then |
|---|---|
| `Schedule()` | pure recurring, no initial one-off |
| `RunNow()` | run immediately, `.Then()` → recurring |
| `RunDelayed(TimeSpan)` | wait, `.Then()` → recurring |
| `RunAt(DateTimeOffset)` | first run at a time, `.Then()` → recurring |

`.Then()` returns the same interval builder as `Schedule()`.

### Interval builder

| Call | Meaning |
|---|---|
| `UseCron("expr")` | cron (see below). **Overrides all other interval calls; never combine.** |
| `Every(n).Seconds()/.Minutes()/.Hours()/.Days()/.Weeks()/.Months()` | every N units |
| `EverySecond()/EveryMinute()/EveryHour()/EveryDay()/EveryWeek()/EveryMonth()` | every 1 unit |
| `OnDays(params DayOfWeek[])` | specific weekdays |
| `OnMonths(params int[])` | specific months (e.g. `1,4,7,10` quarterly) |

There is no hourly counterpart of `OnDays`/`OnMonths`. `IntervalSchedulerBuilder.OnHours()` exists on the
concrete class but not on `IIntervalSchedulerBuilder`, so `Schedule().OnHours()` does not compile — and it
takes no hours anyway: it builds the same plain hourly cadence as `EveryHour()`. For specific hours of the
day, name them: `EveryDay().AtTimes(new TimeOnly(8,0), new TimeOnly(20,0))`.

Refinements per unit:
- Hour: `.AtMinute(0–59)`
- Minute: `.AtSecond(0–59)`
- Day: `.AtTime(TimeOnly)` or `.AtTimes(params TimeOnly[])`
- Week: `.OnDay(DayOfWeek)` / `.OnDays(params DayOfWeek[])` → then `.AtTime(...)`
- Month: `.OnDay(1–31)` / `.OnDays(params int[])` / `.OnFirst(DayOfWeek)` → then `.AtTime(...)`

Terminal limits (chainable at most endpoints): `.RunUntil(DateTimeOffset)` (must be future),
`.MaxRuns(int)` (counts real executions only). Both → stops at whichever comes first.

> ⚠️ **Docs-vs-code gotchas:** `OnLast(DayOfWeek)` appears in the docs but is **not implemented**
> (only `OnFirst` exists). `GetAllRecurringTasksAsync` in best-practices docs is illustrative:
> the real query is `ITaskStorage.Get(t => t.IsRecurring)`.

### Examples

```csharp
r => r.Schedule().Every(5).Minutes()                                   // every 5 min
r => r.Schedule().EveryMinute().AtSecond(30)                           // every minute at :30
r => r.Schedule().EveryDay().AtTimes(new TimeOnly(9,0), new TimeOnly(18,0))  // twice daily
r => r.Schedule().EveryWeek().OnDay(DayOfWeek.Monday).AtTime(new TimeOnly(9,0))
r => r.Schedule().EveryMonth().OnFirst(DayOfWeek.Monday)               // first Monday monthly
r => r.RunNow().Then().EveryHour()                                     // now, then hourly
r => r.Schedule().EveryHour().MaxRuns(10)                              // 10 runs then stop
r => r.Schedule().EveryDay().RunUntil(trialEndDate)                    // until a date
```

## Time zones

`.InTimeZone(TimeZoneInfo)` / `.InTimeZone(string)`, chainable before the interval (on `Schedule()`), on the
interval builder, or after the last refinement — every position but between `Every(n)` and its unit. The id
may be IANA (`Europe/Rome`) or Windows (`W. Europe Standard Time`); the IANA form is what gets persisted,
inside the schedule JSON, with **no new column**.

```csharp
r => r.Schedule().EveryDay().AtTime(new TimeOnly(9,0)).InTimeZone("Europe/Rome")  // 09:00 Rome, all year
r => r.Schedule().InTimeZone("America/New_York").EveryWeek().OnDay(DayOfWeek.Monday)
r => r.Schedule().EveryMonth().InTimeZone("Europe/Rome").OnDay(15)               // zone before the selector
r => r.Schedule().UseCron("0 2 * * *").InTimeZone("Asia/Tokyo")
```

| Semantics | Schedules | A zone… |
|---|---|---|
| **Calendar** | days/weeks/months (`EveryDay`, `Every(3).Days()`, …), `AtTime`/`AtTimes`, `OnDays`, `OnMonths`, `UseCron` | governs them |
| **Elapsed** | `Every(n).Seconds()/.Minutes()/.Hours()`, `EverySecond`/`EveryMinute`/`EveryHour` (+ `AtSecond`/`AtMinute`) | is **refused** |

- A day, week or month cadence is calendar-anchored even without `AtTime`: it defaults to midnight, and
  midnight is a local time. `Every(3).Days()` in Rome fires at local midnight.
- There is no hourly calendar selector. `OnHours()` is not one (see above), so an hour of the day is named the
  same way as any other: `EveryDay().AtTimes(new TimeOnly(8,0), new TimeOnly(20,0)).InTimeZone(...)`.
- `InTimeZone` on an elapsed cadence throws `InvalidOperationException` when the schedule is **built** (not at
  the call): an elapsed step is the same set of instants in every zone. `AtMinute`/`AtSecond` therefore align
  on UTC — `EveryHour().AtMinute(30)` fires at :00 local in India (+05:30) and :15 in Nepal (+05:45).
- An id this machine cannot resolve, or a `TimeZoneInfo.CreateCustomTimeZone` zone, throws `ArgumentException`
  at build. A stored id that stops resolving later is poisoned at recovery like a corrupt cron.
- `AtTime`/`AtTimes` store the `TimeOnly` verbatim: pass the local time you mean and name the zone. The public
  `TimeOnly.ToUniversalTime()` extension is **deprecated** (docs only, no `[Obsolete]`) — it never converted
  anything, it only dropped the milliseconds. Never generate a call to it.
- **DST**: a local time a gap removed fires at the gap's exit, and several slots inside one gap collapse into
  a single occurrence; a repeated local time fires on its **first** pass. Gap widths are not assumed to be an
  hour (Lord Howe moves 30 minutes). An elapsed cadence is untouched by both: it fires twice through the
  repeated hour, and 01:45 + 30 min is 03:15 local across the gap.
- Global default: `SetDefaultScheduleTimeZone(TimeZoneInfo)` (`01-setup.md`), applied at dispatch to calendar
  schedules that did not call `InTimeZone`. It is written INTO the definition, so existing rows never move.
- The handler reads `Context.TimeZoneId` and `Context.ScheduledAtLocal` (offset included, which is what tells
  the two passes of a fall-back apart); both are null for a schedule with no zone.

## Cron

Library: **Cronos**. 5-field standard (`min hour dom month dow`) or 6-field with seconds
(`sec min hour dom month dow`), auto-detected by field count; an invalid expression throws
`ArgumentException` on the first schedule calculation (or explicit `Validate()`), not at the
`UseCron(...)` call itself (parsing is lazy). Supports `*`, `*/n`, `n-m`, `n,m`, and Cronos `?`. DOW 0–6 (Sun=0).

```csharp
r => r.Schedule().UseCron("*/15 9-16 * * 1-5")   // every 15 min, business hours, Mon–Fri
r => r.Schedule().UseCron("0 12 1 1,4,7,10 *")   // quarterly at noon
r => r.RunNow().Then().UseCron("*/30 * * * *")   // now, then every 30 min
```

Validate at https://crontab.guru (standard) or https://cronos.netlify.app (Cronos dialect).
Use the fluent API for simple readable patterns; cron for multi-constraint windows.
A cron expression takes `.InTimeZone(...)` too, and Cronos applies the transition rules.

## Idempotent registration (essential for recurring)

Register recurring tasks at startup with a stable `taskKey` so restarts don't duplicate them.
Behavior by existing status is in `02-tasks-and-handlers.md` (#taskKey). Use an `IHostedService`;
see `templates/RecurringRegistrar.md`.

```csharp
await dispatcher.Dispatch(new DailyCleanupTask(),
    r => r.Schedule().EveryDay().AtTime(new TimeOnly(3,0)), taskKey: "daily-cleanup");
```

Per-entity keys: `taskKey: $"report-{userId}"` or `"tenant-{tenantId}:billing"`.

## Managing recurring tasks

- Update schedule: re-dispatch with the same `taskKey` + new schedule (updates if Pending/Queued).
- Cancel: `dispatcher.Cancel(taskId)` (resolve id via `GetByTaskKey` if you only have the key).
- Inspect: `ITaskStorage.Get(t => t.IsRecurring)`; `task.CurrentRunCount`, `task.Status`, next run.

## What the handler knows about the occurrence

Inside `Handle`, `Context` (see `02-tasks-and-handlers.md`) answers what the payload cannot: which
slot this run stands for (`ScheduledAtUtc`), which run of the series it is (`RunNumber`, durable
across restarts), and whether the run started late or stands for missed work (`Misfire`, threshold
`SetMisfireThreshold`, default 5 s). Use `Context.ScheduledAtUtc` — not `DateTimeOffset.UtcNow` — whenever the work is
defined by its slot (the window a report covers, the day a digest is for): after a downtime or a
rate-limit deferral the two are not the same instant.

## Schedule-drift behavior

Next run is computed from the **scheduled** time, not actual execution time, so late runs don't
drift forward. After downtime, missed occurrences are **skipped** by default (logged only; they do NOT count
against `MaxRuns` and produce no audit rows); the schedule resumes at the next valid future slot
(no catch-up storm). For calendar schedules (`OnDays`, monthly, etc.) the resume point walks to the
next *real* occurrence on the grid, e.g. an `OnDays(Mon,Wed,Fri)` task always lands on a listed
day, never an arbitrary interval-arithmetic slot.

To replay what a downtime missed instead of skipping it, see durable occurrences below.

## Durable occurrences and misfire policies

Opt-in, off by default. `.WithDurableOccurrences()` / `.OnMisfire(...)` / `.BackfillFrom(...)` chain in the
same positions as `InTimeZone`. They turn every due slot into its own one-shot row — own status, retries,
audit trail and rate-limit budget — and the schedule row stops running the handler.

```csharp
// Replay what a downtime missed, oldest first, inside caps
r => r.Schedule().EveryDay().AtTime(new TimeOnly(2,0)).InTimeZone("Europe/Rome")
      .OnMisfire(m => m.CatchUp(new CatchUpOptions(maxAge: TimeSpan.FromDays(2), maxOccurrences: 5)))

// One run stands for the whole missed range
r => r.Schedule().EveryHour().OnMisfire(m => m.FireOnce())

// One row per occurrence, no replay
r => r.Schedule().EveryDay().AtTime(new TimeOnly(3,0)).WithDurableOccurrences()
```

- `CatchUpOptions(maxAge, maxOccurrences)` requires BOTH caps — no defaults. `MaxAge` is how far back a
  replay may reach (the one ordinary way a slot is lost, always reported); `MaxOccurrences` is how many slots
  one episode may replay.
- `OverflowPolicy`: `Halt` (default) stops the schedule and writes a durable marker; the passage of time never
  releases it and neither does a restart, and the API that does (`ResumeSchedule`/`Reschedule`) arrives with
  runtime schedule management. A halted schedule is not re-parked, so it costs no further deliveries or
  writes while it waits. Or `SkipOldest`, which keeps the most recent `MaxOccurrences` and drops the rest.
  Pick `Halt` when a flood of catch-up work would be worse than a stopped schedule.
- `MaxPendingOccurrences` (default `1`) is how many occurrences may be alive at once; `1` is strictly serial,
  which also stops a handler that overruns its period from overlapping itself.
- `FireOnce` and `CatchUp` imply durable occurrences. `.BackfillFrom(startUtc)` starts the cursor in the past
  on a NEW registration, still bounded by the caps.
- `MaxRuns` on a durable schedule counts **materializations**, including occurrences that later fail or are
  cancelled. A failed occurrence is a dead letter; the series advances past it.
- The handler reads the replay from `Context.Misfire`: `Kind` (`CatchUp`/`FireOnce`), `MissedFromUtc`,
  `MissedThroughUtc`, `MissedCount` (how many slots the range holds, both ends included),
  `MissedCountIsExact` (`false` when a walked calendar/cron grid was counted under a bound, so the number is
  "at least this many") and `Lateness`. It is `null` while the schedule is keeping up AND the delivery started on
  time; a delivery that starts late for its own reasons (full queue, long previous run) still reports
  `Kind = Late` with `Lateness` and no range, even when the materializer registered no replay. What separates
  keeping up from missed work is `SetMisfireThreshold` (default 5 s, `01-setup.md`): a slot that came due longer ago than that stands
  for missed work even when it is the only one owed. A run of more than one slot always reports, threshold or
  not — the policy is about to collapse or replay slots nothing ran.
- A dropped slot always says WHICH limit dropped it, in the log and in the monitoring event: the age window
  (`MaxAge`), the episode cap under `SkipOldest`, or the skip policy itself. One catch-up run can report two
  of them, with a separate count each.
- **Contracts**: at-least-once (write idempotent handlers) and **one active host** — materialization is
  idempotent across hosts, execution is not. Requires a storage that implements the atomic occurrence
  operations; every built-in one does, and a custom one that does not is refused at dispatch.
- Occurrences are rows: set `OccurrenceRetentionDays` in the audit-cleanup policy (`03-storage.md`) for any
  schedule that runs often.

## Wizard decision points

1. One-shot vs recurring → dispatch overload.
2. Run immediately on first dispatch, after a delay, or at a fixed time? → `RunNow`/`RunDelayed`/`RunAt` vs `Schedule`.
3. Interval shape → fluent unit or cron (cron overrides everything else).
4. Anchored to a wall clock people read (a 09:00 digest, a 02:00 nightly job) rather than to an absolute
   cadence? → `.InTimeZone("Area/City")`, or `SetDefaultScheduleTimeZone` once for the whole application.
5. Does a run the host missed still have to happen (billing, a nightly report, a digest someone waits for)?
   → `.OnMisfire(m => m.CatchUp(...))` to replay each missed slot, or `m.FireOnce()` when one run covers the
   whole gap. Leave it alone for a heartbeat, a poll or a cache refresh: a slot nobody missed is a row nobody
   needed.
6. Stop condition → `MaxRuns` and/or `RunUntil`.
7. Idempotent on restart → `taskKey` (strongly recommended for all recurring).
8. High-frequency → set `auditLevel: AuditLevel.Minimal`/`ErrorsOnly`.
9. Work defined by its slot rather than by "now" → read `Context.ScheduledAtUtc` (and `Context.Misfire`
   when a stale run should behave differently).
