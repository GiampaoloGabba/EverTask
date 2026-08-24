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
across restarts), and whether the run started late (`Misfire`, threshold `SetMisfireThreshold`,
default 5 s). Use `Context.ScheduledAtUtc` — not `DateTimeOffset.UtcNow` — whenever the work is
defined by its slot (the window a report covers, the day a digest is for): after a downtime or a
rate-limit deferral the two are not the same instant.

## Schedule-drift behavior

Next run is computed from the **scheduled** time, not actual execution time, so late runs don't
drift forward. After downtime, missed occurrences are **skipped** (logged only; they do NOT count
against `MaxRuns` and produce no audit rows); the schedule resumes at the next valid future slot
(no catch-up storm). For calendar schedules (`OnDays`, monthly, etc.) the resume point walks to the
next *real* occurrence on the grid, e.g. an `OnDays(Mon,Wed,Fri)` task always lands on a listed
day, never an arbitrary interval-arithmetic slot.

## Wizard decision points

1. One-shot vs recurring → dispatch overload.
2. Run immediately on first dispatch, after a delay, or at a fixed time? → `RunNow`/`RunDelayed`/`RunAt` vs `Schedule`.
3. Interval shape → fluent unit or cron (cron overrides everything else).
4. Anchored to a wall clock people read (a 09:00 digest, a 02:00 nightly job) rather than to an absolute
   cadence? → `.InTimeZone("Area/City")`, or `SetDefaultScheduleTimeZone` once for the whole application.
5. Stop condition → `MaxRuns` and/or `RunUntil`.
6. Idempotent on restart → `taskKey` (strongly recommended for all recurring).
7. High-frequency → set `auditLevel: AuditLevel.Minimal`/`ErrorsOnly`.
8. Work defined by its slot rather than by "now" → read `Context.ScheduledAtUtc` (and `Context.Misfire`
   when a stale run should behave differently).
