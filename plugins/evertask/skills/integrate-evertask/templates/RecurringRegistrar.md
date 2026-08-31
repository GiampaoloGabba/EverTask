# Template: idempotent recurring-task registrar

Register recurring tasks at startup with a stable `taskKey` so app restarts update (not duplicate)
them. Use a hosted service. Works in both web apps and worker services.

```csharp
public sealed class RecurringTasksRegistrar(ITaskDispatcher dispatcher) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // Daily at 03:00 UTC
        await dispatcher.Dispatch(
            new DailyCleanupTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)),
            taskKey: "daily-cleanup");

        // Every 5 minutes, high frequency → minimal audit
        await dispatcher.Dispatch(
            new HealthCheckTask(),
            r => r.Schedule().Every(5).Minutes(),
            auditLevel: AuditLevel.Minimal,
            taskKey: "health-check");

        // Business-hours monitor via cron (Mon–Fri, every 15 min, 09:00–16:xx)
        await dispatcher.Dispatch(
            new BusinessHoursMonitorTask(),
            r => r.Schedule().UseCron("*/15 9-16 * * 1-5"),
            taskKey: "biz-hours-monitor");

        // A digest people read at 09:00 their time, all year: name the zone, don't convert
        await dispatcher.Dispatch(
            new DailyDigestTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(9, 0)).InTimeZone("Europe/Rome"),
            taskKey: "daily-digest");

        // A nightly batch where a missed night still has to run: every due slot gets its own row,
        // and a downtime is replayed inside caps that are mandatory on purpose.
        await dispatcher.Dispatch(
            new NightlyReconciliationTask(),
            r => r.Schedule()
                  .EveryDay().AtTime(new TimeOnly(2, 0))
                  .InTimeZone("Europe/Rome")
                  .OnMisfire(m => m.CatchUp(
                      new CatchUpOptions(TimeSpan.FromDays(92), maxOccurrences: 200))),
            taskKey: "nightly-reconciliation");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register it:

```csharp
builder.Services.AddHostedService<RecurringTasksRegistrar>();
```

To change one of these while the application runs — an admin screen moving the digest, a tenant picking its
own hour — use `ITaskScheduleManager` instead of re-dispatching. It is registered by `AddEverTask` and takes
the same builder:

```csharp
public sealed class DigestSchedule(ITaskScheduleManager schedules)
{
    public Task<ScheduleUpdateResult> MoveTo(TimeOnly at, string zone) =>
        schedules.Reschedule(
            "daily-digest",
            r => r.Schedule().EveryDay().AtTime(at).InTimeZone(zone),
            // Keeps today's run on today: the day the cursor was in is preserved even across a zone change.
            RescheduleMode.RebaseFromCursor);
}
```

Notes:
- `taskKey` ≤ 200 chars, case-sensitive. Per-entity: `taskKey: $"report-{userId}"`.
- Re-dispatch with the same key + new schedule updates a Pending/Queued task in place.
- `ITaskScheduleManager` is the runtime counterpart: it refuses a key that names no schedule instead of
  creating one, bumps the schedule version so a run finishing at the same moment recomputes, and reports the
  new cursor. `RebaseFromCursor` needs the same cadence and selectors on both sides (only the time of day, the
  zone, the bounds and the misfire settings may move) and refuses cron; `RecalculateFromNow` is the fallback.
- Times are UTC unless the schedule names a zone. For a local wall-clock hour use `.InTimeZone("Area/City")`
  (calendar schedules only) instead of converting once at registration, which freezes the offset and drifts
  by an hour at the next DST change. `RunAt` still takes an absolute instant: build it with
  `zone.GetUtcOffset(localDateTime)`, never `zone.BaseUtcOffset`.
- `UseCron(...)` overrides every other interval call; never combine them.
- With the default `Skip` policy, occurrences a downtime missed are logged only: they don't run and don't
  count against `MaxRuns`. Under `CatchUp` or `FireOnce` they become real rows, so they DO count — a
  replayed slot is a run of the series. Pick per task: a heartbeat wants `Skip`, a nightly batch usually
  does not. The handler reads the slot it stands for from `Context.ScheduledAtLocal`, never from the clock:
  a replay delivers several nights within seconds of each other.
