# EverTask Core

Refer to the root CLAUDE.md for project-wide rules. Recurring math: `Scheduler/Recurring/CLAUDE.md`.
Rate-limit gate: `RateLimiting/CLAUDE.md`.

Dispatcher, worker executor, bounded queue, schedulers (`PeriodicTimerScheduler` + `ShardedScheduler`) and the
in-memory storage.

## Gotchas

- **`Cancel`** blacklists the id (`Worker/WorkerBlacklist.cs`, lapses after `EntryTtl` ≈ 1 h), unschedules,
  bumps the gate epoch and releases the parking-lot entry; `DoWork` checks it BEFORE the gate, so a cancelled
  task never burns tokens.
- **A re-dispatch under the same taskKey UNDOES a cancellation** (`RestoreCancelledSchedule`) — the recurring
  row is reused, and both the blacklist entry and the `Cancelled` status follow its id.
- **The un-cancel writes FIRST, must ANSWER, and bumps the version** (`TryReviveCancelledSchedule`): nothing
  polls behind a swallowed failure, and only `ScheduleVersion` separates the new series' deliveries from the
  cancelled one's.
- **The blacklist entry is MOVED, not dropped** — `CoverOccurrencesTheCancelEndedAsync` covers each in-flight
  occurrence individually, asking the ROW so one found `InProgress` finishes; past the enqueue boundary
  nothing re-reads the row and `SetInProgress` is unconditional.
- **Only a revival clears a standing catch-up halt**; an ordinary re-registration keeps it, since re-declaring
  schedules at startup is not a request to replay.
- **Immediate dispatches are LAZY**: never resolve an eager transient handler from the dispatcher's root
  provider — it pins `IAsyncDisposable` instances until shutdown.
- **The execution context is built ONCE per delivery** in `DoWorkCore` and cleared in both that `finally` and
  `DoWork`'s, which covers a synchronous delivery whose post-execution step threw.
- **The slot is `NominalSlotOfDelivery`, never `ExecutionTime`** (the gate overwrites the latter), and
  `RunNumber` travels on the executor as `CurrentRunCount + 1`; both it and `ScheduleVersion` survive a
  taskKey re-registration.
- **An OCCURRENCE answers slot, run number and zone from its OWN row** (`RuntimeInfo`): its `CurrentRunCount`
  is 0 and it carries no recurring definition, so deriving reports run 1 and loses the zone entirely.
- **The terminal rate-limit rejection injects `Context` and `Logger` itself** and does not persist its log
  capture — it never enters `DoWorkCore`, and `Failed` is the only write a rejection may do.
- **The owned eager scope is released ONCE per delivery, on every exit** (`EagerHandlerOwnership`):
  `DoWorkCore` and the terminal rejection release BEFORE scheduling the next occurrence, `DoWork`'s `finally`
  covers the rest. Never enumerate the exits one by one.
- **The ENQUEUE boundary is the other half** (`Worker/DroppedDelivery`): only a TERMINAL drop releases, since
  `QueueFull` and `DuplicateInProcess` are retried on the same instance and whoever turns one into a verdict
  releases it. Exception paths never release — schedulers read them as a full queue.
- **`Attempt` moves only when an attempt is admitted INTO the handler** and rolls back when `ExecuteTask`
  unwinds, or `OnError` reports an attempt abandoned before it ran.
- **Monitoring events go through `WorkerExecutor.RegisterEvent` only** — never log and publish by hand, never
  call `WorkerExecutorLog`'s `SkipEnabledCheck` methods from outside (bug #32).

## Queue & Recovery Resilience (no task loss, no deadlock)

**Lifecycle invariant**: every persisted task is executed or left in a status `RetrievePending` recovers. A
recovery page is the UNION of `IsRecoverableForExecution(now)` and `IsRecurringSeriesToFinalize()`. The
execution predicate has three server-side copies that must stay in sync (`EfCoreTaskStorage`, the inline list
in `SqliteTaskStorage.RetrievePending`, the Postgres partial index `IX_QueuedTasks_Recovery` — a new status
needs a NEW migration); see the four-places rule in `src/Storage/EverTask.Storage.EfCore/CLAUDE.md`.
`TrySetQueuedIfRecoverable` applies ONLY the execution predicate: a spent series must be finalized.

