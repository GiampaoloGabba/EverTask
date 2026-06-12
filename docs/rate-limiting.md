---
layout: default
title: Keyed Rate Limiting
parent: Scalability
nav_order: 3
---

# Keyed Rate Limiting

Throttle task execution per logical key (tenant, account, external resource): each key respects its own budget while every other key keeps flowing at full speed.

## Why

The driving scenario: your tasks call an external API limited to ~15 requests per minute **per tenant**. You need:

- a *frequency constraint per key*, not a parallelism limit: queue concurrency stays whatever you configured;
- **no head-of-line blocking**: a tenant that exhausted its budget must never delay other tenants;
- **no busy workers**: a worker slot is never held while waiting for budget;
- **no lost tasks**: an over-budget task is re-scheduled, not dropped (unless you opt into `Discard`).

EverTask implements this with a consumer-side gate backed by a [GCRA](https://en.wikipedia.org/wiki/Generic_cell_rate_algorithm) limiter (sliding window with burst tolerance). When a task has no budget, the gate reserves the next available slot for it and re-parks it into the in-memory scheduler. Nothing is written to storage for a deferral: the task simply stays in its recoverable `Queued` status until its slot fires.

## Quick Start

Mark the task with its throttling key and declare the policy on the handler:

```csharp
public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
{
    public string RateLimitKey => TenantId.ToString();
}

public class SyncTenantDataHandler : EverTaskHandler<SyncTenantData>
{
    // Each tenant gets 15 calls per minute; other tenants are unaffected
    public override RateLimitPolicy? RateLimitPolicy =>
        new RateLimitPolicy(15, TimeSpan.FromMinutes(1))
        {
            Burst           = 15,    // back-to-back tolerance (default = Permits)
            ThrottleRetries = true,  // retries respect the same budget (default)
            StartEmpty      = false  // fresh keys start with the full burst (default)
        };

    public override async Task Handle(SyncTenantData task, CancellationToken ct)
    {
        await _externalApi.SyncAsync(task.TenantId, ct);
    }
}
```

That's it. No configuration-time registration: tasks of other types, or tasks of this type without a key, skip the gate entirely.

## Declaring the Key

Two equivalent options:

1. **On the task** (preferred): implement `IRateLimitedTask`.

   ```csharp
   public record SyncTenantData(Guid TenantId) : IEverTask, IRateLimitedTask
   {
       public string RateLimitKey => TenantId.ToString();
   }
   ```

2. **On the handler**: override `GetRateLimitKey` to derive the key without touching the task type.

   ```csharp
   public class SyncTenantDataHandler : EverTaskHandler<SyncTenantData>
   {
       public override string? GetRateLimitKey(SyncTenantData task) => task.TenantId.ToString();
       ...
   }
   ```

Rules and behavior:

- Budgets are scoped per **(task type, key)**: the same key used by two different task types never shares budget.
- A null/empty key disables the gate for that dispatch. A policy without a key logs a warning once per task type.
- A key selector that throws is **fail-safe**: the task executes ungated, with a warning log.
- Keys longer than `MaxKeyLength` (default 256 chars) are hashed (SHA-256) before use.

> Careful: the rate-limit key is a *throttling* key, not the dispatch `taskKey` (idempotency/deduplication). Many tasks share the same rate-limit key, while a `taskKey` identifies one logical task. Never reuse one for the other.

## How It Works

```
dispatch → queue → consumer dequeues → rate-limit gate:
  ├─ budget available  → execute now
  ├─ slot ≤ MaxInSlotWait away → wait inline, then execute (saves a scheduler round-trip)
  └─ otherwise → reserve the slot, re-park into the in-memory scheduler
                 (lazy, no handler instance pinned; storage untouched, status stays Queued)
slot fires → task re-enters the queue (the usual SetQueued write) → gate redeems the
             reservation → executes
```

Key properties:

- **GCRA math**: the steady emission interval is `T = Period / Permits`; `Burst` controls how many executions may happen back-to-back before the steady rate is enforced. With `Burst = 1` executions are strictly evenly spaced.
- **Reservations are per task**: the reserved slot is keyed by the task's persistence id, so a duplicate delivery of the same task *redeems* the same slot instead of consuming new budget.
- **A deferral writes nothing to storage.** The only storage touch of a deferral cycle is the existing `SetQueued` when the slot fires. That write adds an audit row at `AuditLevel.Full`, so for heavily throttled task types we recommend `AuditLevel.Minimal`.
- **Cancel works while parked**: `ITaskDispatcher.Cancel` drops the parked occurrence; a same-`taskKey` re-dispatch replaces the parked payload (latest wins, exactly once).

## Retries

By default (`ThrottleRetries = true`) every retry attempt re-acquires budget before running: if the task calls a rate-limited API, its retries should respect the same limit. The budget wait happens *before* the per-attempt `Timeout` starts, so waiting never erodes it.

- A retry whose slot is at most `MaxInSlotWait` away waits inline between attempts.
- A retry whose slot is farther away re-parks the task at the reserved slot instead of failing it. The attempt counter restarts on redelivery; combined with a low `MaxDegreeOfParallelism`, this trades some throughput for retry fidelity.
- Set `ThrottleRetries = false` to let retries bypass the limiter (e.g. when retrying cheap local failures).

The `OnRetry` callback still fires as usual; throttled waits are visible through its delay parameter.

## Recurring Tasks

Recurring tasks are throttled per occurrence, and the series rhythm is preserved: a deferred occurrence executes late, but the *next* occurrence is still computed from the original schedule (the re-park never touches the occurrence's scheduled time).

- An occurrence whose reserved slot would fall past `RunUntil` is skipped, never fired late.
- An occurrence rejected by the reservation horizon (see below) is skipped through the normal next-occurrence path: the skip counts toward `MaxRuns` (same semantics as downtime) and the series stays alive.
- A recurrence faster than the policy refill rate logs a warning at first dispatch (occurrences would pile up behind the limiter).

## Observability

Deferrals are infrastructure routing: no handler callback fires for them (like the scheduler's full-queue re-park). Observability channels:

- **Monitoring events** (`TaskEventOccurredAsync` / SignalR): deferral events with a machine-parseable message (`Rate limit deferred task {id}: key={key} slotUtc={slot:O} policy={taskType} deferredCount={n}`), aggregated at the source: first deferral per key per window, then one summary per window, so sustained throttling never becomes an event storm. Disable via `EmitDeferralEvents = false`.
- **Logs**: per-deferral details at `Debug` level.
- **Monitor.Api / Dashboard**: `ThrottledTasks` counters in the overview, `GET /api/rate-limits` (per-key parked counts and next slots), and a per-task `throttledUntil` overlay. This view is in-memory and **single-node**.
- **Terminal outcomes DO invoke `OnError`** with a typed `RateLimitRejectedException` (carrying key, computed slot and policy): horizon rejections and `Discard` drops.

## Restart Semantics

The limiter is in-memory: budgets and parked slots are lost on restart, while the tasks themselves are safe in storage (status `Queued`, covered by startup recovery).

- After a restart, recovered tasks re-extract their policy/key on re-dispatch and **buckets restart full**: a backlog can burst up to ~2× `Burst` at the external API across the restart boundary (worst case).
- Opt into `StartEmpty = true` to cap the post-restart burst: fresh buckets admit at the steady rate from the first execution. This also bounds the effect of a forward NTP clock jump.
- A restart storm over a large parked backlog costs ~2 storage round-trips per task at `AuditLevel.Full`; it is bounded by the parking-lot cap, and `AuditLevel.Minimal` removes the audit rows.

## Multi-Instance

Rate limiting is **per-instance** in this release: N instances sharing one database each enforce the limit independently (aggregate worst case ≈ N × limit). The DI registration is the seam for a future distributed limiter:

```csharp
// BEFORE AddEverTask: replaces the default in-memory limiter
services.AddSingleton<IKeyedRateLimiter, MyRedisGcraLimiter>();
```

`IKeyedRateLimiter` documents the contract invariants a distributed implementation must emulate (idempotent reservation redemption, non-decreasing slots, never blocking, wall-clock UTC slots, fail-open on infrastructure errors).

## Interactions & Edge Behavior

| Scenario | Behavior |
|---|---|
| **Other keys / other task types** | Never affected: budgets are per (task type, key); deferred tasks leave the worker immediately |
| **Multi-queue / FallbackToDefault** | The gate is per task type: a task rerouted to another queue is still throttled |
| **Cancel while parked** | The parked occurrence is dropped and never executes; status becomes `Cancelled` |
| **Same-taskKey re-dispatch while parked** | Latest payload wins and executes exactly once at the reserved slot |
| **Duplicate delivery (recovery race)** | A redelivery arriving while the same task is still executing is re-parked at a short delay (never dropped, never double-executed concurrently); handlers should stay idempotent (at-least-once contract) |
| **Slot beyond `MaxReservationHorizon`** (default 1 h) | Terminal rejection: one-shot tasks are persisted `Failed` with `RateLimitRejectedException` delivered to `OnError`; recurring occurrences are skipped, series alive. Same outcome on the first gate pass and on retry re-acquisitions |
| **`OverflowBehavior = Discard`** | No waiting, no parking: terminal `Failed` (never `Cancelled`) with the typed exception |
| **Parked tasks reach `MaxParkedTasks`** | Consumers pause (bounded) before dequeued *rate-limited* tasks of the affected queues (tasks without a policy keep flowing), so the channel fills and the native backpressure reaches producers. Safety valve, not normal operation |
| **More than `MaxTrackedKeys` buckets** | New keys fail OPEN (execute unthrottled) with a rate-limited warning and a mandatory monitoring event |
| **Limiter failure** (future distributed impl) | The gate fails OPEN: the task executes with a warning, consistent with the never-lose-a-task contract |

## Configuration Reference

Per-handler policy (frozen API):

```csharp
public override RateLimitPolicy? RateLimitPolicy =>
    new RateLimitPolicy(permits: 15, period: TimeSpan.FromMinutes(1))
    {
        Burst                 = 15,                       // ≥ 1, default = Permits
        ThrottleRetries       = true,                     // retries re-acquire budget
        StartEmpty            = false,                    // fresh buckets start full
        MaxReservationHorizon = TimeSpan.FromHours(1),    // farther slots → terminal rejection
        MaxInSlotWait         = TimeSpan.FromSeconds(1),  // nearer slots → inline wait
        OverflowBehavior      = RateLimitOverflowBehavior.WaitForCapacity // or Discard
    };
```

Global infrastructure knobs:

```csharp
builder.Services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetRateLimiterOptions(o =>
    {
        o.MaxParkedTasks     = 5000;     // default: min(5000, 2 × default-queue channel capacity)
        o.MaxTrackedKeys     = 100_000;  // key-cardinality bound (fail-open beyond)
        o.MaxKeyLength       = 256;      // longer keys are hashed
        o.EmitDeferralEvents = true;     // aggregated deferral monitoring events
    }));
```

## Best Practices

- **Size `Period`/`Permits` to the external limit, not to your throughput wishes**: the limiter shapes execution to the declared rate. If it feels slow, the constraint is the API, not EverTask.
- **Use `Burst = 1` for strict even spacing** when the external API uses small fixed windows; the default full burst can front-load up to `Permits` calls.
- **Prefer `AuditLevel.Minimal` for heavily throttled task types**: every slot-fire writes the usual `SetQueued`, which adds an audit row at `AuditLevel.Full`.
- **Keep keys low-cardinality and stable** (tenant ids, account ids). Unbounded key spaces eventually hit `MaxTrackedKeys` and fail open.
- **Don't reuse the dispatch `taskKey` as the rate-limit key**: they answer different questions (identity vs throttling).
- **Recurring + rate limit**: keep the recurrence interval ≥ the emission interval (`Period / Permits`), or occurrences will steadily accumulate.
- **Multiple app instances**: divide the external budget across instances (e.g. 2 instances → 7/min each for a 15/min API) until the distributed limiter ships.
