---
layout: default
title: Time Zones
parent: Recurring Tasks
nav_order: 4
---

# Time Zones

EverTask computes every schedule in UTC unless you say otherwise. `InTimeZone` says otherwise: it reads the
schedule's calendar on a real clock, so "09:00" stays 09:00 there all year, including the weekends the clocks
move.

```csharp
await dispatcher.Dispatch(
    new SendDailyDigestTask(),
    r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"),
    taskKey: "daily-digest");
```

That schedule fires at 07:00Z in July and at 08:00Z in January. The instant moves; the local hour does not.

Where the call sits in the chain makes no difference: `Schedule().InTimeZone(z).EveryDay().AtTime(...)`,
`EveryDay().InTimeZone(z).AtTime(...)` and `EveryDay().AtTime(...).InTimeZone(z)` build the same schedule. The
one gap is between `Every(n)` and its unit: `Every(3)` has no shape to read yet, so name the zone before it on
`Schedule()`, or after it on the builder that follows.

## Why Not Convert the Time Yourself

The obvious workaround is to convert a local time to UTC once, at registration, and schedule the result:

```csharp
// Wrong: the offset is frozen at the moment you register
var zone   = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
var utc9am = TimeZoneInfo.ConvertTimeToUtc(DateTime.Today.AddHours(9), zone);

await dispatcher.Dispatch(
    new SendDailyDigestTask(),
    r => r.Schedule().EveryDay().AtTime(TimeOnly.FromDateTime(utc9am)));
```

Registering that in July stores 07:00 UTC. Come the end of October the task starts arriving at 08:00 local,
and it stays wrong until someone re-registers it. Storing `BaseUtcOffset` has the same problem in reverse: it
is the zone's *standard* offset, so a schedule built with it is an hour off for the whole daylight-saving
season.

Give EverTask the zone and it resolves the offset at each occurrence instead.

`AtTime` and `AtTimes` store what you pass, unchanged. The time of day is read on whatever clock the schedule
ends up on: the zone you named, or UTC when you named none. There is nothing for the builder to convert.

### `TimeOnly.ToUniversalTime()` is Deprecated

`EverTask.Scheduler.Recurring.DateTimeOffsetExtensions.ToUniversalTime(this TimeOnly)` is public, and it is
the same idea as the workaround above: declare every time of day in UTC. Despite the name it never converted
anything. It rebuilt the value from today's UTC date, whose offset is zero, so all it has ever done is drop
the milliseconds.

Don't call it. It is still there, and still unmarked, because `[Obsolete]` would fail the build of every
application compiling warnings-as-errors; it will go in a future major version. Pass the local time you mean
and name the zone.

```csharp
// Deprecated: a no-op that loses sub-second precision
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0).ToUniversalTime());

// Say what you mean instead
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome");
```

## What a Zone Governs

Not every schedule has anything for a zone to move. EverTask classifies each one:

| Semantics | Schedules | What a zone does |
|-----------|-----------|------------------|
| **Calendar** | `EveryDay`, `EveryWeek`, `EveryMonth`, `Every(n).Days()/.Weeks()/.Months()`, `OnDays`, `OnMonths`, `AtTime`/`AtTimes`, `UseCron` | Governs them. The calendar is read on the zone's clock. |
| **Elapsed** | `Every(n).Seconds()`, `Every(n).Minutes()`, `Every(n).Hours()`, with `AtSecond` / `AtMinute` | Nothing. These are constant steps in elapsed time and produce identical instants in every zone. |

A cadence in days, weeks or months sits in the first row, not the second. It lands on a time of day (midnight,
if you never named one), and a time of day only means something on a clock. `Every(3).Days()` in Rome fires at
local midnight, so the step across the March transition is 71 hours and the one across October is 73. Seconds,
minutes and hours have no such component to read.

`InTimeZone` on an elapsed schedule throws `InvalidOperationException` when the schedule is built, rather than
being accepted and quietly ignored:

```csharp
// Throws: "every 30 minutes" is the same set of instants everywhere
r.Schedule().Every(30).Minutes().InTimeZone("Europe/Rome");
```

One consequence worth remembering: `AtMinute` and `AtSecond` refine an elapsed cadence, so they align on UTC.
In a zone with a fractional offset, `EveryHour().AtMinute(30)` fires at :00 local in India (+05:30) and at :15
local in Nepal (+05:45). If you need a local minute, anchor the schedule to a calendar instead:
`EveryDay().AtTimes(...)`, or a cron expression.

## The Stored Id

The zone travels with the schedule as an IANA id inside the persisted definition. There is no new column, and
nothing about an existing row changes.

```csharp
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome");            // IANA
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("W. Europe Standard Time"); // Windows id
r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone(TimeZoneInfo.Local);        // a TimeZoneInfo
```

All three are accepted; all three are stored in the IANA spelling, so a row written on Windows resolves on a
Linux replica of the same deployment. An id this machine cannot resolve is refused at registration with
`ArgumentException`, before anything is written.

Two ids are refused outright:

- a zone built with `TimeZoneInfo.CreateCustomTimeZone`, whose rules live only in the current process and
  could never be restored from a row;
- anything `TimeZoneInfo.FindSystemTimeZoneById` does not know.

