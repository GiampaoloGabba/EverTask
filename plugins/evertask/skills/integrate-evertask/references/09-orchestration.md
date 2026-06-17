# 09 — Orchestration & workflow chaining

EverTask has **no dedicated workflow/saga API** (a fluent workflow builder is on the roadmap, not
shipped). Orchestration is built from the primitives you already have: **lifecycle callbacks**
(`OnCompleted` / `OnError`) that **dispatch the next task**. Each step is an independent, persisted
task — so the whole flow survives restarts step by step.

Core rules:
- Pass handoff data forward as **IDs / primitives in the next task's payload** (never entities — see
  `08-payload-contract.md`). Load what you need inside the next handler via DI.
- Inject `ITaskDispatcher` into the handler to dispatch continuations.
- Make steps **idempotent** (stable `taskKey`, or natural idempotency) — a step can re-run after a
  crash/restart, and `OnCompleted`/`OnError` can fire again on recovery.
- A failing step reaches `OnError` only after its retries are exhausted; that's the place to trigger
  compensation.

## Continuation (A → B → C)

```csharp
public class ProcessOrderHandler(ITaskDispatcher dispatcher) : EverTaskHandler<ProcessOrderTask>
{
    public override Task Handle(ProcessOrderTask t, CancellationToken ct) => _orders.ProcessAsync(t.OrderId, ct);

    public override async ValueTask OnCompleted(Guid taskId)
        => await dispatcher.Dispatch(new ReserveStockTask(/* orderId */), taskKey: $"reserve-{/*orderId*/}");
}
```

## Compensation / rollback (saga step)

```csharp
public override async ValueTask OnError(Guid taskId, Exception? ex, string? msg)
{
    Logger.LogError(ex, "Charge failed for {Id}: {Msg}", taskId, msg);
    await dispatcher.Dispatch(new ReleaseReservationTask(/* reservationId */));  // undo prior step
}
```

## Fan-out

Dispatch N child tasks from a coordinator handler (loop + `Dispatch`); use `AuditLevel.Minimal` for
large fan-outs. There is no built-in fan-in/join — if you need "all children done", have each child
report completion to a shared store and let the last one dispatch the join step (or poll via
`ITaskStorage.Get(...)`).

## Delayed / scheduled handoff

Continuations can themselves be delayed or scheduled: `Dispatch(next, TimeSpan.FromMinutes(10))`,
`Dispatch(next, dueDate)`, or a recurring follow-up — see `05-scheduling.md`.

## When this isn't enough

For long, branching, stateful processes, keep the *state* in your own domain store keyed by a
correlation id carried in every task payload; each handler reads state, does one step, and dispatches
the next. EverTask gives durability + retries per step; your store gives the workflow's shape.

> The repo docs (`docs/task-orchestration.md`, `docs/custom-workflows.md`) show fuller saga /
> state-machine / parallel examples built on exactly these primitives.
