# EverTask Roadmap

Planned work. The near-term sequence is decided: analyzer usage guidance
([#63](https://github.com/GiampaoloGabba/EverTask/issues/63)) first, then pipeline behaviors
([#60](https://github.com/GiampaoloGabba/EverTask/issues/60)), then OpenTelemetry
([#61](https://github.com/GiampaoloGabba/EverTask/issues/61)) built on top of them, then durable
continuations ([#62](https://github.com/GiampaoloGabba/EverTask/issues/62)); the distribution track
([#59](https://github.com/GiampaoloGabba/EverTask/issues/59) →
[#31](https://github.com/GiampaoloGabba/EverTask/issues/31)) follows. Everything else has no fixed order,
and priorities shift with real-world feedback: if one of these matters to you,
[open an issue](https://github.com/GiampaoloGabba/EverTask/issues) describing your use case.

## Planned

### Task management from the dashboard

4.0 shipped the first write surface (off by default, behind a separate operate credential): requeue a failed
occurrence, resume a halted schedule, cancel a schedule. Planned next: retry any failed task (not only
occurrences), bulk operations on failed tasks, pause/resume queues, and editing a recurring schedule in
place. The runtime
[`ITaskScheduleManager`](https://GiampaoloGabba.github.io/EverTask/recurring-tasks/managing-tasks.html)
shipped in 4.0 is the seam this builds on.

### Multi-publisher, single consumer

Dispatch from any number of processes, execute in one
([#59](https://github.com/GiampaoloGabba/EverTask/issues/59)): a publisher-only registration mode, a wake
channel so the consumer discovers new work without polling (Postgres LISTEN/NOTIFY natively, Redis pub/sub
for SQL Server and MySQL), a runtime pickup reusing the recovery pipeline, and a slow reconciliation sweep as
the safety net for lost signals. The single-active-host contract stays, guaranteed by deployment: this is
the first slice of distributed execution, shippable without leases.

### Distributed execution

Multiple active hosts ([#31](https://github.com/GiampaoloGabba/EverTask/issues/31)): per-task execution
leases with fencing tokens in place of `SetInProgress`, lease-aware recovery predicates, and a reaper that
notices a crashed peer. Materialization is already idempotent across hosts; claiming execution is what this
adds. Single-instance mode stays supported.

### Distributed rate limiting

A Redis-based (GCRA) keyed limiter sharing budgets across instances. The in-process keyed limiter shipped in
3.7.0, and the `IKeyedRateLimiter` DI seam — with its contract invariants documented — is already in place.

### Advanced throttling

Global rate limits (max N tasks/sec across all queues), concurrency caps per handler and per key
(mutex/semaphore semantics next to the GCRA rate budgets), and adaptive throttling based on CPU/memory
pressure.

### Continuations

Run a task when another one finishes ([#62](https://github.com/GiampaoloGabba/EverTask/issues/62)): continue
on completion or on failure, linked atomically to the parent and surviving restarts (the storage already
carries parent-child linkage for durable occurrences). This is the primitive both workflows and batch
continuations build on.

### Workflow orchestration

Sequential, parallel, conditional and saga/compensation flows with a fluent API, built on the continuation
primitives above, with step-level persistence and monitoring integration.

### Batch dispatch

Dispatch and track a set of tasks as one unit: aggregated batch status (completed, running, failed,
cancelled), batch monitoring events, optional batch persistence, and batch continuations: a task fired when
the whole batch has ended.

### Pipeline behaviors

MediatR-style behaviors around handler execution
([#60](https://github.com/GiampaoloGabba/EverTask/issues/60)), so cross-cutting logic (context enrichment,
custom metrics, veto/skip rules) wraps every handler without changes to the handlers themselves.

### OpenTelemetry and additional monitoring targets

Native instrumentation first ([#61](https://github.com/GiampaoloGabba/EverTask/issues/61)):
`ActivitySource` traces and `Meter` metrics, with W3C context propagated from
dispatch to execution so a task's trace links back to the request that dispatched it (and, with the
multi-publisher mode above, across processes). Then export targets: Sentry Crons, Application Insights.

### More storage options

Redis and Cosmos DB providers. (PostgreSQL shipped in 3.x, MySQL/MariaDB in 4.0.)

### Analyzer usage guidance

A new tier of diagnostics ([#63](https://github.com/GiampaoloGabba/EverTask/issues/63)) that teach good use
at the point of writing, on top of the contract checks in ET0001–ET0012: an Info when a recurring dispatch
carries no `taskKey` (every call creates a new series; with a key it is an idempotent upsert), compile-time
validation of constant cron expressions and schedule values, and a hint when a handler never observes its
`CancellationToken`.

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
