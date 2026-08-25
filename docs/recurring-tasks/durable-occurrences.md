---
layout: default
title: Durable Occurrences
parent: Recurring Tasks
nav_order: 5
---

# Durable Occurrences and Misfire Policies

A recurring task normally runs from its own row: the row holds the schedule, the worker executes the handler,
and the row moves on to the next occurrence. Nothing records that a particular occurrence happened, and
nothing replays one that did not.

Durable occurrences change that. The schedule row becomes a definition plus a cursor and stops running the
handler; every due slot becomes its own row — a one-shot with its own status, retries, audit trail and
rate-limit budget. That is what makes a missed slot something you can replay, and a failed one something you
can look at afterwards.

```csharp
await dispatcher.Dispatch(
    new SendDailyDigestTask(),
    r => r.Schedule()
          .EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome")
          .OnMisfire(m => m.CatchUp(new CatchUpOptions(maxAge: TimeSpan.FromDays(2), maxOccurrences: 5))),
    taskKey: "daily-digest");
```

## When to use it

Turn it on when a run the host missed still has to happen: a nightly billing job that did not run because the
machine was down is a job someone asks about the next morning. The same goes for anything you need a history
of — which run failed, what it logged, how long it took. A durable occurrence keeps its own trail instead of
bumping a counter on the schedule, and a failed one is a dead letter you can look at and requeue.

Leave it off — the default — for a heartbeat, a cache refresh or a poll. A slot nobody missed is a row nobody
needed, and doubling the writes of an `EverySecond()` schedule buys nothing.

## The modes

| Call | What the schedule does with a MISSED slot | Occurrence rows |
|------|-------------------------------------------|-----------------|
| nothing (default) | Skips it; at most the slot that is still current runs | no |
| `.WithDurableOccurrences()` | Skips it, exactly as above | yes |
| `.OnMisfire(m => m.FireOnce(...))` | Collapses the whole missed run into ONE occurrence, at the most recent slot | yes |
| `.OnMisfire(m => m.CatchUp(...))` | Replays every missed slot, oldest first, inside the caps below | yes |

`FireOnce` and `CatchUp` both imply durable occurrences: a replay needs somewhere to put what it replays.
`.WithDurableOccurrences()` on its own is the middle ground — one row per slot, no replay.

`.OnMisfire(m => m.Skip())` is the default spelled out. It changes nothing on its own; it is there so a
schedule can state its policy rather than rely on the default.

## The caps of a catch-up

`CatchUpOptions` takes two caps, and neither has a default, because they answer different questions:

```csharp
new CatchUpOptions(maxAge: TimeSpan.FromHours(6), maxOccurrences: 20)
{
    OverflowPolicy        = CatchUpOverflowPolicy.Halt,   // default
    MaxPendingOccurrences = 1                             // default
}
```

**`MaxAge`** — how far back a replay may reach. Slots older than `now - MaxAge` are dropped. This is the one
ordinary way a durable schedule loses work, and it is always reported: a warning log and a monitoring event
carrying how many slots went, where they started, and which limit dropped them.

**`MaxOccurrences`** — how many slots ONE catch-up episode may replay in total. The age window alone would
not stop a per-second schedule left behind by a three-month downtime; that is eight million due slots. The
count that decides this stops one past the cap, so a schedule with a huge backlog costs the cap, not the
backlog.

**`OverflowPolicy`** — what happens when the backlog exceeds `MaxOccurrences`:

- `Halt` (the default) stops the schedule and waits for a person. Nothing is materialized, a durable marker
  is written on the schedule row, and a `CatchUpHalted` event is raised. The passage of time never releases
  it — not even when `MaxAge` eventually brings the backlog back under the cap, and not a restart either.
  Kubernetes calls the same situation "too many missed start times", and for the same reason: a backlog
  nobody has looked at should not quietly turn into a flood of work. Releasing a halt is an explicit act, and
  the API for it (`ResumeSchedule`, `Reschedule`) arrives with runtime schedule management; until then a
  halted schedule stays halted, and re-registering it under its task key does not clear the marker. A halted
  schedule is also not put back in the scheduler: while it waits it writes nothing and takes no worker
  delivery. A restart reports the halt once more and then leaves it alone.
