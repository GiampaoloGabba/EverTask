# 05: Scheduling: delayed, scheduled, recurring

All schedules execute in **UTC**. Convert local times before passing to `AtTime`/`RunAt`.

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
| `OnHours()` | every hour (1-hour interval; refine with `.AtMinute(...)`) |
| `OnDays(params DayOfWeek[])` | specific weekdays |
| `OnMonths(params int[])` | specific months (e.g. `1,4,7,10` quarterly) |

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
4. Stop condition → `MaxRuns` and/or `RunUntil`.
5. Idempotent on restart → `taskKey` (strongly recommended for all recurring).
6. High-frequency → set `auditLevel: AuditLevel.Minimal`/`ErrorsOnly`.