- **The temporal term is grouped deliberately** — `RunUntil` gains
  `|| (IsRecurring && NextRunUtc != null && NextRunUtc < RunUntil)`, or a slot scheduled before a boundary
  that elapsed *during* the downtime is lost and the row stays `Queued` for ever.
- **The grace window asks the NATURAL successor** (`NextGridOccurrenceAfter`, bounds ignored): the bounded one
  is null both for a current slot and an ended series. Order: finalize → grace → skip-forward.
- **Both finalization sites compare-and-swap on the row the DECISION came from** (cursor, status and version
  carried in `DispatchRowMetadata`, never re-read at write time), or a `Cancelled` a cancel just wrote becomes
  the guard's own expectation. Legacy stores keep the unconditional write; one with durable occurrences but no
  versioning leaves the row alone, its occurrences can race the snapshot.
- **The recovery-failure counter reset runs OUTSIDE the guarded try**, so a failing reset is not reclassified
  as a failing finalization.
- **A poison is REPORTED only once the ROW confirms it** (`WorkerService.TryPoisonAsync`, every site): the
  writes are best effort on every relational provider, so the row is re-read against BOTH predicates. A write
  that THROWS is contained there too — letting it out aborted a whole wave.
- **Startup order**: consumers start FIRST and recovery runs concurrently; recover-before-consume reintroduces
  the capacity deadlock.
- **Recovery runs in two waves** (`RecoverWaveAsync`) — durable schedules wait until after the pagination
  loop, because a schedule counts its active occurrences the moment it is back and one still in a later page
  reads a phantom low count.
- **The second wave is a SECOND KEYSET SCAN, not a buffer**, so memory stays bounded whatever the backlog is;
  two scalars cross the loop (whether a durable schedule was seen, and the keyset position just before the
  first one) and already-recovered rows are filtered out.
- **`RecoveredTaskFactory.FromRow` is the single place row-to-executor mapping lives**, and its `RowMetadata`
  travels through the internal `ExecuteDispatch` overload so a recovered executor keeps its parent, occurrence
  JSON, schedule version and STORED queue. Unreadable `OccurrenceRuntimeInfo` JSON is not an error — the
  columns answer instead.