- `SkipOldest` replays the most recent `MaxOccurrences` slots and drops the rest, reporting how many and
  saying that it was the cap — not the age window — that dropped them.

A series limited by `MaxRuns` is never halted over a backlog it could not replay anyway. The breaker exists to
stop a flood of catch-up work, and a schedule with fewer runs left than the cap allows will create that many
occurrences and then close, whatever the backlog holds — so it spends what it has left instead of stopping for
a person who could only tell it to do the same thing.

**`MaxPendingOccurrences`** — how many occurrences of this schedule may be alive at the same time. The
default of `1` makes the schedule strictly serial: the next slot is only created once the previous occurrence
has ended, which is also what keeps a handler that overruns its own period from overlapping itself. Raise it
to drain a backlog faster; the queue's own parallelism still bounds what actually runs.

## Backfilling

`BackfillFrom` starts the cursor in the past instead of at the first occurrence after the dispatch:

```csharp
r => r.Schedule().EveryDay().AtTime(new TimeOnly(2, 0))
      .BackfillFrom(DateTimeOffset.UtcNow.AddDays(-7))
      .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(10), 10)))
```

It only applies to a brand-new registration, and the replay it triggers is still subject to the caps above: a
backfill window wider than `MaxAge` loses the part that falls outside it, reported like any other dropped
slot. Without a replaying policy it does very little — the cursor starts in the past and the skip policy
immediately moves it forward again.

## What a handler sees

An occurrence's `ITaskExecutionContext` describes the occurrence, not the schedule:

| Property | On an occurrence |
|----------|------------------|
| `TaskId` | the occurrence's own id |
| `ScheduleId` | the schedule row it belongs to |
| `ScheduledAtUtc` | the NOMINAL slot, never the moment the delivery was fired |
| `TimeZoneId` | the zone the schedule is read on, or `null` when it names none |
| `ScheduledAtLocal` | that nominal slot on the zone's own clock, offset included |
| `RunNumber` | which run of the series this occurrence is |
| `IsOccurrence` | `true` |
| `Misfire` | `null` while the schedule is keeping up and the delivery itself started on time; `Kind = CatchUp`/`FireOnce` when the occurrence was created as missed work; `Kind = Late` when the materializer registered nothing but the delivery still started more than the threshold after the slot (a full queue, a long previous run) |

A schedule is keeping up while the slot it materializes came due less than `SetMisfireThreshold` ago (5
seconds by default) and is the only one owed. Past that threshold the occurrence stands for missed work even
when it is alone — a ten-minute outage on an hourly schedule leaves exactly one slot owed, and replaying it is
the whole point of the policy. A run of more than one slot always says so, whatever the threshold: `FireOnce`
is about to collapse those slots and `CatchUp` to replay them, and neither happens unreported.

When the occurrence stands for missed work, `Misfire` describes the backlog that produced it: `Kind` is
`CatchUp` or `FireOnce`, `MissedFromUtc` and `MissedThroughUtc` are its oldest and newest slot, `MissedCount`
is how many slots that range holds (both ends included), and `Lateness` is how far past its own slot this
delivery actually started. For a `FireOnce` occurrence the whole range collapsed into the one delivery running
now; for a `CatchUp` occurrence it is the backlog this row is one slot of, as it stood when the row was
created. Successive rows of the same drain therefore report a smaller range, and a count that shrinks with it.

`MissedCountIsExact` says whether the count is the real total. Counting a calendar or cron grid means walking
it, and EverTask does not walk three months of one-minute slots to produce a number for a log line: past a
bound the count becomes "at least this many" and this flag turns `false`. A grid it can count by division —
every plain cadence — always answers exactly, however long the outage was.

