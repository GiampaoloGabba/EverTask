# Changelog

All notable changes to EverTask will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Pipeline review hardening

A review of the dispatch → recovery → scheduler → worker path found a batch of concurrency and accounting defects. Most live in recurring-task scheduling and recovery. One of them changes observable behavior; the rest fix cases that already contradicted the docs or only showed up after a crash or downtime.

#### Changed (behavior)

- **`OnDays(...)` now fires on every listed day instead of once a week.** `EveryWeek().OnDays(Monday, Wednesday, Friday)` advanced a full week after the first matching day, so it ran once a week rather than three times — even though the fluent-API docs already describe it as "multiple days per week", and the business-hours example there relies on every listed day firing. Week and day intervals now fire on each listed day and only advance by the configured number of weeks once that week's days are used up. If you depended on the old once-a-week behavior, list a single day or switch to a cron expression.
- **Completed tasks are no longer hard-deleted the moment they have no audit rows.** With audit cleanup enabled, a finished non-recurring task that never wrote audits (for example `AuditLevel.None`, or `Minimal`/`ErrorsOnly` that succeeded) was deleted on the next cleanup cycle, regardless of any retention window. It is now deleted only once it is older than the longest configured retention window. The misleading `DeleteCompletedTasksWithAudits` option is renamed `DeleteCompletedTasksAfterRetention`; the old name still works as a deprecated alias.
- **Eager-mode recurring tasks run lazily after their first occurrence** (L27/CU17). Eager handlers used to be resolved from the singleton dispatcher's root provider, where they stayed pinned until shutdown and were disposed twice. An eager handler is now resolved in an EverTask-owned scope that is disposed right after the task runs, every recurring occurrence after the first resolves a fresh handler (the lazy default), and `EverTaskHandler.DisposeAsync` is idempotent. The lazy path is unchanged.

#### Fixed

Recurring accounting and recovery:

- **`MaxRuns` overran by one after a downtime** (F7/F8): the stop check counted the occurrences skipped while the host was down, but the persisted run counter only moved by one per real execution, so the two drifted. The counter now advances by `1 + skipped`. For cron schedules the skipped count is the real number of missed occurrences (walked from the cron expression) instead of an elapsed-over-interval estimate, which was wrong on uneven schedules like `0 9,17 * * *`.
- **A crash between "completed" and "advanced" re-ran a recurring occurrence** (CU14): marking an occurrence `Completed` and advancing its run counter / next run were two writes, and a crash in between left the row completed but not advanced, so recovery re-dispatched the finished occurrence. They are now a single transaction.
- **A short downtime across an occurrence could drop it** (L16): if the host was down while an occurrence came due and it ended up slightly in the past, recovery skipped to the next one. Recovery now runs an occurrence that slipped into the recent past (within one interval) instead of skipping it.
- **Recovery re-applied the initial delay on every restart** (L25): a recurring task whose first run never happened, recovered with a stale `NextRunUtc`, had its `InitialDelay`/`RunNow`/`RunAt` applied again each time, shifting the whole schedule forward by the delay. The initial-run configuration is no longer re-applied on the recovery path.
- **`DayInterval` skipped a day** (L26): when every configured time was earlier than the reference time of day, the next-occurrence math added an extra day and returned day+2 instead of day+1.
- **A rate-limited re-park could fire an occurrence past `RunUntil`** (F14): the in-flight-redelivery re-park lacked the `RunUntil` guard the normal deferral path applies, so an occurrence could fire up to a second late. It now drops the occurrence.
- **A recurring task with corrupt scheduling metadata was re-executed on every restart** (CU3/L44): if a recurring row's `RecurringTask` JSON stopped deserializing (after a serializer or type change), recovery quietly demoted it to a one-shot and ran it once. The row stayed recurring and recoverable, so this repeated at every restart and it was never poisoned. It is now marked `Failed` with the deserialization error instead of being run.

Queue, recovery and taskKey:

- **A terminated recurring row could be resurrected from the scheduler path** (L11): recovered scheduled/recurring tasks re-entered through an unconditional `SetQueued`, so a row that finished after its recovery page was read could be set back to `Queued` when a stale scheduler slot fired. Every enqueue path now goes through the conditional `TrySetQueuedIfRecoverable`.
- **The recovery `Queued` transition and its audit were not atomic** (L20): EF Core wrote the transition and then the audit in a separate `SaveChanges` outside a shared transaction, and an audit failure was swallowed. They now commit together.
- **A taskKey re-dispatch while a delivery was in flight could lose the new payload or double-execute** (CU6/L31, G16, G17): an in-flight same-taskKey re-dispatch was treated as an idempotent success and dropped the new payload, and the terminal-status branch could `Remove` a row under a concurrent recovery. The read-decide-write of a taskKey is now serialized per key, an in-flight one-shot re-dispatch is rejected, a recurring row is never converted to a one-shot, and the remove is conditional on the current status.
- **A concurrent same-taskKey insert behaved inconsistently across providers** (G13, G14/CU23): in-memory storage allowed two rows with the same key (both executed), and on SQL Server/SQLite the losing insert either threw a raw storage exception or carried on under its own id. The unique conflict is now caught, the winner re-read, and its id returned; in-memory storage enforces the same uniqueness.
- **A failed taskKey enqueue leaked its scheduler/parking-lot registration** (CU15): the stale registration was dropped before the enqueue, so a failed enqueue left nothing scheduled and leaked the reservation. The drop now happens before the enqueue with a compensating re-schedule on failure.

Cancellation:

- **A user cancel racing shutdown was recoverable instead of terminal** (F17): the OperationCanceled branch checked the service token first and classified the task `ServiceStopped` (recoverable), so it re-ran at the next restart. A blacklisted (user-cancelled) id now persists `Cancelled` even when the service token also fired.
- **A cancel landing just before the per-task token was created could write `Completed` over `Cancelled`** (CU9/L46): the general path did not re-check the blacklist after creating the token, so a handler that ignored the (uncancelled) token ran to completion and overwrote the user's status. It re-checks before writing the outcome.
- **A disposed CTS aborted the rest of the cancel cleanup** (CU12): `CancelTokenForTask` could throw `ObjectDisposedException` and propagate out of `Cancel` before the blacklist, unschedule, gate-invalidation and parking-lot cleanup ran. The cancel is wrapped so the cleanup always completes.
- **A racing enqueue could revive a cancelled task** (CU13): `Cancel` persisted `Cancelled` before adding the blacklist, so an enqueue in between wrote `SetQueued` over it. The blacklist is now set first and `Cancelled` persisted last.
- **A cancelled recurring series could resurrect itself** (L23/CU10): the only suppression was the in-memory blacklist, swept after about an hour, so a series with an interval longer than the TTL came back. The persisted `Cancelled` status now stops the series durably.

Configuration and logging:

- **A queue with zero consumers deadlocked at startup** (F5): `SetMaxDegreeOfParallelism(0)` created no consumers, and with a waiting full-mode queue every producer blocked forever. Parallelism is clamped to at least one and validated up front.
- **The default queue raised `QueueFullException` instead of applying backpressure** (G19): a `FallbackToDefault` default queue degenerated into a self-reference and threw at the caller. The default queue now waits (backpressure) as documented.
- **A bad format specifier in a log call failed the task** (G9, CU18): the log-capture renderer applied the user's format string without a guard, so a specifier like `{0:Q}` (or `Math.Abs(int.MinValue)` on alignment) threw out of `Handle`, and a separate `OnError` template used `{1}` with a single argument and masked the real failure. Log capture no longer throws (it falls back to the raw template), and the `OnError` template is fixed.

Retry and callbacks:

- **`OnError` received the retry `AggregateException` instead of the real exception** (G11): after retries were exhausted the policy threw an `AggregateException`, and `OnError` got that wrapper rather than the underlying failure, so type-based handling (dead-letter, compensation) silently missed it, which the non-retryable path never did. `OnError` now receives the underlying exception; the persisted failure still keeps the full aggregate.
- **A cancel during the inter-retry delay dropped the accumulated causes** (G12): only the `OperationCanceledException` survived, so the record lost why the task had been retrying. The accumulated causes are now kept as an inner `AggregateException`. It is still a cancel, so the terminal classification does not change.

Handler registration:

- **Eager handlers were pinned in the root container and disposed twice** (L27/CU17): a short-delayed or short-interval recurring handler was resolved from the singleton dispatcher's root provider, so it accumulated in the root's disposables until shutdown and was disposed both by the worker and by the container. It is now resolved in a per-dispatch EverTask scope disposed after execution, and `DisposeAsync` is idempotent (see Changed above for the recurring-occurrence consequence).
- **A manually registered singleton handler was shared across concurrent eager dispatches** (G3): the eager path resolved the handler through `IEverTaskHandler<T>`, so registering one with `AddSingleton` before `AddEverTask` handed the same instance to every dispatch, and the worker's per-execution log capture crossed between concurrent runs. The eager path now resolves a fresh instance per dispatch by concrete type, the way the lazy path already did.
- **Duplicate and open-generic handlers were dropped without a word** (G1, G2): two handlers for the same task type were registered first-wins with no warning, and open-generic handlers were filtered out by unreachable code and never registered at all. Both cases now record a warning that is logged when the worker starts.

Provider consistency and audit:

