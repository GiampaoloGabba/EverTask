# EverTask.RateLimiting

Refer to the root CLAUDE.md for project-wide rules.

Keyed (per tenant/account/resource) rate limiting: consumer-side gate + in-memory GCRA limiter with
reservations + re-park into the in-memory scheduler. User docs: `docs/rate-limiting.md`.

`InMemoryKeyedRateLimiter` holds the GCRA budget per (task type, key) with `PersistenceId`-keyed reservations
and is registered `TryAddSingleton` — the DI seam for a distributed implementation. `RateLimitGate` makes the
dequeue-time decision (proceed / in-slot wait / re-park / terminal rejection); `RateLimitParkingLot` is the L2
bound. Policy + key are extracted in `TaskHandlerWrapperImp` (policy cached per handler type, first-wins; key
per dispatch, fail-safe) and stamped on `TaskHandlerExecutor` (memory-only, preserved by `ToLazy()` and `with`).

## HARD INVARIANTS (violating these is a design break, not a refactor)

- **A deferral writes NOTHING to storage.** The parked task stays `Queued`, already covered by all three
  synced recovery filters (EfCore/Sqlite/Memory); the only storage touch of a deferral cycle is the existing
  `SetQueued` at slot-fire re-enqueue. The ONLY sanctioned rejection-cycle writes are (1) horizon/`Discard`
  rejection persisting `Failed` for one-shot tasks and (2) the retry-path rejection of a RECURRING task
  persisting `SetQueued` (the occurrence was `InProgress`; the skip returns it to the parked status).
- **The Deferred path NEVER enters `WorkerExecutor.DoWorkCore`** — its `finally` would run
  `QueueNextOccourrence` (run-count corruption + lost occurrence). The gate sits in `DoWork`, BEFORE
  `_inFlightTasks.TryAdd` and AFTER the hoisted blacklist check (cancelled tasks must not burn tokens). The
  blacklist is RE-checked after the gate, BEFORE applying Proceed/Rejected (gate waits can take seconds and
  the per-task token does not exist yet): on Rejected this stops `Failed` clobbering the user's `Cancelled`.
  The Deferred branch returns WITHOUT consuming the blacklist entry.
- **A gated redelivery overlapping its in-flight original is re-parked, never dropped** — via
  `IRateLimitGate.ReparkInFlightRedelivery`, checked BEFORE the gate (no reservation redemption) and again on
  `TryAdd` failure; dropping it strands the only live copy until restart (one-shot stuck `Queued`, recurring
  occurrence lost). The re-park never overwrites an existing registration (`IsScheduled` latest-wins guard),
  registers the lot entry and runs the same epoch set-then-check as Defer. The L2 parking-capacity pause
  applies to gated tasks only.
- **The set-then-check cleanup signal is `IsScheduled`, not the blacklist** — when the epoch moved and the
  conditional `TryUnschedule(id, parked)` fails, `!IsScheduled(id)` means the invalidator's own unconditional
  `TryUnschedule` already removed the registration (Cancel or same-taskKey re-dispatch landed mid-re-park):
  clean up the lot entry + reservation or they leak forever. `IsScheduled(id) == true` means a newer
  registration took over and must survive.
- **Rate limiting itself changes no storage schema and no recovery filter.** `EverTaskEventData` may only
  grow `init` properties in its body: appending a positional parameter would change its primary constructor
  and `Deconstruct`, which every subscriber compiled against them depends on.
- **Never a general budget rollback**: `ReleaseAsync` is newest-only CAS at most; orphan reservations lapse
  via TTL (waste = exactly one emission interval — under-use, never violation).
- **Wall-clock UTC only** for slot math (slots are handed to the scheduler) — never a monotonic clock. The
  limiter, the gate and the parking lot all read the SAME injected `TimeProvider` the schedulers sleep on
  (P9), which is what keeps a reserved slot and the scheduler's due check on one clock.

## Re-park rules

- Unconditional `ToLazy()` (no pinned handler instance).
- One-shot: `parked with { ExecutionTime = slot }`; recurring: `Schedule(parked, nextRecurringRun: slot)` with
  `ExecutionTime` UNTOUCHED (the schedule-drift fix in `QueueNextOccourrence` depends on it).
- Floor a past slot only (`slot <= now → now + PastSlotFloor`, 100 ms); no flat clamp — it would overshoot the
  GCRA slot.
- A recurring occurrence past `RunUntil` is skipped (never fired late) through the normal next-occurrence
  path. A rejected occurrence does **NOT** consume the `MaxRuns` budget: `QueueNextOccourrence(countsAsRun:
  false)` writes no run-counter/audit (accounting: `Scheduler/Recurring/CLAUDE.md`). It skips ahead to the
  limiter's next slot (`skipAheadTo: gateResult.SlotUtc` as the skip-forward "now") instead of grinding
  occurrence by occurrence, so a too-fast cadence re-checks once per refill interval and never busy-churns.
- Set-then-check after `Schedule`: if the invalidation epoch moved, conditional `TryUnschedule(id, parked)` +
  best-effort `ReleaseAsync`.

## Retry / restart / failure semantics

- Retries (`ThrottleRetries`, default `true`) re-acquire through the gate in `ExecuteTask`'s action lambda
  BEFORE the timeout branch (budget waits must never erode the per-attempt `Timeout`); a far slot re-parks
  (attempt count restarts on redelivery) — never a retryable exception. **NEVER put this inside
  `onRetryCallback`: `LinearRetryPolicy` swallows its exceptions.**
- Restart: limiter state is in-memory → buckets restart full (~2× burst worst case at the external API);
  `StartEmpty` opts into steady-rate fresh buckets. Parked tasks recover via their `Queued` status.
- A throwing limiter (future distributed impl) fails OPEN with a warning — never-lose-a-task contract;
  `MaxTrackedKeys` overflow also fails open + mandatory monitoring event.
- Terminal outcomes (horizon, `Discard`) invoke `OnError` with a typed `RateLimitRejectedException`; plain
  deferrals have NO handler callback (observability via aggregated events, Debug logs, Monitor.Api).
  Retry-path rejections follow the SAME split: one-shot → `Failed` + `OnError`; recurring → occurrence
  skipped (status back to `Queued`, series advanced via `QueueNextOccourrence`, no callback).

## Tests

`test/EverTask.Tests/RateLimiting/KeyedRateLimiterTests.cs` + `RateLimitGateTests.cs`,
`IntegrationTests/RateLimitingIntegrationTests.cs`, `test/EverTask.Tests.Monitoring/API/RateLimitMonitoringTests.cs`.
Storage tests: **zero changes** — if a change here seems to require touching `test/EverTask.Tests.Storage/`,
stop: it's a design violation.