An occurrence has no schedule definition of its own, so the zone is copied onto its row when the row is
created. It survives a restart that way, and a reschedule into a different zone does not rewrite the rows that
already exist: they keep the zone they were created under.

## Contracts worth knowing before you turn it on

**At-least-once, and idempotent handlers.** A crash between a handler's side effect and the storage write that
records it re-runs the occurrence at the next startup. Write handlers that can run twice — check before you
insert, key your external calls, make the second run a no-op.

**One active host.** EverTask 4.0 contracts a single ACTIVE host. Materialization is idempotent across hosts —
a unique index on (schedule, slot) means two hosts racing the same backlog still produce one row per slot —
but EXECUTION is not claimed by anyone: a second live host recovers an occurrence the first is already running
and runs it again. Run one active instance (a standby that is not started is fine). Distributed execution is a
separate epic; see [Scalability](../scalability.md).

**A failed occurrence does not stop the series.** It ends `Failed`, keeps its trail, and the schedule advances
to the next slot. Putting one back in a queue is a separate, explicit act: the storage operation is there
(`ITaskStorage.RequeueTerminal`), and the API that hands it to you arrives with runtime schedule management.

**An occurrence this build cannot read is ended too.** A task type a deployment renamed away, a payload that
no longer deserializes, or a handler the application no longer registers leaves a row the running process
cannot turn back into a delivery, and asking again a minute later gets the same answer. It ends `Failed`, with
the reason on the row, instead of waiting: an occurrence that is neither running nor finished still holds a
place in `MaxPendingOccurrences`, and with the default of one the series never materializes anything again.
Requeue it once a deployment can read it.

A handler that is registered and only failed to build is a different case. A scoped dependency that timed out
looks exactly like a handler nobody registers any more, so the schedule asks the container which of the two it
is: if something is registered, the occurrence keeps its place and the next run tries again. Ending it there
would drop work no handler ever saw, without one of the retries its policy promises.

**A schedule this build cannot read is not ended, it is parked.** The same thing can happen to the schedule
row — a payload that stopped deserializing, a time zone id this machine no longer resolves — and ending that
row would end the whole series, so the run does not. It materializes nothing, reports why, and puts the row
back in the scheduler at the retry interval. The verdict belongs to startup recovery, which retries such a row
a few times across restarts and then poisons it. Nothing runs in the meantime: a series that has gone quiet
with that event against it is waiting for a deployment that can read its row again.

**`MaxRuns` counts materializations.** A durable series spends a run when it CREATES an occurrence, including
one that later fails or is cancelled — the schedule did produce it. `RunUntil` stays exclusive on the slot.

**Cancelling the schedule cancels its pending occurrences**, in the same write: the ones still waiting become
`Cancelled`, and one that was already on its way to a worker is dropped before it runs rather than quietly
requeued. A materializer racing the cancel can only find an inactive schedule, and one that committed a slot a
moment before the cancel reached the row is cancelled with the rest. Occurrences already executing own a live
delivery and run to their own end, but however that end comes it counts as a cancellation: one a
shutdown interrupts is recorded `Cancelled` rather than "stopped by the service", and one a crash left
mid-flight is cancelled by the next startup instead of being put back in a queue.

**Storage support is required.** Every built-in provider (SQL Server, PostgreSQL, MySQL/MariaDB, SQLite and
the in-memory store) implements the atomic occurrence operations. A custom storage that does not is refused at
dispatch, with no partial emulation — see [Custom Storage](../storage/custom-storage.md).

## Keeping the table from growing

An occurrence is a row, and a per-minute schedule produces 1,440 of them a day. `OccurrenceRetentionDays`
prunes terminal occurrences — `Completed`, `Failed` and `Cancelled` alike — once they are older than the
window. Retention is not a builder option: it lives on the cleanup service of the EF Core storage package, and
registering that service is what turns retention on at all.

