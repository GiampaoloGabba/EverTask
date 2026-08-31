---
layout: default
title: Occurrence Providers
parent: Recurring Tasks
nav_order: 6
---

# Occurrence Providers

Some schedules cannot be written as an interval or a cron expression. "Every business day at 07:00, except
the holidays in our table." "The day after each invoicing cycle closes." In those, the calendar lives in the
application, not in the schedule.

An occurrence provider is that calendar, plugged into the scheduler. It answers one question — which
occurrence comes after this instant — and everything else keeps working: misfire policies, durable
occurrences, time zones, `MaxRuns`, the skip-forward after a downtime, the runtime schedule manager.

```csharp
public sealed class BusinessDaysProvider(HolidayRepository holidays) : INextOccurrenceProvider
{
    public bool IsDeterministic => true;

    public async ValueTask<DateTimeOffset?> GetNextOccurrenceAsync(NextOccurrenceRequest request,
                                                                   CancellationToken cancellationToken)
    {
        var zone  = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId ?? "UTC");
        var local = TimeZoneInfo.ConvertTime(request.AfterUtc, zone);

        // Start at TODAY, not tomorrow. Asked at 06:00 on a working day, this schedule's next occurrence is
        // 07:00 the same morning; what skips a slot already past is the `slot > AfterUtc` test below, and
        // nothing else needs to.
        var day = local.Date;

        for (var i = 0; i < 400; i++, day = day.AddDays(1))
        {
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            if (await holidays.IsHolidayAsync(day, cancellationToken))
                continue;

            var wall = day.AddHours(7);
            var slot = new DateTimeOffset(wall, zone.GetUtcOffset(wall)).ToUniversalTime();

            if (slot > request.AfterUtc)
                return slot;
        }

        return null;   // no further occurrence: the series ends
    }
}
```

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
        .AddSqlServerStorage(connectionString)
        .AddOccurrenceProvider<BusinessDaysProvider>("business-days");

await dispatcher.Dispatch(
    new SendInvoiceReminderTask(),
    r => r.Schedule()
          .UseOccurrenceProvider("business-days", config: "{\"calendar\":\"IT\"}")
          .InTimeZone("Europe/Rome"),
    taskKey: "invoice-reminder");