A schedule that never calls `InTimeZone` stores no id at all, which is what the historical UTC behaviour looks
like. `InTimeZone(TimeZoneInfo.Utc)` stores `"UTC"`: same arithmetic, but the row now records that someone
chose it.

If a stored id stops resolving (the zone was removed from the system database, or the row moved to a host with
a smaller one), startup recovery treats the row the way it treats an unparseable cron expression: the schedule
is marked `Failed` permanently rather than run at a time nobody chose.

## Daylight Saving

Twice a year a zone's clock skips an hour or repeats one, and a time of day is then either missing or
duplicated. Both cases have a defined answer here, so you do not have to special-case them in a handler.

**A slot the clock skipped** fires at the first local time that does exist. On 29 March 2026 Rome jumps from
02:00 straight to 03:00, so a schedule set to 02:30 fires at 03:00 local, which is 01:00Z. Nothing about the
gap is assumed to be an hour wide: Lord Howe Island moves by thirty minutes, and a 02:15 slot there fires at
02:30 local.

When several slots fall inside one gap they all point at the same instant, and EverTask fires **once**:

```csharp
// On the transition day, 02:15 and 02:45 are the same instant. One occurrence, not two.
r.Schedule().OnDays(DayOfWeek.Sunday)
            .AtTimes(new TimeOnly(2, 15), new TimeOnly(2, 45))
            .InTimeZone("Europe/Rome");
```

When that happens the run says how many slots it stood for, at `Information`:

```text
Task 6f1b… collapsed 2 nominal slot(s) into the occurrence at 2026-03-29T01:00:00+00:00:
a daylight-saving transition maps them to the same instant
```

That log line is the only place the number shows up. Everything else counts the occurrence, once: it spends
one run of `MaxRuns` and calls the handler once.

**A slot the clock repeated** fires on the first pass. On 25 October 2026 Rome reads 02:30 twice, at 00:30Z
and again at 01:30Z; the schedule fires at 00:30Z and skips the second reading. A handler that needs to tell
the two apart can: `Context.ScheduledAtLocal` carries the offset.

An elapsed cadence goes through both untouched, which is what "every 30 minutes" ought to mean. Across the
repeated hour `Every(30).Minutes()` fires four times, because that hour really does last two hours, and
nothing is compressed inside the gap: 01:45 local plus thirty minutes is 03:15 local.

## An Application-Wide Default

Applications whose schedules all belong to one zone can set it once:

```csharp
services.AddEverTask(cfg => cfg
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetDefaultScheduleTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome")));
```

The default is applied when a schedule is built, to calendar-anchored schedules that did not call
`InTimeZone`, and elapsed cadences are left untouched. An explicit `InTimeZone` always wins.

The zone the default picks is written into the definition, so a row keeps meaning what it meant when it was
registered. Changing the default later affects new registrations; it does not silently move schedules that are
already stored. To move those, re-register them under the same `taskKey`.

## Cron Expressions

A cron expression takes the zone the same way, and the DST rules above apply to it too:

```csharp
await dispatcher.Dispatch(
    new NightlyReportTask(),
    r => r.Schedule().UseCron("0 2 * * *").InTimeZone("America/New_York"),
    taskKey: "nightly-report");
```

Cron is evaluated by [Cronos](https://github.com/HangfireIO/Cronos), which owns the transition rules for
expressions. EverTask's own fluent grid is tested against it in nine zones, so the two agree on where every
occurrence falls.

## What a Handler Sees

A delivery reports the zone it belongs to:

```csharp
public class SendDailyDigestHandler : EverTaskHandler<SendDailyDigestTask>
{
    public override Task Handle(SendDailyDigestTask task, CancellationToken ct)
    {
        Context.TimeZoneId;       // "Europe/Rome", or null for a schedule with no zone
        Context.ScheduledAtUtc;   // 2026-07-02T07:00:00+00:00
        Context.ScheduledAtLocal; // 2026-07-02T09:00:00+02:00

        return Task.CompletedTask;
    }
}
```

`ScheduledAtLocal` keeps its offset, which is what distinguishes the two passes of a repeated hour. Both are
null when the schedule carries no zone. See
[Execution Context](../task-creation.md#execution-context) for the rest of what a delivery knows about itself.

## One-Shot Dispatches

`Dispatch(task, DateTimeOffset)` takes an absolute instant and is not affected by any of this. Build the
`DateTimeOffset` from the zone at the moment you mean, rather than from `BaseUtcOffset`:

```csharp
var zone  = TimeZoneInfo.FindSystemTimeZoneById("Europe/Rome");
var local = new DateTime(2026, 12, 25, 10, 0, 0);
var when  = new DateTimeOffset(local, zone.GetUtcOffset(local));

await dispatcher.Dispatch(new SendGreetingTask(userId), when);
```

`GetUtcOffset(local)` resolves the offset in force on that date. `BaseUtcOffset` returns the zone's standard
offset regardless of the date, so it is wrong for half the year.

## Next Steps

- **[Fluent Scheduling API](fluent-api.md)** - The full set of interval builders
- **[Cron Expressions](cron-expressions.md)** - Cron syntax and its supported fields
- **[Best Practices](best-practices.md)** - Recurring task guidance