```csharp
builder.Services.AddAuditCleanup(
    new AuditRetentionPolicy { OccurrenceRetentionDays = 30 },
    cleanupIntervalHours: 24);
```

It leaves the schedule row alone, and a pruned occurrence is never re-materialized: the cursor decides what
exists, not the rows behind it. An occurrence that still owns execution logs inside their own retention window
is kept until those expire too, so pruning occurrences never deletes logs the log retention deliberately
preserved.

The same policy carries the audit and execution-log windows; they are listed in
[Audit & Execution-Log Retention](../configuration-reference.md#audit--execution-log-retention-addauditcleanup).

## Configuration

Three host-wide knobs shape how the materializer behaves. None of them is usually worth touching.

| Option | Default | Accepted range | What it does |
|--------|---------|----------------|----------------|
| `SetMaterializationConcurrency(int)` | `MaxDegreeOfParallelism` | 1 or more | How many schedules may be materializing occurrences at once |
| `SetBacklogRetryInterval(TimeSpan)` | 1 minute | 1 second to 1 day | How long a blocked schedule waits before trying again |
| `SetMisfireThreshold(TimeSpan)` | 5 seconds | 0 or more | How old a due slot must be for the occurrence it produces to say it stands for missed work |

Anything outside those ranges throws `ArgumentOutOfRangeException` at configuration time. The retry interval
is bounded at both ends: below the scheduler's own one-second tick it only adds churn, and above a day it
guarantees nothing, since it is the last thing between a blocked schedule and the next restart.

The ordinary way a blocked schedule resumes is the kick each occurrence gives when it ends, which is
immediate. The retry interval is the guarantee behind it: startup recovery runs once, so without it a schedule
whose kick was lost would wait for the next restart.

`SetMisfireThreshold` is the same tolerance a delivery reports its own lateness against
([Task Creation](../task-creation.md#when-a-delivery-counts-as-late)), applied one step earlier here: it
decides what the materializer records on the row, not what a running delivery observes about itself. Raise it
when the grid is fine-grained and the handler does not care about seconds of drift. It is host-wide, so it
moves for every schedule at once.

## Events

| Event message | When |
|---------------|------|
| `Materialized occurrence … of schedule …` | a slot became a row |
| `Schedule … skipped N due slot(s) from …: outside the misfire window` | slots fell outside `MaxAge` |
| `Schedule … skipped N due slot(s) from …: older than the most recent slots the catch-up cap keeps` | `SkipOldest` dropped the part of the backlog beyond `MaxOccurrences` |
| `Schedule … skipped N due slot(s) from …: the skip policy does not replay a slot that is no longer current` | a durable schedule under `Skip` moved past a backlog |
| `Stale occurrence … was stranded in … and has been requeued` | an occurrence with no live delivery behind it was rescued |
| `Occurrence … cannot be rebuilt from its row and was marked Failed` | its type, payload or handler no longer loads, so nothing can deliver it |
| `Schedule … cannot be rebuilt from its row and materializes nothing` | the same on the schedule row, which is parked for the retry instead of being ended |
| `Catch-up of schedule … halted at cursor …` | the backlog exceeded `MaxOccurrences` under `Halt` |
| `Slot … of schedule … already has an occurrence` | the cursor pointed at a slot that was already served, and was carried past it |

Every dropped run says which limit dropped it, and one catch-up run can report two of them with a count each.
An age window too narrow for the outage and a cap that kept only the newest slots are different settings with
different fixes; one number under one cause sent you to the wrong one.

The counting messages say `at least N` instead of `N` when the number is a lower bound, for the same reason
`MissedCountIsExact` exists: a walked grid is counted under a bound, and a bounded count is never reported as
a total.

## See also

- [Time Zones](time-zones.md) — a calendar schedule read on a real clock
- [Managing Tasks](managing-tasks.md) — inspecting and cancelling recurring tasks
- [Scalability](../scalability.md) — what one active host means for a deployment
