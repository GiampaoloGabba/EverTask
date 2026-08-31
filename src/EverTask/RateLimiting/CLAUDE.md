# EverTask.RateLimiting

Refer to the root CLAUDE.md for project-wide rules.

Keyed (per tenant/account/resource) rate limiting: consumer-side gate + in-memory GCRA limiter with reservations +
re-park into the in-memory scheduler. User docs: `docs/rate-limiting.md`.

`InMemoryKeyedRateLimiter` holds the GCRA budget per (task type, key) with `PersistenceId`-keyed reservations,
registered `TryAddSingleton` — the DI seam for a distributed implementation. `RateLimitGate` makes the dequeue-time
decision (proceed / in-slot wait / re-park / terminal rejection); `RateLimitParkingLot` is the second-level capacity
bound. Policy + key are extracted in `TaskHandlerWrapperImp` (policy cached per handler type, first-wins; key per
dispatch, fail-safe) and stamped on `TaskHandlerExecutor` (memory-only, preserved by `ToLazy()` and `with`).

## HARD INVARIANTS (violating these is a design break, not a refactor)

- **A deferral writes NOTHING to storage.** The parked task stays `Queued`, already covered by all three synced
  recovery filters; the only storage touch of a deferral cycle is the existing `SetQueued` at slot-fire re-enqueue.
  The ONLY sanctioned rejection-cycle writes are (1) horizon/`Discard` persisting `Failed` for one-shot tasks and (2)
  the retry-path rejection of a RECURRING task persisting `SetQueued` (the occurrence was `InProgress`; the skip
  returns it to the parked status).
- **The Deferred path NEVER enters `WorkerExecutor.DoWorkCore`** — its `finally` would run `QueueNextOccourrence`
  (run-count corruption + lost occurrence). The gate sits in `DoWork`, BEFORE `_inFlightTasks.TryAdd` and AFTER the
  hoisted blacklist check (a cancelled task must not burn tokens). The blacklist is RE-checked after the gate and
  BEFORE applying Proceed/Rejected, since gate waits take seconds and the per-task token does not exist yet: on
  Rejected that stops `Failed` clobbering a user's `Cancelled`. The Deferred branch returns WITHOUT consuming the
  entry.
- **A gated redelivery overlapping its in-flight original is re-parked, never dropped** — via
  `IRateLimitGate.ReparkInFlightRedelivery`, checked BEFORE the gate (no reservation redemption) and again on `TryAdd`
  failure; dropping it strands the only live copy until restart. The re-park never overwrites an existing registration
  (`IsScheduled` latest-wins), registers the lot entry and runs the same epoch set-then-check as Defer. The
  parking-capacity pause applies to gated tasks only.
- **The set-then-check cleanup signal is `IsScheduled`, not the blacklist**: when the epoch moved and the conditional
  `TryUnschedule(id, parked)` fails, `!IsScheduled(id)` means the invalidator's own unconditional `TryUnschedule`
  already removed the registration (a Cancel or same-taskKey re-dispatch landed mid-re-park) — clean up the lot entry
  + reservation or they leak forever. `IsScheduled(id) == true` means a newer registration took over and must survive.
- **Both re-parks go through `IScheduler.TrySchedule`**, which refuses to replace a registration carrying a NEWER
  `ScheduleVersion`. That is what keeps the guard above true: `TaskScheduleManager` moves the epoch and RE-PARKS
  instead of unscheduling, so a deferral landing after it would otherwise replace the reschedule's registration and
  then delete the series' only one. A refused re-park owns no registration, so the deferral drops the lot entry and
  the reservation with it and returns `Deferred` with no event: the delivery is dead, the newer registration carries
  the series.