```

## The key is what gets persisted

A schedule row stores the KEY and the opaque `config` string, never a type name. That is deliberate: a row
outlives the assembly that wrote it, so a persisted type name would turn a rename or a move into a schedule
nobody can run. Treat the key as part of the durable contract — renaming it orphans every schedule that
carries the old one.

The `config` string is yours. EverTask never reads it and never validates it; if its format is going to
change, version it yourself (`{"v":1,…}` is enough).

A schedule can use a provider **or** the built-in grid, never both: a provider replaces the grid instead of
refining it, so naming an interval or a cron expression beside one is refused when the schedule is built.

## What the provider is asked

| `NextOccurrenceRequest` | What it holds |
|-------------------------|---------------|
| `ProviderKey` | The key this provider was registered under |
| `Config` | The opaque string the schedule carries, or `null` |
| `AfterUtc` | The instant to answer strictly after |
| `TimeZoneId` | The schedule's IANA zone, or `null`. EverTask converts nothing for a provider — the answer is taken as UTC — so a calendar that means "07:00 in Rome" reads this and resolves it itself |
| `RunNumber` | The 1-based run being computed, or `0` when the question is not about a particular run |
| `TaskKey` | The dispatch key, when the schedule has one |
| `ScheduleId` | The schedule row, or `Guid.Empty` while it does not exist yet (the first occurrence of a brand-new dispatch is computed before anything is persisted) |

An answer has to be strictly later than `AfterUtc`. An equal or earlier instant would be scheduled in the
past and fire immediately, so it is refused, and logged as the bug it is, rather than run. `null` is the other
legitimate answer: it says there is nothing left, and the schedule is completed.

The part that surprises people is the third rule. The provider is asked about ARBITRARY instants, not only
about the ones it has already returned: a catch-up plan probes the instant axis to find where a backlog
begins, so `AfterUtc` will sometimes be a moment your calendar knows nothing about. Answer it anyway.

The provider is resolved from a fresh scope for every call, so it can depend on scoped services. It is also on
the path of everything that moves a schedule forward, so keep it fast: cache the calendar, index the table,
and remember that measuring a backlog costs several questions rather than one.

Honour the cancellation token while you are at it. Every call carries the token of whatever is asking — a
dispatch, the advance that closes a run, the materializer bringing a durable schedule up to date — and a host
that is stopping cancels it. A calendar that ignores it holds the delivery that asked, and with it a worker
and the host's shutdown, until it comes back on its own.

## Determinism, and the one policy that needs it

`IsDeterministic` (default `false`) says that the same `AfterUtc` always gets the same answer. Only one thing
needs it: `CatchUpOverflowPolicy.SkipOldest`, which keeps the most recent slots of an over-long backlog by
probing the grid at instants it never returned. A schedule that asks for that policy over a provider which
does not declare determinism is refused when it is dispatched, instead of replaying the wrong slots.

Everything else works either way — including `Halt`, the default overflow policy.

## When a provider cannot answer

Two failures, two different verdicts.

**An unregistered key is a configuration error.** It does not heal by waiting, so it is never retried: a
dispatch is refused with `ArgumentException` while the caller is still holding it, and a persisted row naming
a key this build no longer registers is poisoned by startup recovery exactly like an unparseable cron
expression — `Failed`, cursor cleared, no restart bringing it back.

**An exception from the provider is transient.** The application's own database being briefly unavailable must
not end a series, so nothing is written: not the cursor, not the status, not an audit row. The row stays
exactly where the outage found it, which is why a crash during one costs only the wait. The schedule is parked
to ask again after a backoff that doubles with each consecutive failure of that schedule, one minute to
fifteen by default, and one answer resets it. It also logs a warning and publishes a monitoring event, both of
them carrying the failure count and the instant it will ask again, so a schedule waiting on its calendar is
visible rather than merely quiet.

What does NOT happen is a poison. The startup-recovery failure counter is neither incremented nor cleared: a
calendar down across five restarts would otherwise mark the series `Failed` for ever, and a restart that
proved nothing about the row must not forget the failures it had really earned either.

Two places hand the failure to you instead, and for the same reason: a caller is holding the call and nothing
has been written, so there is nothing to park and nobody to retry behind. One is a dispatch, where there is no
row yet. The other is the schedule manager: `Reschedule` and `ReevaluateSchedule` have to ask the provider
before they can decide the new cursor, so both throw `OccurrenceProviderException` when it will not answer.
The schedule keeps the cursor it already had; call again once the calendar is back.

One case is worth spelling out. When the provider fails on the question asked **after a run**, that run's
completion cannot be written either — the write and the next cursor are one operation. The row is then in the
state a crash between a side effect and its storage write leaves, and the at-least-once contract covers what
follows: the slot may run a second time when the schedule comes back. Handlers on a provider-driven schedule
should be idempotent for the same reason every other EverTask handler should be.

## What works, and what does not

| Feature | Over a provider |
|---------|-----------------|
| Misfire policies (`Skip`, `FireOnce`, `CatchUp`) | Yes — the provider's grid is the grid they replay |
| Durable occurrences, `BackfillFrom` | Yes |
| `InTimeZone` | Yes, and the id travels to the provider, which is what reads the calendar on it |
| `MaxRuns`, `RunUntil` | Yes, applied exactly as on any other schedule |
| Skip-forward after a downtime | Yes |
| `ITaskScheduleManager.ReevaluateSchedule(taskKey)` | Yes — the way to say "my calendar changed" |
| `CatchUpOverflowPolicy.SkipOldest` | Only with `IsDeterministic => true` |
| `RescheduleMode.RebaseFromCursor` | No. A rebase carries a cursor across a nominal period — a day, a week, a month — and a provider exposes none. Use `RecalculateFromNow` |
| An interval or a cron expression on the same schedule | No: a provider replaces the grid |

## Changing the calendar at runtime

A provider is asked when the schedule needs its next slot, not on a timer, so an application that adds a
holiday tells the schedule so:

```csharp
await scheduleManager.ReevaluateSchedule("invoice-reminder");
```

It re-asks the provider with the definition as it stands and re-parks the schedule on the answer. On a durable
schedule it recomputes the cursor from now, which discards a backlog that was still owed — `ResumeSchedule` is
the one that keeps it.

It asks the provider, which means it can also come back empty-handed. An endpoint that calls it right after
the holiday table was edited should catch `OccurrenceProviderException` and retry instead of turning a
momentarily unreachable calendar into a 500. Nothing was written, so retrying is the whole recovery.

## Configuration

| Knob | Default | What it does |
|------|---------|--------------|
| `AddOccurrenceProvider<T>(key)` | — | Registers `T` (as scoped, unless you registered it yourself) under `key` |
| `SetOccurrenceProviderRetry(r => …)` | `InitialBackoff` 1 min, `MaxBackoff` 15 min | How long a schedule waits before asking a failed provider again. Both must be positive and at most a day |

```csharp
services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetOccurrenceProviderRetry(retry =>
    {
        retry.InitialBackoff = TimeSpan.FromSeconds(30);
        retry.MaxBackoff     = TimeSpan.FromMinutes(5);
    }));
```

## Cost

Every question is a round trip, so the answers EverTask needs are bounded rather than exhaustive. A count that
only ends up in a log line or an event walks at most 250 slots and then reports its number as a lower bound;
the count of what a downtime cost and the backlog a `Reschedule` throws away both take that bound.

Finding where the newest slots of a backlog begin — what `SkipOldest` does when a catch-up overflows its cap —
is a bisection over the instants, and it stops at the first probe that lands on the answer instead of
narrowing to the tick. Each probe counts forward as far as the cap, but the two bounds do not multiply: the
probes walk the same stretch of your calendar and the search remembers it, so no instant is asked about twice.
One search costs about one question per slot it looks at, which is never worse than walking the backlog once
and usually much better.

One number is yours to pick, and it is the expensive one. `CatchUpOptions.MaxOccurrences` decides something,
so no bound may shrink it: "is the backlog bigger than this?" cannot be answered in fewer than about that many
questions, and capping it lower would turn "more than the cap" into "exactly the cap". Over a provider, a cap
of a few thousand is a few thousand queries on any backlog that really exceeds it. Size it the way you would
size a batch.

That price belongs to the episode, not to every occurrence it replays. A catch-up working through a backlog
keeps what it measured and asks only about the slots that came due since, so replaying three hundred missed
slots costs about three hundred questions in total rather than that number squared. The measurement is taken
again from scratch every so often, since a provider is free to answer differently the second time.

A run that can create nothing asks nothing at all. While a long occurrence holds the only slot
`MaxPendingOccurrences` allows, the schedule is still re-examined about once a minute, and those runs cost a
comparison instead of a walk of your calendar: the cap decides whether a replay may start, and a run with no
room starts none.

The ordinary case is cheaper than any of that. A schedule that is keeping up asks one question per run.
