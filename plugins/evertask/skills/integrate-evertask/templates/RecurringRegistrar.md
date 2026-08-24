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
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Register it:

```csharp
builder.Services.AddHostedService<RecurringTasksRegistrar>();
```

Notes:
- `taskKey` ≤ 200 chars, case-sensitive. Per-entity: `taskKey: $"report-{userId}"`.
- Re-dispatch with the same key + new schedule updates a Pending/Queued task in place.
- Times are UTC unless the schedule names a zone. For a local wall-clock hour use `.InTimeZone("Area/City")`
  (calendar schedules only) instead of converting once at registration, which freezes the offset and drifts
  by an hour at the next DST change. `RunAt` still takes an absolute instant: build it with
  `zone.GetUtcOffset(localDateTime)`, never `zone.BaseUtcOffset`.
- `UseCron(...)` overrides every other interval call; never combine them.
- Skipped occurrences after downtime are logged only: they don't count against `MaxRuns`.
