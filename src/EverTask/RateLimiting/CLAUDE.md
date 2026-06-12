# EverTask.RateLimiting

## Purpose

Keyed (per tenant/account/resource) rate limiting for task execution: consumer-side gate +
in-memory GCRA limiter with reservations + re-park into the in-memory scheduler. User docs:
`docs/rate-limiting.md`. Design source: `.claude/tasks/keyed-rate-limiting-implementation.md`.

## Components

| Component | Responsibility |
|-----------|----------------|
| `IKeyedRateLimiter` / `InMemoryKeyedRateLimiter` | GCRA budget per (task type, key); PersistenceId-keyed reservations; DI seam for a distributed impl (`TryAddSingleton`) |
| `IRateLimitGate` / `RateLimitGate` | Dequeue-time decision: proceed / in-slot wait / re-park / terminal rejection; deferral event aggregation; parking-lot backpressure wait |
| `RateLimitParkingLot` | L2 bound: distinct parked tasks, per-queue counts, enqueue-notification decrement, snapshot |
| `GateInvalidationRegistry` | Epochs closing the dequeue→re-park limbo window (Cancel / same-taskKey re-dispatch) |
| `IRateLimiterIntrospection` | Single-node monitoring view (ThrottledTasks, /api/rate-limits, throttledUntil) |
| `RateLimiterOptions` | Global knobs (`SetRateLimiterOptions`): MaxParkedTasks, MaxTrackedKeys, MaxKeyLength, EmitDeferralEvents |

Policy + key are extracted in `TaskHandlerWrapperImp` (policy cached per handler type,
first-wins; key per dispatch, fail-safe) and stamped on `TaskHandlerExecutor`
(`RateLimitPolicy`/`RateLimitKey`, memory-only, preserved by `ToLazy()` and `with`).

## HARD INVARIANTS (violating these is a design break, not a refactor)

- **A deferral writes NOTHING to storage.** The parked task's status stays `Queued`, which is
  already covered by all three synced recovery filters (EfCore/Sqlite/Memory). The only storage
  touch of a deferral cycle is the existing `SetQueued` at slot-fire re-enqueue. The ONLY
  sanctioned rejection-cycle writes are: (1) horizon/Discard rejection persisting `Failed` for
  one-shot tasks; (2) the retry-path rejection of a RECURRING task persisting `SetQueued`
  (the occurrence was `InProgress`; the skip returns it to the parked-occurrence status).
- **The Deferred path NEVER enters `WorkerExecutor.DoWorkCore`** — its `finally` would run
  `QueueNextOccourrence` (run-count corruption + lost occurrence). Gate sits in `DoWork`,
  BEFORE `_inFlightTasks.TryAdd`, AFTER the hoisted blacklist check (cancelled tasks must not
  burn tokens). The blacklist is RE-checked after the gate, BEFORE applying the
  Proceed/Rejected outcomes (gate waits can take seconds and the per-task token does not exist
  yet): on Rejected this prevents clobbering the user's `Cancelled` status with `Failed`. The
  Deferred branch returns WITHOUT consuming the blacklist (the parked occurrence is discarded
  by the entry check at redelivery).
- **A gated redelivery overlapping its in-flight original is re-parked, never dropped** — via
  `IRateLimitGate.ReparkInFlightRedelivery`, checked BEFORE the gate (no reservation
  redemption) and again on `TryAdd` failure; dropping it would strand the only live copy until
  restart (one-shot stuck `Queued`, recurring occurrence lost). The re-park never overwrites an
  existing registration (`IsScheduled` latest-wins guard), registers the parking-lot entry, and
  runs the same epoch set-then-check as Defer. The L2 parking-capacity pause applies to gated
  tasks only.
- **Set-then-check cleanup signal is `IsScheduled`, not the blacklist** — when the epoch moved
  and the conditional `TryUnschedule(id, parked)` fails, `!IsScheduled(id)` means the
  invalidator's own unconditional TryUnschedule already removed the registration (Cancel or
  same-taskKey re-dispatch landed mid-re-park): clean up lot entry + reservation or they leak
  forever. `IsScheduled(id) == true` means a newer registration took over and must survive.
- **No storage schema changes, no recovery-filter changes, no `EverTaskEventData` changes**
  (positional record, binary compat).
- **Never a general budget rollback**: `ReleaseAsync` is newest-only CAS at most; orphan
  reservations lapse via TTL (waste = exactly one emission interval — under-use, never violation).
- **Wall-clock UTC only** for slot math (slots are handed to the scheduler); the injectable
  `TimeProvider` is for unit tests only.

## Re-park rules

- Unconditional `ToLazy()` (no pinned handler instance).
- One-shot: `parked with { ExecutionTime = slot }`; recurring: `Schedule(parked,
  nextRecurringRun: slot)` with `ExecutionTime` UNTOUCHED (the schedule-drift fix in
  `QueueNextOccourrence` depends on it).
- Floor a past slot only (`slot <= now → now + PastSlotFloor`); no flat clamp (it would
  overshoot the GCRA slot).
- Recurring occurrence past `RunUntil` → skipped (never fired late), routed through the normal
  next-occurrence path (skip counts toward `MaxRuns`, same as downtime).
- Set-then-check after `Schedule`: if the invalidation epoch moved, conditional
  `TryUnschedule(id, parked)` + best-effort `ReleaseAsync`.

## Retry / restart / failure semantics

- Retries (`ThrottleRetries`, default on) re-acquire through the gate in `ExecuteTask`'s action
  lambda BEFORE the timeout branch (budget waits never erode the per-attempt `Timeout`); a far
  slot re-parks (attempt count restarts on redelivery) — never a retryable exception. NEVER put
  this inside `onRetryCallback` (LinearRetryPolicy swallows its exceptions).
- Restart: limiter state is in-memory → buckets restart full (~2× burst worst case at the
  external API, documented); `StartEmpty` opts into steady-rate fresh buckets. Parked tasks are
  recovered via their `Queued` status.
- Fail policy: a throwing limiter (future distributed impl) fails OPEN with a warning —
  never-lose-a-task contract. `MaxTrackedKeys` overflow also fails open + mandatory monitoring
  event.
- Terminal outcomes (horizon, Discard) invoke the existing `OnError` with a typed
  `RateLimitRejectedException`; plain deferrals have NO handler callback (infrastructure
  routing — observability via aggregated events/Debug logs/Monitor.Api). Retry-path rejections
  follow the SAME split: one-shot → `Failed` + `OnError`; recurring → occurrence skipped
  (status back to `Queued`, series advanced via `QueueNextOccourrence`, no callback).

## 🔗 Test Coverage

- `test/EverTask.Tests/RateLimiting/KeyedRateLimiterTests.cs` — GCRA math, fake clock, zero sleep
- `test/EverTask.Tests/RateLimiting/RateLimitGateTests.cs` — gate mechanics, no-storage-write, wrapper extraction
- `test/EverTask.Tests/IntegrationTests/RateLimitingIntegrationTests.cs` — end-to-end (no-HOL, restart, cancel, taskKey, flood, retry, recurring, bounds)
- `test/EverTask.Tests.Monitoring/API/RateLimitMonitoringTests.cs` — dashboard counters, /api/rate-limits, overlay
- Storage tests: **zero changes** — if a change here seems to require touching
  `test/EverTask.Tests.Storage/`, stop: it's a design violation.
