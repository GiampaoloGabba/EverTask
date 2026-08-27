# EverTask Roadmap

Planned work, in no particular order. Priorities shift with real-world feedback: if one of these matters to
you, [open an issue](https://github.com/GiampaoloGabba/EverTask/issues) describing your use case.

## Planned

### Task management from the dashboard

The dashboard and REST API are read-only today. Planned: stop, cancel and requeue tasks from the UI, bulk
operations on failed tasks, pause/resume queues, and editing a recurring schedule in place. The runtime
[`ITaskScheduleManager`](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/managing-tasks.html)
shipped in 4.0 is the seam this builds on.

### Distributed clustering

Multiple active hosts with leader election and automatic failover. EverTask 4.0 contracts a single active
host (a standby that is not started is fine): materialization is already idempotent across hosts, but
execution is not claimed, so clustering means pluggable queue and distributed-lock providers on top of the
existing single-instance mode, which stays supported.

### Distributed rate limiting

A Redis-based (GCRA) keyed limiter sharing budgets across instances. The in-process keyed limiter shipped in
3.7.0, and the `IKeyedRateLimiter` DI seam — with its contract invariants documented — is already in place.

### Advanced throttling

Global rate limits (max N tasks/sec across all queues), per-handler concurrency caps, and adaptive
throttling based on CPU/memory pressure.

### Workflow orchestration

Sequential, parallel, conditional and saga/compensation flows with a fluent API, built on the existing
continuation primitives, with step-level persistence and monitoring integration.

### Batch dispatch

Dispatch and track a set of tasks as one unit: aggregated batch status (completed, running, failed,
cancelled), batch monitoring events, optional batch persistence.

### Additional monitoring targets

Sentry Crons, Application Insights, OpenTelemetry metrics and traces export.

### More storage options

Redis and Cosmos DB providers. (PostgreSQL shipped in 3.x, MySQL/MariaDB in 4.0.)

### Richer samples

Complete sample applications rather than code snippets: an order-processing pipeline, a bulk mailer under
rate limiting, an ETL workflow, each with a Docker Compose setup.

## Shipped

The [changelog](CHANGELOG.md) has the full history; the milestones:

- **4.0.0** — durable occurrences: one persisted row per due slot. Misfire policies (`Skip`, `FireOnce`,
  `CatchUp` under mandatory caps) decide what a downtime does to the slots it missed, and every dropped
  slot is reported. Time-zone-aware schedules with daylight-saving semantics (`InTimeZone`).
  Execution context in handlers (`Context.ScheduledAtUtc`, `Context.Misfire`). Runtime schedule management
  (`ITaskScheduleManager`: reschedule, re-evaluate, resume, cancel, requeue a failed occurrence). Custom
  occurrence providers (`INextOccurrenceProvider`) for calendars the library cannot know. MySQL/MariaDB
  storage provider.
- **3.7.0** — keyed rate limiting: per-tenant/per-resource GCRA budgets at dequeue, no head-of-line
  blocking across keys, no worker held while waiting.
- **3.2.0** — monitoring dashboard: embedded React UI, REST API with OpenAPI, JWT authentication, SignalR
  real-time updates, execution logs viewer, multi-queue analytics.
- **3.0–3.1** — execution log capture with optional database persistence and retention, lazy handler
  resolution, schedule-drift fix for recurring tasks, retry policies with exception filtering and the
  `OnRetry` callback.
