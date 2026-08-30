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
| **Elapsed** | `Every(n).Seconds()/.Minutes()/.Hours()`, `EverySecond`/`EveryMinute`/`EveryHour` (+ `AtSecond`/`AtMinute`) | is refused unless day/date exclusions need its calendar clock |

- A day, week or month cadence is calendar-anchored even without `AtTime`: it defaults to midnight, and
  midnight is a local time. `Every(3).Days()` in Rome fires at local midnight.
- There is no hourly calendar selector. `OnHours()` is not one (see above), so an hour of the day is named the
  same way as any other: `EveryDay().AtTimes(new TimeOnly(8,0), new TimeOnly(20,0)).InTimeZone(...)`.
- `InTimeZone` on an elapsed cadence without day/date exclusions throws `InvalidOperationException` when the schedule is **built** (not at
  the call): an elapsed step is the same set of instants in every zone. `AtMinute`/`AtSecond` therefore align
  on UTC — `EveryHour().AtMinute(30)` fires at :00 local in India (+05:30) and :15 in Nepal (+05:45).
  Analyzer **ET0010** warns at compile time on a completed chain it can prove is elapsed; `Except` or
  `ExceptWeekends` anywhere in that chain suppresses it. A chain split over a variable
  or a helper method is left to the exception, so a clean build proves nothing on its own.
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
  schedules and day/date exclusions that did not call `InTimeZone`. It is written INTO the definition, so existing rows never move.
- The handler reads `Context.TimeZoneId` and `Context.ScheduledAtLocal` (offset included, which is what tells
  the two passes of a fall-back apart); both are null for a schedule with no zone.

## Fixed exclusions

Subtract moments from any built-in interval or cron grid:

```csharp
r => r.Schedule().EveryDay().AtTime(new TimeOnly(8,0))
      .Except(e => e.OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday)
                    .OnDates(new DateOnly(2026,12,25))
                    .Between(maintenanceStart, maintenanceEnd))

r => r.Schedule().Every(4).Hours().ExceptWeekends()
```

- `Except` calls union. `OnDays`/`OnDates` use the persisted schedule zone or UTC; `Between` is an absolute
  half-open `[from, to)` range. `ExceptWeekends()` excludes Saturday and Sunday.
- An excluded grid slot does not exist: no run budget, misfire count, durable occurrence, audit or event.
  `RunNow`/`RunDelayed`/`RunAt` first-run overrides are explicit instants and stay unfiltered.
- Works with built-in intervals, cron, `RunUntil`, all misfire policies, `SkipOldest`, backfill and durable
  occurrences. Refuse it with `UseOccurrenceProvider`: that provider owns its calendar.
- `RescheduleMode.RebaseFromCursor` is refused when either definition has exclusions; use
  `RecalculateFromNow`.
- Evaluation has a fixed search budget. Exhaustion surfaces at dispatch/schedule management without a write;
  live schedules retry with the `SetOccurrenceProviderRetry` backoff, while startup recovery uses its bounded
  poison counter. Never interpret it as a completed series.

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

- Registration-time update: re-dispatch with the same `taskKey` + new schedule (updates if Pending/Queued).
  Right at startup, where the registering code owns the definition.
- Runtime update: `ITaskScheduleManager` (below). Right for an admin screen, a tenant setting, a support
  action — anything changing a series someone else registered.
- Cancel: `dispatcher.Cancel(taskId)`, or `ITaskScheduleManager.CancelSchedule(taskKey)` when the key is all
  you have. Terminal either way.
- Inspect: `ITaskStorage.Get(t => t.IsRecurring)`; `task.CurrentRunCount`, `task.Status`, next run.

## Runtime schedule management (`ITaskScheduleManager`)

Registered by `AddEverTask` next to `ITaskDispatcher`. Schedules are addressed by `taskKey`, occurrences by
id. Every call needs a registered storage; `Reschedule`/`ReevaluateSchedule`/`ResumeSchedule` rewrite a
schedule row and need `SupportsScheduleVersioning`, `RequeueFailedOccurrence` needs
`SupportsDurableOccurrences`, and `CancelSchedule` needs neither. A `Reschedule` whose NEW definition is
durable (`WithDurableOccurrences()`, or `OnMisfire` picking `FireOnce`/`CatchUp`) needs
`SupportsDurableOccurrences` too, and is refused before any write. Every built-in storage has both; a store
missing the one a call needs gets `NotSupportedException`.

```csharp
public class ScheduleAdmin(ITaskScheduleManager schedules)
{
    public Task<ScheduleUpdateResult> MoveDailyReport(TimeOnly at) =>
        schedules.Reschedule("daily-report",
            r => r.Schedule().EveryDay().AtTime(at).InTimeZone("Europe/Rome"),
            RescheduleMode.RebaseFromCursor);
}
```

- `Reschedule(taskKey, configure, mode)` replaces the definition; `ReevaluateSchedule(taskKey)` keeps it and
  recomputes the cursor from now — which on a durable schedule DISCARDS the backlog it still owed, so reach
  for `ResumeSchedule(taskKey)` when the work is still wanted: it releases a durable catch-up halt KEEPING
  the cursor, so the backlog is planned again; `RequeueFailedOccurrence(id)` returns one terminal occurrence
  to the queue with its id, history and audit trail (and spends no run), unless its schedule was cancelled —
  a cancellation is terminal for every row under it; `CancelSchedule(taskKey)` runs the full cancel pipeline,
  pending occurrences included.