- **The in-memory audit trail diverged from the relational providers** (F19/L29, L43, L28): in-memory storage audited every `ServiceStopped` — including the expected shutdown cancellation and a null exception — at `Minimal`/`ErrorsOnly` while the EF Core providers did not; it skipped the recovery `Queued` audit the EF providers write; and it stamped `RunsAudit.ExecutedAt` from the task's previous execution time instead of the moment of the update. A shared `AuditPolicy` now makes the decision for every provider, so the trail for an event is the same whichever backend you use.
- **EF Core `SetStatus` wrote its audit and its row update non-atomically** (F20): the audit committed in its own `SaveChanges` before the row update, so a failure in between left an audit without the matching row change — unlike the transactional SQL Server stored procedure. They now commit together: one transaction on relational providers, a single tracked `SaveChanges` on EF Core InMemory.
- **A recurring series stopped after one run when no storage was registered** (F18): the next occurrence was scheduled only inside the storage guard, so without a storage provider a recurring task ran once and the series died quietly. The next occurrence is now scheduled regardless of storage, backed by an in-memory run counter that still honors `MaxRuns` and `RunUntil`.
- **The scheduler logged an error on every shutdown** (F12): `Dispose` disposed the wake-up semaphore under the running loop, and the loop's next `WaitAsync` threw `ObjectDisposedException`, which landed in the error log. Both schedulers now treat that as shutdown.

#### Added

- **`ITaskStorage.UpdateCurrentRun(..., int runsToAdvance)`** and **`ITaskStorage.CompleteRecurringRun(...)`**, both default interface members (non-breaking). The first advances the run counter by more than one (a downtime catching up); the second writes a recurring completion and its counter/next-run advance in one transaction. Custom storages that do not override them keep the previous one-step, two-write behavior; the built-in providers override both.
- SQL Server migration `AddRunsToAdvanceToUpdateRunProcedure`: an optional `@RunsToAdvance` parameter (default 1) on `usp_UpdateCurrentRun`.
- **`AuditPolicy`** (public, in `EverTask.Storage`): the shared audit-decision helper (`IsRealError`, `ShouldCreateStatusAudit`, `ShouldCreateRunsAudit`) used by every storage provider. Custom providers can use it to keep their audit trail consistent with the built-in ones.
- **`AuditRetentionPolicy.DeleteCompletedTasksAfterRetention`**, replacing the misnamed `DeleteCompletedTasksWithAudits` (kept as a deprecated alias that forwards to it).

#### Security

- **Task (de)serialization no longer honors the process-global Newtonsoft settings** (L33): EverTask used parameterless `JsonConvert`, so a host that set `JsonConvert.DefaultSettings`, for example opening `TypeNameHandling` globally, could corrupt the recovery round-trip or expose a gadget-deserialization surface when recovery deserialized a stored task. All task and recurring-metadata (de)serialization now uses an isolated `JsonSerializerSettings` with `TypeNameHandling.None`.

### Performance & stability hardening

A second pass over the same dispatch → scheduler → worker → rate-limit path, this time for allocation, memory retention and two robustness gaps rather than correctness. The happy path is unchanged; two items change observable behavior under specific conditions, both noted below. A BenchmarkDotNet project (`benchmarks/EverTask.Benchmarks`, kept out of the CI test run) records the before/after numbers in `benchmarks/RESULTS.md`.

#### Changed (behavior)

- **A rate-limited task whose slot is near is re-parked instead of waited for inline** (L14): the gate used to `await` a near slot (up to `MaxInSlotWait`) on the consumer before admitting the task, which on a single-consumer queue head-of-line-blocked every following item — including tasks with no rate-limit policy. A near slot is now re-parked into the scheduler like a far one, so the consumer is free immediately; the task still fires at its reserved slot via redelivery. `RateLimitPolicy.MaxInSlotWait` is kept for binary compatibility but no longer drives an inline wait.
- **A recovered task that keeps failing to re-dispatch is poisoned instead of retried forever** (L18): a persistent (non-transient) re-dispatch failure during startup recovery left the row untouched, so it was retried at every restart while the run logged "Completed processing N pending tasks" — a constant error masked as success. The failure count is now persisted; after `MaxRecoveryDispatchAttempts` (default 5) the task is marked `Failed`, and a successful re-dispatch clears the count. The recovery summary reports failures instead of unconditional success.

#### Fixed

- **The recurring-task `ToString` cache leaked** (F22): `ToQueuedTask` memoized `RecurringTask.ToString()` in a process-wide static dictionary keyed by reference identity, but every persisted dispatch builds a fresh `RecurringTask`, so it never hit and grew by one permanent entry per dispatch. It is computed inline now — no retention, and about half the per-dispatch allocation.
- **Lifecycle callbacks were resolved by reflection on every execution** (F23): in lazy mode (the default for immediate tasks) `OnStarted`/`OnCompleted`/`OnError` were looked up with `GetMethod` per task, unlike `OnRetry`, which was already cached. They are now cached per handler type alongside the rest of the handler options.
- **Event messages were formatted even when nobody consumed them** (L30): `RegisterEvent` ran `string.Format` and boxed the argument array before the log-level check and before the zero-subscriber short-circuit, so a filtered event with no monitoring subscribers still paid for the format. It is skipped when the level is disabled and there are no subscribers.
- **Monitoring fan-out was unbounded** (F24): each event spawned one fire-and-forget `Task.Run` per subscriber with no cap, so a slow subscriber under load could flood the thread pool. Concurrent monitoring callbacks are now bounded by a semaphore — non-blocking for the worker, with over-cap events dropped (monitoring stays fire-and-forget).
- **Latest-wins scheduling left orphan heap entries** (CU19): re-scheduling the same id updated the registration but left the previous node in the priority queue until its (possibly far-future) due time. The stale node is now evicted at replacement, so repeated re-registrations of one id keep a single entry instead of piling up.
- **The tracked-keys cap was not atomic** (CU20): the rate limiter checked `Count` and then added a key in two steps, so concurrent acquisitions of distinct new keys could overshoot `MaxTrackedKeys`. The check-and-add is now serialized on the new-key path; existing keys keep the lock-free path.
- **A reservation could lapse before its redelivery redeemed it** (L22): for a short-`Period` policy under congestion the reservation expiry margin did not cover the parking-lot pause, so the redelivery re-booked budget — double consumption / over-throttling. The owner's reservation is now honored before any purge, and the margin covers the consumer pause.
- **Recovery of one saturated queue blocked recovery of the others** (L34): startup recovery used a single global-parallelism loop, so blocking enqueues toward a full queue occupied every slot and head-of-line-blocked the recovery of unrelated, idle queues. Recovery is now partitioned per target queue and each group recovers independently.

#### Added

- **`QueuedTask.RecoveryDispatchFailureCount`** (nullable), with SQL Server and SQLite migrations, tracking consecutive failed recovery re-dispatches for the poison logic above.
- **`ITaskStorage.IncrementRecoveryFailure` / `ClearRecoveryFailure`**, default interface members (non-breaking): custom storages that do not override them keep the previous retry-forever behavior; the built-in providers persist the counter.
- **`benchmarks/EverTask.Benchmarks`**: a BenchmarkDotNet project (Central Package Management, kept out of `EverTask.sln` and the CI test run); the A/B measurements live in `benchmarks/RESULTS.md`.

### Monitor UI dependency security update

A dependency refresh of the SignalR monitoring dashboard (`src/Monitoring/EverTask.Monitor.Api/UI`) that clears all advisories reported by `pnpm audit` (54 total: 30 high, 22 moderate, 2 low). Vulnerable subtrees are bumped within their current majors, so there are no breaking changes; `tsc && vite build` passes with unchanged `wwwroot` output. This touches only the bundled monitoring UI, not the EverTask libraries.

#### Security

- Bump runtime deps: `axios` 1.13.2 → 1.18.0 (proxy MITM / NO_PROXY bypass), `qs` 6.14.1 → 6.15.2 (DoS), `react-router-dom` 7.12.0 → 7.17.0.
- Bump build/dev deps: `vite` 7.3.1 → 7.3.5, `rollup` 4.55.1 → 4.62.0, `postcss` 8.5.6 → 8.5.15, `eslint` 9.39.2 → 9.39.4, `@typescript-eslint` 8.52.0 → 8.61.0.
- Pin transitive fixes via `pnpm.overrides`: `esbuild` ≥0.28.1 (GHSA-gv7w-rqvm-qjhr), `flatted` ≥3.4.2, `picomatch` ≥2.3.2 / ≥4.0.4.

## [3.8.0] - 2026-06-13

### Recovery & double-delivery hardening

Startup recovery and live dispatch could deliver the same persisted task to a worker queue twice and, on a capacity-1 queue, leave the producer blocked forever. The hang surfaced under .NET 10 timing, but the defect was structural on every target framework. Nothing changes for callers: the at-least-once contract, no task loss, and `taskKey` idempotency are the same — the runtime just honors them correctly now.

#### Fixed