- **A DURABLE schedule row never reaches the gate, and never carries a policy.** It runs no handler — its slot firing
  means "materialize what is due" — so `DoWorkGuarded` routes it to the materializer right after the blacklist check
  and BEFORE the gate, and `ExtractRateLimit` returns `(null, null)` for a durable definition (the "recurrence faster
  than the limiter" warning goes with it). Taking the gate would spend the key's budget on a row that executes nothing
  and starve the occurrences it produces. The limit applies to the OCCURRENCES, one by one, per key, each an ordinary
  one-shot delivery under every rule above.
- **Rate limiting itself changes no storage schema and no recovery filter.** `EverTaskEventData` may only grow `init`
  properties in its body: appending a positional parameter would change its primary constructor and `Deconstruct`,
  which every subscriber compiled against them depends on.
- **Never a general budget rollback**: `ReleaseAsync` is newest-only CAS at most; orphan reservations lapse via TTL
  (waste = exactly one emission interval — under-use, never violation).
- **Wall-clock UTC only** for slot math (slots are handed to the scheduler) — never a monotonic clock. The limiter,
  the gate and the parking lot all read the SAME injected `TimeProvider` the schedulers sleep on, which keeps a
  reserved slot and the scheduler's due check on one clock.

## Re-park rules

- Unconditional `ToLazy()` (no pinned handler instance).
- One-shot: `ReparkOneShot` (`ExecutionTime = slot`, the original kept in `NominalSlotUtc` and
  `ExecutionTimeIsReservedSlot` set — the execution context and the monitoring event must keep reporting the slot the
  task was SCHEDULED for, not the one the limiter moved it to). Recurring: `Schedule(parked, nextRecurringRun: slot)`
  with `ExecutionTime` UNTOUCHED (the schedule-drift fix in `QueueNextOccourrence` depends on it).
- The kept slot is read through `NominalSlotOfDelivery`, never `ExecutionTime` directly: on a SECOND re-park (the
  first reservation evicted or expired) `ExecutionTime` already holds the previous reserved slot, and an immediate
  dispatch — which must stay slotless — would inherit it.
- Floor a past slot only (`slot <= now → now + PastSlotFloor`, 100 ms); no flat clamp, which would overshoot the GCRA
  slot.
- A recurring occurrence past `RunUntil` is skipped (never fired late) through the normal next-occurrence path. A
  rejected occurrence does **NOT** consume the `MaxRuns` budget: `QueueNextOccourrence(countsAsRun: false)` writes no
  run counter and no audit (accounting: `Scheduler/Recurring/CLAUDE.md`). It skips ahead to the limiter's next slot
  (`skipAheadTo: gateResult.SlotUtc`) instead of grinding occurrence by occurrence, so a too-fast cadence re-checks
  once per refill interval.
- Set-then-check after `Schedule`: if the invalidation epoch moved, conditional `TryUnschedule(id, parked)` +
  best-effort `ReleaseAsync`.

## Retry / restart / failure semantics

- Retries (`ThrottleRetries`, default `true`) re-acquire through the gate in `ExecuteTask`'s action lambda BEFORE the
  timeout branch (budget waits must never erode the per-attempt `Timeout`); a far slot re-parks (attempt count
  restarts on redelivery) — never a retryable exception. **NEVER put this inside `onRetryCallback`:
  `LinearRetryPolicy` swallows its exceptions.** The action commits the attempt number only AFTER the gate admits it,
  so a retry turned back keeps `Context.Attempt` on the previous attempt.
- Restart: limiter state is in-memory, so buckets restart full (~2× burst worst case at the external API);
  `StartEmpty` opts into steady-rate fresh buckets. Parked tasks recover via their `Queued` status.
- A throwing limiter (a future distributed implementation) fails OPEN with a warning — never-lose-a-task contract;
  `MaxTrackedKeys` overflow also fails open, with a mandatory monitoring event.
- Terminal outcomes (horizon, `Discard`) invoke `OnError` with a typed `RateLimitRejectedException`; plain deferrals
  have NO handler callback (observability via aggregated events, Debug logs, Monitor.Api). Retry-path rejections
  follow the SAME split: one-shot → `Failed` + `OnError`; recurring → occurrence skipped (`Queued` again, series
  advanced, no callback).
- That `OnError` runs OUTSIDE `DoWorkCore`, so `HandleRateLimitRejectionAsync` injects the execution context and the
  log capture itself and publishes the context on the ambient accessor — `Context` and `Logger` are promised in every
  callback, and without it both fail into the worker's generic callback-failure event while the user's compensation
  never runs. The capture is not persisted: `Failed` stays the only write of the cycle. For the same reason it
  releases the executor's owned eager scope itself, since `DoWorkCore`'s finally never runs here; the recurring branch
  releases BEFORE `QueueNextOccourrence`, or the series schedules its next occurrence while the dead executor's scope
  is still alive, and the method's own `finally` covers the one-shot branch.
- **Every gated exit releases the delivery's owned eager scope** — the deferral, both in-flight re-parks and the
  post-gate blacklist drop continue through a `ToLazy()` copy that drops the scope, so the executor they were handed
  is its last owner. None of them releases it itself: `WorkerExecutor.DoWork`'s `finally` does, once per delivery,
  whatever exit it took.

## Tests

`test/EverTask.Tests/RateLimiting/{KeyedRateLimiterTests,RateLimitGateTests}.cs`,
`IntegrationTests/RateLimitingIntegrationTests.cs`, `test/EverTask.Tests.Monitoring/API/RateLimitMonitoringTests.cs`,
and `IntegrationTests/EagerHandlerScopeReleaseTests.cs` for the eager scope of a deferred, re-parked or rejected
delivery, release ORDER included. Storage tests: **zero changes** — if a change here seems to require touching
`test/EverTask.Tests.Storage/`, stop: it is a design violation.