- `RescheduleMode.RecalculateFromNow` (default) starts at the new definition's first occurrence after now,
  dropping a durable backlog and reporting it (`DiscardedBacklog`, plus a monitoring event).
  `RebaseFromCursor` keeps the schedule inside the day/week/month it was already in — the period is read on
  the OLD definition's clock, and the new cursor is the NEW definition's slot at the same POSITION inside it.
  That is what preserves the logical date when only the hour or the zone moves, and what keeps a period
  holding several slots (`OnDays(Mon, Wed).AtTimes(09:00, 15:00)`) from rewinding onto one that already ran.
- A rebase is narrow on purpose: same cadence and same day/month selectors (only the time of day, the zone,
  `RunUntil`/`MaxRuns` and the misfire settings may change), no cron on either side, never crossing into the
  next period (nor onto a period holding fewer slots than the cursor had already passed), and never past
  bounds the series has already reached — a `MaxRuns` budget it has spent, or a
  `RunUntil` its cursor is already at or past. Anything else throws `InvalidOperationException` and writes
  nothing — fall back to `RecalculateFromNow`.
- `EveryWeek()` and `EveryMonth()` name no day inside their period, so that day rides on the CURSOR: a rebase
  keeps the weekday or the day of the month the series was on, and moves only the time of day and the zone.
- Never refused because a run is in flight. It is immediate for occurrences that have not fired; one already
  in a worker queue may finish under the old definition, and its advance then applies the new one.
- Also refused (before any write): an unknown key, a one-shot, a cancelled schedule, and a new definition
  with no occurrence left — use `CancelSchedule` to end a series on purpose.

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
  releases it and neither does a restart — only `ITaskScheduleManager.ResumeSchedule`/`Reschedule` does. A
  halted schedule is not re-parked, so it costs no further deliveries or writes while it waits. Or
  `SkipOldest`, which keeps the most recent `MaxOccurrences` and drops the rest. Pick `Halt` when a flood of
  catch-up work would be worse than a stopped schedule.
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

## Occurrence providers (a calendar the builder cannot express)

When the grid lives in the application — business days minus a holiday table, opening hours, "the day after
each invoicing cycle closes" — register an `INextOccurrenceProvider` and let the schedule take its slots from
it. Everything else keeps working over it: misfire policies, durable occurrences, `InTimeZone`, `MaxRuns`,
skip-forward, the schedule manager.

```csharp
public sealed class BusinessDaysProvider(HolidayRepository holidays) : INextOccurrenceProvider
{
    public bool IsDeterministic => true;   // needed only by CatchUpOverflowPolicy.SkipOldest

    public async ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                                   CancellationToken ct)
    {
        // STRICTLY after request.AfterUtc, in UTC; null ends the series.
        // Asked about arbitrary instants, not only the ones already returned.
    }
}

services.AddEverTask(...)
        .AddSqlServerStorage(cs)
        .AddOccurrenceProvider<BusinessDaysProvider>("business-days");

await dispatcher.Dispatch(new SendReminderTask(),
    r => r.Schedule().UseOccurrenceProvider("business-days", config: "{\"calendar\":\"IT\"}")
          .InTimeZone("Europe/Rome"),
    taskKey: "reminder");
```

- The KEY is persisted on every row that uses it, never a type name: renaming it orphans those schedules.
  `config` is opaque — EverTask never reads it — so version its format yourself.
- Exclusive with every interval and with cron: a provider REPLACES the grid instead of refining it.
- The provider is resolved in a fresh scope per call (register it yourself for another lifetime), so it may
  depend on a DbContext. Keep it fast: it sits on the path of every advance, and measuring a backlog costs
  several questions.
- `TimeZoneId` travels in the request and nothing is converted for you: a calendar meaning "07:00 in Rome"
  resolves the zone itself.
- Unknown key = configuration error: `ArgumentException` at dispatch, terminal poison at recovery.
  A provider that THROWS is transient: nothing is written, the schedule is re-parked after the backoff of
  `SetOccurrenceProviderRetry` (1 min doubling to 15 by default), a warning event is published, and the
  recovery poison counter is not touched. It surfaces as `OccurrenceProviderException` only where a caller
  is holding the call: a dispatch, and `ITaskScheduleManager.Reschedule`/`ReevaluateSchedule`, which ask the
  provider to decide the new cursor — catch it there and retry, nothing was written.
- A provider failure on the question asked AFTER a run leaves that run unwritten: the slot may run again when
  the schedule comes back (at-least-once, like every other EverTask handler).
- `RescheduleMode.RebaseFromCursor` is refused (no nominal period); `ReevaluateSchedule(taskKey)` is how the
  application says its calendar changed.

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
10. Does anything outside the registration code change the schedule while it runs (an admin screen, a tenant
    setting, an on-call action)? → `ITaskScheduleManager`, not a re-dispatch: `Reschedule` with
    `RebaseFromCursor` when only the hour or the zone moves and today's run must stay today's,
    `RecalculateFromNow` otherwise.
11. Can the schedule be written as an interval or a cron expression at all? A calendar the application owns
    (business days, holidays, opening hours) → `AddOccurrenceProvider<T>("key")` +
    `.UseOccurrenceProvider("key", config?)`, and `ReevaluateSchedule` when that calendar changes.
