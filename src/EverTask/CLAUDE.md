# EverTask Core

Refer to the root CLAUDE.md for project-wide rules.

Dispatcher, worker executor, bounded queue, schedulers (`PeriodicTimerScheduler` + `ShardedScheduler`) and the
in-memory storage.

## Gotchas

- **Cancellation blacklist**: `ITaskDispatcher.Cancel(Guid)` adds to `Worker/WorkerBlacklist.cs` (entries lapse
  after `EntryTtl` ≈ 1 h, so cancelled parked tasks don't leak them). `WorkerExecutor.DoWork` checks it BEFORE
  the rate-limit gate — cancelled tasks must not burn tokens. `Cancel` also calls `IScheduler.TryUnschedule`,
  bumps the gate invalidation epoch and releases the parking-lot entry.
  - **A re-dispatch under the same taskKey UNDOES a cancellation** (`Dispatcher.RestoreCancelledSchedule`),
    because that is the documented way to restart a cancelled schedule and a RECURRING re-registration reuses
    the row. Both halves of the cancel would otherwise follow the id: the blacklist entry, which makes
    `WorkerQueue` drop every delivery the new registration produces while the registration is consumed all
    the same, and the `Cancelled` status, which `UpdateTask` never rewrites and no recovery predicate
    selects — so a restart before the first slot lost the series for good. The row goes back to
    `WaitingQueue` (where a brand new dispatch leaves it) AFTER the new definition is written, and the
    transition is audited. A one-shot needs none of this: a terminal row is removed and recreated under a new
    id.
- **Immediate dispatches are LAZY**: the wrapper resolves a short-lived metadata handler in a disposable scope
  and the worker resolves the executing instance in its per-task scope. Never resolve an eager transient
  handler from the dispatcher's root provider — it pins `IAsyncDisposable` instances until shutdown.
- **Recurring auto-rescheduling**: after a run `WorkerExecutor` computes the next occurrence and updates
  storage; `MaxRuns` counts real executions only — see `Scheduler/Recurring/CLAUDE.md`.
- **Rate-limit gate** (handlers declaring a `RateLimitPolicy`): all invariants, re-park rules and
  retry/restart semantics live in `RateLimiting/CLAUDE.md`.
- **The delivery's execution context is built ONCE per delivery** in `DoWorkCore`, right after the log
  capture and BEFORE `OnStarted`: pushed to the handler through the cached injector and to the ambient
  `AmbientTaskExecutionContextAccessor` (static `AsyncLocal`, cleared at the end of the delivery's `finally`
  and again in `DoWork`'s, which covers a fully synchronous delivery whose post-execution step threw). The
  slot it reports is `TaskHandlerExecutor.NominalSlotOfDelivery`, never `ExecutionTime`: the rate-limit gate
  overwrites the latter with its reserved slot for one-shots (and flags it), so reading it would tell a
  deferred task it had been scheduled for the moment it finally ran. `RunNumber` travels on the executor
  (`CurrentRunCount + 1`, stamped by the dispatcher and advanced by `QueueNextOccourrence`) because the
  counter only moves after a run — reading storage here would cost a round-trip AND report one run too few.
  `RunNumber` **and `ScheduleVersion` are preserved by a taskKey re-registration**, whether or not the row
  still has a cursor: a terminal series re-registered under its key resumes at `CurrentRunCount + 1`, which
  is where storage resumes too, and the version travels on the row metadata because `UpdateTask` never
  rewrites that column — a rescheduled series that reported version 0 again would say so to the handler and
  to every monitoring event of the delivery. An OCCURRENCE answers slot and run number from its own row
  instead (C1/C4): both are read out of `RuntimeInfo` by `RecoveredTaskFactory` and travel as
  `NominalSlotUtc` / `RunNumber`, because a child is a one-shot — its `CurrentRunCount` is zero, and deriving
  the number there would report every occurrence of every series as run 1. A delivery that arrives with
  neither stamp — an occurrence handed to the scheduler the moment it was materialized — reads them back out
  of the row's own `RuntimeInfo` through `TaskHandlerExecutor.RowOccurrence`, so the answer is the row's
  either way and an overdue catch-up never reports the moment it was fired at as its slot. The **time zone**
  travels the same way, and for a stronger reason: an occurrence is dispatched with `recurring: null`, so
  there is no definition on the delivery to read it from at all. `OccurrenceMaterializer` stamps the
  schedule's `TimeZoneId` into `RuntimeInfo` and `TaskExecutionContext` falls back to it, which is what keeps
  `TimeZoneId` and `ScheduledAtLocal` from being null on exactly the schedules that are anchored to a wall
  clock (C1/T13). Copied, not looked up on the parent: no round-trip on the delivery path, and a later
  reschedule cannot rewrite what an occurrence already meant.
- **The terminal rate-limit rejection gets the same two injections** (`HandleRateLimitRejectionAsync`):
  it is the one callback path that never enters `DoWorkCore`, and `OnError` is documented to always read
  `Context` and `Logger`. Its log capture is deliberately NOT persisted — the `Failed` status is the only
  storage write a rejection cycle may do. Never entering `DoWorkCore` also means its `finally` never runs,
  so the rejection method releases the executor's **owned eager scope** itself, on BOTH branches, before
  the recurring one schedules the next occurrence.
- **The owned eager scope is released ONCE per delivery, on every exit** (`EagerHandlerOwnership`, the same
  principle as the single `End`): `DoWorkCore` and the terminal rejection keep their ORDERED release —
  both must run before the next occurrence is scheduled — and `DoWork`'s `finally` covers every other exit
  of `DoWorkGuarded`, which for those two is a no-op. Without it the exits that reach neither (both
  blacklist drops, the rate-limit deferral, the in-flight re-park, the duplicate-delivery skip, a gate wait
  cancelled by shutdown) strand one scope, and every scoped dependency inside it, per dropped delivery: the
  executor is dead on all of them, because whatever they continue into is a `ToLazy()` copy that drops the
  scope. Never enumerate those paths one by one — that enumeration is exactly what kept missing them.
- **The ENQUEUE boundary is the other half of that rule** (`Worker/DroppedDelivery`): a delivery the queue
  refuses never becomes one, so `DoWork`'s finally is never entered and the same scope is stranded — and
  these are not edge cases. Startup recovery racing a live dispatch is the race the delivery registry exists
  to stop, and it hands `WorkerQueue` one refused eager executor every time it happens; the same goes for a
  cancel landing between dispatch and enqueue, a row that terminally finished since it was read, and a Drop*
  eviction. **Only a TERMINAL drop releases**: `QueueFull` and `DuplicateInProcess` come back out of
  `TryQueueCore` untouched because both schedulers re-park and retry the very same instance, and disposing
  there would run the next delivery on a disposed handler. Whoever turns one of those into a verdict releases
  it instead — `WorkerQueueManager.TryEnqueue`, which reports `DuplicateInProcess` as idempotent success and
  throws on `QueueFull`. Exception paths never release: the schedulers read them as a full queue.
- **`Attempt` moves only when an attempt is admitted INTO the handler**, and is rolled back to it when
  `ExecuteTask` unwinds: `OnRetry` publishes the attempt about to start, and that retry can still be
  abandoned — a cancel inside the callback or during the delay, or the `ThrottleRetries` gate turning it
  back before the handler — so `OnError` would otherwise report an attempt that never ran.
- **Monitoring events go through `WorkerExecutor.RegisterEvent` only** (one gate: log template + rendered
  `Message` + publish). Never log and publish by hand, and never call the `SkipEnabledCheck` methods of
  `WorkerExecutorLog` from outside that gate — that is how the rendered-string-as-template bug (#32) comes back.

## Queue & Recovery Resilience (no task loss, no deadlock)

**Lifecycle invariant**: every persisted task is executed or left in a status `RetrievePending` recovers. A
recovery page is the UNION of two canonical predicates on `QueuedTask`: `IsRecoverableForExecution(now)` (rows
with work left) and `IsRecurringSeriesToFinalize()` (a series whose remaining slots all fall past `RunUntil`,
or whose run budget is spent — Completed with the cursor cleared, no handler, no run counted). The execution
predicate has three server-side copies that must **stay in sync** — `EfCoreTaskStorage`, the inline list in
`SqliteTaskStorage.RetrievePending`, and the Postgres partial index `IX_QueuedTasks_Recovery` (a new status
needs a NEW migration): the four-places rule in `src/Storage/EverTask.Storage.EfCore/CLAUDE.md`.
`MemoryTaskStorage` keeps no copy — it calls the predicates directly. `TrySetQueuedIfRecoverable` applies ONLY
the execution predicate: a spent series must be finalized, never handed back to a worker queue.

- **The temporal term is grouped, deliberately** (X3): status and `MaxRuns` stay ANDed in front, and
  `RunUntil` gains `|| (IsRecurring && NextRunUtc != null && NextRunUtc < RunUntil)`. Without it the
  occurrence a series had already scheduled before a boundary that elapsed *during* the downtime is silently
  lost, and the row stays `Queued` forever.
- **The recovery grace window asks the NATURAL successor** (`RecurringTask.NextGridOccurrenceAfter`, bounds
  ignored), never `IsOccurrenceStillCurrent`: the bounded successor is null both when the slot is still
  current and when the series simply ended, and reading that null as the former executes a months-old slot at
  restart. Order in the dispatcher's recovery branch: finalize → grace → skip-forward. **Both finalization
  sites are conditional** wherever the storage can be — `WorkerService` for category (ii) and the dispatcher's
  own "exhausted series" branch — through `TrySetRecurringSeriesCompleted`, compare-and-swapped on the cursor,
  status and version **of the row the decision was computed from** (the recovery page's, carried to the
  dispatcher in `DispatchRowMetadata`). Never read them back at write time: `SetStatus` leaves a cancellation's
  cursor and version untouched, so a fresh read hands `Cancelled` to the guard as its own expectation and the
  cancellation is overwritten with `Completed`. Both sites check `SupportsScheduleVersioning` first — a storage
  without the compare-and-swap keeps the historical unconditional write, since letting the member's
  `NotSupportedException` reach the recovery counts a normal end of series as an L18 failure and poisons the
  row. The L18 counter reset runs OUTSIDE the try that guards the terminal write: the write is already
  committed, and a failing reset must not be reclassified as a failing finalization.
- **A poison is REPORTED only once the ROW confirms it** (`WorkerService.TryPoisonAsync`, every poison site
  including the finalization's). Both writes are best effort — `SetStatus` and `SetRecurringTaskPoisoned` log
  their own failed write and return on every relational provider — so returning normally says nothing, and the
  row is re-read and measured against BOTH recovery predicates before the summary counts a permanent failure.
  Counting a swallowed write as a terminalization declared progress a restart had not made, on the one line an
  operator reads, for a row that was still recoverable and repeated the same cycle at the next restart: it is
  the transient failure it really is. A write that THROWS is contained there too — a custom storage inherits
  the interface's non-swallowing default — because it is the same answer for the row, and letting it out
  aborted the whole wave over one unusable row. Pinned by `RecoveryPoisonOutcomeTests.cs`, which swallows and
  throws the poison over a real `MemoryTaskStorage`.
- **Startup order matters**: `WorkerService.ExecuteAsync` starts consumers **first**, then runs recovery
  **concurrently** (`RunRecoveryAsync`). Recover-before-consume reintroduces the capacity deadlock.
- **Recovery runs in two waves** (`RecoverWaveAsync`), across the WHOLE recovered set and not page by page:
  ordinary rows are recovered as each page arrives, durable schedule rows are skipped and recovered after the
  pagination loop. A schedule row asks how many of its occurrences are still active the moment it is back, and
  answering that while an occurrence of its own is still sitting in a later page reads a phantom low count.
  **The second wave is a SECOND KEYSET SCAN, not a buffer** (R8): the same recovery page query over the same
  cutoff, keeping only the durable schedules, so a recovery holds a bounded number of pages whatever the
  backlog is — buffering rows kept a payload and a definition alive per schedule, buffering ids kept a list
  that still grew with them. Two scalars cross the loop instead: whether the first pass saw a durable schedule at all (a host with
  none never pays for the second scan) and the keyset position just BEFORE the first one, so the scan starts
  where the durable schedules start. A row the first pass already recovered reappears in the second scan and
  is filtered out.
  `RecoveredTaskFactory.FromRow` rebuilds each row once (payload, validated schedule, audit level, occurrence
  metadata) and is the single place that mapping lives; its `RowMetadata` travels to the re-dispatch through
  the internal `ExecuteDispatch` overload, so a recovered executor keeps its parent, occurrence JSON, schedule
  version and STORED queue instead of re-deriving them from the handler. It is also where the occurrence half
  of `RuntimeInfo` is PARSED (`OccurrenceRuntimeInfo`, once per row, never on a schedule row): the durable slot
  and run number a child carries. Unreadable JSON there is not an error — the columns answer instead, exactly
  as they do for a row written before the metadata existed.
- **The reader does not wait for the page it handed over** (#39, `WavePipeline`, both passes): a wave is
  started and the next page is read while it runs, up to `MaxRecoveryPagesInFlight`. Awaiting each page in
  turn meant one slow delivery — a blocking enqueue toward a saturated queue — held the NEXT page too, and
  with it the recovery of rows belonging to queues that are completely idle: the per-queue fan-out inside a
  wave only ever protected them from each other WITHIN a page. A slot is freed by whichever wave finishes
  FIRST, never by the oldest, or one wedged page would stop the pipeline exactly as the old await did; the
  gate sits BEFORE the read, so a page is fetched only when there is a slot to recover it in. The cap is what
  the memory claim above rests on, and it multiplies the concurrent re-dispatches (pages × queues ×
  `MaxDegreeOfParallelism`); at ONE it is byte-for-byte the old behaviour, which is how the pinning test
  reproduces the bug. **Nothing about the M7 barrier moves**: the pipeline is DRAINED before the second wave
  starts — not merely emptied of the waves that happen to have finished — and the reader still decides the
  durable count and the second scan's keyset position itself, in page order. A wave that FAILS ends the
  recovery as it always did, after the waves still running have settled rather than been abandoned: one left
  behind would go on re-dispatching rows past the summary line, and its own failure would surface as an
  unobserved task exception.
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

## Deterministic scheduling clock (P9)

ONE `TimeProvider`, registered by `AddEverTask` with `TryAddSingleton(TimeProvider.System)`, governs every
scheduling decision: dispatcher, `IScheduleEvaluator`, builders, both schedulers, recovery, the rate limiter,
the gate and the parking lot. Register a provider before `AddEverTask` and the whole pipeline follows it.

- **Storage never resolves the clock**: the core always calls the `nowUtc` overloads of `RetrievePending` /
  `TrySetQueuedIfRecoverable`. Both are default members delegating to the intact legacy signatures, so a
  custom storage keeps its own override (and its own atomicity) at the cost of reading the real clock. A
  provider deriving from `EfCoreTaskStorage` gets the same hand-back — see that module's CLAUDE.md.
- **The schedulers race the signal against `Task.Delay(delay, timeProvider)`** instead of
  `SemaphoreSlim.WaitAsync(timeout)`, whose timeout is hard-wired to the real clock. The signal waiter is
  CREATED ONCE and kept across iterations when the delay wins — abandoning it would let it silently consume
  the next `Release` and the scheduler would miss a wake-up.
- **Out of scope, by design**: retry policies (`IRetryPolicy` owns its own waits), audit and logging stay on
  the real clock. A test using a fake clock must not expect `Advance()` to complete a retry delay.

## Durable occurrences (opt-in; the default path is untouched)

A schedule whose definition says `OccurrenceMode.Durable` stops running the handler and becomes a definition
plus a cursor: `Scheduler/Occurrences/OccurrenceMaterializer` turns each due slot into its own one-shot child
row. Nothing here runs for an inline schedule — `IsScheduleOnly` is the only branch, and it is false unless
the definition opted in.

- **The materializer is the ONLY thing that moves a durable schedule**, and every entry point calls the same
  `RunAsync`: the schedule's own slot firing (`DoWorkGuarded`, before the rate-limit gate), the kick each
  occurrence gives when it ends (`DoWork`'s finally, before the single `End`), recovery's second wave, and the
  operational re-park. It re-reads the row every time and every write is a compare-and-swap on the cursor and
  version it decided from, so two runs racing end with one winner and one re-read. The per-schedule gate is a
  contention optimization on top of that, and it COALESCES: a run that finds the gate taken sets a pending
  flag the holder re-checks before releasing, so a kick is never simply dropped.
  - **Every one of them hands it the CALLER's token**, the kick and `ReparkFromRowAsync` included: a run
    re-plans the schedule, and since phase 6 that grid may be an `INextOccurrenceProvider` doing real I/O.
    `CancellationToken.None` there let a calendar that never answers hold the delivery's registry entry —
    the occurrence stays "in delivery", so a budget of one is spent for ever — plus the worker consumer and
    the host's shutdown. A kick the shutdown cancels is not a failure and is not logged as one: nothing is
    written by a plan that could not be computed, and the operational retry and startup recovery both bring
    the schedule back.
- **It never throws at its callers, and a failed run re-parks the schedule itself.** The re-park is armed
  INSIDE the per-schedule gate, so it covers the runs the holder absorbed: a schedule's own delivery that
  finds the gate taken returns without parking the row, and if the holder (a kick) then failed silently the
  row was parked nowhere at all until a restart. It prefers the delivered executor over rebuilding one from
  the row, because what usually failed is the storage. `WorkerExecutor.MaterializeScheduleAsync` keeps the
  same re-park as the backstop for anything that fails outside the gate.
  - **A row this build cannot rebuild is parked, not dropped** (`ParkUnusableRow`). A payload that stopped
    deserializing, a zone id that stopped resolving: the VERDICT there is startup recovery's — it owns the
    bounded retry and the terminal poison, and a second copy would spend the same budget twice — but the
    PARKING is the run's, because the schedule's own delivery consumed the registration that made it. A Debug
    line and a return left that row in no scheduler, no queue and no delivery until a restart. The delivered
    executor is the only one that can be re-parked: rebuilding one from the row is what just failed, and one
    built without the durable definition would run the handler. A row that is no longer durable at all (a task
    key re-registered inline over it, a reschedule that turned it inline) is the opposite case: the durable
    executor is never re-parked there — it would replace a registration that runs a handler with one that
    materializes nothing — but the row is not simply abandoned either. Whoever wrote the inline definition
    owns the parking and normally did it, which costs one registry lookup to confirm; the case this exists for
    is the only thing that lets an old durable delivery reach a rewritten row at all, a re-park that FAILED,
    where "somebody else has parked it" is exactly the assumption that does not hold. The executor is rebuilt
    FROM THE ROW, never taken from the run.
- **One run, ONE reading of the row.** A run snapshots version, cursor, status, run count and queue at its
  start, and every compare-and-swap it makes carries those values — never a property read again at write
  time, which would absorb whatever a concurrent writer did in between and make the write that had to lose
  win instead (the same shape as R1 in phase 1). The in-memory store hands back LIVE entities, so there "the
  row" and "the row a cancel just changed" are the same object, and the finalization of a spent series would
  expect the `Cancelled` it is about to overwrite.
- **A halted schedule is NOT re-parked.** The operational retry exists so a schedule that could not advance
  gets another chance; a halt has nothing to try again — it never releases itself, by contract — and only
  `ResumeSchedule`/`Reschedule` clear the marker, both of which park the row themselves. Re-parking it put the
  row through the worker queue every minute for ever, each pass a `Queued` transition plus a `StatusAudit`
  row. A halt whose CAS was lost is re-parked, because that one is an ordinary lost race.
- **`AlreadyExists` advances the cursor, it is not a re-read.** Every other non-`Created` outcome self-heals on
  the next run; that one means the cursor still points at a slot that already has a row (written from outside,
  or replayed after a rewind), so re-reading answers the same thing for ever. The run carries the cursor past
  the served slot — compare-and-swapped, no run counted, one warning and one event — and goes on to the next.
  **"The next" means inside the same PASS**: `MaterializeAsync` walks the grid itself and writes the occurrence
  the plan granted at the slot behind the served one. Ending there made a rewound cursor walk its backlog at
  one slot per `BacklogRetryInterval` — nothing shortens that wait, because a run that materializes nothing
  produces no occurrence and therefore no kick — and handing the walk back for a fresh plan per slot made ONE
  run quadratic in the length of the stretch, since each plan re-counts and re-bisects everything still due.
  Nothing a walk does can change what the plan decided: `now` is fixed, a newer slot is inside the age window a
  fortiori, a shrinking backlog cannot exceed a cap the bigger one passed, and a served slot spends neither a
  run nor a unit of the budget. So there is ONE plan per run, and `MaxServedSlotsPerRun` bounds the whole cost
  of the walk, because every unit it counts is constant: one grid step, one executor build and two round trips
  per slot. A truncated run resumes at its retry from where it stopped, not from where it began.
  - **The two things the walk must still restate.** A plan nulls its cursor when the slot it grants is meant to
    be the last run `MaxRuns` allows, so the series ends in the same commit that creates it — but a served slot
    creates nothing and spends no run, so that premise is false FOR THAT SLOT and the series is not over: the
    run asks the grid for the successor and only a grid with nothing left ends it there. The premise itself is
    still true, and `DueSlotPlan.RunBudgetEndsSeries` is what keeps it: a budget counts materializations, not
    slots, so the write that really spends the last run — wherever the walk put it — is the one that carries a
    null cursor and closes the series in its own commit (M6/M14). Reading the plan's null as "this slot ends
    it" dropped the residual run; ignoring it left the row `Queued` with a spent budget for a later run to
    close. And the misfire stamped on a row created after walking is `MisfireAfterWalking`: the range starts at
    the slot being created and the count drops by the slots walked, because the range and the count are one
    statement (below).
- **The policy is `Scheduler/Occurrences/DueSlotEnumerator`, and every count in it is bounded — where a bound
  buys something.** A grid that counts by division (every plain cadence) is asked for the real total, because
  the cap costs it nothing and would turn "7,900,000 runs were lost" into "10,001"; a grid that has to be
  WALKED keeps the cap, and its count travels with `IsExact = false` so no surface reports a truncated number
  as a total (`SlotLoss.IsExact`, `OccurrenceMisfire.MissedCountIsExact`, `ScheduleHaltInfo.IsExact`
  and the public `MisfireInfo.MissedCountIsExact`). `SkipOldest` finds where the last N slots begin by
  bisecting the INSTANT axis, each probe a bounded forward count — never an enumeration.
  - **One count, ONE bound: the one the caller passed.** `CountMissedOccurrences` spends the whole cap it is
    handed (a catch-up cap can be far above ten thousand — a month of a five-minute schedule is 8,640 slots),
    and its own `MaxSkipCountIterations` applies only to the ask that carries NO cap. Give the walk a second,
    smaller bound and every answer above it comes back as "10,001", which `count <= cap` then reads as a
    total: the `Halt` breaker never fires, `SkipOldest` bisects to the wrong instant, and the number reaches
    the handler stamped exact. `DueSlotEnumerator` therefore never asks a walked grid for an UNCAPPED count
    either — `int.MaxValue` is a legal `MaxOccurrences`, and it falls back to `DiagnosticCountCap`.
  - **The breaker is bounded by the RUN BUDGET too.** `Halt` fires only when `MaxRuns` still allows more
    occurrences than the cap: a series with one run left can create exactly one more whatever the backlog
    holds, so there is no flood to stop, and halting it — a marker that by contract never releases itself —
    left an operator to resume a schedule for the sole purpose of spending its last run. `SkipOldest` is NOT
    short-circuited the same way: which end of a backlog matters is the policy the caller chose, so the last
    run still gets the newest slot instead of the oldest.
- **What makes a slot MISSED is `SetMisfireThreshold`** (M1), and the planner reads it at materialization time
  exactly as `TaskExecutionContext` reads it at delivery time. A slot older than the threshold stands for
  missed work even when it is the ONLY one owed — a ten-minute outage on an hourly grid owes one slot, which
  is precisely the case a replaying policy exists for, and deciding on "more than one slot" alone left that
  row indistinguishable from an ordinary occurrence. The threshold only ever WIDENS the definition: a run of
  more than one slot reports whatever its age, because `FireOnce` is about to collapse slots and `CatchUp` to
  replay them, and P5 forbids either happening unreported (decisions §3.5).
- **A misfire's range and its count are one statement.** `MissedFromUtc`/`MissedThroughUtc` bound the backlog
  the decision saw and `MissedCount` is how many grid slots that range holds, both ends included — so the
  catch-up range ends at the newest slot the backlog owes, NOT at the last slot the concurrency budget let
  this run take (which under the default budget of one would be a range of one slot carrying a count of six).
  The extra bisection that finds it is paid only when there IS a backlog, and never for a single slot, which
  is its own range.
- **Skipping a slot usually costs no write**: the first materialization carries the cursor from the slot that
  was dropped to the one that was kept. `TryAdvanceScheduleCursor` exists for the case where nothing survives
  at all, and it counts no run and writes no audit.
  - **Every dropped run says WHICH rule dropped it** (`SlotLoss.Reason`, M10(1)/P5), and one plan can carry
    two of them: the age window and then the `SkipOldest` cap are different settings with different fixes,
    and adding the second count into the first reported the whole loss as "outside the misfire window" —
    sending an operator to widen a window that had dropped a fraction of it. The `Skip` policy's own drop is
    the third reason, and it was wearing the age window's sentence too. One EventId and one event message per
    reason (1802, 1819, 1820).
- **A replay has two boundary events, and they are decided from the PLAN** (`TrackCatchUpEpisode`): a plan
  that stamps its rows as catch-up work opens an episode, the first plan that owes nothing more closes it.
  Everything in between is reported per occurrence, which cannot say where a backlog began, how big it was,
  or that it has drained. Only an `Exhausted` plan may close one: a run with no concurrency budget left
  (`WindowFull`) plans nothing at all precisely while the replay is busiest, and reading that as the end
  opened and closed an episode between every two occurrences of a serial catch-up. The episode is
  per-process — a restart mid-replay opens a new one, which is what the materializer does with the backlog
  anyway — and a halt or a cancel ends it without a completion, because those have their own event.
- **A stale occurrence keeps consuming the budget until it terminates.** Reconciliation notices an occurrence
  that is non-terminal but neither delivering nor parked, requeues it under a compare-and-swap on the status it
  was found in, and hands it back to the scheduler — but it still counts as active. Only a terminal state frees
  capacity. Without `IScheduler.SupportsScheduleInspection` there is no evidence to tell stranded from parked
  (the default answer is a constant "yes"), so nothing is reconciled and every non-terminal occurrence counts.
  The executor is REBUILT BEFORE the requeue, and a row that cannot produce one is marked `Failed` instead:
  that verdict never changes while the process lives, so requeuing first wrote a `Queued` transition plus a
  `StatusAudit` row on every run for ever while the row went on holding a slot of a budget of one. It is the
  same poison shape recovery uses for a one-shot, and `RequeueTerminal` is the way back after a deploy.
  **A row is unusable for TWO reasons and both end it**: the payload may not rebuild, and the rebuilt payload
  may have no handler left to run it — a type still loadable after a re-registration pointed its schedule
  elsewhere, whose `IEverTaskHandler<T>` nobody registers any more. Resolution happens inside `Handle`, so that
  second one escaped as an exception out of the whole reconciliation: the run failed, the schedule re-parked,
  and the occurrence stayed non-terminal, stalling the series on every pass.
  - **A handler that failed to BUILD is neither of them.** A scoped dependency whose factory threw looks
    identical from here, so after a failed rebuild the container is asked the question that separates them —
    is anything registered for this task at all? — and only a "no" is final. Ending an occurrence on a
    transient activation failure drops work no handler ever saw, without one of the retries its policy
    promises; it keeps its slot of the budget instead, and the next run looks again — but only
    `MaxOccurrenceRebuildAttempts` **process starts** in a row. A constructor that throws EVERY time is a
    misconfiguration, not an outage, and an unbounded "look again" held the series for the life of the
    process. The ceiling is the row's own `RecoveryDispatchFailureCount`, incremented and cleared exactly as
    the recovery's L18 does it (a rebuild that succeeds clears what it burned, so the count is CONSECUTIVE
    failures), and reaching it ends the row through the same confirmed poison — with its own EventId (1825)
    and its own event sentence, because "cannot be rebuilt from its row" sends an operator after a type or a
    payload that are both fine. `RequeueTerminal` clears the counter with the exception for the same reason:
    a row that came back carrying the attempts that ended it would be poisoned again by its first failure.
    - **One attempt per PROCESS START, not per run**, which is the cadence of the counter it borrows: the
      recovery spends its five over five restarts, while a schedule held behind a stale occurrence re-plans
      every `BacklogRetryInterval` — a minute by default — so counting per run spent the whole ceiling in
      five minutes and made a fifteen-minute database failover indistinguishable from the misconfiguration
      the bound exists to catch. `_rebuildFailuresCounted` is the in-process memory of "this outage is
      already counted"; it is dropped the moment the row heals or ends, and it is deliberately not durable —
      what has to survive a restart is the column, what must not is the memory of an outage this process is
      still inside.
  - **Only a CONFIRMED terminal state frees capacity.** `SetStatus` is best-effort on every relational
    provider — it logs its own failed write and returns — so the row is re-read before the slot is counted
    free. Taking the call's return as the answer let a swallowed write leave the old occurrence alive while a
    successor was created behind it, two of them under a budget that says one. The read-back also decides what
    is REPORTED: the log line and the monitoring event are written after it, and the run that could not end the
    row says so instead of announcing a `Failed` nothing put there. "Was marked Failed" over a row still
    `Queued` gives an operator no reason to look for the occurrence that is holding the schedule.
- **The schedule row is not a delivery**: it never enters `DoWorkCore`, never sets `InProgress`, never touches
  the rate-limit gate and never runs `QueueNextOccourrence`. Rate limiting applies to the occurrences, per key
  — `TaskHandlerWrapper.ExtractRateLimit` returns nothing for a durable definition.
- **A schedule's blacklist entry covers its occurrences.** `Cancel(scheduleId)` cancels the pending ones in
  storage, but an occurrence already parked in the scheduler or already in a channel carries no entry of its
  own: without the parent check in `WorkerQueue.IsCancelled` its enqueue would write `Queued` over the
  `Cancelled` the cancel had just persisted, and `DoWorkGuarded` would run it. It is asked a THIRD time in
  `DoWorkCore`, right before `SetInProgress`: handler resolution sits between the entry check and that write
  and takes as long as the handler's dependencies do, and the transition is unconditional — a cancel landing
  in there would put a row `CancelSchedule` had just marked `Cancelled` back into `InProgress`, run it and
  complete it. None of the three CONSUMES the entry — it has to keep covering the siblings behind this one.
  That includes the SCHEDULE's own dropped delivery: a mid-catch-up schedule has its own row in the queue
  too, and consuming the entry there destroyed the only cover its occurrences had.
- **`Cancel` asks for occurrences TWICE, and the second time is the one that matters.** Its two steps are a
  classification (are there occurrences to cascade to?) and a write, and a materializer that had claimed the
  schedule row before the first read had not inserted its occurrence yet — so the cancel saw nothing to
  cascade to, took the simple single-row write, and left a live occurrence under a cancelled schedule. Asked
  again AFTER the status is persisted the answer is final: both writes go through the schedule row, so a
  materialization in flight has committed by then (its occurrence is visible) or lost, and every later one
  reads `Cancelled` and is refused. A cancel with nothing to cascade pays one more indexed read on an
  administrative path; one that finds something writes the cascade the race deprived it of. A lookup that
  THREW cascades too: "the query failed" is not an answer that lets a cancel declare the series terminal, and
  the cascade over a schedule with no occurrence is the parent's own write — the one it would have made
  anyway. Degrading that failure to "no occurrences" let the cancel return normally while an occurrence it
  could not see stayed non-terminal, covered only by a blacklist entry that lapses after an hour.
- **The cancelled set is the complement of the requeued one, on every path.** `CancelSchedule` leaves an
  `InProgress` occurrence to finish, so the two places that decide what its ending MEANS both ask the parent:
  `HandleExceptionAsync` classifies a shutdown OCE as a user cancel when the schedule is blacklisted (else it
  writes `ServiceStopped`, which recovery puts straight back in a queue), and the recovery cancels an
  occurrence whose schedule row is `Cancelled` instead of re-dispatching it — the case a hard crash leaves
  behind, where no blacklist survives. That read is ONE query per recovery wave, and only when the wave
  carries occurrences at all.
- **Children are always lazy and always inherit the parent's stored queue.** An occurrence is dispatched with
  no recurring definition, and the fallback for one of those is the DEFAULT queue, so a durable series routed
  to its own queue would quietly leave it one occurrence at a time.
- Contracts, spelled out in `docs/recurring-tasks/durable-occurrences.md`: at-least-once, and a single ACTIVE
  host — materialization is idempotent across hosts (unique index), execution is not claimed by anyone.

## Runtime schedule management (`ITaskScheduleManager`)

`Dispatcher/TaskScheduleManager` is the one place a schedule that is ALREADY registered is changed. It reads
the row, decides the new definition and cursor, writes them with `UpdateSchedule` (compare-and-swapped on the
version it read), and hands the row back to whatever owns its parking.

- **It holds the dispatcher's own per-taskKey section**, now `Dispatcher/TaskKeyLockRegistry` in the
  container. A dispatch under the same key is the same read-decide-write over the same row and its
  `UpdateTask` carries no version, so without one shared section a dispatch that had read the row first would
  overwrite the definition a reschedule just committed. The dispatcher resolves the registry lazily and keeps
  a private fallback, so a hand-wired dispatcher still serializes its own dispatches.
- **Nothing here is best effort.** A lost compare-and-swap is reported to the caller, and a new definition
  with no occurrence left is REFUSED rather than written: a row with a null cursor satisfies neither recovery
  predicate, so it would sit `Queued` for ever. `CancelSchedule` is how a series is ended on purpose.
- **Each method gates on the capability it actually uses**, and the docs say so method by method: the three
  that rewrite a schedule row need `SupportsScheduleVersioning`, `RequeueFailedOccurrence` addresses a child
  row and needs `SupportsDurableOccurrences`, and `CancelSchedule` needs only a registered storage. One
  blanket sentence for the whole interface was wrong in both directions. What a call is asked to WRITE gates
  too: a `Reschedule` whose new definition is durable goes through the same
  `Dispatcher.RequireDurableOccurrenceSupport` a dispatch does, inside `Build`, before anything is written.
- **A requeue asks the SCHEDULE TWICE, and the second time is the one that matters** — the same shape as
  `Cancel`'s own two lookups. `CancelSchedule` cancels the pending occurrences in the same transaction, so
  by status alone every one of them is a legitimate `RequeueFailedOccurrence` target; the parent's
  `Cancelled` is what refuses it. But that check and `RequeueTerminal` are two round trips, and a `Failed`
  occurrence is not in the set a cancel cascades to, so a cancel landing between them was caught by nobody:
  the parent is re-read AFTER the requeue commits, and a cancellation that won puts the row back to
  `Cancelled` and answers false. One of the two orderings always sees the other, because the cancel asks for
  occurrences after persisting the status. The occurrence's OWN blacklist entry is dropped here too —
  nothing else on this path ever consumes it, so `WorkerQueue` discarded the enqueue while the call reported
  success. A schedule's entry is left alone: it covers the siblings, and its own entries lapse after about
  an hour and never existed in a process that did not issue the cancel. A schedule merely OVER is not
  refused: that occurrence was owed, and replaying it materializes nothing.
- **A re-park that belongs to a REPLACED definition is refused, not merely late.** `IScheduler.TrySchedule`
  is the conditional half of latest-wins: it leaves a registration carrying a newer `ScheduleVersion` alone
  and answers false. Everything holding an executor of a definition a reschedule has retired goes through it
  — the next occurrence an advance computed (both the counted path and the rate-limit skip, which writes
  nothing to storage and so has no compare-and-swap to speak for it), the gate's deferral and in-flight
  re-park, the materializer's operational retry, `ReparkFromRowAsync`. Without it that registration replaced
  the one the reschedule had just published, `IsSupersededSchedule` dropped it the moment it fired, and the
  series was left in no scheduler, no queue and no delivery until a restart. The comparison lives INSIDE the
  registry's own swap because every ordering outside it still has a window, and a scheduler that cannot
  compare versions keeps the unconditional behaviour through the member's default body. When the gate is
  refused it also drops the bookkeeping it would have made — the parking-lot entry and the reservation —
  since the registration `DropStaleRegistrationIfInvalidated` would have cleaned up was never made.
- **Re-park, THEN publish, and bump the gate epoch last of all** (S4). The registration is replaced
  latest-wins — never `TryUnschedule` first, which opens a window with the schedule parked nowhere — and
  only then does `ScheduleVersionRegistry.Publish` raise the lower bound. The rate-limit gate's invalidation
  epoch moves at that same point and for the same reason: bumping it FIRST assumes a re-park always produces
  a registration, and one that throws produces none — so the only thing left holding the series was the
  registration the delivery stuck at the gate was about to make, and the gate's own set-then-check deleted
  exactly it. Nothing is lost by waiting: a stale re-park landing after that point is refused by
  `TrySchedule` inside the registry's swap. Publishing a version whose executor never reached the scheduler
  drops the old delivery and puts nothing in its place; if the re-park throws, nothing is published and the
  old occurrence runs once more, its advance losing the compare-and-swap, applying the new definition **and
  parking the row from itself** (`ReparkFromRowAsync` above) — the half that was missing, without which a
  failed re-park cost the series rather than one extra run.
  - **The version is not published when the parking ENDED the series either.** A durable schedule is parked
    by the materializer, and a resume whose replanned backlog spends the last run the budget allows closes
    the series inside this very call — dropping the entry where a durable series really ends. Publishing
    afterwards put it straight back for a schedule that will never run again. The row is asked once, on this
    administrative path only, and a null cursor is the answer.
  - **The S5 event is published on BOTH exits**, because the write is committed before the parking is
    attempted: a subscriber told only that a re-park failed had no record of the version, the cursors, the
    mode or the backlog the row now carries. The sentence names the version the schedule came FROM as well
    as the one it is at — the new one alone cannot tell a first reschedule from a tenth.
- **A durable schedule is parked by the MATERIALIZER**, not by `Schedule()`: it is what decides which slot the
  row waits for, and it re-reads the row this call has just written. The executor is handed to it so its own
  failure path has one to re-park.
- **`ResumeSchedule` keeps the cursor.** That is the whole point: a halted catch-up is released by planning
  its backlog AGAIN against the definition as it stands (and halting again if it still overflows). Moving the
  cursor there would silently do what the halt existed to prevent. Every operation clears the halt marker
  (M10), because asking is what these calls are.
- **The row is read ONCE.** Version, cursor and runtime info are snapshotted before the update: the in-memory
  store hands back LIVE entities, so reading the version back after `UpdateSchedule` reports what was just
  written as what was there before.
- **`UpdateSchedule` compare-and-swaps on the CURSOR as well as the version, and refuses a `Cancelled` row**,
  and the snapshot above is what it carries. A successful advance moves the cursor and the run counter
  without touching the version, so a version-only guard let a reschedule decided on a run count and a cursor
  a completion had already superseded commit over it — and a rebase computed from that reading is parked one
  occurrence past the budget it was just given. A `null` expectation is legal here and means "the series has
  ended", which every storage has to read as IS NULL rather than as an equality. The STATUS is checked apart
  from both, because a cancel is the one write neither answers for: it writes the status and leaves the
  version and the cursor exactly where they were, so a reschedule that read the row first matched on both
  and put a live definition over a series an operator had ended — then reported success while the blacklist
  dropped every delivery it produced. Only `Cancelled` refuses; `InProgress` never does (S3), and a spent
  series is an ordinary target. The manager re-reads the row on the FAILURE path alone, to say which of the
  three lost.
- **`Scheduler/Recurring/ScheduleRebase`** owns `RescheduleMode.RebaseFromCursor`: the nominal period of the
  old cursor under the OLD definition, and the NEW definition's first slot inside that period read on the new
  zone. `RecurringTask.PeriodKind` names the unit (`SchedulePeriodKind`, coarsest selector wins; cron has
  none), except where the period is smaller than its name — see that namespace's CLAUDE.md gotcha 18. The
  BOUNDS are checked apart from the period arithmetic, because two of its three branches never touch the grid
  at all: a plain cadence keeps its cursor verbatim and a day-carrying week or month cadence composes its slot
  from the period start, so `FirstOccurrenceOnOrAfter` — the only thing that applies `RunUntil` — is never
  asked, and `MaxRuns` no branch applies at all. `ScheduleRebase` therefore refuses a slot at or past the new
  `RunUntil` itself, and the manager refuses a spent `MaxRuns` before it, so a definition `RecalculateFromNow`
  turns down is turned down here too. It refuses more than it accepts on purpose — a different cadence or
  selector, a cron on either side, a period with no slot — because every one of those either loses a period of
  work or replays one.

- **`ScheduleRebase` keeps the cursor's POSITION inside the period, not only the period.** "The new
  definition's first slot in that period" is where the cursor stood only while the period holds ONE slot;
  with two it is a slot that has already run, so the rebase rewound onto it, replayed that occurrence and
  spent one more of `MaxRuns` — while `RecalculateFromNow` on the same definition answered the later one.
  See that namespace's CLAUDE.md gotcha 18 for how the position is counted.

`WorkerExecutor` is the other half. `IsSupersededSchedule` drops an INLINE delivery whose version is below the
published one (never a durable schedule row: it runs no handler, the materializer re-reads the row anyway, and
dropping it would consume the registration that produced it); the absence of an entry is not a lower bound of
zero, which is what keeps a recovered executor alive across a restart. `AdvanceVersionedRunAsync` is the CAS
advance, and on a mismatch it re-aims at the row's own cursor instead of dropping the write, because the run
happened and has to be recorded.

- **Past the last re-aim the GUARD is given up, never the write.** The bound exists so a third party rewriting
  the row in a loop cannot spin an advance; reaching it recorded nothing at all, which left the row in the
  `InProgress` the delivery had set, the execution unaudited, `CurrentRunCount` — and with it `MaxRuns` —
  permanently one short, and the series in no scheduler, no queue and no delivery until a restart. The last
  attempt therefore writes unconditionally, against the cursor the last reading carried: that value is the
  current owner's own, so it advances nothing, and the row travels back so the caller parks the schedule from
  it. A lost run is permanent; a cursor one generation behind is overwritten by the next advance of the
  definition that owns the row.

- **A schedule the manager can ADDRESS is compare-and-swapped from its first advance**, and the address is the
  taskKey: every entry point of `ITaskScheduleManager` takes one, so a recurring row without a key can never
  be rescheduled and keeps the unconditional writes byte for byte. Deciding instead on "has it been
  rescheduled yet" cannot be done without a race — the row was read before the run was even evaluated, the
  delivery's version is older still, and the registry is published only after the re-park — so the FIRST
  reschedule of a schedule could linearize inside that gap and be overwritten by the very write S1 exists to
  guard. The version fields stay in the test for what a key cannot answer for: a row whose key was cleared,
  and a delivery rebuilt from a row that already carries a version.
- **The rate-limit SKIP ends a series under the same compare-and-swap as every other finalization.** It is
  the one advance that writes nothing, so when the skip is the LAST one — the limiter's slot falls past the
  series' `RunUntil` — the terminal `Completed` and the null cursor were written unconditionally, over a
  row a reschedule had just extended: the result answers NEITHER recovery predicate, so no restart brings
  the series back and the audit reads like an ordinary end. The expectation is the DELIVERY's version (the
  row's would confirm the reschedule instead of losing to it) plus the cursor and status snapshotted at the
  top of `QueueNextOccourrence`. A lost compare-and-swap parks the row from itself and KEEPS the registry
  entry: the unconditional `ScheduleVersions.Remove` beside it wiped the S4 lower bound just published.
- **A DURABLE series drops its published version inside the materializer**, because that is where it ends: the
  cursor is nulled in the same commit as the terminal status, so such a row never passes through
  `QueueNextOccourrence`, which is where an inline one drops it. The other two ends are `Dispatcher.Cancel` and
  a row that is removed. Nothing depends on the entry surviving — `IsSupersededSchedule` never looks at a
  durable schedule, and a taskKey re-registration reuses the row and its version — but an entry per finished
  durable schedule is the one way the registry could grow for the life of the process.
- **An advance that applied against a definition it never saw PARKS the row itself** (`ReparkFromRowAsync`:
  that row's definition, cursor and version, never the delivery's). Whoever rewrote the row is supposed to
  have parked it and normally has — this is then a second registration of the same instant, replaced latest
  wins — but the one thing that lets a delivery of the replaced definition reach a rewritten row at all is a
  re-park that FAILED, and there returning empty-handed left the series in no scheduler, no queue and no
  delivery until a restart. A row that has turned durable goes to the materializer instead, because that owns
  a durable schedule's parking. The row is the one the re-aim already read: no extra round trip, and the
  ordinary advance (applied on its first attempt) never reads one at all.

## Occurrence grid: one seam

`IScheduleEvaluator` (internal, `ScheduleEvaluator`) is what the dispatcher, the worker and the recovery ask
about a schedule's grid — never the occurrence math directly. For a built-in definition it is a synchronous
wrapper over the pure primitives on `RecurringTask`; the asynchronous shape is what a schedule driven by an
`INextOccurrenceProvider` needs, since that grid is real I/O. Its two grid questions that no primitive
answered before are `NextGridOccurrenceAfterAsync` (the natural successor, bounds ignored — the recovery grace
window) and `EnumerateDueSlotsAsync` (the slots already owed at a given now, oldest first, under a MANDATORY
cap).

- **A provider grid answers through the same seam, so nothing above it branches** (`ProviderScheduleGrid`).
  The parts of the schedule math that are not the grid step — the first-run configuration, the termination
  bounds, the realignment past a downtime — are REUSED through `RecurringTask.PlanNextRun` /
  `SelectNextRun`, never mirrored: two copies of "is this first run RunNow or the grid's slot" would drift.
  The arithmetic entry point (`GetNextOccurrence`) THROWS for a provider definition instead of answering,
  because with no interval and no cron the cascade would report "no occurrence, ever" and every primitive
  would read that as a series that has ended.
- **A provider failure is transient and writes NOTHING** (V4). It is wrapped in `OccurrenceProviderException`
  with the backoff its schedule has earned (`OccurrenceProviderRetryRegistry`, doubling per consecutive
  failure, reset by one answer), and each caller parks its row rather than acting on a grid it does not have:
  the dispatcher's recovery branch re-parks a `IsScheduleRetry` delivery (which re-reads the row and re-runs
  the DECISION — parking an ordinary delivery would execute a slot nobody chose yet), `QueueNextOccourrence`
  does the same for a failed advance, and the materializer re-parks the schedule row on the provider's
  backoff instead of the operational one. It must never reach the recovery's generic catch, whose L18 counter
  would poison the row after a few restarts of a database outage. An UNREGISTERED key is the opposite verdict
  — configuration, not transience — so it is an `ArgumentException` at dispatch and a terminal poison at
  recovery, through `RecurringTask.Validate(registry)`.
  - **"Writes nothing" includes the QUEUE boundary.** A retry delivery is the one enqueue that skips
    `TrySetQueuedIfRecoverable` (`WorkerQueue.TryQueueCore`): it runs no handler and decides nothing, so the
    `Queued` transition and the `StatusAudit` row it used to write were a state write per retry, for as long
    as the outage lasted — over a row whose whole point is to look untouched. The recoverable check is not
    lost, only moved to where the retry can act on it: `RetryScheduleDecisionAsync` re-reads the row and
    abandons a schedule that was cancelled, removed or finished under the wait.
  - **A deferral is a THIRD dispatch outcome** (`ScheduleDeferredByProviderException`, internal), and the
    recovery loop catches it apart from both the success and the failure. Returning the schedule id said the
    dispatch had worked, so `WorkerService` cleared the L18 counter a previous restart had really earned;
    letting the provider's own exception out said it had failed, which is the poison V4(1) forbids. Neither
    is true of a calendar outage, so the counter is neither cleared nor incremented, and the row still counts
    as recovered — it IS back in the scheduler. The schedule-retry delivery catches it too, where it means
    "still down", not a retry that failed.
  - **Nothing that can only be decided by RESOLVING a provider runs on the recovery path.**
    `Dispatcher.RequireProviderSupportAsync` keeps its key check everywhere (a dictionary lookup, and the
    ratified poison route) but returns before the `SkipOldest` determinism probe when `isRecovery` — that
    probe builds a scope and an object, and neither the `InvalidOperationException` it throws for a
    non-deterministic provider nor a constructor that cannot reach its database yet is an
    `OccurrenceProviderException`, so both would sail past the V4 catch into the recovery's L18 counter and
    poison the series after five restarts. It is a gate for a caller holding a dispatch or a reschedule; a
    persisted row carrying `SkipOldest` over a provider that stopped declaring itself deterministic keeps
    running and bisects as a deterministic grid would (decisions §3.7).
  - **A re-park that FAILED is an error EVENT, not only a log line** (V4's second mandatory condition):
    nothing polls behind it, so the series stops until a restart, and a subscriber that was told "parked to
    ask again at …" must not be the last thing it hears. The three sites say it the same way —
    `Dispatcher.ParkProviderRetryAsync`, `OccurrenceMaterializer.ReParkAfterFailureAsync` (whose
    `ReParkOutcome` is what keeps `DeferForProviderAsync` from publishing the success sentence over it) and
    `WorkerExecutor.DeferScheduleForProvider`.
  - **"Parked to ask again" is said only once the registration is IN**, log line and event alike, on all
    three. A throw is not the only way to park nothing: `TrySchedule` also ANSWERS false — a newer definition
    owns the row, or the scheduler is stopping — and reading that as a park announced a schedule that was in
    no scheduler, no queue and no delivery. A refusal reports itself instead (`ProviderRetryParkRefused` 1021,
    `ScheduleReparkRefused` 1822) and says nothing else, because whether the row is waiting on anything at
    all is then somebody else's business.
  - **The retry delivery carries the slot it was about BESIDE the instant it fires at**, because those are
    two different instants and it needs both. `WorkerExecutor.DeferScheduleForProvider` re-parks
    `task.ToLazy()` with `ExecutionTime = retryAt` — what the scheduler is handed — and the interrupted
    slot on `TaskHandlerExecutor.ScheduleRetryFromUtc`, internal like the flag itself. With storage the
    question is answered by the row's cursor and the carried slot is never read, which is why the recovery
    re-park (`Dispatcher.ParkProviderRetryAsync`, reached only on `isRecovery`) sets the flag alone. With NO
    storage the delivery is the only place that slot exists, so `RetryScheduleDecisionAsync` re-runs the
    advance from `ScheduleRetryFromUtc ?? ExecutionTime`: deciding from the instant it fired at would
    silently skip everything in between, and abandoning the retry there stopped a storage-less series (F18,
    in-memory run counter) for good on the first hiccup of its calendar. It drops BOTH marks first, or the
    occurrence that advance parks would come back to decide again instead of running the handler.
- **The advance is the one place a provider failure costs something**: the run that just happened cannot be
  written either, because the write and the next cursor are one operation. The row is left exactly as a crash
  between a side effect and its storage write leaves it, and the at-least-once contract covers the replay.
- **Every walk over a provider grid is bounded by round trips, not by patience.**
  `ProviderScheduleGrid.DiagnosticCapFor` is the ONE rule and every diagnostic count goes through it —
  `DueSlotEnumerator`'s drop counts and `TaskScheduleManager`'s discarded backlog alike — so a provider walks
  at most `MaxDiagnosticWalk` and the number says it is a lower bound, exactly as a walked calendar count
  does. A count that feeds a DECISION keeps the caller's own cap whatever the grid is (see the "one count,
  one bound" rule above), which is why a large `MaxOccurrences` really does cost that many questions over a
  provider: it is the price of telling "more than the cap" from "exactly the cap", and it is documented as
  the knob to size. The `SkipOldest` bisection stops at the first probe that counts exactly N — that instant's
  successor IS the answer — instead of narrowing to the tick and spending all 64 probes every time.
  - **The skip-forward count is one of those, and it says so too** (`NextRunResult.SkippedCountIsExact`). It
    is the number of runs a downtime cost, and it reaches a log line and a monitoring event: a grid that
    counts by division answers the real total however long the outage was, a walked one stops at
    `MaxSkipCountIterations`, and a provider one at `MaxDiagnosticWalk` — where "251" against a backlog of
    thousands is simply a wrong number in an operator's incident log. The flag defaults to `true`, which is
    what every count below its bound answers, so the built-in path is byte-identical.
  - **Its two bounds must not MULTIPLY.** Bounding the probes says nothing about what a probe costs, and each
    one counts up to the cap one round trip at a time: probes × cap is an order of magnitude above the cap the
    docs tell an application to size. The probes walk the same stretch of chain, so `FindNthSlotFromEndAsync`
    remembers it (provider grids only — a built-in one counts by division or walks in memory) and no instant
    is asked about twice. What one search costs is then one question per slot it looks at, never more than
    walking the backlog once. It is sound for exactly the grids `SkipOldest` accepts: a provider that would
    answer differently the second time is refused at dispatch. `ProviderScheduleGrid.CountCeiling` is shared
    with that walk so the memoized count and the grid's own answer the same question identically.
  - **A cap bounds an EPISODE, so the count that decides it is taken once per episode, not once per run.**
    "Is the backlog bigger than the cap?" costs one question per slot — counting a chain has no cheaper
    answer — and a serial replay plans once per occurrence, so re-asking it made a replay quadratic in its own
    backlog: 360 owed slots cost 360 queries to materialize the first, 359 for the second, before a row was
    written. `DueSlotEnumerator.MeasureBacklogAsync` keeps what a run measured and the next run CONTINUES it,
    asking only about the stretch that came due since — which is what a schedule keeping up owes anyway — so
    the numbers are the ones a full walk would give (the cap decision, the count stamped on the row and its
    exactness) and the newest slot comes with them, which is the misfire's own bisection gone too. It is a
    reading of a chain, so everything about it is guarded: provider grids only (a built-in one divides or
    walks in memory), continued only when this run resumes at exactly the cursor the last plan left on the
    same grid, dropped if the count was ever a lower bound or if the grid no longer owes the slot the cursor
    names, and re-taken from scratch after `MaxContinuedMeasurements` because a provider may answer
    differently the second time.
  - **And a plan that may create nothing measures nothing.** `PlanCatchUpAsync` takes the early exit its two
    siblings already had, one step later: the age window drops slots without needing capacity, materializing
    one does not. The only decision it defers is the halt, which is about whether a REPLAY may start — a run
    with no budget starts none, and the next run that has one halts instead. What it saves is the whole walk,
    at every operational retry, for as long as the occurrence holding the budget runs.
- **The provider's scope is disposed ASYNCHRONOUSLY** (`CreateAsyncScope`, both resolution sites). A scoped
  dependency that implements only `IAsyncDisposable` — a DbContext, a repository — makes a synchronous
  disposal throw, and that throw lands inside the try that classifies provider failures: the schedule would
  re-park for ever over a provider that answered every question correctly.

## Tests

Durable occurrences: the policy is pinned by `test/EverTask.Tests/Occurrences/DueSlotEnumeratorTests.cs` (pure
arithmetic, every combination of window, cap, overflow policy and budget, including the bound that makes a
three-month one-second backlog affordable), what the public surface REFUSES by
`Occurrences/DurableOccurrenceOptionsValidationTests.cs` (every cap, window, enum and host knob, plus the
`OnMisfire` callback that picks none or two), and the wiring by
`IntegrationTests/DurableOccurrencesIntegrationTests.cs` on a real host over a shared storage. Real databases:
`test/EverTask.Tests.Storage/CatchUpRecoveryIntegrationTests.cs` (SQLite, seeded downtime, restart mid-replay)
and `SqlServerDurableOccurrencesMultiHostTests.cs`, whose second test pins the single-active-host LIMIT rather
than a guarantee — it is the assertion the distributed-lease epic will invert.

Runtime schedule management: `IntegrationTests/RescheduleIntegrationTests.cs` on a real host over a shared
storage (the old slot really stops firing, a completion in flight really loses its compare-and-swap, a failed
re-park really publishes nothing), and `RecurringTests/ScheduleRebaseTests.cs` for the period arithmetic —
including the two negatives that matter: a cron schedule and a period with no slot are refused rather than
answered from the next period.

Integration tests build a real `IHost` through `test/EverTask.Tests/TestHelpers/IsolatedIntegrationTestBase.cs`.
The invariants above are pinned by `test/EverTask.Tests/IntegrationTests/QueueResilienceIntegrationTests.cs`,
`SchedulerResilienceTests.cs`, `WorkerQueueResilienceTests.cs`, `RecoveryPagePipelineTests.cs` (#39: a wedged page must not hold the one behind it, the cap really is a cap,
and the recovery still ends only when every wave it started has),
`MemoryStorageRecoveryFilterTests.cs` and, on a
real DB, `test/EverTask.Tests.Storage/SqlServerRecoveryIntegrationTests.cs` plus the recovery-filter section of
`EfCore/EfCoreTaskStorageTestsBase.cs` (run by all four EF Core providers: SQLite, SQL Server, PostgreSQL, MySQL).
The execution-vs-finalization split, the natural-successor grace and the injected clock are pinned by
`IntegrationTests/RecoveryExecutionVsFinalizationTests.cs`, `IntegrationTests/DeterministicSchedulingClockTests.cs`
and `SchedulerDeterministicClockTests.cs`; the two-wave barrier by
`RecoveryDurableScheduleBarrierTests.cs`, over a backlog that really spans several pages (a per-page
implementation passes anything smaller); the byte-identical default by
`Serialization/RecurringTaskGoldenJsonTests.cs` and `Serialization/ConsumerCompatibilityTests.cs` (whose
`LegacyMinimalTaskStorage` exists to be COMPILED: it breaks the day a new storage member stops being default).
The once-per-delivery release of the owned eager scope is pinned by
`IntegrationTests/EagerHandlerScopeReleaseTests.cs`, one test per exit that reaches neither `DoWorkCore` nor
the terminal rejection, each counting the disposals of a SCOPED probe injected into the handler; one of them
pins the ORDER on the rejection's recurring branch, by wrapping the real scheduler and noting how many
scopes had been released when the next occurrence was handed to it. Its last four walk the ENQUEUE boundary
instead, driving the real `WorkerQueue` and `WorkerQueueManager` with hand-built eager executors — the only
way to hold an id in flight and hand the queue a second executor of it on purpose, which is what startup
recovery does by accident on every restart that overlaps a live delivery. What a handler reads about its own
delivery — an occurrence's schedule, slot and run number included — is pinned by
`IntegrationTests/ExecutionContextIntegrationTests.cs`, and the row-to-executor half of it by
`RecoveredTaskFactoryTests.cs`.
