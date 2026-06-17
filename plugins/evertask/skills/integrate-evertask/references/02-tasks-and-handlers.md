# 02 — Tasks, handlers, dispatching, lifecycle

## Defining a task (`IEverTask`)

`IEverTask` is a **pure marker interface** (no members). Any record/class implementing it is a
dispatchable payload. Use records:

```csharp
public record ProcessOrderTask(Guid OrderId, string CustomerEmail) : IEverTask;
```

Payload rules are enforced by the analyzers bundled in `EverTask.Abstractions` — see
`08-payload-contract.md`. Short version: **public properties only, IDs not entities,
primitives/Guid/DateTimeOffset/enums/collections.**

## Defining a handler (`EverTaskHandler<TTask>`)

```csharp
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
```

Required method:

```csharp
public abstract Task Handle(TTask backgroundTask, CancellationToken cancellationToken);
```

The `CancellationToken` is signalled on timeout, on `Dispatcher.Cancel(taskId)`, or on graceful
shutdown. Always let `OperationCanceledException` propagate (re-throw) so the task is recorded as
cancelled/ServiceStopped.

### Overridable virtual members

| Member | Signature / default | Purpose |
|---|---|---|
| `RetryPolicy` | `IRetryPolicy? => null` | Per-handler retry override (null → queue/global default). |
| `Timeout` | `TimeSpan? => null` | Per-handler execution timeout. |
| `QueueName` | `string? => null` | Route to a named queue (null → "default"; recurring → "recurring"). An **unregistered** name logs a warning and falls back to `default` (routing **and** retry/timeout config) — never throws, never drops the task. |
| `RateLimitPolicy` | `RateLimitPolicy? => null` | Per-key throttle (v3.7+). See `06-rate-limiting-queues.md`. |
| `GetRateLimitKey(TTask)` | `string? => (task as IRateLimitedTask)?.RateLimitKey` | Derive the throttle key. |
| `OnStarted(Guid taskId)` | `ValueTask` | Fires immediately before each `Handle()` attempt. |
| `OnCompleted(Guid taskId)` | `ValueTask` | After `Handle()` returns successfully. |
| `OnError(Guid taskId, Exception?, string?)` | `ValueTask` | After all retries exhausted, on timeout, cancel, or terminal rate-limit rejection. Exception is **unwrapped** from the retry `AggregateException`. |
| `OnRetry(Guid taskId, int attemptNumber, Exception, TimeSpan delay)` | `ValueTask` | After the delay, before each retry. `attemptNumber` is 1-based. |
| `DisposeAsyncCore()` | `protected virtual ValueTask` | During handler disposal. |
| `CpuBoundOperation` | — | **Obsolete, no effect.** Do not use. |

Exceptions thrown inside `OnRetry`/`OnError` are logged but swallowed (they never block the retry
or the failure path).

`protected ITaskLogCapture Logger { get; }` is injected by the framework — use
`Logger.LogInformation(...)` etc. inside handlers. It works with **zero configuration**: messages
are always forwarded to the host's `ILogger`; they are *additionally* persisted to the DB only when
`WithPersistentLogger` is enabled. Do not assign it yourself.

### Handler with DI (primary constructor, repo style)

```csharp
public class SendWelcomeEmailHandler(IEmailService emailService)
    : EverTaskHandler<SendWelcomeEmailTask>
{
    public override async Task Handle(SendWelcomeEmailTask task, CancellationToken ct)
        => await emailService.SendAsync(task.UserEmail, task.UserName, ct);
}
```

Each task executes in its **own DI scope**, so injecting scoped services (`DbContext`, etc.) is safe.

## Dispatching (`ITaskDispatcher`)

All overloads return `Task<Guid>` (the persistence ID). Inject `ITaskDispatcher`.

```csharp
// Immediate
Task<Guid> Dispatch(IEverTask task, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken ct = default);

// Delayed (relative)
Task<Guid> Dispatch(IEverTask task, TimeSpan scheduleDelay, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken ct = default);

// Scheduled (absolute; if past, runs immediately)
Task<Guid> Dispatch(IEverTask task, DateTimeOffset scheduleTime, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken ct = default);

// Recurring (fluent builder — see 05-scheduling.md)
Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, AuditLevel? auditLevel = null, string? taskKey = null, CancellationToken ct = default);

// Cancel by id (no cancel-by-key overload)
Task Cancel(Guid taskId, CancellationToken ct = default);
```

