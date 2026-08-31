![EverTask Logo](https://raw.githubusercontent.com/GiampaoloGabba/EverTask/master/assets/logo-main.png)

[![Build](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml/badge.svg)](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.svg?label=EverTask)](https://www.nuget.org/packages/EverTask)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Abstractions.svg?label=EverTask.Abstractions)](https://www.nuget.org/packages/EverTask.Abstractions)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Storage.SqlServer.svg?label=EverTask.Storage.SqlServer)](https://www.nuget.org/packages/EverTask.Storage.SqlServer)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Storage.Sqlite.svg?label=EverTask.Storage.Sqlite)](https://www.nuget.org/packages/EverTask.Storage.Sqlite)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Storage.Postgres.svg?label=EverTask.Storage.Postgres)](https://www.nuget.org/packages/EverTask.Storage.Postgres)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Storage.MySql.svg?label=EverTask.Storage.MySql)](https://www.nuget.org/packages/EverTask.Storage.MySql)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Storage.EfCore.svg?label=EverTask.Storage.EfCore)](https://www.nuget.org/packages/EverTask.Storage.EfCore)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Logging.Serilog.svg?label=EverTask.Logging.Serilog)](https://www.nuget.org/packages/EverTask.Logging.Serilog)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.AspnetCore.SignalR.svg?label=EverTask.Monitor.AspnetCore.SignalR)](https://www.nuget.org/packages/EverTask.Monitor.AspnetCore.SignalR)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.Api.svg?label=EverTask.Monitor.Api)](https://www.nuget.org/packages/EverTask.Monitor.Api)
[![NuGet](https://img.shields.io/nuget/vpre/EverTask.Monitor.Api.Scalar.svg?label=EverTask.Monitor.Api.Scalar)](https://www.nuget.org/packages/EverTask.Monitor.Api.Scalar)

## Overview

**EverTask** runs background work in your .NET app: fire-and-forget jobs, delayed and scheduled tasks, and recurring schedules. Everything is persisted, so tasks survive a restart.

With 4.0, a recurring schedule can also survive a downtime *observably*: every due slot gets its own persisted row, and a misfire policy decides what happens to the slots an outage missed. Nothing is lost silently: every dropped slot is reported.

It runs in-process (no external scheduler, no Windows Service, no separate worker host), and it doesn't poll the database in a loop. An in-memory scheduler drives execution through channels, and persistence happens on enqueue, on status changes, and for recovery after a restart.

If you've used MediatR, the request/handler pattern will feel familiar. The difference is that here tasks are persisted, can be isolated across queues, and keep working under load.

Tasks can be CPU-bound or I/O-bound, long- or short-running. Works with ASP.NET Core, Windows Services, or any .NET host. One active instance per store (a cold standby is fine); distributed execution is on the [roadmap](ROADMAP.md).

## Key Features

### Core execution
- **Background execution**: fire-and-forget, scheduled, and recurring tasks
- **No database polling**: the scheduler lives in memory and runs through channels; the database is written, not polled in a loop
- **Persistence**: tasks resume after a restart (SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, In-Memory)
- **Fluent scheduling**: recurring tasks by minute, hour, day, week, month, or cron
- **Time zones**: schedule on local wall-clock hours that keep their meaning across daylight saving
- **Durable occurrences & misfire policies**: give every due slot its own row (with its own status, retries
  and audit trail) and choose what a downtime does to the slots it missed: skip them, collapse them into one
  run, or replay them under explicit caps, with every dropped slot reported
- **Execution context**: a handler sees the slot it runs for (`Context.ScheduledAtUtc` / `ScheduledAtLocal`)
  and whether its delivery is late or replayed work (`Context.Misfire`)
- **Runtime schedule management**: change, re-evaluate, resume or cancel a schedule while the app is running
- **Custom occurrence providers**: compute the next run yourself, from a calendar the library cannot know
- **Fixed recurring exclusions**: subtract weekdays, dates and absolute maintenance windows from any built-in interval or cron grid
- **Named exclusion calendars**: register a holiday or blackout set once and reuse it across schedules
- **Idempotent registration**: a task key keeps duplicate recurring registrations out

### Performance & scalability
- **Multi-queue**: isolate workloads by priority, resource type, or business domain
- **Keyed rate limiting**: throttle per tenant/account/resource against external API limits, without blocking workers or other keys
- **Light scheduler**: minimal lock contention, zero CPU when idle
- **Sharded scheduler**: optional, for high scheduling load
- **Lower overhead**: reflection caching and lazy serialization

### Monitoring
- **Dashboard + REST API**: an embedded React UI for monitoring and analytics
- **Real-time updates**: SignalR push with event-driven cache invalidation
- **Execution log capture**: a proxy logger with optional database persistence and configurable retention
- **Audit levels**: tune how much audit history you keep, to control table growth

### Resilience
- **Retry policies**: built-in linear and exponential backoff (cap + jitter), custom policies, Polly integration, exception filtering
- **Timeouts**: global and per-task

### Developer experience
- **Extensible**: custom storage, retry policies, and schedulers
- **Serilog integration**: structured logging
- **Async throughout**
- **Compile-time analyzer**: a Roslyn analyzer (ET0001–ET0012) bundled in `EverTask.Abstractions`
  catches System.Text.Json contract violations, configuration mistakes and handler-registration problems (open-generic or duplicate handlers) in the IDE/build, with code fixes (see below)


<img src="assets/screenshots/4.png" style="width:100%;max-width:900px;display: block; margin:20px auto;" alt="Task Details" />

## Quick Start

### Installation

```bash
dotnet add package EverTask
dotnet add package EverTask.Storage.SqlServer  # Or EverTask.Storage.Postgres / EverTask.Storage.MySql / EverTask.Storage.Sqlite
```

### Configuration

```csharp
// Register EverTask with SQL Server storage
builder.Services.AddEverTask(opt =>
{
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddSqlServerStorage(builder.Configuration.GetConnectionString("EverTaskDb"));
```

### Create Your First Task

Define a task request:

```csharp
public record SendWelcomeEmailTask(string UserEmail, string UserName) : IEverTask;
```

Create a handler:

```csharp
public class SendWelcomeEmailHandler : EverTaskHandler<SendWelcomeEmailTask>
{
    private readonly IEmailService _emailService;

    public SendWelcomeEmailHandler(IEmailService emailService)
    {
        _emailService = emailService;
    }

    public override async Task Handle(SendWelcomeEmailTask task, CancellationToken cancellationToken)
    {
        Logger.LogInformation("Sending welcome email to {Email}", task.UserEmail);

        await _emailService.SendWelcomeEmailAsync(
            task.UserEmail,
            task.UserName,
            cancellationToken);
    }
}
```

Dispatch the task:

```csharp
// Send welcome email in background
await _dispatcher.Dispatch(new SendWelcomeEmailTask(dto.Email, dto.Name));
```

## AI-assisted setup (agent skill)

This repo ships an agent skill that wires up EverTask for you. On [Claude Code](https://claude.com/claude-code):

```text
/plugin marketplace add GiampaoloGabba/EverTask
/plugin install evertask@evertask
```

Then `/reload-plugins` and run `/evertask:integrate-evertask`. For other agents, copy `plugins/evertask/skills/integrate-evertask/` into your skills directory. Full guide: [agent skill](https://GiampaoloGabba.github.io/EverTask/agent-skill.html).

## Documentation

📚 **[Full Documentation](https://GiampaoloGabba.github.io/EverTask)** - Complete guides, tutorials, and API reference

### Quick Links

- **[Getting Started](https://GiampaoloGabba.github.io/EverTask/getting-started.html)** - Installation, configuration, and your first task
- **[Task Creation](https://GiampaoloGabba.github.io/EverTask/task-creation.html)** - Requests, handlers, lifecycle hooks, and best practices
- **[Task Dispatching](https://GiampaoloGabba.github.io/EverTask/task-dispatching.html)** - Fire-and-forget, delayed, and scheduled tasks
- **[Recurring Tasks](https://GiampaoloGabba.github.io/EverTask/recurring-tasks.html)** - Fluent scheduling API, cron expressions, idempotent registration
- **[Durable Occurrences](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/durable-occurrences.html)** - One row per occurrence, misfire policies, and what to do with the slots a downtime missed
- **[Time Zones](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/time-zones.html)** - Local hours that survive daylight saving
- **[Managing Recurring Tasks](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/managing-tasks.html)** - Reschedule, re-evaluate, resume, cancel and requeue at runtime
- **[Occurrence Providers](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/occurrence-providers.html)** - Compute the next run from a calendar the library cannot know
- **[Resilience & Error Handling](https://GiampaoloGabba.github.io/EverTask/resilience.html)** - Retry policies, timeouts, CancellationToken usage
- **[Monitoring](https://GiampaoloGabba.github.io/EverTask/monitoring.html)** - Complete monitoring guide (Dashboard, Events, and Logs)
- **[Scalability](https://GiampaoloGabba.github.io/EverTask/scalability.html)** - Multi-queue support, keyed rate limiting, and sharded scheduler for high-load scenarios
- **[Task Orchestration](https://GiampaoloGabba.github.io/EverTask/advanced-features.html)** - Chain tasks, build workflows, and coordinate complex processes
- **[Storage Configuration](https://GiampaoloGabba.github.io/EverTask/storage.html)** - SQL Server, PostgreSQL, MySQL/MariaDB, SQLite, In-Memory, custom implementations
- **[Configuration](https://GiampaoloGabba.github.io/EverTask/configuration.html)** - Configure EverTask (Reference + Cheatsheet)
- **[Agent Skill](https://GiampaoloGabba.github.io/EverTask/agent-skill.html)** - AI-assisted integration: install the skill and let an agent wire up EverTask (one-step on Claude Code)
- **[Architecture & Internals](https://GiampaoloGabba.github.io/EverTask/architecture.html)** - How EverTask works under the hood

## A closer look

### Fluent Recurring Scheduler

Schedule recurring tasks with a type-safe API:

```csharp
// Run every day at 3 AM
await dispatcher.Dispatch(
    new DailyCleanupTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)));

// Run every Monday, Wednesday, Friday at 9 AM (for 30 days)
var days = new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday };
await dispatcher.Dispatch(
    new BackupTask(),
    builder => builder.Schedule().EveryWeek().OnDays(days).AtTime(new TimeOnly(9, 0)).RunUntil(DateTimeOffset.UtcNow.AddDays(30)));
```

### Excluding weekends, holidays and maintenance windows

Any built-in interval or cron grid can subtract fixed moments. Those slots never exist, so they
consume no run, no misfire count and no durable row:

```csharp
// Daily report at 8:00, but never on weekends, Christmas, or during the maintenance window
await dispatcher.Dispatch(
    new DailyReportTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(8, 0))
        .Except(e => e
            .OnDays(DayOfWeek.Saturday, DayOfWeek.Sunday)
            .OnDates(new DateOnly(2026, 12, 25))
            .Between(maintenanceStart, maintenanceEnd)));

// Metrics every 4 hours, weekdays only - the zone decides what "weekend" means
await dispatcher.Dispatch(
    new MetricsRollupTask(),
    builder => builder.Schedule().Every(4).Hours()
        .InTimeZone("Europe/Rome").ExceptWeekends());

// Or define the holidays ONCE at the host and reuse them by name
services.AddEverTask(opt => opt
    .AddScheduleCalendar("it-holidays", cal => cal
        .OnDates(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25), new DateOnly(2026, 12, 26))));

await dispatcher.Dispatch(
    new DailyReportTask(),
    builder => builder.Schedule().EveryDay().AtTime(new TimeOnly(8, 0))
        .ExceptCalendar("it-holidays"));
```

Exclusions are part of the persisted definition and compose with misfire policies, catch-up, backfill and
durable occurrences. Details: [Recurring Tasks](docs/recurring-tasks.md#excluding-moments).

### Time Zones and Durable Occurrences

A calendar schedule can name the zone its hours are read on, and a downtime no longer has to lose the slots it
covered:

```csharp
// 02:00 in Rome, every day, whatever daylight saving does to the offset.
// A downtime replays the slots it missed, one row each, up to 92 days back and 200 occurrences per episode.
await dispatcher.Dispatch(
    new NightlyReconciliationTask(),
    r => r.Schedule()
          .EveryDay().AtTime(new TimeOnly(2, 0))
          .InTimeZone("Europe/Rome")
          .OnMisfire(m => m.CatchUp(new CatchUpOptions(TimeSpan.FromDays(92), maxOccurrences: 200))),
    taskKey: "nightly-reconciliation");
```

Three misfire policies decide what a downtime does to the slots it covered:

- `.OnMisfire(m => m.Skip())` is the default: missed slots are skipped, and a durable schedule reports how many
- `.OnMisfire(m => m.FireOnce())` collapses the whole missed run into ONE occurrence, at the most recent slot
- `.OnMisfire(m => m.CatchUp(...))` replays every missed slot, oldest first, inside mandatory caps; a
  backlog beyond the caps either halts the schedule until a person resumes it (`Halt`, the default) or keeps
  the most recent slots and reports the drop (`SkipOldest`)

Each replayed slot becomes its own persisted row, with its own status, retries and audit trail. The caps are
mandatory on purpose: a schedule is never allowed to replay an unbounded backlog, and every slot it does drop
is reported with a count and a range. See
[Durable Occurrences](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/durable-occurrences.html) and
[Time Zones](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/time-zones.html).

### Execution Context

A handler knows the slot it stands for, not just the moment it happened to start, and replayed or late work
says so:

```csharp
public override async Task Handle(NightlyReconciliationTask task, CancellationToken ct)
{
    var slot = Context.ScheduledAtLocal; // the nominal slot, on the schedule's own clock

    if (Context.Misfire is { } misfire)
        Logger.LogInformation("Recovering slot {Slot}: {Kind}, {Count} slot(s) missed between {From} and {Through}",
            slot, misfire.Kind, misfire.MissedCount, misfire.MissedFromUtc, misfire.MissedThroughUtc);

    await ReconcileAsync(slot, ct);
}
```

`Context` also carries the task id and key, the attempt number, the run number and the time zone; services
deeper in the dependency graph read the same context through `ITaskExecutionContextAccessor`. See
[Task Creation](https://GiampaoloGabba.github.io/EverTask/task-creation.html).

### Runtime Schedule Management

`ITaskScheduleManager` changes a live schedule without touching the running app's registration code:

```csharp
public class ScheduleAdminController(ITaskScheduleManager schedules) : ControllerBase
{
    [HttpPost("daily-report/time")]
    public async Task<IActionResult> MoveTo(TimeOnly newTime)
    {
        var result = await schedules.Reschedule(
            "daily-report",
            r => r.Schedule().EveryDay().AtTime(newTime),
            RescheduleMode.RebaseFromCursor);

        return Ok(result);
    }
}
```

It can also re-evaluate a schedule from now (`ReevaluateSchedule`), release a halted catch-up
(`ResumeSchedule`), cancel a series along with its pending occurrences (`CancelSchedule`), and put a failed
occurrence back in a queue with its history intact (`RequeueFailedOccurrence`). Every accepted change
publishes a monitoring event carrying what changed. See
[Managing Recurring Tasks](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/managing-tasks.html).

### Multi-Queue Workload Isolation

Keep critical tasks separate from heavy background work:

```csharp
// High-priority queue for critical operations
.AddQueue("critical", q => q
    .SetMaxDegreeOfParallelism(20)
    .SetChannelCapacity(500)
    .SetDefaultTimeout(TimeSpan.FromMinutes(2)))
```

### Retry policies with exception filtering

Control which exceptions trigger retries to fail-fast on permanent errors:

```csharp
// Predefined sets for common scenarios
RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2)).HandleTransientDatabaseErrors();

// Whitelist: Only retry specific exceptions (you can also use DoNotHandle for blacklist)
RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)).Handle<DbException>().Handle<HttpRequestException>();

// Predicate: Custom logic (e.g., HTTP 5xx only)
RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1)).HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);

// Exponential backoff (1s, 2s, 4s, 8s...) with cap and jitter, same filtering API
RetryPolicy => new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30), useJitter: true).HandleTransientNetworkErrors();
```

### Keyed Rate Limiting

Throttle tasks against external API limits (per tenant, per account, per resource) without slowing anyone else down:

```csharp
public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId.ToString();
}

public class SyncTenantDataHandler : EverTaskHandler<SyncTenantData>
{
    // Each tenant gets 15 calls per minute; other tenants are unaffected
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(15, TimeSpan.FromMinutes(1));

    public override Task Handle(SyncTenantData task, CancellationToken ct) => ...;
}
```

When a task exceeds its key's budget, EverTask reserves the next available slot and re-schedules it automatically: no worker is blocked, no task is dropped, and tasks for other keys keep flowing. Rate limiting is in-memory and per-instance (a pluggable seam for distributed limiters is on the [roadmap](ROADMAP.md)).

### Idempotent Task Registration

Use unique keys to safely register recurring tasks at startup without creating duplicates:

```csharp
// Register recurring tasks - safe to call on every startup
    await _dispatcher.Dispatch(
        new DailyCleanupTask(),
        r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)),
        taskKey: "daily-cleanup"); // Won't create duplicates
```

> ⚠️ **Self-redispatch gotcha**: while a handler is executing, its task is `InProgress`. A dispatch with the
> same key as an `InProgress` task is a no-op that returns the existing ID without scheduling anything (a
> warning is logged). If a handler re-dispatches itself (e.g. polling chains), use a null or per-attempt key
> like `"my-task-{id}-{attempt}"`; reserve stable keys for dispatches originating outside the handler.

### Monitoring Dashboard

Monitor tasks from a built-in web dashboard with live status, task history, execution logs, and analytics:

**Dashboard Preview:**

<div align="center">
<table>
<tr>
<td align="center" width="20%">
<img src="assets/screenshots/1.png" width="100%" alt="Dashboard Overview" />
<br />
<em>Dashboard Overview</em>
</td>
<td align="center" width="20%">
<img src="assets/screenshots/3.png" width="100%" alt="Task List" />
<br />
<em>Task List with Filters</em>
</td>
<td align="center" width="20%">
<img src="assets/screenshots/4.png" width="100%" alt="Task Details" />
<br />
<em>Task Details & History</em>
</td>
<td align="center" width="20%">
<img src="assets/screenshots/6.png" width="100%" alt="Execution Logs" />
<br />
<em>Execution Logs Viewer</em>
</td>
<td align="center" width="20%">
<img src="assets/screenshots/8.png" width="100%" alt="Execution Logs" />
<br />
<em>Realtime flow</em>
</td>
</tr>
</table>

📸 **[View all 10 screenshots in the documentation](https://GiampaoloGabba.github.io/EverTask/monitoring-dashboard-ui#screenshots)**

</div>

### Task Execution Log Capture

Capture all logs written during task execution and persist them to the database for debugging and auditing. Built-in retention (a time window plus a per-task cap) keeps log growth bounded for long-running and recurring tasks:

<img src="assets/screenshots/5.png" style="width:100%;max-width:900px;display: block; margin:20px auto;" alt="Task Details" />
<br />
<em>View logs in dashboard or retrieve via storage</em>

### Compile-time analyzers

Tasks are persisted with System.Text.Json, and its contract is stricter than Newtonsoft's. A violation used to
surface only at runtime, on recovery: a silently dropped member, or a deserialization throw. The Roslyn analyzer
bundled in `EverTask.Abstractions` (no extra package, no runtime dependency) catches it the moment you reference
`IEverTask`, in the IDE and in the build, with code fixes for the common cases.

Twelve rules (ET0001–ET0012) cover the payload serialization contract (public fields, unreachable setters,
Newtonsoft attributes, polymorphism without `[JsonPolymorphic]`, ambiguous constructors, …), delays beyond
what a .NET timer can arm, monitoring API misconfiguration, `.InTimeZone(...)` on a schedule that cannot
honor it, and handler-registration mistakes (open-generic or duplicate handlers). Every rule is configurable via `.editorconfig` (e.g. `dotnet_diagnostic.ET0001.severity = error`).
Full rule list: [serialization analyzers](https://GiampaoloGabba.github.io/EverTask/storage/serialization.html#catching-mistakes-at-build-time).

> Note: the payload serializer is reflection-based and isolated: a consumer's own STJ source generators don't
> affect it, and Native AOT / reflection-disabled builds are unsupported (see `EverTask.Abstractions` docs).

## Resources

- [Changelog](CHANGELOG.md) - Version history and release notes
- [GitHub Repository](https://github.com/GiampaoloGabba/EverTask) - Source code and issues
- [Examples](samples/) - Sample applications (ASP.NET Core, Console)

## Roadmap

Distributed clustering with leader election, Redis-backed distributed rate limiting, workflow orchestration,
task management from the dashboard, more storage providers: see [ROADMAP.md](ROADMAP.md).

## Contributing

Contributions are welcome. Bug reports, feature requests, and pull requests all help.

- Report issues: https://github.com/GiampaoloGabba/EverTask/issues
- Contribute code: https://github.com/GiampaoloGabba/EverTask/pulls

## License

EverTask is licensed under the [MIT License](LICENSE).

The task/handler pattern is inspired by Jimmy Bogard's [MediatR](https://github.com/jbogard/MediatR). Thanks for years of great ideas in the .NET space.

See [ATTRIBUTION.md](ATTRIBUTION.md) for acknowledgements and attributions.

---

**Developed with ❤️ by [Giampaolo Gabba](https://github.com/GiampaoloGabba)**