- **Recovery/live duplicate-delivery race**: the recovery cutoff dropped same-instant ties and the old in-channel dedup forgot a task's id at dequeue, so a recovered copy could enter the channel while its live copy was still running. On a capacity-1 queue the producer then blocked indefinitely. Delivery dedup is now a per-host `TaskDeliveryRegistry` that holds each id from the channel write until the delivery terminally ends, so a second write of the same id is rejected at the boundary — double delivery is impossible by construction rather than by enumerating exit paths.
- **In-memory storage could resurrect an exhausted recurring task**: `MemoryTaskStorage.TrySetQueuedIfRecoverable` checked only the status and skipped the `MaxRuns`/`RunUntil` guards its own `RetrievePending` applies. The recoverable rule now lives in one place — `QueuedTask.IsRecoverable` — shared across every provider, so the guards can no longer drift apart.
- **SQLite threw once per recovered task**: the conditional recovery `UPDATE` compares `RunUntil` (a `DateTimeOffset`), which SQLite cannot translate, so every recovered task threw and fell back. `SqliteTaskStorage` now evaluates the recoverable predicate client-side directly, the same way it already overrides `RetrievePending`.

#### Added

- **`TaskDeliveryRegistry`** (per host, shared by all queues): registers each `PersistenceId` from the channel write until the delivery ends. The single `End` is the last act of `WorkerExecutor.DoWork`, plus the enqueue rollback paths and the channel drop callback.
- **`EnqueueResult.DuplicateInProcess`**: returned when an id is already in flight. Recovery and live dispatch treat it as an idempotent skip; schedulers retry it like a full queue.
- **`ITaskStorage.TrySetQueuedIfRecoverable`** (default interface member, non-breaking): an atomic conditional `SetQueued` used by recovery so a row that finished after its recovery page was read is never set back to `Queued`. The EF Core (atomic `UPDATE`), SQLite and In-Memory providers override it.

## [3.7.0] - 2026-06-12

### Keyed Rate Limiting