Shared params: `auditLevel` (null → global default `Full`; use `Minimal`/`ErrorsOnly` for
high-frequency tasks), `taskKey` (idempotency, ≤200 chars, case-sensitive — see below),
`cancellationToken` (cancels the *dispatch op*, not execution).

**There is no first-class batch dispatch API** (an `IBatchDispatcher` is designed but not
implemented). Loop and dispatch; use `AuditLevel.Minimal` to cut DB writes for bulk.

### Examples

```csharp
// Immediate
await dispatcher.Dispatch(new SendWelcomeEmailTask(user.Email, user.Name));

// In 30 minutes
await dispatcher.Dispatch(new SendReminderTask(userId), TimeSpan.FromMinutes(30));

// At an absolute time, keep the id to cancel later
var id = await dispatcher.Dispatch(new CancelPendingOrderTask(orderId), TimeSpan.FromHours(24));
await dispatcher.Cancel(id);   // if the user acts first
```

## `taskKey` — idempotency / deduplication

A unique DB index backs it. Behavior depends on the existing task's status **and on whether it's recurring**:

**Non-recurring** existing task:

| Existing status | Result |
|---|---|
| `InProgress` | **No-op**: returns the in-flight id, schedules nothing. |
| immediate one-shot with a **delivery already in flight** | **No-op**: returns existing id (checked before the status logic, to avoid losing the payload / double-executing). |
| `Pending` / `Queued` / `WaitingQueue` | **Updates** config (new schedule/payload), preserves id + run count. |
| `Completed` / `Failed` / `Cancelled` / `ServiceStopped` | **Replaces**: removes old, creates new. |

**Recurring** existing task (a recurring row is never treated as "terminated"):

| Existing status | Result |
|---|---|
| `InProgress` | **No-op**: returns the in-flight id. |
| any other (incl. `Completed` / `Failed`) | **Updates in place**, never removed/recreated. Preserves `NextRunUtc` + `CurrentRunCount` **only if `NextRunUtc.HasValue`**; an exhausted series (no stored next run) is recalculated from the new schedule instead. |
| re-dispatch with **no** recurring config (recurring → one-shot) | **Discarded** (no-op) — a `taskKey` cannot convert a recurring task to a one-shot. |

Rules: ≤200 chars (enforced by the storage column, not validated up-front), null/empty = no dedup.
Case-sensitivity follows your storage provider's collation (case-sensitive for in-memory/SQLite
default; SQL Server's default collation is case-*insensitive*, so keys differing only by case would
collide) — pick distinct keys regardless. Use kebab-case or namespaced keys (`"daily-cleanup"`,
`"tenant-{id}:billing"`).

**Gotcha — self-redispatch:** a handler re-dispatching itself with the same stable `taskKey` is a
silent no-op (it is `InProgress`). Use a `null` or per-attempt key (`"mytask-{id}-{attempt}"`).

The classic use is idempotent startup registration of recurring tasks — see
`templates/RecurringRegistrar.md` and `05-scheduling.md`.

## Managing tasks at runtime

- **Cancel by id**: `await dispatcher.Cancel(taskId)`. Pending/queued → cancelled before running;
  in-progress → the handler's `CancellationToken` is signalled (handler must cooperate).
- **Cancel by key**: no overload — resolve first via `ITaskStorage.GetByTaskKey(key)` then cancel
  by `PersistenceId`.
- **Bulk cancel**: loop over stored ids.

`QueuedTaskStatus`: `WaitingQueue, Queued, InProgress, Pending, Cancelled, Completed, Failed,
ServiceStopped`. `ServiceStopped` is recoverable — re-queued on next startup.

## Wizard decision points

1. What is the task? → record + payload validated against `08-payload-contract.md`.
2. Needs DI services? → primary-constructor injection (scoped is safe).
3. Immediate / delayed / scheduled / recurring? → pick the dispatch overload.
4. Needs callbacks? `OnCompleted` (chain next task), `OnError` (compensation/alert), `OnRetry`
   (metrics).
5. Custom retry/timeout/queue/rate-limit? → override the matching member (see the feature refs).
6. Idempotent across restarts? → `taskKey`.