- **The reader does not wait for the page it handed over** (#39, `WavePipeline`, both passes) up to
  `MaxRecoveryPagesInFlight`, or one blocking enqueue toward a saturated queue holds rows belonging to idle
  queues. A slot is freed by whichever wave finishes FIRST and the gate sits BEFORE the read; the cap
  multiplies concurrency (pages × queues × `MaxDegreeOfParallelism`) and at ONE is the old behaviour exactly.
- **The two-wave barrier does not move**: the pipeline is DRAINED before the second wave, the reader decides
  the durable count and rescan position in page order, and a failing wave ends the recovery only after the
  running waves have settled.
- **`recoveryCutoff` is STRICT (`CreatedAtUtc < cutoff`)** — the wall clock is coarse (≈15 ms on Windows), so
  `<=` re-dispatches a live row sharing the tick; `TaskDeliveryRegistry` is the actual defense.
- **`isRecovery`**: (1) blocking enqueue, never drop; (2) preserves the stored `NextRunUtc`, since
  recalculating past it skips an occurrence; (3) skips `UpdateTask`, so a live taskKey re-registration is not
  overwritten; (4) carries the persisted `AuditLevel` (null ⇒ Full), or the task reverts to the global one.
- **Double-execution defense (at-least-once)**: `TaskDeliveryRegistry` (one per host, shared by all queues)
  holds each `PersistenceId` from the channel write until the delivery terminally ends. **Exactly ONE `End`
  per delivery** — `DoWork`'s outer `finally`, `WorkerQueue`'s rollback paths, or the channel `itemDropped`
  callback; never add another site.
- **CancellationToken** flows `Dispatch → TryEnqueue → WriteAsync`: a cancel during a full-queue wait raises
  OCE to the caller and leaves the task `Queued`, never `Failed`.
- **Schedulers never block on a full queue** (`TryEnqueueImmediate`, then re-enqueue at
  `now + FullQueueRetryDelay`) and never mark `Failed` on shutdown or transient errors — only real handler
  failures fail a task. `QueueFullBehavior` applies to immediate dispatches only.

## Deterministic scheduling clock

ONE `TimeProvider` (`TryAddSingleton(TimeProvider.System)` in `AddEverTask`) governs every scheduling
decision: dispatcher, `IScheduleEvaluator`, builders, both schedulers, recovery, rate limiter, gate, parking
lot. Register one before `AddEverTask` and the whole pipeline follows it.

- **Storage never resolves the clock** — the core calls the `nowUtc` overloads, default members delegating to
  the intact legacy signatures, so a custom storage keeps its own override and its own atomicity.
- **The schedulers race the signal against `Task.Delay(delay, timeProvider)`**, not
  `SemaphoreSlim.WaitAsync(timeout)` whose timeout is hard-wired to the real clock; the waiter is CREATED ONCE
  and kept, since abandoning it silently consumes the next `Release`.
- **Out of scope**: retry policies own their waits, audit and logging stay on the real clock — a fake clock's
  `Advance()` will not complete a retry delay.

## Durable occurrences (opt-in; the default path is untouched)

`OccurrenceMode.Durable` turns a schedule into a definition plus a cursor:
`Scheduler/Occurrences/OccurrenceMaterializer` writes each due slot as its own one-shot child row.
`IsScheduleOnly` is the only branch, false unless the definition opted in. Contracts in
`docs/recurring-tasks/durable-occurrences.md`: at-least-once, single ACTIVE host — materialization is
idempotent across hosts (unique index), execution is claimed by nobody.

- **The materializer is the ONLY thing that moves a durable schedule**, through one `RunAsync` for every entry
  point (its own slot, an occurrence's end-of-run kick, recovery's second wave, the operational re-park); it
  re-reads the row and compare-and-swaps every write, and the per-schedule gate COALESCES via a pending flag
  so a kick is never dropped.
- **Every entry point hands it the CALLER's token**: a run re-plans over a grid that may do real I/O, so
  `CancellationToken.None` let an unanswering calendar hold the registry entry, the consumer and the shutdown.
  A kick the shutdown cancels is not a failure and is not logged as one.
- **A failed run re-parks the schedule itself, and the re-park is armed INSIDE the gate** so it covers the
  runs the holder absorbed; it prefers the delivered executor, since what usually failed is the storage.
  `MaterializeScheduleAsync` is the backstop outside the gate.
- **A row this build cannot rebuild is PARKED, not dropped** (`ParkUnusableRow`) — the verdict belongs to
  startup recovery, which owns the bounded retry and terminal poison; only the delivered executor may be
  re-parked, since rebuilding is what just failed and one built without the durable definition runs the handler.
- **A row that is no longer durable is the opposite** — never re-park the durable executor over an inline
  registration, but confirm with one registry lookup and rebuild FROM THE ROW if nobody parked it, because a
  re-park that FAILED is the only thing that lets an old durable delivery reach a rewritten row.
- **One run, ONE reading of the row**: version, cursor, status, run count and queue are snapshotted at the
  start and every CAS carries them, or the write absorbs a concurrent change — the in-memory store hands back
  LIVE entities.
- **A halted schedule is NOT re-parked** (a halt never releases itself; only `ResumeSchedule`/`Reschedule`
  clear it, both parking the row), or the row goes through the queue every minute for ever. A halt whose CAS
  was lost IS re-parked — an ordinary lost race.
- **`AlreadyExists` advances the cursor, it is not a re-read**, and the run keeps walking **in the same PASS**:
  ending there made a rewound cursor crawl at one slot per `BacklogRetryInterval`, and re-planning per slot
  made a run quadratic. ONE plan per run, `MaxServedSlotsPerRun` bounds the walk.
- **`RunBudgetEndsSeries` decides which write ends the series, not the plan's null cursor** — a served slot
  spends no run, so the budget's last real materialization nulls the cursor in its own commit. A row created
  after walking is stamped `MisfireAfterWalking`: the range starts at that slot and the count drops by the
  slots walked.
- **Every count in `DueSlotEnumerator` is bounded where a bound buys something** — a grid counting by division
  is asked for the real total (a cap turns "7,900,000 runs lost" into "10,001"), a WALKED grid keeps the cap
  and carries `IsExact = false` so no surface reports a truncated number as a total. `SkipOldest` bisects the
  INSTANT axis, each probe a bounded forward count, never an enumeration.
- **One count, ONE bound: the caller's.** `CountMissedOccurrences` spends the whole cap it is handed and
  `MaxSkipCountIterations` applies only to the ask carrying none; a second smaller bound makes every larger
  answer "10,001", read as a total. An uncapped ask falls back to `DiagnosticCountCap`.
- **The `Halt` breaker is bounded by the RUN BUDGET too**, firing only when `MaxRuns` allows more occurrences
  than the cap: a series with one run left has no flood to stop. `SkipOldest` is NOT short-circuited — which
  end matters is the caller's policy.
- **What makes a slot MISSED is `SetMisfireThreshold`**, read by the planner exactly as `TaskExecutionContext`
  reads it at delivery; a slot older than the threshold is missed work even when it is the ONLY one owed, and
  the threshold only ever WIDENS the definition.
- **A misfire's range and its count are one statement** — `MissedFromUtc`/`MissedThroughUtc` bound the backlog
  the decision saw and `MissedCount` counts the grid slots inside it, both ends included, so the range ends at
  the newest slot owed and not at the last slot this run's budget allowed.
- **Skipping a slot usually costs no write** (the first materialization carries the cursor across);
  `TryAdvanceScheduleCursor` covers the case where nothing survives, counting no run and writing no audit.
- **Every dropped run says WHICH rule dropped it** (`SlotLoss.Reason`; EventIds 1802, 1819, 1820) and one plan
  can carry two — merging the age window's count into `SkipOldest`'s sends operators to widen a window that
  dropped a fraction of the loss.
- **A replay's two boundary events are decided from the PLAN** (`TrackCatchUpEpisode`): only a plan stamping
  catch-up rows opens an episode and only an `Exhausted` plan closes it, since a `WindowFull` run plans nothing
  precisely while the replay is busiest. Per-process; a halt or cancel ends it without a completion.
- **A stale occurrence keeps consuming the budget until it TERMINATES** — reconciliation requeues one that is
  non-terminal but neither delivering nor parked, under a CAS on the status it was found in, and it still
  counts as active. Without `IScheduler.SupportsScheduleInspection` nothing is reconciled at all.
- **The executor is REBUILT BEFORE the requeue** and a row that cannot produce one is marked `Failed`, or the
  row writes a `Queued` transition plus a `StatusAudit` row every run for ever while holding a slot of a budget
  of one. `RequeueTerminal` is the way back after a deploy.
- **A row is unusable for TWO reasons** — the payload may not rebuild, and the rebuilt payload may have no
  handler left; resolution happens inside `Handle`, so that second one escaped as an exception out of the whole
  reconciliation and stalled the series on every pass.
- **A handler that failed to BUILD is neither**: a scoped factory that threw looks identical, so the container
  is asked whether anything is registered for the task at all and only a "no" is final. It keeps its slot and
  is retried, but only `MaxOccurrenceRebuildAttempts` **process starts** in a row (on the row's
  `RecoveryDispatchFailureCount`, cleared by a success; `_rebuildFailuresCounted` is the deliberately
  non-durable memory of an outage already counted) — counting per RUN spent the ceiling in five minutes and
  made a failover indistinguishable from a misconfiguration. Reaching it poisons the row under EventId 1825.
- **Only a CONFIRMED terminal state frees capacity** — `SetStatus` is best effort, so the row is re-read before
  the slot is counted free, or a swallowed write leaves two occurrences under a budget of one. The read-back
  also decides what is REPORTED, so a run that could not end the row says so.
- **The schedule row is not a delivery**: never `DoWorkCore`, never `InProgress`, never the rate-limit gate,
  never `QueueNextOccourrence` — rate limiting applies per key to the occurrences, and `ExtractRateLimit`
  returns nothing for a durable definition.
- **A schedule's blacklist entry covers its occurrences**, checked in `WorkerQueue.IsCancelled` and a THIRD
  time in `DoWorkCore` right before the unconditional `SetInProgress`, because the gate's in-slot wait and the
  handler resolution sit between the two. Both checks ask BOTH halves, and only the entry covering THIS
  delivery alone is consumed — never the schedule's, which still covers the siblings.
- **`Cancel` asks for occurrences TWICE and the second time is the one that matters**: a materializer holding
  the row before the first read had not inserted its occurrence yet, so asked again AFTER the status is
  persisted the answer is final. A lookup that THREW cascades too — a failed query does not let a cancel
  declare the series terminal.
- **The cancelled set is the complement of the requeued one, on every path**: `HandleExceptionAsync` classifies
  a shutdown OCE as a user cancel when the schedule is blacklisted (else `ServiceStopped`, which recovery
  re-queues), and recovery cancels an occurrence whose schedule row is `Cancelled` — the hard-crash case, where
  no blacklist survives.
- **Children are always lazy and inherit the parent's STORED queue**, since an occurrence carries no recurring
  definition and the fallback for one of those is the default queue.

## Runtime schedule management (`ITaskScheduleManager`)

`Dispatcher/TaskScheduleManager` is the one place an ALREADY registered schedule is changed: read the row,
decide the new definition and cursor, write with `UpdateSchedule`, hand the row back to whoever owns its
parking.

- **It shares the dispatcher's per-taskKey section** (`Dispatcher/TaskKeyLockRegistry`) — a dispatch under the
  same key is the same read-decide-write and its `UpdateTask` carries no version, so without one section a
  dispatch that read first overwrites a reschedule that just committed. Resolved lazily, with a private
  fallback so a hand-wired dispatcher still serializes its own dispatches.
- **Nothing here is best effort**: a lost CAS is reported to the caller, and a definition with no occurrence
  left is REFUSED rather than written, since a null cursor satisfies neither recovery predicate.
- **Each method gates on the capability it actually uses**, documented method by method: rewriting a schedule
  row needs `SupportsScheduleVersioning`, `RequeueFailedOccurrence` needs `SupportsDurableOccurrences`,
  `CancelSchedule` needs only a storage. What a call WRITES gates too — a `Reschedule` to a durable definition
  goes through `RequireDurableOccurrenceSupport` inside `Build`, before any write.
- **A requeue asks the SCHEDULE TWICE**, like `Cancel`: only the parent's `Cancelled` refuses a target that by
  status alone looks legitimate, but that check and `RequeueTerminal` are two round trips and a `Failed`
  occurrence is not in the cascaded set, so the parent is re-read AFTER the requeue commits. The occurrence's
  OWN blacklist entry is dropped here (nothing else consumes it); the schedule's is left alone, and a schedule
  merely OVER is not refused.
- **A re-park belonging to a REPLACED definition is refused, not merely late**: `IScheduler.TrySchedule` is the
  conditional half of latest-wins, and everything holding a retired executor goes through it (the advance's
  next occurrence, the rate-limit skip, the gate's deferral and in-flight re-park, the operational retry,
  `ReparkFromRowAsync`). The comparison lives INSIDE the registry's swap because every ordering outside it has
  a window; a scheduler that cannot compare versions keeps the unconditional default body.
- **Re-park, THEN publish, and bump the gate epoch LAST** — every other order has a window where a stale
  registration survives or the series is parked nowhere. Never `TryUnschedule` first, and never bump the epoch
  before the re-park: one that throws produces no registration, and the gate's set-then-check then deletes the
  only one still holding the series.
- **The version is not published when the parking ENDED the series**: a resume whose replanned backlog spends
  the last run closes the series inside the call, and publishing afterwards puts the entry back for a schedule
  that will never run again.
- **The reschedule event is published on BOTH exits**, since the write commits before the parking is attempted
  and a subscriber told only that a re-park failed has no record of the version, cursors, mode or backlog the
  row now carries. The sentence names the version it came FROM as well as the new one.
- **A durable schedule is parked by the MATERIALIZER**, not by `Schedule()` — it decides which slot the row
  waits for and re-reads what this call just wrote.
- **`ResumeSchedule` keeps the cursor**, releasing a halted catch-up by planning its backlog AGAIN against the
  current definition (halting again if it still overflows); moving the cursor does what the halt prevented.
  Every operation clears the halt marker, because asking is what these calls are.
- **The row is read ONCE**: the in-memory store's LIVE entities report what was just written as what was there
  before.
- **`UpdateSchedule` compare-and-swaps on the CURSOR as well as the version, and refuses a `Cancelled` row.**
  An advance moves cursor and run counter without touching the version, so a version-only guard let a
  reschedule commit over superseded values; a cancel moves neither, so the status is checked apart. A `null`
  expectation is legal, means "the series has ended" and must be read as IS NULL, not equality. Only
  `Cancelled` refuses; `InProgress` never does. The row is re-read on the FAILURE path alone, to say which lost.
- **`Scheduler/Recurring/ScheduleRebase`** owns `RescheduleMode.RebaseFromCursor`: the old cursor's nominal
  period under the OLD definition, then the NEW definition's slot at the same POSITION inside it — "the first
  slot" is right only while a period holds ONE. BOUNDS are checked apart from the period arithmetic, since two
  of the three branches never touch the grid and so never reach `FirstOccurrenceOnOrAfter`, the only thing
  applying `RunUntil`. It refuses more than it accepts on purpose — a different cadence or selector, a cron on
  either side, a period with no slot each lose or replay a period of work. See that namespace's gotcha 18.

`WorkerExecutor` is the other half. `IsSupersededSchedule` drops an INLINE delivery below the published version
(never a durable schedule row: it runs no handler, and dropping it consumes the registration that produced it),
and a missing entry is not a lower bound of zero — that keeps a recovered executor alive across a restart.
`AdvanceVersionedRunAsync` is the CAS advance; on a mismatch it re-aims at the row's own cursor rather than
dropping the write, because the run happened and has to be recorded.

- **A run already EXECUTING when its definition was replaced does not persist its ending either** — `Cancelled`
  is the status no recovery predicate selects and no advance moves past, so a late ending kills the series that
  just took the row over. Only the storage write is skipped: the callback and the monitoring event still fire.
- **The registry is the fast answer, never the whole one** (`PersistEndingAsync`): it is EMPTY for the whole
  cancel-to-publish span, so the write is `TrySetTerminalOutcome`, compare-and-swapped on the version the
  DELIVERY carries. A plain cancel moves neither version nor cursor, so that write refuses anything but a
  cancellation over a `Cancelled` row — a late `Failed` erased the cancellation and is itself recoverable.
  Storage without `SupportsScheduleVersioning` keeps the unconditional writes byte for byte.
- **Past the last re-aim the GUARD is given up, never the write**: recording nothing left the row `InProgress`,
  the run unaudited, `CurrentRunCount` (and with it `MaxRuns`) permanently short and the series parked nowhere.
  The last attempt writes unconditionally against the cursor the last reading carried, and the row travels back
  so the caller parks the schedule from it.
- **A schedule the manager can ADDRESS is compare-and-swapped from its first advance**, the address being the
  taskKey; a recurring row without one keeps the unconditional writes byte for byte. Deciding on "has it been
  rescheduled yet" cannot be done without a race.
- **The rate-limit SKIP ends a series under the same CAS as every other finalization** — it is the one advance
  that writes nothing, so a last skip wrote `Completed` and a null cursor over a row a reschedule had just
  extended, answering NEITHER recovery predicate. The expectation is the DELIVERY's version plus the cursor and
  status snapshotted at the top of `QueueNextOccourrence`; a lost CAS parks the row from itself and KEEPS the
  registry entry.
- **A DURABLE series drops its published version inside the materializer**, because that is where it ends: the
  cursor is nulled in the same commit as the terminal status, so the row never passes through
  `QueueNextOccourrence`. Otherwise the registry grows by one entry per finished schedule, for ever.
- **An advance that applied against a definition it never saw PARKS the row itself** (`ReparkFromRowAsync`, from
  that row's definition/cursor/version, never the delivery's): the one thing that lets a replaced delivery reach
  a rewritten row is a re-park that FAILED, and returning empty-handed left the series parked nowhere. A row
  that turned durable goes to the materializer instead.

## Occurrence grid: one seam

`IScheduleEvaluator` (internal, `ScheduleEvaluator`) is what the dispatcher, the worker and the recovery ask
about a schedule's grid — never the occurrence math directly. Built-in definitions get a synchronous wrapper
over `RecurringTask`'s pure primitives; the async shape exists for an `INextOccurrenceProvider` grid, which is
real I/O. Its two new questions are `NextGridOccurrenceAfterAsync` (the natural successor, bounds ignored) and
`EnumerateDueSlotsAsync` (slots already owed, oldest first, under a MANDATORY cap).

- **A provider grid answers through the same seam, so nothing above it branches** (`ProviderScheduleGrid`), and
  the non-grid parts of the math — first-run configuration, termination bounds, realignment past a downtime —
  are REUSED through `RecurringTask.PlanNextRun` / `SelectNextRun`, never mirrored. `GetNextOccurrence` THROWS
  for a provider definition, since "no occurrence, ever" reads as a series that has ended.
- **A provider failure is transient and writes NOTHING** — wrapped in `OccurrenceProviderException` with the
  backoff its schedule earned (`OccurrenceProviderRetryRegistry`, doubling per consecutive failure, reset by one
  answer), and every caller parks its row instead of acting on a grid it does not have: the recovery branch
  re-parks an `IsScheduleRetry` delivery (which re-reads the row and re-runs the DECISION, since parking an
  ordinary delivery would execute a slot nobody chose), `QueueNextOccourrence` does the same for a failed
  advance, and the materializer uses the provider's backoff. It must never reach the recovery's generic catch,
  whose counter would poison the row; an UNREGISTERED key is the opposite verdict — `ArgumentException` at
  dispatch, terminal poison at recovery.
- **"Writes nothing" includes the QUEUE boundary**: a retry delivery is the one enqueue that skips
  `TrySetQueuedIfRecoverable`, over a row whose whole point is to look untouched. The check moves to
  `RetryScheduleDecisionAsync`, which abandons a schedule cancelled, removed or finished under the wait.
- **A deferral is a THIRD dispatch outcome** (`ScheduleDeferredByProviderException`): returning the id cleared a
  failure counter a restart had earned and letting the exception out poisoned the row, so the counter is
  untouched and the row still counts as recovered.
- **Nothing decided by RESOLVING a provider runs on the recovery path**: `RequireProviderSupportAsync` keeps its
  key check but returns before the `SkipOldest` determinism probe when `isRecovery`, since neither that probe's
  `InvalidOperationException` nor an unreachable database is an `OccurrenceProviderException` and both would
  sail into the poison counter.
- **A re-park that FAILED is an error EVENT, not only a log line**, since nothing polls behind it; four sites
  say it identically — `Dispatcher.ParkProviderRetryAsync`, `OccurrenceMaterializer.ReParkAfterFailureAsync`
  (whose `ProviderRetryParkOutcome` keeps `DeferForProviderAsync` from publishing a success sentence over it),
  `WorkerExecutor.DeferScheduleForProvider` and `WorkerExecutor.DeferScheduleForExclusionAsync` (#36).
- **"Parked to ask again" is said only once the registration is IN**, on all three: `TrySchedule` also ANSWERS
  false, and reading that as a park announced a schedule parked nowhere. A refusal reports itself instead
  (`ProviderRetryParkRefused` 1021, `ScheduleReparkRefused` 1822) and says nothing else.
- **The retry delivery carries the interrupted slot BESIDE the instant it fires at**
  (`ScheduleRetryFromUtc` next to `ExecutionTime = retryAt`): with storage the row's cursor answers, but with NO
  storage the delivery is the only place that slot exists, so `RetryScheduleDecisionAsync` advances from
  `ScheduleRetryFromUtc ?? ExecutionTime` rather than skipping everything in between. It drops BOTH marks first,
  or the occurrence that advance parks comes back to decide again instead of running.
- **The advance is the one place a provider failure costs something**: the write and the next cursor are one
  operation, so the row is left as a crash between a side effect and its write leaves it, and at-least-once
  covers the replay.
- **Every walk over a provider grid is bounded by round trips, not by patience** —
  `ProviderScheduleGrid.DiagnosticCapFor` is the ONE rule for diagnostic counts, so a provider walks at most
  `MaxDiagnosticWalk` and the number says it is a lower bound. A count feeding a DECISION keeps the caller's own
  cap whatever the grid is, which is why a large `MaxOccurrences` really costs that many questions — the
  documented knob to size. The `SkipOldest` bisection stops at the first probe counting exactly N.
- **The skip-forward count says whether it is exact too** (`NextRunResult.SkippedCountIsExact`), because it
  reaches a log line and a monitoring event; it defaults to `true`, so the built-in path is byte-identical.
- **Its two bounds must not MULTIPLY**: each probe counts up to the cap one round trip at a time, so
  `FindNthSlotFromEndAsync` memoizes the stretch (provider grids only) and no instant is asked twice. Sound for
  exactly the grids `SkipOldest` accepts, since a provider answering differently twice is refused at dispatch;
  `CountCeiling` is shared so memoized and live counts answer identically.
- **A cap bounds an EPISODE, so the count deciding it is taken once per episode, not once per run**, or a serial
  replay planning once per occurrence is quadratic in its own backlog. `MeasureBacklogAsync` keeps what a run
  measured and the next run CONTINUES it — guarded: provider grids only, continued only when this run resumes
  at exactly the cursor the last plan left on the same grid, dropped if the count was ever a lower bound or the
  grid no longer owes that slot, re-taken after `MaxContinuedMeasurements`.
- **A plan that may create nothing measures nothing**: `PlanCatchUpAsync` exits early one step later than its
  siblings, since the age window drops slots without capacity but materializing one does not. The only decision
  it defers is the halt, which is about whether a REPLAY may start.
- **The provider's scope is disposed ASYNCHRONOUSLY** (`CreateAsyncScope`, both resolution sites), or a scoped
  `IAsyncDisposable` throws on synchronous disposal inside the try that classifies provider failures, and the
  schedule re-parks for ever over a healthy provider.

## Tests

Integration tests build a real `IHost` through `test/EverTask.Tests/TestHelpers/IsolatedIntegrationTestBase.cs`.

- Queue & recovery: `IntegrationTests/QueueResilienceIntegrationTests.cs`, `SchedulerResilienceTests.cs`,
  `WorkerQueueResilienceTests.cs`, `MemoryStorageRecoveryFilterTests.cs`, `RecoveryPagePipelineTests.cs` (#39),
  `RecoveryDurableScheduleBarrierTests.cs` (a backlog spanning several pages — a per-page implementation passes
  anything smaller), `RecoveryPoisonOutcomeTests.cs`, `RecoveredTaskFactoryTests.cs`,
  `IntegrationTests/RecoveryExecutionVsFinalizationTests.cs`, and on a real DB
  `test/EverTask.Tests.Storage/SqlServerRecoveryIntegrationTests.cs` plus the recovery-filter section of
  `EfCore/EfCoreTaskStorageTestsBase.cs` (all four EF Core providers).
- Clock: `IntegrationTests/DeterministicSchedulingClockTests.cs`, `SchedulerDeterministicClockTests.cs`.
- Durable occurrences: `Occurrences/DueSlotEnumeratorTests.cs` (pure policy arithmetic),
  `Occurrences/DurableOccurrenceOptionsValidationTests.cs` (what the public surface REFUSES),
  `IntegrationTests/DurableOccurrencesIntegrationTests.cs`, and on real databases
  `test/EverTask.Tests.Storage/CatchUpRecoveryIntegrationTests.cs` and
  `SqlServerDurableOccurrencesMultiHostTests.cs`, whose second test pins the single-active-host LIMIT — the
  assertion the distributed-lease epic will invert.
- Schedule management: `IntegrationTests/RescheduleIntegrationTests.cs` and
  `RecurringTests/ScheduleRebaseTests.cs`, including the two negatives that matter (a cron schedule and a period
  with no slot are refused, not answered from the next period).
- Scope release: `IntegrationTests/EagerHandlerScopeReleaseTests.cs`, one test per exit reaching neither
  `DoWorkCore` nor the terminal rejection; the last four drive the real `WorkerQueue` / `WorkerQueueManager`
  with hand-built eager executors to walk the ENQUEUE boundary.
- Execution context: `IntegrationTests/ExecutionContextIntegrationTests.cs`. Serialization defaults:
  `Serialization/RecurringTaskGoldenJsonTests.cs` and `Serialization/ConsumerCompatibilityTests.cs`, whose
  `LegacyMinimalTaskStorage` exists to be COMPILED — it breaks the day a new storage member stops being default.