Opt-in, per-key (tenant/account/resource) rate limiting for task execution. The driving use case: tasks calling an external API limited to ~15 requests per minute **per tenant**. The limit is a *frequency constraint per logical key*, orthogonal to queue parallelism: a key without budget never stalls tasks of other keys (no head-of-line blocking), and a worker slot is never held while waiting for budget. See the new [Keyed Rate Limiting docs](https://GiampaoloGabba.github.io/EverTask/rate-limiting.html).

#### Added

- **`IRateLimitedTask`** (Abstractions): marks a task as rate-limited and carries the throttling key (e.g. `TenantId.ToString()`). Alternatively, handlers can override **`GetRateLimitKey(task)`** to derive the key without touching the task type. The rate-limit key is a *throttling* key, distinct from the dispatch `taskKey` (idempotency) — never reuse one for the other.
- **`RateLimitPolicy`** (Abstractions): declared on the handler (`public override RateLimitPolicy? RateLimitPolicy => new(15, TimeSpan.FromMinutes(1))`), next to `RetryPolicy`/`Timeout`/`QueueName`. Sealed, immutable, validated. Knobs: `Burst` (default = Permits), `ThrottleRetries` (default true), `StartEmpty` (default false), `MaxReservationHorizon` (default 1 h), `MaxInSlotWait` (default 1 s), `OverflowBehavior` (`WaitForCapacity` | `Discard`). Declared as default interface members on `IEverTaskHandlerOptions`/`IEverTaskHandler<T>` so existing external implementors keep compiling.
- **Consumer-side gate + in-memory GCRA limiter with reservations**: at dequeue, a task without budget gets the next slot *reserved* (keyed by its persistence id, so redeliveries redeem the same slot instead of double-consuming) and is re-parked into the in-memory scheduler. Near slots (≤ `MaxInSlotWait`) are awaited inline. **A deferral writes nothing to storage**: the task stays in its recoverable `Queued` status; the only storage touch of the cycle is the usual `SetQueued` when the slot fires (recommend `AuditLevel.Minimal` for heavily throttled types).
- **Retry integration**: with `ThrottleRetries` (default), every retry attempt re-acquires budget *before* its per-attempt `Timeout` starts (the budget wait never erodes it); a far slot re-parks the task instead of failing it (attempt count restarts on redelivery).
- **Recurring integration**: occurrences are throttled individually while the series rhythm is preserved (re-parks never touch the occurrence's scheduled time). Occurrences whose slot falls past `RunUntil` are skipped, never fired late; horizon-rejected occurrences are skipped through the normal next-occurrence path (the skip counts toward `MaxRuns`, same semantics as downtime) and the series stays alive.
- **Layered defense** (zero-config defaults): unconditional lazy re-park (no pinned handler instances); `MaxParkedTasks` cap with bounded consumer pause → native channel backpressure; `MaxReservationHorizon` terminal rejection (one-shot: persisted `Failed` + `OnError` with the typed **`RateLimitRejectedException`** carrying key/slot/policy); `MaxTrackedKeys` key-cardinality bound with fail-open + mandatory monitoring event; `Discard` overflow behavior (terminal `Failed`, never `Cancelled`); `StartEmpty` opt-in post-restart burst cap.
- **Observability**: aggregated deferral monitoring events (machine-parseable `Rate limit deferred task {id}: key={key} slotUtc={slot:O} policy={taskType}`; first deferral per key per window + periodic summaries — no per-task event storms; per-deferral logs at Debug); `SetRateLimiterOptions` global knobs; **`IRateLimiterIntrospection`** (single-node view) feeding **`ThrottledTasks`** dashboard counters, the new **`GET /api/rate-limits`** endpoint and a per-task **`throttledUntil`** overlay in Monitor.Api.
- **`IKeyedRateLimiter` DI seam** for a future distributed (e.g. Redis GCRA) limiter, with documented contract invariants (idempotent redemption, non-decreasing slots, never blocks, wall-clock UTC, fail-open on infrastructure errors). Replace via `services.AddSingleton<IKeyedRateLimiter, ...>()` before `AddEverTask`.
- **`ITaskStorageStatistics`** optional storage side-interface (count by status, per-queue GROUP BY) implemented by the EF Core and In-Memory providers: Monitor.Api counts no longer require materializing the whole backlog.
- `IScheduler.TryUnschedule(Guid, TaskHandlerExecutor)` conditional overload (preserves a concurrent newer registration).

#### Fixed

- **MEM-2 (pre-existing leak)**: immediate dispatches resolved an eager transient handler from the root provider; `IAsyncDisposable` handlers were pinned in the root container's disposables list until shutdown. Immediate dispatches are now lazy by default: the dispatch-time metadata instance is resolved and disposed in a short-lived scope, and the executing instance lives in the worker's per-task scope. (Behavior note: in lazy mode `DisposeAsyncCore` fires once at dispatch for the never-executed metadata instance and once after each execution.)
- **Lazy-mode retry filtering**: lazy execution invoked handlers via `MethodInfo.Invoke`, which wrapped synchronous handler exceptions in `TargetInvocationException` and broke retry-policy exception filtering (whitelist/predicate policies failed fast instead of retrying). Lazy callbacks now use compiled delegates (also faster).
- **Dashboard PendingCount omitted `Queued` tasks** from queue summaries (shared status-bucketing helper now used by dashboard and statistics services).
- **`WorkerBlacklist` leak**: cancelling a task whose occurrence was never redelivered (e.g. unscheduled while parked) leaked its blacklist entry forever; entries now lapse via TTL sweep.
- **Scheduler hardening**: `Schedule()` after `Dispose()` no longer throws into the caller (the task stays recoverable for the next startup); guarded semaphore wake-up against concurrent dispose.
- Recurring `Cancel` semantics: a blacklisted (cancelled) occurrence no longer schedules the next occurrence — the blacklist check moved before the execution path.

## [3.6.0] - 2026-06-11

### Added

#### Monitoring Dashboard v3.2 - Feature Complete (Read-Only Mode)

The EverTask monitoring dashboard is now **feature complete** in version 3.2. The current release provides comprehensive **read-only monitoring capabilities** for complete visibility into your task execution pipeline.

**Dashboard Features:**
- **Execution Logs Terminal UI**: New tab in task detail modal displaying captured execution logs with terminal-like UI
  - Monospace font with color-coded log levels (Trace=gray, Debug=cyan, Info=blue, Warning=yellow, Error=red, Critical=red bold)
  - Dark background for optimal readability
  - Expandable exception details for error logs
  - Auto-scroll toggle for following log stream
  - Pagination support (100 logs per page)
  - **Log level filtering**: Dropdown to filter logs by severity level (All/Trace/Debug/Information/Warning/Error/Critical)
  - **Export functionality**: Export logs as JSON or plain text format
  - Empty state handling when log capture is disabled or no logs exist

- **Audit Level Visibility**: Task detail modal now displays the audit level configuration
  - Badge with color coding: Full (default), Minimal (secondary), ErrorsOnly (secondary), None (outline)
  - Tooltip description explaining what each audit level means
  - Helps users understand incomplete audit trails (e.g., Minimal only shows errors)

- **Real-Time Updates**: SignalR-based event-driven cache invalidation with intelligent throttling
  - Immediate first update for responsive UI feedback
  - Regular updates at configurable intervals (default: 1000ms) during continuous task activity
  - Prevents API request bursts during task completion
  - Configurable via `EventDebounceMs` property in `EverTaskApiOptions`

**Backend API Enhancements:**
- **New Execution Logs Endpoint**: `GET /api/tasks/{id}/execution-logs`
  - Query parameters: `skip` (default 0), `take` (default 100), `level` (optional filter)
  - Returns `ExecutionLogsResponse` with logs array, total count, and pagination metadata
  - Supports filtering by log level for targeted log retrieval
  - Integrates with existing `ITaskStorage.GetExecutionLogsAsync()` method

- **Audit Level DTO Exposure**: `TaskDetailDto` now includes `auditLevel` property
  - Exposes database audit configuration to frontend
  - Nullable integer matching `AuditLevel` enum values (0=Full, 1=Minimal, 2=ErrorsOnly, 3=None)

**Type Definitions:**
- **New TypeScript types** in monitoring UI:
  - `ExecutionLogDto`: Single log entry (id, timestampUtc, level, message, exceptionDetails, sequenceNumber)
  - `ExecutionLogsResponse`: Paginated logs response (logs[], totalCount, skip, take)
  - `AuditLevel` enum: TypeScript enum matching backend values

**Current State (v3.2):**
- ✅ Complete read-only monitoring and observability
- ✅ Real-time task status updates via SignalR
- ✅ Comprehensive analytics and performance metrics
- ✅ Detailed execution logs and audit trail visualization
- ✅ Multi-queue monitoring and task filtering

**Future Releases:**
- ⏳ Task management operations (stop, restart, cancel tasks)
- ⏳ Runtime parameter modification for queued/scheduled tasks
- ⏳ Queue management (pause/resume queues)

> **Note**: Both the REST API and dashboard operate in read-only mode. Future releases will introduce task management capabilities.

### Changed
- Monitoring dashboard "History" section renamed to "History & Logs" to reflect new execution logs tab
- Task detail tabs layout changed from 2-column to 3-column grid (Status History | Runs History | Execution Logs)
- SignalR auto-refresh implementation changed from debounce to throttle pattern for predictable update intervals
- Dispatch with a `taskKey` matching an `InProgress` task now logs a **warning** (was info): the dispatch is a
  no-op returning the existing ID, which silently kills handler self-redispatch chains using a stable key.
  Documented the rule (use null or per-attempt keys for self-redispatch) in `ITaskDispatcher` XML docs and README.

### Queue & Recovery Resilience Hardening

A focused pass on the task lifecycle to guarantee the two core promises under queue saturation, restarts and cancellation: **no task is ever lost** and **nothing deadlocks or crashes**. Found via an in-app integration that uses many isolated queues with `QueueFullBehavior.Wait` (webhook-driven dispatch).

#### Fixed

- **Startup deadlock when the cold-start backlog exceeded a queue's capacity.** `ProcessPendingAsync` ran *before* the queue consumers were started; with `Wait` behavior the recovery's blocking write filled the channel and no consumer ever drained it, wedging startup permanently (self-perpetuating, since the stuck tasks stayed `Queued` and re-wedged on the next boot). Consumers now start **first** and recovery runs **concurrently**, so a full queue exerts real backpressure instead of deadlocking.
- **Silent task loss: `WaitingQueue` was invisible to startup recovery.** Every task is persisted as `WaitingQueue` until it enters a channel, but `RetrievePending` excluded that status. A delayed one-shot parked in the in-memory scheduler (and any task dropped by a full queue) was **never recovered after a restart**, contradicting the "tasks resume after restart" guarantee. `WaitingQueue` is now a recoverable status across all three storage providers (SQL Server, SQLite, In-Memory).
- **Recurring tasks died after a restart unless re-registered.** Between two runs a recurring task is `Completed`/`Failed` with a future `NextRunUtc`; recovery skipped those rows, so a dynamically-created recurring task that was not re-dispatched at boot stopped firing. Recovery now revives recurring tasks (`IsRecurring && NextRunUtc != null` in `Completed`/`Failed`), while never reviving cancelled or schedule-exhausted ones.
- **Recurring revival skipped one occurrence at every restart** (and could mark the last occurrence before `RunUntil` as `Failed`). Recovery recalculated the next run *strictly after* the stored `NextRunUtc`. The stored `NextRunUtc` is now treated as the preserved next occurrence and used as-is when still in the future.
- **Head-of-line blocking across queues.** The scheduler dispatched with a blocking write on a single shared loop, so one full queue stalled the scheduled/recurring tasks of **every other queue**. Both schedulers (`PeriodicTimerScheduler`, `ShardedScheduler`) now dispatch non-blocking and re-enqueue a due task with a backoff (`FullQueueRetryDelay`, default 2s) when its target queue is full, leaving the other queues flowing.
- **Dispatch ignored the caller's `CancellationToken` on a full queue.** The token flowed only to `Persist`, never to the channel write, so a dispatch from an aborted HTTP request (e.g. a Stripe webhook retry) hung indefinitely and accumulated. The token now flows `Dispatch → TryEnqueue → WriteAsync`; on cancellation the task stays persisted as `Queued` (recovered at the next startup), never `Failed`.
- **Double execution of immediate tasks.** The same persisted task could be delivered to the channel twice (recovery racing a live dispatch, an `IHostedService` dispatching before the worker started, or a `taskKey` re-dispatch) and both copies executed. Added an in-channel dedupe registry per `PersistenceId` plus an in-flight guard in the executor.
- **Schedulers turned shutdown and transient storage errors into permanent `Failed`.** `OperationCanceledException` at shutdown and transient storage failures were caught and the task marked `Failed`, losing one-shot tasks. Shutdown now leaves the task recoverable; transient failures are parked and retried with backoff. The startup recovery likewise no longer marks a task `Failed` on a transient re-dispatch error (only genuinely poison payloads are failed).
- **Recovery could overwrite a concurrent live re-registration (lost update).** Recovery called `UpdateTask`, rewriting the definition read from storage; a concurrent `taskKey` re-registration with a new cron/payload could be lost. Recovery dispatches no longer rewrite the task definition.
- **`ShardedScheduler` could throw `SemaphoreFullException`** under concurrent `Schedule()` calls (racy check-then-act on a max-count-1 semaphore). Fixed with the same `Interlocked` wake-up signaling as `PeriodicTimerScheduler`.
- **`QueueFullBehavior.ThrowException` + blacklisted task** threw `QueueFullException` instead of returning cleanly; `FallbackToDefault` re-routed a blacklisted (cancelled) task to the default queue. Both now treat a blacklisted task as a no-op.
- Pinned `System.Security.Cryptography.Xml` to a patched version to clear a pre-existing transitive high-severity advisory (NU1903) that broke the warnings-as-errors build.

#### Added

- `IScheduler.TryUnschedule(Guid)` — invalidates a parked occurrence; called by the dispatcher on an immediate `taskKey` re-dispatch and by `Cancel()`.
- `IWorkerQueue.Name`, `Count`, `Capacity` — queue depth observability.
- `IWorkerQueueManager.EnqueueBlocking` (recovery) and `TryEnqueueImmediate` (scheduler); `WorkerQueue.TryQueue`/`Queue` and `WorkerQueueManager.TryEnqueue` now accept a `CancellationToken`.

#### Changed (breaking — minor API surface)

- `IWorkerQueue.TryQueue` now returns `EnqueueResult` (`Enqueued`/`QueueFull`/`Discarded`) instead of `bool`, to distinguish a full queue from a blacklisted task.
- `QueueConfiguration.ChannelOptions` default is now `SingleWriter = false` (the dispatcher, scheduler and recovery write concurrently by design).
- **Semantic note**: `QueueFullBehavior` applies to **immediate** dispatches only. Delayed/recurring tasks dispatched by the scheduler always use a non-blocking write and retry with backoff on a full queue (no fallback, no exception), so a saturated isolated queue cannot stall other queues.

#### Execution Contract (no change, now explicit)

- Execution is **at-least-once**. A task interrupted mid-execution (`InProgress`) is re-dispatched and re-executed from scratch at the next startup. Handlers with external side effects must be idempotent — prefer a stable `taskKey` (e.g. an external event id) for idempotent registration.

### Storage Performance & `LastExecutionUtc` Fixes

#### Added

- `IX_QueuedTasks_Recovery` covering index on `QueuedTasks (CreatedAtUtc, Id)`: startup recovery no longer degenerates into a clustered scan + sort on large tables (SQL Server migration `AddRecoveryIndexAndUpdateRunProcedure`).
- `usp_UpdateCurrentRun` stored procedure (SQL Server): audit decision, run-counter update and `RunsAudit` insert in a **single atomic roundtrip** (was 2-3 roundtrips per recurring run).

#### Changed

- `EfCoreTaskStorage.UpdateCurrentRun` loads the task once instead of projection + full reload; with `AuditLevel.None` it issues a single `ExecuteUpdate` with server-side counter increment (no SELECT at all).

#### Fixed

- `LastExecutionUtc` is now written only on **terminal** transitions and **preserved** on intermediate ones (`WaitingQueue`, `Queued`, `InProgress`, `Cancelled`, `Pending`), consistently across SQL Server (`usp_SetTaskStatus`), EF Core and In-Memory storage: a full-queue revert to `WaitingQueue` no longer stamps a fake execution time on a task that never ran, and re-queueing a recurring task no longer wipes the timestamp of its last real run.

### Dependencies

- Update NuGet servicing packages (EF Core 8.0.28 / 9.0.17 / 10.0.9, Cronos 0.13.0, UUIDNext 4.2.4, Respawn 7.0.0, Testcontainers.MsSql 4.12.0).
- Pin `System.Security.Cryptography.Xml` to patched versions to clear a transitive high-severity advisory (NU1903).

## [3.2.0] - 2025-11-04

### Added

#### Configurable Audit Levels for Database Bloat Control
- **AuditLevel enum** with four granularity levels to control audit trail verbosity:
  - `Full` (default): Complete audit trail with all status transitions and executions
  - `Minimal`: Only errors in StatusAudit, all executions in RunsAudit (75% reduction)
  - `ErrorsOnly`: Only failed executions tracked (60% reduction)
  - `None`: No audit trail (100% reduction for extremely high-frequency tasks)

- **Global audit configuration**: `SetDefaultAuditLevel(AuditLevel)` method in `EverTaskServiceConfiguration`
  - Sets default audit level for all tasks
  - Default: `AuditLevel.Full` (backward compatible)

- **Per-task audit override**: Optional `auditLevel` parameter added to all `ITaskDispatcher.Dispatch()` overloads
  - Override global default on a per-task basis
  - Ideal for high-frequency recurring tasks (health checks, monitoring, cache refresh)

- **Database schema updates**:
  - `AuditLevel` column added to `QueuedTasks` table (nullable, default: Full)
  - Database migrations for SQL Server (`20251104000000_AddAuditLevel`, `20251104004411_UpdateStoredProcedureForAuditLevel`)
  - Database migrations for SQLite (`20251104000000_AddAuditLevel`)

- **Storage interface updates**:
  - `AuditLevel` parameter added to `ITaskStorage.SetStatus()` method signature
  - `AuditLevel` parameter added to `ITaskStorage.UpdateCurrentRun()` method signature
  - All storage implementations updated: `MemoryTaskStorage`, `EfCoreTaskStorage`, `SqlServerTaskStorage`

- **Audit retention policy system**:
  - `AuditRetentionPolicy` class for configurable retention periods per audit level
  - `AuditCleanupHostedService` for automatic background cleanup of old audit records
  - `AddAuditCleanup()` extension method for opt-in audit cleanup registration
  - Configurable retention periods: Full (90 days), Minimal (60 days), ErrorsOnly (30 days)
  - Scheduled cleanup runs every 24 hours by default

### Changed
- **ITaskDispatcher interface**: All `Dispatch()` method signatures now include optional `AuditLevel? auditLevel = null` parameter
- **Dispatcher implementation**: Passes `AuditLevel` through execution pipeline to storage layer
- **WorkerExecutor**: Passes task's `AuditLevel` to storage methods during status updates
- **Database behavior**: Audit records conditionally created based on task's `AuditLevel` configuration

### Performance Impact

**Database Growth Example** (task running every 5 minutes, 24/7):

| Audit Level | Daily Audit Records | Storage Reduction |
|-------------|---------------------|-------------------|
| Full        | ~2,304 records/day | Baseline          |
| Minimal     | ~576 records/day   | 75% reduction     |
| ErrorsOnly  | ~903 records/day*  | 60% reduction     |
| None        | 0 records/day      | 100% reduction    |

*Assuming typical failure rates (5-10%)

**Use Cases by Task Type**:
- **Full**: Critical payment processing, order fulfillment, compliance-required operations
- **Minimal**: Health checks, data sync, recurring monitoring tasks (tracks last run + errors)
- **ErrorsOnly**: Fire-and-forget tasks, background cleanup, non-critical operations
- **None**: Extremely high-frequency tasks (< 1 minute intervals), temporary cache warming

### Documentation
- Comprehensive audit configuration guide added to `docs/storage.md`
- Configuration reference updated in `docs/configuration-reference.md`
- Quick reference added to `docs/configuration-cheatsheet.md`
- README.md updated with audit level examples and performance impact tables

### Backward Compatibility
- **Fully backward compatible**: Null `AuditLevel` in database treated as `Full` (default)
- **Existing tasks**: Tasks created before v3.2 continue with Full audit level
- **Custom storage**: Implementations must update method signatures (see Migration Notes below)

### Migration Notes
- **Custom storage implementations**: Update `SetStatus()` and `UpdateCurrentRun()` signatures to accept `AuditLevel` parameter
- **No data migration required**: Column added with nullable constraint (backward compatible)
- **Automatic migrations**: Enabled by default via `AutoApplyMigrations = true`
- **Manual migrations**: Run EF Core migrations if `AutoApplyMigrations = false`

### Examples

**Global Configuration:**
```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetDefaultAuditLevel(AuditLevel.Minimal)) // Conservative default
    .AddSqlServerStorage(connectionString);
```

**Per-Task Override:**
```csharp
// High-frequency health check - minimal audit
await dispatcher.Dispatch(
    new HealthCheckTask(),
    recurring => recurring.Every(5).Minutes(),
    auditLevel: AuditLevel.Minimal);

// Critical payment - full audit
await dispatcher.Dispatch(
    new ProcessPaymentTask(orderId),
    auditLevel: AuditLevel.Full);

// Temporary cache warmer - no audit
await dispatcher.Dispatch(
    new WarmCacheTask(),
    recurring => recurring.Every(30).Seconds(),
    auditLevel: AuditLevel.None);
```

**Audit Cleanup Configuration:**
```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddSqlServerStorage(connectionString)
    .AddAuditCleanup(opt => opt
        .SetRetentionPeriod(AuditLevel.Full, TimeSpan.FromDays(90))
        .SetRetentionPeriod(AuditLevel.Minimal, TimeSpan.FromDays(60))
        .SetRetentionPeriod(AuditLevel.ErrorsOnly, TimeSpan.FromDays(30))
        .SetCleanupInterval(TimeSpan.FromHours(24)));
```

## [3.1.1] - 2025-10-23

### Fixed

#### Critical Bug Fixes
- **ShardedScheduler hash overflow**: Fixed IndexOutOfRangeException when `GetHashCode()` returns `int.MinValue` by using unsigned hash calculation `(uint)GetHashCode() % (uint)shardCount`
- **Queue-full policies never triggered**: Fixed `QueueFullBehavior.ThrowException` and `QueueFullBehavior.FallbackToDefault` policies that silently behaved like `Wait` under pressure
  - Added `IWorkerQueue.TryQueue()` method that uses `TryWrite` for immediate rejection
  - Created `QueueFullException` for meaningful error reporting
  - Reworked `WorkerQueueManager.TryEnqueue()` to honor configured overflow policies
- **Pending recovery OOM**: Fixed memory issues when recovering from outages with large task backlogs (100k+ tasks)
  - Replaced skip/take paging with **keyset pagination** using `(CreatedAtUtc, Id)`
  - Updated `ITaskStorage.RetrievePending(...)` signature to accept `lastCreatedAt`, `lastId`, and `take`
  - Implemented keyset logic in `MemoryTaskStorage`, `EfCoreTaskStorage`, and `SqliteTaskStorage`
  - Refactored `WorkerService.ProcessPendingAsync()` to iterate via `(lastCreatedAt, lastId)` cursor (default: 100 tasks/page)
- **MemoryTaskStorage concurrency**: Fixed race conditions when dispatcher threads add tasks while worker enumerates the list
  - Added `_pendingTasksLock` to protect all `_pendingTasks` operations
  - All read/write operations now thread-safe via lock-based synchronization

#### Performance Improvements
- **Reduced logging verbosity**: Changed hot-path logging from `LogInformation` to `LogDebug` in:
  - `Dispatcher.Persist/Update` operations
  - `WorkerQueue.Queue` operations
  - Prevents log saturation at high throughput (10k+ tasks/sec)

### Added
- **New Methods**:
  - `IWorkerQueue.TryQueue(task)`: Non-blocking queue attempt that returns false if full

- **New Tests**:
  - `ShardedSchedulerTests.Should_Handle_Negative_Hash_Without_Exception()`: Verifies no exceptions with random GUIDs (including negative hash codes)
  - `ShardedSchedulerTests.Should_Distribute_Tasks_Across_Shards_Without_Index_Out_Of_Range()`: Theory test for multiple shard counts
  - `MemoryTaskStorageConcurrencyTests`: 6 comprehensive concurrency tests covering parallel persist, read/write, status updates, removals, and run counters
  - `PendingRecoveryPagingTests` & `EfCoreTaskStorageTestsBase` updated with deterministic GUID v7 helpers and keyset assertions to guarantee completeness and ordering

### Changed
- **Breaking change**:
  - `ITaskStorage.RetrievePending(...)` signature changed to `(DateTimeOffset? lastCreatedAt, Guid? lastId, int take, CancellationToken ct = default)` for keyset pagination
  - Custom storage implementations must update to the new signature and honor `(CreatedAtUtc, Id)` ordering

### Migration Notes
- **Breaking**: Update custom storage providers to the new `RetrievePending` signature and implement `(CreatedAtUtc, Id)` keyset logic
- **SQLite note**: Due to provider limitations, SQLite applies keyset filtering in memory; recommended only for demos or small workloads
- **Optional**: Switch logging level to `Debug` for Dispatcher and WorkerQueue operations to reduce verbosity

### Testing
- Added comprehensive concurrency tests for `MemoryTaskStorage`
- Added regression tests for ShardedScheduler hash calculation
- Added deterministic keyset pagination tests covering completeness, ordering, and overlap detection
- All existing tests continue to pass

## [3.1.0] - 2025-01-23

### Added

#### Database-Optimized GUID Generator
- **IGuidGenerator interface** in `EverTask.Abstractions` for dependency injection
- **DefaultGuidGenerator** implementation with automatic database-specific optimization
  - SQL Server: UUID v8 with optimized byte ordering (3x insert performance vs random GUID)
  - SQLite: UUID v7 with standard byte ordering
  - PostgreSQL: UUID v7 with PostgreSQL-optimized ordering
  - Other databases: UUID v7 standard format
- **Automatic registration** via storage provider extensions (`.AddSqlServerStorage()`, `.AddSqliteStorage()`)
- **Clustered index optimization**: Temporally ordered GUIDs prevent index fragmentation
- **Performance improvement**: SQL Server inserts up to 3x faster (2.2M vs 700K rows) with UUID v8
- **Zero breaking changes**: Existing code works without modification
- **UUIDNext library** integration for RFC 9562 compliant UUID v7/v8 generation

#### Other Improvements
- `RecurringTask.GetMinimumInterval()` method to calculate task intervals including cron expressions
- Adaptive algorithm for lazy/eager mode selection based on task scheduling patterns
- Internal threshold of 5 minutes for recurring tasks (< 5 min = eager, >= 5 min = lazy)
- `DisableLazyHandlerResolution()` convenience method for opting out

### Changed
- Simplified lazy handler resolution configuration with adaptive algorithm
- Removed `LazyHandlerResolutionThreshold` configuration property (now internal: 30 minutes for delayed tasks)
- Removed `AlwaysLazyForRecurring` configuration property (now adaptive based on task interval)
- Removed `SetLazyHandlerResolutionThreshold()` and `SetAlwaysLazyForRecurring()` configuration methods
- **TaskLogCapture** now accepts `IGuidGenerator` via constructor for database-optimized log entry IDs
- **TaskHandlerWrapper** resolves `IGuidGenerator` from DI for database-optimized task persistence IDs

### Improved
- Frequent recurring tasks (< 5 min interval) now automatically use eager mode for better performance
- Infrequent recurring tasks (>= 5 min interval) now automatically use lazy mode for memory efficiency
- Cron expressions now have smart interval detection for optimal lazy/eager selection
- Reduced handler allocations for high-frequency recurring tasks (up to 43,000 fewer allocations/day)

### Migration Notes
- **Non-breaking change**: Old configuration methods/properties are removed but had no functional impact
- Remove `SetLazyHandlerResolutionThreshold()` and `SetAlwaysLazyForRecurring()` from your configuration (no replacement needed - adaptive algorithm handles everything automatically)
- `UseLazyHandlerResolution` property and `DisableLazyHandlerResolution()` method remain available for opt-out

## [3.0.0] - 2025-10-23

### Added

#### Task Execution Log Capture with Proxy Pattern
- **Proxy logger architecture**: Logger ALWAYS forwards to ILogger infrastructure (console, file, Serilog, Application Insights) with optional database persistence for audit trails
  - Configure via fluent `.WithPersistentLogger()` API that auto-enables persistence
  - Options: `.SetMinimumLevel()`, `.SetMaxLogsPerTask()`, `.Disable()`
  - Handlers use the built-in `Logger` property (from `EverTaskHandler<T>`)
  - Logs saved to `TaskExecutionLogs` table with cascade delete (foreign key to `QueuedTasks`)
  - Retrieve logs via `storage.GetPersistedLogsAsync(taskId)` with pagination support
  - Zero overhead when persistence disabled (conditional allocation + minimal forwarding cost)
  - Logs persist even when tasks fail (captured in finally block)
  - Thread-safe in-memory collection with lock-based synchronization
  - Includes exception details (stack traces) when logging errors
  - Sequence numbers for log ordering
  - Storage extension methods: `GetExecutionLogsAsync(taskId, skip, take)`
  - ILogger<THandler> injection for proper log categorization

**Architecture**:
```
Handler.Logger.LogInformation("msg")
         ↓
   TaskLogCapture (proxy)
    ↙          ↘
ILogger        Database
(always)     (optional)
```

**Example Configuration**:
```csharp
services.AddEverTask(cfg =>
{
    cfg.RegisterTasksFromAssembly(typeof(Program).Assembly)
        .WithPersistentLogger(log => log           // Auto-enables DB persistence
            .SetMinimumLevel(LogLevel.Information) // Filter persisted logs
            .SetMaxLogsPerTask(1000));             // Prevent unbounded growth
})
.AddSqlServerStorage(connectionString);
```

**Example Usage in Handler**:
```csharp
public class SendEmailHandler : EverTaskHandler<SendEmailTask>
{
    public override async Task Handle(SendEmailTask task, CancellationToken ct)
    {
        Logger.LogInformation($"Sending email to {task.Recipient}");
        await _emailService.SendAsync(task.Recipient, task.Subject, task.Body);
        Logger.LogInformation("Email sent successfully");
    }
}
```

#### Lazy Handler Resolution for Memory Optimization
- **Lazy handler resolution**: Handlers disposed after dispatch, re-created at execution time
  - 70-90% memory reduction for delayed and recurring tasks
  - Configurable via `EverTaskHandlerOptions.LazyResolutionMode` (Eager/Lazy/Auto)
  - Auto mode: lazy for tasks delayed >1 hour or recurring tasks
  - Full backward compatibility with eager mode preserved
- **Comprehensive integration test infrastructure**: `IsolatedIntegrationTestBase` pattern for zero-flaky tests
  - Each test creates isolated `IHost` instance (eliminates state sharing)
  - Intelligent polling with `TaskWaitHelper` (replaces fixed `Task.Delay()`)
  - 4-12x faster test execution through safe parallel testing
  - Thread-safe `TestTaskStateManager` for execution tracking

#### Retry Policy Enhancements
- **OnRetry Lifecycle Callback**: Handlers can now override `OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)` to receive notifications before each retry attempt. This callback provides visibility into retry behavior for:
  - Logging retry attempts with full context (task ID, attempt number, exception details)
  - Tracking retry metrics and histograms for monitoring dashboards
  - Alerting on excessive retry patterns indicating systemic issues
  - Debugging intermittent failures with diagnostic snapshots
  - Implementing custom circuit breaker patterns

- **Exception Filtering for IRetryPolicy**: Retry policies can now implement `ShouldRetry(Exception exception)` to determine if specific exceptions should trigger retries. Default implementation retries all exceptions except `OperationCanceledException` and `TimeoutException`.

- **LinearRetryPolicy Fluent API for Exception Filtering**:
  - `Handle<TException>()` - Whitelist specific exception type to retry (type-safe generic method)
  - `Handle(params Type[])` - Whitelist multiple exception types at once (convenient for many types)
  - `DoNotHandle<TException>()` - Blacklist specific exception type to NOT retry
  - `DoNotHandle(params Type[])` - Blacklist multiple exception types at once
  - `HandleWhen(Func<Exception, bool>)` - Custom predicate-based filtering for complex retry logic

- **Predefined Exception Sets**: New extension methods for common retry scenarios:
  - `HandleTransientDatabaseErrors()` - Retries `DbException`, `TimeoutException` (database-related)
  - `HandleTransientNetworkErrors()` - Retries `HttpRequestException`, `SocketException`, `WebException`, `TaskCanceledException`
  - `HandleAllTransientErrors()` - Combines database and network transient errors

**Examples**:

```csharp
// Exception filtering with OnRetry callback
public class DatabaseTaskHandler : EverTaskHandler<DatabaseTask>
{
    public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
        .HandleTransientDatabaseErrors(); // Only retry DB transient errors

    public override ValueTask OnRetry(Guid taskId, int attemptNumber, Exception exception, TimeSpan delay)
    {
        _logger.LogWarning(exception,
            "Database task {TaskId} retry {Attempt} after {DelayMs}ms",
            taskId, attemptNumber, delay.TotalMilliseconds);

        _metrics.IncrementCounter("db_task_retries", new { attempt = attemptNumber });

        return ValueTask.CompletedTask;
    }
}

// HTTP status code filtering with predicate
public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);
```

### Changed

- **Handler disposal moved to post-execution**: `WorkerExecutor` now disposes handlers after task completion
  - Previously disposed during dispatch in lazy mode (incorrect lifecycle)
  - Fixes resource cleanup for handlers with IAsyncDisposable dependencies
- **N-consumer pattern for channel consumption**: `WorkerService` now uses multiple concurrent readers per queue
  - Respects `MaxDegreeOfParallelism` configuration
  - Better channel throughput under high load
- **Handler DI registration by concrete type**: Enables proper lazy resolution with scoped dependencies
- **IRetryPolicy.Execute Signature**: Added optional `Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback` parameter for retry notifications. Existing implementations remain compatible (parameter defaults to null).
- **ITaskStorage Interface Signatures** (minor breaking change for custom storage implementations):
  - `SetStatus(Guid taskId, QueuedTaskStatus status, Exception? exception, AuditLevel auditLevel, CancellationToken ct)` - Added `AuditLevel auditLevel` parameter
  - `UpdateCurrentRun(Guid taskId, DateTimeOffset? nextRun, AuditLevel auditLevel)` - Added `AuditLevel auditLevel` parameter
  - **Impact**: Custom `ITaskStorage` implementations must update method signatures
  - **Migration**: Add `AuditLevel auditLevel` parameter and use it to conditionally create audit records (see `EfCoreTaskStorage.ShouldCreateStatusAudit()` for reference implementation)

### Fixed

- **Schedule drift in recurring tasks**: `CalculateNextValidRun()` now correctly skips past occurrences
  - Both `Dispatcher` and `WorkerExecutor` use consistent calculation logic
  - Prevents drift accumulation during system downtime
  - Preserves `ExecutionTime` across rescheduling cycles
- **Test flakiness eliminated**: All 445 integration tests pass consistently on .NET 6/7/8/9
  - Replaced shared `IHost` pattern with per-test isolation
  - Removed timing-dependent `Task.Delay()` calls
  - Optimized test timeouts (50% reduction) without sacrificing reliability

### Improved

#### Audit System Performance Optimizations

- **Eliminated AuditLevel SELECT queries**: Storage operations no longer query database to retrieve task's AuditLevel
  - `ITaskStorage.SetStatus()` now accepts `AuditLevel` as parameter (passed from `WorkerExecutor`)
  - `ITaskStorage.UpdateCurrentRun()` now accepts `AuditLevel` as parameter
  - **Impact**: Reduces database roundtrips by 1 SELECT per status update (50% reduction from 2 to 1 query)
  - **SQL Server optimization preserved**: Stored procedure `usp_SetTaskStatus` updated to accept `@AuditLevel` parameter
    - Single atomic operation: audit insert (if required) + status update in one database call
    - AuditLevel logic (`ShouldCreateStatusAudit`) implemented in T-SQL for maximum performance
    - Backward compatible with default `@AuditLevel = 0` (Full audit level)
  - **Migration**: New migration `UpdateStoredProcedureForAuditLevel` updates SQL Server stored procedure
  - All storage implementations updated: `EfCoreTaskStorage`, `SqlServerTaskStorage`, `MemoryTaskStorage`

#### Retry Policy Enhancements

- **Fail-Fast on Permanent Errors**: Retry policies configured with exception filters now immediately fail for non-transient errors (e.g., `ArgumentException`, `ValidationException`, `NullReferenceException`), reducing wasted retry attempts and improving error visibility
- **Better Retry Visibility**: `OnRetry` callback provides granular insight into retry behavior, enabling proactive monitoring and alerting
- **Derived Exception Type Support**: Exception filtering uses `Type.IsAssignableFrom()` to automatically match derived exception types (e.g., `Handle<IOException>()` also catches `FileNotFoundException`)
- **Priority-Based Filter Evaluation**: Clear precedence order (Predicate > Whitelist > Blacklist > Default) prevents ambiguity
- **Validation Against Mixed Approaches**: `LinearRetryPolicy` throws `InvalidOperationException` if `Handle<T>()` and `DoNotHandle<T>()` are mixed, preventing configuration errors

### Backward Compatibility

- All changes are backward compatible
- Existing handlers work without `OnRetry` override (default no-op implementation)
- Existing retry policies work without `ShouldRetry` override (default interface method implementation)
- Default retry behavior unchanged (retry all exceptions except `OperationCanceledException` and `TimeoutException`)
- `onRetryCallback` parameter is optional with default `null`

### Documentation

- Comprehensive exception filtering and OnRetry documentation added to `docs/resilience.md`
- README updated with retry policy enhancement examples
- CLAUDE.md updated with implementation architecture details

## [3.0.0] - 2025-10-20

### Added
- **Monitoring & Dashboard** (in testing on branch `api-dashboard`):
  - New REST API for task monitoring and management (`EverTask.Monitor.Api`)
  - Real-time dashboard UI for visualizing task execution, queues, and performance metrics
  - These features are currently in testing phase and will be merged in a future release

### Changed
- **BREAKING CHANGE**: `RetryPolicy`, `Timeout`, and `QueueName` properties in `IEverTaskHandlerOptions` and `EverTaskHandler<TTask>` are now read-only `virtual` properties instead of settable properties
  - `QueueName` has been added to `IEverTaskHandlerOptions` for consistency with `RetryPolicy` and `Timeout`
  - **Migration**: Change from property initialization in constructor to property override:
    ```csharp
    // ❌ Old way (no longer works):
    public class MyHandler : EverTaskHandler<MyTask>
    {
        public MyHandler()
        {
            RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(1));
            Timeout = TimeSpan.FromMinutes(10);
        }

        public override string? QueueName => "critical";
    }

    // ✅ New way:
    public class MyHandler : EverTaskHandler<MyTask>
    {
        public override IRetryPolicy? RetryPolicy => new LinearRetryPolicy(5, TimeSpan.FromSeconds(1));
        public override TimeSpan? Timeout => TimeSpan.FromMinutes(10);
        public override string? QueueName => "critical";
    }
    ```
  - **Rationale**: Provides consistency with `QueueName` property pattern and better semantic clarity that these are handler type characteristics, not instance state
  - All documentation, samples, and tests updated to reflect this pattern

## [2.0.0] - 2025-10-19

### Added
- **High-performance scheduler**: `PeriodicTimerScheduler` with SemaphoreSlim-based wake-up signaling
  - 90%+ reduction in lock contention compared to `TimerScheduler`
  - Zero CPU usage when task queue is empty (sleeps until new task scheduled)
  - Dynamic delay calculation based on next task execution time
  - Replaces continuous timer updates with event-driven wake-up pattern
- **Optional sharded scheduler** for extreme high-load scenarios (>10k Schedule() calls/sec)
  - Opt-in via `.UseShardedScheduler(shardCount)` configuration method
  - 2-4x throughput improvement for workloads exceeding 10k Schedule() calls/sec
  - Independent timer shards with complete failure isolation
  - Auto-scaling based on `Environment.ProcessorCount` (minimum 4 shards)
  - Hash-based task distribution for uniform shard load balancing
  - Minimal overhead: ~300 bytes per shard
  - Recommended for: 100k+ scheduled tasks, 10k+ Schedule/sec sustained, 20k+ Schedule/sec bursts
  - Comprehensive test coverage: 12 unit tests, 8 integration tests
  - Full documentation in README with performance comparison table
- **DbContext factory pattern**: `ITaskStoreDbContextFactory` abstraction for storage providers
  - 30-50% performance improvement in storage operations
  - Enables built-in EF Core DbContext pooling via `IDbContextFactory<T>`
  - Backward compatible with existing `IServiceScopeFactory` pattern
  - `ServiceScopeDbContextFactory` adapter for legacy scenarios
- **Smart configuration defaults** that scale automatically with CPU cores:
  - `MaxDegreeOfParallelism`: `Environment.ProcessorCount * 2` (minimum 4, replaces hardcoded 1)
  - `ChannelCapacity`: `Environment.ProcessorCount * 200` (minimum 1000, replaces hardcoded 500)
- **Configuration validation**: Warning log when `MaxDegreeOfParallelism=1` (production anti-pattern)
  - Suggests recommended parallelism value based on processor count
  - Helps identify suboptimal configurations in development and staging

### Changed
- **Default scheduler**: `PeriodicTimerScheduler` now registered by default (replaces `TimerScheduler`)
- **Storage implementations** (SqlServer, Sqlite):
  - Use `AddDbContextFactory<T>` with built-in pooling instead of `AddDbContextPool`
  - Register `ITaskStoreDbContextFactory` for high-performance DbContext creation
  - Scoped `ITaskStoreDbContext` registration now uses factory internally
- **EfCore storage**: `EfCoreTaskStorage` now depends on `ITaskStoreDbContextFactory` (breaking change for custom storage implementations)
  - All database operations use `await contextFactory.CreateDbContextAsync()` pattern
  - Improved async/await patterns throughout storage layer

### Deprecated
- `TimerScheduler` marked as `[Obsolete]` with migration guidance
  - Will be removed in future major version
  - Users should migrate to `PeriodicTimerScheduler` (automatic if using default DI registration)
  - Custom registrations need manual update: `services.TryAddSingleton<IScheduler, PeriodicTimerScheduler>()`

### Performance
- **Scheduler improvements**:
  - 90%+ reduction in lock contention under high load
  - Zero CPU overhead when no tasks scheduled (vs continuous polling)
  - Faster task scheduling response time through immediate wake-up
- **Storage improvements**:
  - 30-50% faster storage operations through DbContext pooling
  - Reduced memory allocations via context reuse
  - Better connection pool utilization
  - **SQL Server optimized status updates**: 50% reduction in database roundtrips for status changes
    - New `SqlServerTaskStorage` with stored procedure-based `SetStatus()` implementation
    - Atomic status update + audit insert in single database call (was 2 separate calls)
    - Transactional consistency guaranteed via stored procedure
    - Fully backward compatible with EF Core-based implementations
- **Parallelism improvements**:
  - Default configuration now leverages all CPU cores (8-core = 16 parallel workers vs previous 1)
  - Channel capacity scales with workload (8-core = ~1600 vs previous 500)
  - Production-ready defaults eliminate need for manual tuning
- **Dispatcher hotpath optimizations**:
  - **Reflection caching**: Compiled Expression tree cache for `TaskHandlerWrapper` instantiation
    - 93% faster task dispatching for repeated task types (~150μs → ~0.01μs per dispatch)
    - `ConcurrentDictionary<Type, Func<TaskHandlerWrapper>>` replaces `Activator.CreateInstance()` + `MakeGenericType()`
    - Zero performance impact for high task-type diversity scenarios (minimal memory overhead)
  - **Lazy serialization**: `ToQueuedTask()` invoked only when `ITaskStorage` configured
    - Eliminates unnecessary JSON serialization for in-memory-only workloads
    - 100% reduction in serialization overhead when storage is disabled
  - **Reduced allocations**: Single `ToQueuedTask()` call shared between `Persist()` and `UpdateTask()`
    - 50% reduction in serialization operations during task updates
    - Consolidated exception handling reduces code paths and improves maintainability
- **Worker executor & monitoring optimizations**:
  - **Event data caching**: Task JSON and type metadata cached to eliminate redundant serialization
    - `ConditionalWeakTable<IEverTask, string>` for automatic task JSON cache cleanup
    - `ConcurrentDictionary<Type, string>` for permanent type string caching
    - 99% reduction in JSON serializations for monitoring events (60k-80k → ~10-20 per 10k tasks)
    - Single `EverTaskEventData` object created and reused across all subscribers
    - Early exit when no monitoring subscribers (zero overhead)
  - **Handler options caching**: Runtime casts eliminated via per-type option caching
    - `ConcurrentDictionary<Type, HandlerOptionsCache>` stores retry policy and timeout per handler type
    - 99% reduction in runtime casts (10k → ~100 unique types per 10k executions)
    - Options "frozen" at first handler execution (consistent behavior, faster subsequent calls)
  - **Type metadata string caching**: AssemblyQualifiedName and RecurringTask.ToString() cached
    - Eliminates repeated string generation for same types/configurations
    - 99% reduction in metadata string allocations (20k → ~100 per 10k dispatches)
    - ~3-5 MB memory saved in high-throughput scenarios
  - **Stopwatch allocation elimination**: .NET 7+ uses `Stopwatch.GetTimestamp()` and `GetElapsedTime()` (zero-allocation)
    - Conditional compilation with fallback to `Stopwatch.StartNew()` for .NET 6
    - ~400 KB/sec allocation reduction at 10k tasks/sec throughput
  - **String.Format optimization**: Conditional formatting only when `messageArgs.Length > 0`
    - Eliminates unnecessary string allocations in event publishing hot path
    - ~50-100 KB/sec reduction in allocations at 1k events/sec
  - **Fire-and-forget exception handling**: `Task.Run` with try/catch wrapper for monitoring event handlers
    - Prevents process crashes from unobserved task exceptions in event subscribers
    - **Critical stability fix** - eliminates potential `TaskScheduler.UnobservedTaskException` crashes
  - **Combined impact**: 85-90% reduction in memory allocations, 2-5x throughput improvement for monitoring-enabled workloads
- **CancellationTokenSource lifecycle improvements**:
  - **Race condition fix**: `AddOrUpdate` pattern replaces check-then-act in `CancellationSourceProvider`
    - Eliminates memory leaks from failed `TryAdd` operations
    - ~100+ bytes per leaked CTS eliminated
    - Added `ObjectDisposedException` handling in `Delete()` for thread-safe disposal
- **Startup performance optimizations**:
  - **Parallel pending task processing**: `ProcessPendingAsync` now uses `Parallel.ForEachAsync`
    - Respects configured `MaxDegreeOfParallelism` settings
    - Scoped `ITaskStorage` per iteration for DbContext thread safety
    - 80% reduction in startup time with 1000+ pending tasks (10+ sec → ~2 sec)
- **Queue management optimizations**:
  - **Dictionary lookup reduction**: `WorkerQueueManager.TryEnqueue` optimized from 2-3 to 1-2 lookups per enqueue
    - Inline queue name determination eliminates redundant `ContainsKey` checks
    - Config retrieved directly from `WorkerQueue` when possible
    - ~10-20k fewer dictionary operations/sec at 10k tasks/sec throughput
  - **WorkerBlacklist memory efficiency**: `HashSet<Guid>` with lock replaces `ConcurrentDictionary<Guid, EmptyStruct>`
    - Lower memory overhead (~32 bytes per entry saved)
    - Lock contention negligible (Add/Remove rare, IsBlacklisted frequent on hot path)
    - Maintains O(1) performance characteristics

### Fixed
- **Critical correctness bug in MonthInterval**: Missing return value assignments in `GetNextOccurrence()`
  - `FindFirstOccurrenceOfDayOfWeekInMonth()` and `AdjustDayToValidMonthDay()` results were discarded
  - Monthly recurring tasks with `OnFirst` or `OnDay` specifications executed at incorrect times
  - Now correctly assigns calculated values to `nextMonth` variable
  - Added conditional check for `OnDay.HasValue` to prevent unnecessary adjustments
- **Race condition in PeriodicTimerScheduler wake-up logic**: Semaphore signaling now thread-safe
  - `Schedule()` method had check-then-act race between `CurrentCount == 0` check and `Release()` call
  - Under high concurrency (100+ concurrent Schedule() calls), multiple threads threw `SemaphoreFullException`
  - Exception overhead: 100-1000x slower than normal control flow
  - Replaced with atomic `Interlocked.CompareExchange` pattern using `_wakeUpPending` flag
  - Flag reset after `WaitAsync()` consumes signal, eliminating all exception overhead
- **Unbounded loops in DateTimeExtensions**: Added bounds checking and validation to prevent infinite loops
  - `NextValidDayOfWeek()`: Max 7 iterations with empty array validation
  - `NextValidDay()`: Max days-in-month iterations with range validation (1-31)
  - `NextValidHour()`: Max 24 iterations with range validation (0-23)
  - `NextValidMonth()`: Max 12 iterations with range validation (1-12)
  - `FindFirstOccurrenceOfDayOfWeekInMonth()`: Max 7 iterations, starts from first day of month
  - All methods throw `ArgumentException` for invalid inputs (empty arrays, out-of-range values)
  - Prevents thread hangs from malicious or buggy task configurations
- **Cron expression repeated parsing**: Eliminated redundant parsing overhead in `CronInterval`
  - `GetNextOccurrence()` called `ParseCronExpression()` on every invocation (~100-500μs per parse)
  - Recurring cron tasks running every 5 seconds incurred 17,280 parses per day
  - Implemented lazy caching: `_parsedExpression` field with invalidation on `CronExpression` property change
  - ~99.9% reduction in parsing operations for stable recurring tasks (17,280 → ~1 per task lifecycle)
- **TimeOnly array repeated sorting**: Eliminated redundant sorting in recurring time calculations
  - `GetNextRequestedTime()` called `OrderBy().ToArray()` on every next-occurrence calculation
  - Caused GC pressure and unnecessary allocations for recurring tasks with multiple daily times
  - Implemented automatic sorting in `DayInterval.OnTimes` and `MonthInterval.OnTimes` property setters
  - Guarantees sorted arrays in all scenarios: builder API, direct assignment, JSON deserialization
  - 100% reduction in sorting operations during task execution (sorting now happens once on configuration)

### Migration Notes
- **No breaking changes** for standard DI registration (automatic migration to new scheduler)
- **Breaking for custom storage implementations**: Replace `IServiceScopeFactory` with `ITaskStoreDbContextFactory`
- **Breaking for tests**: Update `TimerScheduler` casts to `PeriodicTimerScheduler` in integration tests
- **Obsolete warnings**: Review and update any direct `TimerScheduler` references

## [1.6.0] - 2025-10-19

### Added
- **Idempotent task registration** using unique task keys to prevent duplicate scheduled tasks
  - `taskKey` optional parameter added to all `ITaskDispatcher.Dispatch()` methods
  - `TaskKey` property (max 200 chars, nullable, unique index) added to `QueuedTask` storage model
  - `GetByTaskKey()`, `UpdateTask()`, and `Remove()` methods added to `ITaskStorage` interface
  - Smart deduplication logic: ignores if InProgress, updates if Pending/Queued, replaces if Completed/Failed
  - Preserves `CurrentRunCount` when updating recurring tasks
  - Comprehensive integration test suite in `test/EverTask.Tests/IntegrationTests/TaskKeyIntegrationTests.cs`
  - Documentation in README.md with examples for startup task registration and dynamic updates
- Multi-queue support for workload isolation and prioritization
- `QueueName` property to `EverTaskHandler` base class for queue routing
- `QueueConfiguration` class for individual queue settings
- `QueueFullBehavior` enum with Wait, FallbackToDefault, and ThrowException strategies
- `IWorkerQueueManager` interface and implementation for managing multiple queues
- Fluent API methods: `ConfigureDefaultQueue()`, `AddQueue()`, `ConfigureRecurringQueue()`
- Automatic routing of recurring tasks to "recurring" queue
- `QueueName` field to `QueuedTask` storage model
- Comprehensive test suite in `test/EverTask.Tests/MultiQueue/`
- Multi-queue examples in ASP.NET Core sample application

### Changed
- `TaskHandlerExecutor` record now includes `QueueName` parameter
- `TaskHandlerWrapper` reads and passes `QueueName` from handlers
- `Dispatcher` uses `IWorkerQueueManager` instead of single `IWorkerQueue`
- `TimerScheduler` dispatches tasks to appropriate queues based on `QueueName`
- `WorkerService` consumes all queues concurrently with independent parallelism
- `WorkerQueue` now accepts `QueueConfiguration` instead of `EverTaskServiceConfiguration`
- Updated ASP.NET Core sample with multi-queue configuration examples
- Enhanced README.md with multi-queue configuration documentation

### Deprecated
- `WorkerQueue` constructor accepting `EverTaskServiceConfiguration` (use `QueueConfiguration` instead)

## [1.5.4] - Previous Release
- [Previous release notes would go here]
