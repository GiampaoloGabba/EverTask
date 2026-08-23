# EverTask Core

Refer to the root CLAUDE.md for project-wide rules.

Dispatcher, worker executor, bounded queue, schedulers (`PeriodicTimerScheduler` + `ShardedScheduler`) and the
in-memory storage.

## Gotchas

- **Cancellation blacklist**: `ITaskDispatcher.Cancel(Guid)` adds to `Worker/WorkerBlacklist.cs` (entries lapse
  after `EntryTtl` ≈ 1 h, so cancelled parked tasks don't leak them). `WorkerExecutor.DoWork` checks it BEFORE
  the rate-limit gate — cancelled tasks must not burn tokens. `Cancel` also calls `IScheduler.TryUnschedule`,
  bumps the gate invalidation epoch and releases the parking-lot entry.
- **Immediate dispatches are LAZY**: the wrapper resolves a short-lived metadata handler in a disposable scope
  and the worker resolves the executing instance in its per-task scope. Never resolve an eager transient
  handler from the dispatcher's root provider — it pins `IAsyncDisposable` instances until shutdown.
- **Recurring auto-rescheduling**: after a run `WorkerExecutor` computes the next occurrence and updates
  storage; `MaxRuns` counts real executions only — see `Scheduler/Recurring/CLAUDE.md`.
- **Rate-limit gate** (handlers declaring a `RateLimitPolicy`): all invariants, re-park rules and
  retry/restart semantics live in `RateLimiting/CLAUDE.md`.
- **Monitoring events go through `WorkerExecutor.RegisterEvent` only** (one gate: log template + rendered
  `Message` + publish). Never log and publish by hand, and never call the `SkipEnabledCheck` methods of
  `WorkerExecutorLog` from outside that gate — that is how the rendered-string-as-template bug (#32) comes back.

## Queue & Recovery Resilience (no task loss, no deadlock)

**Lifecycle invariant**: every persisted task is executed or left in a status `RetrievePending` recovers. A
recovery page is the UNION of two canonical predicates on `QueuedTask`: `IsRecoverableForExecution(now)` (rows
with work left) and `IsRecurringSeriesToFinalize()` (a series whose remaining slots all fall past `RunUntil`,
or whose run budget is spent — Completed with the cursor cleared, no handler, no run counted). The execution
predicate has three server-side copies that must **stay in sync** — `EfCoreTaskStorage`, the inline list in
`SqliteTaskStorage.RetrievePending`, and the Postgres partial index `IX_QueuedTasks_Recovery` (a new status
needs a NEW migration): the four-places rule in `src/Storage/EverTask.Storage.EfCore/CLAUDE.md`.
`MemoryTaskStorage` keeps no copy — it calls the predicates directly. `TrySetQueuedIfRecoverable` applies ONLY
the execution predicate: a spent series must be finalized, never handed back to a worker queue.

- **The temporal term is grouped, deliberately** (X3): status and `MaxRuns` stay ANDed in front, and
  `RunUntil` gains `|| (IsRecurring && NextRunUtc != null && NextRunUtc < RunUntil)`. Without it the
  occurrence a series had already scheduled before a boundary that elapsed *during* the downtime is silently
  lost, and the row stays `Queued` forever.
- **The recovery grace window asks the NATURAL successor** (`RecurringTask.NextGridOccurrenceAfter`, bounds
  ignored), never `IsOccurrenceStillCurrent`: the bounded successor is null both when the slot is still
  current and when the series simply ended, and reading that null as the former executes a months-old slot at
  restart. Order in the dispatcher's recovery branch: finalize → grace → skip-forward. **Both finalization
  sites are conditional** wherever the storage can be — `WorkerService` for category (ii) and the dispatcher's
  own "exhausted series" branch — through `TrySetRecurringSeriesCompleted`, compare-and-swapped on the cursor,
  status and version **of the row the decision was computed from** (the recovery page's, carried to the
  dispatcher in `DispatchRowMetadata`). Never read them back at write time: `SetStatus` leaves a cancellation's
  cursor and version untouched, so a fresh read hands `Cancelled` to the guard as its own expectation and the
  cancellation is overwritten with `Completed`. Both sites check `SupportsScheduleVersioning` first — a storage
  without the compare-and-swap keeps the historical unconditional write, since letting the member's
  `NotSupportedException` reach the recovery counts a normal end of series as an L18 failure and poisons the
  row. The L18 counter reset runs OUTSIDE the try that guards the terminal write: the write is already
  committed, and a failing reset must not be reclassified as a failing finalization.
- **Startup order matters**: `WorkerService.ExecuteAsync` starts consumers **first**, then runs recovery
  **concurrently** (`RunRecoveryAsync`). Recover-before-consume reintroduces the capacity deadlock.
- **Recovery runs in two waves** (`RecoverWaveAsync`), across the WHOLE recovered set and not page by page:
  ordinary rows are recovered as each page arrives, durable schedule rows are buffered and run after the
  pagination loop. A schedule row asks how many of its occurrences are still active the moment it is back, and
  answering that while an occurrence of its own is still sitting in a later page reads a phantom low count.
  Only the schedule rows are buffered, so the pagination stays bounded in memory.
  `RecoveredTaskFactory.FromRow` rebuilds each row once (payload, validated schedule, audit level, occurrence
  metadata) and is the single place that mapping lives; its `RowMetadata` travels to the re-dispatch through
  the internal `ExecuteDispatch` overload, so a recovered executor keeps its parent, occurrence JSON, schedule
  version and STORED queue instead of re-deriving them from the handler.
- **`recoveryCutoff` is STRICT (`CreatedAtUtc < cutoff`)**: the wall clock is coarse (≈15 ms on Windows), so a
  live dispatch can share the cutoff tick and a `<=` filter would re-dispatch that live row as recovery.
  Best-effort first pass — `TaskDeliveryRegistry` is the actual defense.
- **`isRecovery` flag** (Dispatcher): (1) blocking enqueue (`EnqueueBlocking`, never drop); (2) preserves the
  stored `NextRunUtc` for recurring (recalculating past it skips an occurrence — the P0 bug); (3) skips
  `UpdateTask` (avoids overwriting a concurrent live taskKey re-registration); (4) **carries the persisted
  `AuditLevel`** (null ⇒ Full) — omitting it silently reverts a recovered task to the global `DefaultAuditLevel`.
- **Double-execution defense (at-least-once contract)**: `TaskDeliveryRegistry` (one per host,
  `TryAddSingleton`, shared by all queues) holds each `PersistenceId` from the channel write until the
  delivery terminally ends; a second write is rejected as `EnqueueResult.DuplicateInProcess` (recovery/live
  skip idempotently, schedulers retry like `QueueFull`). **End discipline**: exactly ONE `End` per delivery —
  the outer `finally` of `WorkerExecutor.DoWork`, the enqueue rollback paths in `WorkerQueue`, or the channel
  `itemDropped` callback; never add other `End` sites. Recovery additionally uses `TrySetQueuedIfRecoverable`
  so a row that terminally finished after the page read is never resurrected.
- **CancellationToken** flows `Dispatch → TryEnqueue → WriteAsync`. Cancel during a full-queue wait → OCE to
  the caller, task stays `Queued` (recovered later), never `Failed`.
- **Schedulers never block on a full queue**: dispatch via `TryEnqueueImmediate`; on `QueueFull` re-enqueue at
  `now + FullQueueRetryDelay` (no head-of-line blocking). They do **not** mark `Failed` on shutdown (OCE) or
  transient errors — only real handler failures fail a task. `QueueFullBehavior` applies to **immediate
  dispatches only**.

## Deterministic scheduling clock (P9)

ONE `TimeProvider`, registered by `AddEverTask` with `TryAddSingleton(TimeProvider.System)`, governs every
scheduling decision: dispatcher, `IScheduleEvaluator`, builders, both schedulers, recovery, the rate limiter,
the gate and the parking lot. Register a provider before `AddEverTask` and the whole pipeline follows it.

- **Storage never resolves the clock**: the core always calls the `nowUtc` overloads of `RetrievePending` /
  `TrySetQueuedIfRecoverable`. Both are default members delegating to the intact legacy signatures, so a
  custom storage keeps its own override (and its own atomicity) at the cost of reading the real clock. A
  provider deriving from `EfCoreTaskStorage` gets the same hand-back — see that module's CLAUDE.md.
- **The schedulers race the signal against `Task.Delay(delay, timeProvider)`** instead of
  `SemaphoreSlim.WaitAsync(timeout)`, whose timeout is hard-wired to the real clock. The signal waiter is
  CREATED ONCE and kept across iterations when the delay wins — abandoning it would let it silently consume
  the next `Release` and the scheduler would miss a wake-up.
- **Out of scope, by design**: retry policies (`IRetryPolicy` owns its own waits), audit and logging stay on
  the real clock. A test using a fake clock must not expect `Advance()` to complete a retry delay.

## Occurrence grid: one seam

`IScheduleEvaluator` (internal, `ScheduleEvaluator`) is what the dispatcher, the worker and the recovery ask
about a schedule's grid — never the occurrence math directly. Today it is a synchronous wrapper over the pure
primitives on `RecurringTask`; the asynchronous shape is there because occurrences will later come from a
user-supplied provider that may do I/O. Its two grid questions that no primitive answered before are
`NextGridOccurrenceAfterAsync` (the natural successor, bounds ignored — the recovery grace window) and
`EnumerateDueSlotsAsync` (the slots already owed at a given now, oldest first, under a MANDATORY cap).

## Tests

Integration tests build a real `IHost` through `test/EverTask.Tests/TestHelpers/IsolatedIntegrationTestBase.cs`.
The invariants above are pinned by `test/EverTask.Tests/IntegrationTests/QueueResilienceIntegrationTests.cs`,
`SchedulerResilienceTests.cs`, `WorkerQueueResilienceTests.cs`, `MemoryStorageRecoveryFilterTests.cs` and, on a
real DB, `test/EverTask.Tests.Storage/SqlServerRecoveryIntegrationTests.cs` plus the recovery-filter section of
`EfCore/EfCoreTaskStorageTestsBase.cs` (run by all four EF Core providers: SQLite, SQL Server, PostgreSQL, MySQL).
The execution-vs-finalization split, the natural-successor grace and the injected clock are pinned by
`IntegrationTests/RecoveryExecutionVsFinalizationTests.cs`, `IntegrationTests/DeterministicSchedulingClockTests.cs`
and `SchedulerDeterministicClockTests.cs`; the two-wave barrier by
`RecoveryDurableScheduleBarrierTests.cs`, over a backlog that really spans several pages (a per-page
implementation passes anything smaller); the byte-identical default by
`Serialization/RecurringTaskGoldenJsonTests.cs` and `Serialization/ConsumerCompatibilityTests.cs` (whose
`LegacyMinimalTaskStorage` exists to be COMPILED: it breaks the day a new storage member stops being default).
