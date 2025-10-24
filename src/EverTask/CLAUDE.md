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
`ITaskDispatcher.Cancel(Guid)` adds to blacklist. `WorkerExecutor` checks before execution.

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
