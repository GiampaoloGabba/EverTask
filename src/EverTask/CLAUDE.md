# EverTask Core

## Purpose

Core implementation: dispatcher, worker executor, scheduler, in-memory storage.

## Key Components

| Component | Location | Responsibility | MediatR Origin |
|-----------|----------|----------------|----------------|
| **Dispatcher** | `Dispatcher/Dispatcher.cs` | Resolve handlers, persist, route tasks | ✅ `Mediator.cs` |
| **WorkerExecutor** | `Worker/WorkerExecutor.cs` | Execute with retry/timeout/lifecycle | - |
| **TaskHandlerExecutor** | `Handler/TaskHandlerExecutor.cs` | Task metadata, serialization | ✅ `NotificationHandlerExecutor.cs` |
| **TaskHandlerWrapper** | `Handler/TaskHandlerWrapper.cs` | Generic handler resolution | ✅ `NotificationHandlerWrapper.cs` |
| **WorkerQueue** | `Worker/WorkerQueue.cs` | Bounded queue (`System.Threading.Channels`) | - |
| **TimerScheduler** | `Scheduler/TimerScheduler.cs` | Priority queue for delayed/recurring | - |
| **HandlerRegistrar** | `MicrosoftExtensionsDI/HandlerRegistrar.cs` | Assembly scanning + DI registration | ✅ MediatR logic |

**MediatR Attribution**: Components marked ✅ adapted from MediatR (Apache 2.0).

## Critical Gotchas

### Scoped Service Provider Per Task
**WHY**: DbContext is NOT thread-safe. Each concurrent task needs its own scope.

**WHERE**: `WorkerExecutor` creates scope at task start, disposes after completion.

**CONSISTENCY**: Same pattern used in `EfCoreTaskStorage` (see `src/Storage/EverTask.Storage.EfCore/CLAUDE.md`).

### Fire-and-Forget Monitoring
Monitoring events (`TaskEventOccurredAsync`) published fire-and-forget to prevent monitoring failures from blocking execution.

### Recurring Auto-Rescheduling
After recurring task completes, `WorkerExecutor` auto-calculates next run and updates storage. Stops when `MaxRuns` reached or `RunUntil` exceeded.

### Blacklist for Cancelled Tasks
`ITaskDispatcher.Cancel(Guid)` adds to blacklist. `WorkerExecutor` checks before execution. `Cancel` also calls `IScheduler.TryUnschedule(id)` to drop any parked occurrence.

### Queue & Recovery Resilience (no task loss, no deadlock)
**Lifecycle invariant**: every persisted task is executed or stays in a status `RetrievePending` recovers. Recoverable statuses: `WaitingQueue`, `Queued`, `Pending`, `InProgress`, `ServiceStopped`, plus recurring tasks (`IsRecurring && NextRunUtc != null`) in `Completed`/`Failed`. Identical across EfCore/Sqlite/Memory storage — **keep the three filters in sync**.

- **Startup order matters**: `WorkerService.ExecuteAsync` starts consumers **first**, then runs `ProcessPendingAsync` **concurrently** (`RunRecoveryAsync`). Reverting to recover-before-consume reintroduces the capacity deadlock. Recovery has a `recoveryCutoff` (CreatedAtUtc) and uses `ExecuteDispatch(isRecovery: true)`.
- **`isRecovery` flag** (Dispatcher): (1) blocking enqueue (`EnqueueBlocking`, never drop); (2) preserves stored `NextRunUtc` for recurring (recalculating past it skips an occurrence — the P0 bug); (3) skips `UpdateTask` (avoids overwriting a concurrent live taskKey re-registration).
- **Double-execution defense (at-least-once contract)**: `WorkerQueue` dedupes by `PersistenceId` via an in-channel registry (removed at dequeue); `WorkerExecutor.DoWork` has an in-flight guard. Handlers with side effects must still be idempotent — recommend a stable `taskKey`.
- **CancellationToken** flows `Dispatch → TryEnqueue → WriteAsync`. Cancel during a full-queue wait → OCE to caller, task stays `Queued` (recovered later), never `Failed`.
- **Schedulers never block on a full queue**: dispatch via `TryEnqueueImmediate`; on `QueueFull` re-enqueue at `now + FullQueueRetryDelay` (no head-of-line blocking). They do **not** mark `Failed` on shutdown (OCE) or transient errors — only real handler failures fail a task. `QueueFullBehavior` applies to **immediate dispatches only**.

### Async Best Practices
Code follows [David Fowl's AsyncGuidance.md](https://github.com/davidfowl/AspNetCoreDiagnosticScenarios/blob/master/AsyncGuidance.md) (see inline comments in `WorkerExecutor.cs`).

## 🔗 Test Coverage

**Integration tests**: `test/EverTask.Tests/IntegrationTests/` (uses `IntegrationTestBase` with real IHost)

**Test helpers**: `test/EverTask.Tests/TestHelpers/` (TaskWaitHelper, TestTaskStateManager)

**When modifying dispatcher**:
- Update: `test/EverTask.Tests/DispatcherTests.cs`
- Verify handler resolution: `test/EverTask.Tests/HandlerRegistrationTests.cs`

**When modifying worker executor**:
- Update: `test/EverTask.Tests/WorkerExecutorTests.cs`
- Verify retry integration: `test/EverTask.Tests/RetryPolicyTests.cs`
- Check lifecycle callbacks: `test/EverTask.Tests/LifecycleTests.cs`

**When modifying queue/recovery/scheduler resilience** (no-loss/no-deadlock invariants):
- `test/EverTask.Tests/IntegrationTests/QueueResilienceIntegrationTests.cs` (deadlock, WaitingQueue recovery, HOL, cancellation, recurring revival, RunUntil, exactly-once)
- `test/EverTask.Tests/SchedulerResilienceTests.cs`, `WorkerQueueResilienceTests.cs`, `MemoryStorageRecoveryFilterTests.cs`
- Real-DB recovery: `test/EverTask.Tests.Storage/SqlServerRecoveryIntegrationTests.cs` (Docker) + recovery-filter section in `EfCoreTaskStorageTestsBase.cs` (all 3 providers)
