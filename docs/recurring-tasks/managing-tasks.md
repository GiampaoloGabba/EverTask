---
layout: default
title: Managing Recurring Tasks
parent: Recurring Tasks
nav_order: 8
---

# Managing Recurring Tasks

Learn how to change, cancel, retrieve information about, and monitor your recurring tasks.

## Changing a Schedule While It Runs

`ITaskScheduleManager` is registered by `AddEverTask` next to `ITaskDispatcher`. It works on a schedule that
is already registered, addressed by the `taskKey` it was dispatched with:

```csharp
public class ScheduleAdminController(ITaskScheduleManager schedules) : ControllerBase
{
    [HttpPost("reports/daily/time")]
    public async Task<IActionResult> MoveDailyReport(TimeOnly at, CancellationToken ct)
    {
        var result = await schedules.Reschedule(
            "daily-report",
            builder => builder.Schedule().EveryDay().AtTime(at).InTimeZone("Europe/Rome"),
            RescheduleMode.RebaseFromCursor,
            ct);

        return Ok(new { result.NextRunUtc, result.ScheduleVersion });
    }
}
```

| Method | What it does | What the storage must support |
|--------|--------------|-------------------------------|
| `Reschedule(taskKey, configure, mode)` | Replaces the definition and picks a new cursor | `SupportsScheduleVersioning` — and `SupportsDurableOccurrences` as well when the new definition is a durable one |
| `ReevaluateSchedule(taskKey)` | Keeps the definition, recomputes the cursor from now — **and a durable backlog is discarded** | `SupportsScheduleVersioning` |
| `ResumeSchedule(taskKey)` | Releases a durable catch-up halt, keeping the cursor | `SupportsScheduleVersioning` |
| `RequeueFailedOccurrence(occurrenceId)` | Puts one terminal occurrence back in the queue | `SupportsDurableOccurrences` |
| `CancelSchedule(taskKey)` | Cancels the schedule and every pending occurrence of it | nothing beyond a registered storage |

All the built-in storages support both capabilities, so the distinction only matters for a custom store.
The three calls that rewrite a schedule row need the compare-and-swap overloads, and there is no half-atomic
emulation to fall back on: without a real compare-and-swap a reschedule could report success while a run
finishing at the same moment overwrote it. `RequeueFailedOccurrence` needs the other capability instead —
occurrences are rows only a durable schedule ever creates. `CancelSchedule` writes a cancellation, which every
storage has always been able to do. A call whose capability is missing throws `NotSupportedException`.

What a call is asked to write counts as well as what it is. A `Reschedule` whose new definition turns the
schedule durable — `WithDurableOccurrences()`, or an `OnMisfire` policy of `FireOnce` or `CatchUp` — asks the
store for the atomic materialization behind those occurrences too, so it needs `SupportsDurableOccurrences`
on top of the versioning. It is refused before anything is written, exactly as a dispatch of the same
definition would be.

### Which cursor the new definition starts from

`RescheduleMode.RecalculateFromNow` (the default) points the cursor at the new definition's first occurrence
after now. On a durable schedule that drops whatever the old definition still owed, and says how much in
`ScheduleUpdateResult.DiscardedBacklog` and in a monitoring event.

`RescheduleMode.RebaseFromCursor` keeps the schedule inside the calendar period it was already in. The day,
week or month the old cursor fell in is read on the old definition's clock, and the new cursor is the
occurrence at the same POSITION inside that period, read on the new definition's clock. Moving a daily 09:00
job to 10:00 leaves it on today; moving a Rome schedule to `Pacific/Kiritimati` keeps its logical date even
though the instant moves. Position matters as soon as a period holds more than one slot: with
`OnDays(Monday, Wednesday).AtTimes(09:00, 15:00)` a cursor standing on the afternoon run rebases onto the new
afternoon time, never back onto the morning one that has already run. `EveryWeek()` and `EveryMonth()` name no
day inside their period, and for those the day itself rides on the cursor: the weekday, or the day of the
month, is what the rebase keeps. It is deliberately narrow:

- the two definitions must have the same shape — same cadence, same weekday and month selectors, same period
  kind. What may change is the time of day, the zone, the bounds and the misfire settings;
- a cron schedule states no nominal period and is refused, and so does a schedule whose occurrences come from
  an occurrence provider;
- a period the new definition has no slot in is refused too, rather than answered from the next period — and
  so is one that holds fewer slots than the cursor had already passed, since there is no position to land on.

Anything refused throws `InvalidOperationException` and writes nothing, so `RecalculateFromNow` is always
available as the fallback. There is one more reason a rebase is refused, and it has nothing to do with the
calendar: the bounds. Winding a series down means a `RunUntil` the cursor has already passed, or a `MaxRuns`
budget it has already spent, and either way there is no occurrence left to run. The period arithmetic does not
notice. A plain cadence keeps its cursor verbatim, `EveryWeek()` and `EveryMonth()` place their slot by hand,
and neither ever asks the grid — which is the only thing that applies `RunUntil`. Both bounds are checked here
instead, and both get the answer `RecalculateFromNow` gives for the same definition. Use `CancelSchedule` to
end a series on purpose.

### Re-evaluating without changing the definition

`ReevaluateSchedule(taskKey)` is `RecalculateFromNow` applied to the definition already on the row: it is what
to call when something the schedule depends on has changed — a calendar, a feature flag, an occurrence
provider's configuration — and the schedule itself has not.

It moves the cursor to the next occurrence after now, so on a durable schedule that is behind, **the slots it
still owed are dropped**, counted in `ScheduleUpdateResult.DiscardedBacklog` and carried on the
`ScheduleRescheduled` event described below. That is the right answer when the old backlog was computed from
something that is no longer true, and the wrong one when the work is still owed: to release a late or halted
catch-up while keeping its backlog, call `ResumeSchedule`, which is the one method that leaves the cursor
where it is.

### What the change publishes

Every accepted change publishes one monitoring event, `ScheduleRescheduled`, and it carries the whole change
rather than the state it ended at: the version the schedule came from and the one it is now at, the cursor it
stood on and the one it stands on, the mode that decided it, whether a catch-up halt was released, and how
many due slots were discarded. The count is bounded, because a long one-minute backlog owes more slots than
anything here needs to enumerate, so a truncated one is published as `at least N`;
`ScheduleUpdateResult.DiscardedBacklogIsExact` says which of the two you are reading. The event's severity is
a warning when slots were discarded and information otherwise.

There is no second event for the discarded backlog. A schedule change is one thing that happened, so it is
one event, and a consumer that matches on a name matches `ScheduleRescheduled` and reads the count off it.
`BacklogDiscarded` carries the same count in the logs, for whoever reads logs; it is not something a
subscriber can match.

The write commits before the schedule is handed back to whatever parks it, so a re-park that fails does not
make the change disappear: the event is published all the same, followed by an error event saying the row
could not be parked. The version is not published as a lower bound in that case, which is what lets the
previous occurrence run once more and apply the new definition itself.

### What happens to a run already in flight

A reschedule is never refused because a run is in progress, and it takes effect immediately for occurrences
that have not fired yet: the scheduler's registration is replaced. A delivery already handed to a worker queue
is considered fired — it may finish under the definition it started with, and its advance then loses the
compare-and-swap and applies the new one.

That advance re-aims a bounded number of times, so a schedule rewritten again and again cannot spin it. What
the bound never costs is the run itself: past the last attempt the guard is given up and the run is recorded
anyway, against the cursor the last reading carried, and the schedule is parked from that row. A run that
happened is always counted — losing it would leave the row mid-flight, the execution unaudited and the
`MaxRuns` budget permanently one short.

### Requeuing a failed occurrence

