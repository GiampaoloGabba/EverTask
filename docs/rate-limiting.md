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
            Burst           = 15,    // how many may run back-to-back from saved-up budget (default: same as Permits)
            ThrottleRetries = true,  // retry attempts consume budget too (default)
            StartEmpty      = false  // a new key starts with the full Burst already saved up (default)
        };

    public override async Task Handle(SyncTenantData task, CancellationToken ct)
    {
        await _externalApi.SyncAsync(task.TenantId, ct);
    }
}
```

That's it. No configuration-time registration: tasks of other types, or tasks of this type without a key, skip the gate entirely.

### What the options mean

`Permits` and `Period` set the *average* rate: 15 per minute means one execution every 4 seconds on average (`Period / Permits`, the *emission interval*). The limiter does not count executions in fixed one-minute windows.

> **The mental model: a self-refilling token jar**
>
> Each key owns a jar of *execution tokens*. The jar refills by itself, continuously, at the steady rate: one token every 4 seconds (`Period / Permits`). Every task that executes spends one token; when the jar is empty, the next task waits for the next token to drip in. The three options are three questions about the jar:
>
> - `Burst`: *how many tokens does the jar hold?* That cap is how many tasks can run back-to-back when work arrives after a quiet stretch, because an idle key keeps collecting the dripping tokens until the jar is full.
> - `ThrottleRetries`: *do retries also pay a token?*
> - `StartEmpty`: *does a brand-new jar come full or empty?* A jar is brand new the first time its key is seen, and again after every process restart: the limiter is in-memory and forgets all jars.
>
> `Burst` never changes the average rate (the jar always refills at one token per 4 seconds); it only changes how bunched up executions can be.

What that looks like on a timeline, with 20 tasks arriving at once for a key that has been idle for a while:

```text
Burst = 15 (default = Permits): the jar was full, 15 tokens ready

  tasks  1..15 ───────────► run immediately, back-to-back
  task   16 ─ 4s ─► 17 ─ 4s ─► 18 ─ 4s ─► ...   steady pace from here on

Burst = 1: the jar holds a single token, no matter how long the key was idle

  task   1 ─ 4s ─► 2 ─ 4s ─► 3 ─ 4s ─► ...      strictly one every 4 s, never two in a row
```

And what `StartEmpty` changes across a restart while a backlog is waiting:

```text
StartEmpty = false (default):
  ...15 calls burst → ✖ restart ✖ → jar reborn FULL  → 15 more immediately
                                                       (worst case ≈ 2 × Burst at the external API)

StartEmpty = true:
  ...steady pace    → ✖ restart ✖ → jar reborn EMPTY → first task now, then steady pace
```

In detail:

- **`Burst`** is the cap on how much budget a key can save up while it sits idle, which makes it the number of tasks that may execute immediately, back-to-back, when work arrives after a quiet stretch. The default is the same value as `Permits` (here 15): a tenant that has been quiet for a minute or more can fire all 15 calls at once, and only then is paced down to one every 4 seconds. `Burst = 1` is the opposite extreme: no budget ever accumulates, so executions are strictly spaced 4 seconds apart no matter how long the key was idle. Values in between trade burstiness for smoothness (`Burst = 5`: at most 5 back-to-back, then steady pacing).

- **`ThrottleRetries`** decides whether retry attempts also consume budget. Default `true`: if the task failed while calling the rate-limited API, its retries hit the same API, so they must respect the same limit. Set it to `false` only when retries don't touch the limited resource (e.g. retrying a cheap local failure).

- **`StartEmpty`** decides how much saved-up budget a brand-new key begins with. Default `false`: a new key starts with the full `Burst` already saved up, so its first 15 tasks run immediately. With `true`, a new key starts with nothing saved: the first task runs immediately, but every following one is paced at the steady 4-second interval from the start. Use `StartEmpty = true` when a restart must not cause a spike of calls at the external API (see [Restart Semantics](#restart-semantics)).

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

- **GCRA math**: the steady emission interval is `T = Period / Permits`. The budget refills continuously at one execution per `T`; `Burst` caps how much unused budget a key can accumulate while idle, i.e. how many executions may happen back-to-back before the steady pace kicks in. With `Burst = 1` no budget accumulates and executions are strictly evenly spaced `T` apart.
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

- After a restart, recovered tasks re-extract their policy/key on re-dispatch and **buckets restart full**: every key starts again with the whole `Burst` saved up. Worst case, a backlog can burst up to ~2× `Burst` at the external API across the restart boundary (full burst spent just before the restart + a fresh full burst right after).
- Opt into `StartEmpty = true` to cap the post-restart burst: a fresh bucket starts with no saved-up budget, so the first execution runs immediately and every subsequent one is paced at the steady interval (`Period / Permits`). This also bounds the effect of a forward NTP clock jump.
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
        Burst                 = 15,                       // max back-to-back executions (≥ 1, default: same as Permits)
        ThrottleRetries       = true,                     // retry attempts consume budget too (default)
        StartEmpty            = false,                    // new keys start with the full Burst saved up (default)
        MaxReservationHorizon = TimeSpan.FromHours(1),    // farther slots → terminal rejection
        MaxInSlotWait         = TimeSpan.FromSeconds(1),  // nearer slots → inline wait
        OverflowBehavior      = RateLimitOverflowBehavior.WaitForCapacity // or Discard
    };
```

| Option | Default | What it does |
|---|---|---|
| `Permits` / `Period` | — (constructor) | The average rate: at most `Permits` executions per `Period` per key. Steady emission interval = `Period / Permits`. |
| `Burst` | same value as `Permits` | Cap on the budget a key can save up while idle = max executions that may run back-to-back after a quiet stretch. `Permits` (default): the whole per-period budget can be spent at once; `1`: strictly even spacing, never two in a row. Does not change the average rate, only how bunched executions may be. |
| `ThrottleRetries` | `true` | Retry attempts re-acquire budget before running, so retries of a rate-limited API call respect the same limit. `false`: retries bypass the limiter. |
| `StartEmpty` | `false` | Budget of a brand-new bucket (first time a key is seen, or any key after a restart, since the limiter is in-memory). `false`: starts with the full `Burst` saved up, so the first `Burst` tasks run immediately. `true`: starts with nothing saved, so after the first task the steady pacing applies right away; this caps the post-restart spike. |
| `MaxReservationHorizon` | 1 hour | If the next available slot for a key is farther away than this, the task is rejected instead of parked: one-shot tasks fail with `RateLimitRejectedException` (delivered to `OnError`), recurring occurrences are skipped. |
| `MaxInSlotWait` | 1 second | If the reserved slot is at most this close, the consumer waits inline instead of re-parking through the scheduler. |
| `OverflowBehavior` | `WaitForCapacity` | What happens when a key has no budget: `WaitForCapacity` reserves a slot and defers; `Discard` fails the task immediately (terminal `Failed`, typed exception to `OnError`). |

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
