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

**Lifecycle invariant**: every persisted task is executed or left in a status `RetrievePending` recovers. The
canonical predicate is `QueuedTask.IsRecoverable` (statuses plus the `MaxRuns` / `RunUntil` gating), and it
has three copies that must **stay in sync** — `EfCoreTaskStorage.RecoverableQuery`, the inline list in
`SqliteTaskStorage.RetrievePending`, and the Postgres partial index `IX_QueuedTasks_Recovery` (a new status
needs a NEW migration): the four-places rule in `src/Storage/EverTask.Storage.EfCore/CLAUDE.md`.
`MemoryTaskStorage` keeps no copy — it calls `IsRecoverable` directly.

- **Startup order matters**: `WorkerService.ExecuteAsync` starts consumers **first**, then runs recovery
  **concurrently** (`RunRecoveryAsync`). Recover-before-consume reintroduces the capacity deadlock.
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

## Tests

Integration tests build a real `IHost` through `test/EverTask.Tests/TestHelpers/IsolatedIntegrationTestBase.cs`.
The invariants above are pinned by `test/EverTask.Tests/IntegrationTests/QueueResilienceIntegrationTests.cs`,
`SchedulerResilienceTests.cs`, `WorkerQueueResilienceTests.cs`, `MemoryStorageRecoveryFilterTests.cs` and, on a
real DB, `test/EverTask.Tests.Storage/SqlServerRecoveryIntegrationTests.cs` plus the recovery-filter section of
`EfCore/EfCoreTaskStorageTestsBase.cs` (run by all four EF Core providers: SQLite, SQL Server, PostgreSQL, MySQL).