A durable occurrence that ended `Failed` (or `Cancelled`) can be put back in the queue with its identity
intact:

```csharp
var requeued = await schedules.RequeueFailedOccurrence(occurrenceId);
```

Re-dispatching under the same task key would delete the row and create a new one, losing exactly the history
an operator is looking at when they decide to retry. Requeuing keeps the id, the audit trail and the execution
logs; it returns `false` when the row is no longer terminal, and refuses a schedule row outright. It spends no
run of the series: `MaxRuns` counts materializations, and a requeue materializes nothing.

Cancelling one occurrence on its own, with `ITaskDispatcher.Cancel(occurrenceId)`, leaves it a normal target
here, and requeuing it undoes that cancel: the entry it left in this process's blacklist is dropped too, so
the redelivery reaches the handler instead of being thrown away on its way into the queue. If a cancel of the
whole schedule commits while the requeue is in flight, the occurrence goes back to `Cancelled` and the call
answers `false`. A cancelled series never keeps a live row under it.

The schedule has the last word. `CancelSchedule` cancels the pending occurrences along with the schedule, so
by status alone every one of them would look like a legitimate target here; requeuing one is refused with an
`InvalidOperationException` instead, because a cancellation is terminal for the whole series. A schedule that
is simply over — its budget spent, its `RunUntil` passed — is not: that occurrence was owed, and replaying it
creates nothing new.

## Cancelling Recurring Tasks

```csharp
// Store task ID when registering
Guid taskId = await dispatcher.Dispatch(
    new RecurringTask(),
    builder => builder.Schedule().EveryHour(),
    taskKey: "my-recurring-task");

// Later, cancel it
await dispatcher.Cancel(taskId);
```

`ITaskScheduleManager.CancelSchedule("my-recurring-task")` does the same thing without the id, which is
useful when the key is all the calling code has. Either way the cancellation is terminal: a cancelled
schedule cannot be rescheduled, it has to be dispatched again.

## Retrieving Task Information

```csharp
// Get task by key
var task = await _taskStorage.GetByTaskKey("daily-report");

if (task != null)
{
    Console.WriteLine($"Task ID: {task.Id}");
    Console.WriteLine($"Status: {task.Status}");
    Console.WriteLine($"Current Run Count: {task.CurrentRunCount}");
    Console.WriteLine($"Next Run: {task.NextRunUtc}");
}
```

## Monitoring Recurring Tasks

Lifecycle hooks let you track execution patterns and catch issues:

```csharp
public class MonitoredRecurringHandler : EverTaskHandler<MonitoredRecurringTask>
{
    private readonly ILogger<MonitoredRecurringHandler> _logger;
    private readonly ITaskStorage _storage;

    public MonitoredRecurringHandler(ILogger<MonitoredRecurringHandler> logger, ITaskStorage storage)
    {
        _logger = logger;
        _storage = storage;
    }

    public override async Task Handle(MonitoredRecurringTask task, CancellationToken cancellationToken)
    {
        // The run count lives on the persisted row, not on the request payload.
        // Read it from storage by task key when you need it inside the handler.
        var persisted = await _storage.GetByTaskKey("monitored-recurring");
        _logger.LogInformation("Recurring task execution #{Count}", persisted?.CurrentRunCount);

        // Task logic here
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        _logger.LogInformation("Recurring task {TaskId} completed successfully", taskId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Recurring task {TaskId} failed: {Message}", taskId, message);

        // Send alerts, page on-call engineers, etc.

        return ValueTask.CompletedTask;
    }
}
```

## Next Steps

- **[Fluent Scheduling API](fluent-api.md)** - Build recurring schedules
- **[Idempotent Task Registration](idempotent-registration.md)** - Prevent duplicate tasks
- **[Best Practices](best-practices.md)** - Monitor recurring task health
- **[Monitoring](../monitoring.md)** - Advanced monitoring with SignalR integration
