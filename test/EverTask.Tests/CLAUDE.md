# EverTask.Tests

Refer to the root CLAUDE.md for project-wide rules. Core-library tests (`src/EverTask`), fully in-memory — no
Docker or Testcontainers here. Subsets filter on namespace: `--filter "FullyQualifiedName~IntegrationTests"`
(also `~RecurringTests`, `~RateLimiting`, `~Serialization`, `~MultiQueue`).

## Helpers (`TestHelpers/`)

- Integration tests inherit `IsolatedIntegrationTestBase` and build their host with
  `CreateIsolatedHostAsync(channelCapacity, maxDegreeOfParallelism, configureEverTask, configureServices, clock)`:
  one `IHost` per test, disposed by the base, which re-exposes the common waits as instance methods.
- Poll with `TaskWaitHelper`, never `Task.Delay`. Argument order is
  `WaitForTaskStatusAsync(storage, taskId, expectedStatus, timeoutMs)` — storage FIRST, timeout an `int` in ms.
- `TestTaskStateManager` is keyed by a `string taskKey`, not by task id, and only records (`RecordStart`,
  `RecordCompletion`, `IncrementCounter`, `GetState`) — no wait method; pair it with `WaitForCounterAsync`.

## Gotchas

- **`startHost: false` does NOT pause the scheduler**: `PeriodicTimerScheduler` / `ShardedScheduler` start
  `ProcessScheduledTasksAsync` in their constructor, so an already-due occurrence (e.g. `RunNow()`) can reach
  the worker queue and be marked `Queued` right after `Dispatch` returns — only the consumers wait for
  `Host.StartAsync()`. Use `WaitForTaskAcceptedAsync` (or assert `ShouldBeOneOf(WaitingQueue, Queued)`);
  asserting `WaitingQueue` alone is flaky under load.
- **Driving the clock**: pass `clock: new FakeTimeProvider(instant)` to either `CreateIsolatedHost…` overload.
  The base registers it AFTER `AddEverTask` — which uses `TryAddSingleton(TimeProvider.System)`, so an earlier
  registration would lose — and exposes it as `Clock`. Seed rows from `Clock.GetUtcNow()` rather than
  `DateTimeOffset.UtcNow` and the test reads identically on either clock; that is what lets
  `RunUntilPastNextRunRecoveryReproTests` run its original assertions a decade away from the wall clock.
  Every scheduling decision follows the injected provider. `FakeTimeProvider` also overrides `CreateTimer`, so
  the `Task.Delay(delay, timeProvider)` both schedulers and the parking lot wait on is VIRTUAL: a frozen clock
  means a wait that never ends, and `Advance()` is the only thing that ends it. Wait for the code under test
  to arm its delay (`WaitForPendingTimersAsync`) before jumping, or the jump lands before there is anything to
  elapse. Retry delays are out of scope (they run on `IRetryPolicy`'s own clock) — never expect `Advance()` to
  complete one.
- **Which clock the CONTAINER hands out is a separate question.** `RateLimiting/RateLimiterDeterministicClockTests`
  resolves the limiter, the gate and the parking lot from a real `AddEverTask` container, because two of the
  three are built by hand-written factories that pass the clock explicitly: every other rate-limiting test
  constructs them directly and so only proves they read the clock they are GIVEN.
- **Compile-time compatibility pin**: `Serialization/ConsumerCompatibilityTests.LegacyMinimalTaskStorage`
  implements only what `ITaskStorage` required before durable occurrences. It exists to be COMPILED — the day
  a new storage member stops being a default one, this class fails to build. Its handler twin is
  `TestTasks.ExecutionContext.cs`'s `RawInterfaceTaskHandler`: it implements `IEverTaskHandler<T>` directly
  and declares no `SetExecutionContext`, and `ExecutionContextIntegrationTests` also DISPATCHES it, so the
  interface's default body runs on a real delivery instead of only compiling. `RawInterfaceContextTaskHandler`
  is the other half — it implements that member, so the injector's call is proven to reach the interface slot.
  Both are recompiled against the new sources; the assembly that is not is in the binary pin below.
- **Binary compatibility pin**: `test/EverTask.ConsumerCompatibility.Baseline` is compiled against the
  `issue23-baseline` packages in `nupkg/issue23-baseline` and RUN against the current assemblies by the same
  test class, a major apart (3.11 → 4.0) — both its tests assert that distance, so a missed version bump
  cannot turn the proof into 3.11 against 3.11. It is the only thing that catches an optional parameter
  appended to an existing public method or constructor — source-level probes keep compiling while the IL
  signature changes. Add a public method, not an optional parameter; see that project's `README.md` for the
  wiring and for repacking the baseline.
  Its `BaselineScheduleBuilders.cs` is the same proof for the fluent API: one type implementing all ten
  schedule builder interfaces as the baseline declared them, so constructing it builds an interface map
  against TODAY's interfaces and an `InTimeZone` that had arrived abstract would fail the type load. The test
  then calls all eight `InTimeZone` slots on it and expects `NotSupportedException` — the default bodies are
  reachable, and they refuse rather than drop the zone.
  Its `BaselineHandlers.cs` carries the other half: two handlers built when neither `SetExecutionContext` nor
  `EverTaskHandler<T>.Context` existed — one implementing `IEverTaskHandler<T>` directly, one deriving from
  the base class — dispatched on a REAL host by
  `ConsumerCompatibilityTests.Handlers_compiled_against_the_baseline_are_still_executed_end_to_end`. Their
  interface map is built against today's interface, so a member arriving abstract fails the type load where a
  recompiled twin would just keep building. They record through a `BaselineHandlerProbe` the test registers,
  because the fixture may reference nothing but the baseline packages.
- **Fault injection**: `TestHelpers/FaultInjectingTaskStorage` wraps a REAL storage and throws only where the
  test arms it (`FailNext` / `FailAlways` / `Heal`), so the failure and the recovery from it both execute for
  real. Used by `RecoveryFinalizationFailureTests` and by the durable-occurrence kick test; a mock in its
  place would make both fictional. It forwards EVERY default interface member explicitly — a new one left out
  silently runs the interface's own default (a `NotSupportedException`) instead of the inner store.
  `RunBefore(operation, hook)` is the other half: the hook runs on the calling thread just before the
  operation reaches the inner store, so a test can land something else inside the window that operation is
  about to open (a cancel arriving mid-`MaterializeOccurrence`) or measure how many callers are inside one at
  once (the global materialization budget). A blocking hook blocks its caller — that is the point.
  `SwallowNext(operation, times)` is the third: the call RETURNS without reaching the store, which is the
  shape of a write every relational provider swallows (`SetStatus` logs its own failure and returns), and the
  only one honoured — a fault that throws is a different test, because there the caller sees the failure.
- **Durable occurrences**: the policy is pure arithmetic and lives in `Occurrences/DueSlotEnumeratorTests`
  (including the two halves of a misfire agreeing with each other, a count that says whether it is a total or
  a lower bound, and a catch-up whose backlog CONTAINS a DST transition — checked against the grid itself,
  walked one step at a time, because a zoned calendar grid is the one shape no shortcut in that class
  applies to); the backfill cursor is `RecurringTests/BackfillCursorTests` at the grid level and
  `DurableOccurrencesIntegrationTests.A_backfilled_schedule_starts_its_cursor_in_the_past…` through the
  public builder;
  the wiring is `IntegrationTests/DurableOccurrencesIntegrationTests`, which SEEDS its backlogs as rows
  (`_shared`, one `MemoryTaskStorage` registered into every host the test builds, so "restart" means the same
  rows in a new process). There is no honest way to produce a downtime by sleeping. `TestTasks.DurableOccurrences.cs`
  carries the recorder: it tracks the highest number of handlers inside `Handle` at once, which is what the
  `MaxPendingOccurrences` tests assert, and its handlers override `RetryPolicy` with ONE quick retry so a test
  that wants a `Failed` occurrence does not wait out the global three at half a second. It also carries
  `ResolutionGate`, whose handler blocks in its CONSTRUCTOR so a test can hold a delivery inside DI
  resolution — the stretch between the blacklist check and the `InProgress` write. **Arm it only after
  building the executor**: the dispatch path resolves the handler once already, for its per-type metadata, and
  a gate that holds every resolution hangs the test instead of the delivery it meant to hold.
  `HandlerlessOccurrenceTask` has NO handler on purpose — it is a row that rebuilds its payload and then finds
  nothing to run it, the half of "unusable" that used to throw out of reconciliation. Writing a handler for it
  would quietly retire the test that pins that. `FlakyResolutionTask` is its opposite number and the reason
  the two are not the same verdict: its handler IS registered and throws from its constructor while
  `ActivationFaultGate` says so, which is what a scoped dependency failing to build looks like from the
  reconciliation — indistinguishable from a missing handler except by asking the container.
  `InspectionBlindScheduler` is the real scheduler minus `IsScheduled`, the only way to reach the branch a
  CUSTOM `IScheduler` lands on: every other wrapper in the suite forwards `SupportsScheduleInspection`, so
  without it the value is `true` everywhere and the disabled-reconciliation path never runs.
  `RecordingLogger<T>`, registered as `IEverTaskLogger<OccurrenceMaterializer>`, is what observes that
  branch's once-per-process warning, which publishes no monitoring event of its own. What the durable surface
  REFUSES — every cap, window and enum, the `OnMisfire` callback that picks none or two, and the two host
  knobs — is `Occurrences/DurableOccurrenceOptionsValidationTests`.
- **Runtime schedule management**: `IntegrationTests/RescheduleIntegrationTests` builds every host over one
  shared `MemoryTaskStorage` (same rows, new process) and drives the REAL worker: the S4 cases hand
  `WorkerExecutor.DoWork` an executor built from the row BEFORE the reschedule, which is the only honest way
  to produce a delivery the scheduler can no longer reach. Its `ScheduleFaultingScheduler` is the real
  `PeriodicTimerScheduler` with one armed `Schedule()` throwing — the shape of a re-park that fails after the
  new definition is already committed. Two assertions there rely on the in-memory store handing back LIVE
  entities: read the version or the cursor AFTER the update and it reports what was just written.
  Its `StartHostAsync` does not return until the host's STARTUP RECOVERY has ended (`StartupRecoveryWatch`
  waits for `WorkerServiceLog` 1104/1105/1115/1117/1118 on `IEverTaskLogger<WorkerService>`). Recovery
  captures its cutoff when it BEGINS, on a thread pool thread, not when `StartAsync` returns: a row dispatched
  in between is created before that cutoff and is re-dispatched like any leftover — one extra `Schedule()` for
  the id the test is working on, which ate the armed fault and let the re-park succeed. Any test that arms,
  counts or reads back a registration has to order itself after recovery instead of assuming it. The S1 race
  is produced, not waited for: `FaultInjectingTaskStorage.RunBefore(CompleteRecurringRun)` runs the
  `Reschedule` on the worker's own thread, inside the window between the row the advance read and the write it
  is about to make — the only place a FIRST reschedule can be overwritten.
  The same `RunBefore` seam produces the two races the manager can LOSE: an `UpdateCurrentRun` in front of
  `UpdateSchedule` (the reschedule loses the compare-and-swap and must write, publish and park nothing), and
  a rewrite in front of EVERY `CompleteRecurringRun` attempt, which is the only way to reach the end of the
  advance's re-aim loop — where the guard is dropped but the run still has to be recorded.
  Monitoring events are read through `EventsOfAsync`, which subscribes, runs the call and WAITS for every
  phrase it was given: publishing is fire-and-forget by contract, so a read taken when the call returns is a
  race, and two events published in a row arrive in no order.
  The window AFTER that write has its own seam: `RegistrationWatchingScheduler` is the real
  `PeriodicTimerScheduler` with every registration recorded together with the answer it got, and one armed
  hook (`RegistrationWatch.ArmBeforeNextRegistrationOf`) that runs the reschedule just before a chosen
  registration reaches it. That is what puts a reschedule between a delivery deciding what comes next and the
  moment it hands it over — the advance, the rate-limit skip that writes nothing at all, and the gate's own
  deferral, each of which used to replace the registration the reschedule had just published.
  `AcceptedAStaleRegistration` is the assertion those three share: no registration older than one already
  accepted was ever let through after it. `ScheduleFaultingScheduler` takes that watch too, which is what
  lets one test hold BOTH halves of a window: a reschedule landing inside a delivery AND its own re-park
  failing, the only shape where a stranded delivery's registration is all the series has left.
  The two throttled probes in `TestTasks.ScheduleManagement.cs` are how a test reaches the gate
  deterministically — one permit an hour with a millisecond horizon rejects every delivery after the first,
  one permit a second under the default horizon defers it.
  The two `Resuming_a_halt_…` tests are the two halves of one contract, and the released one is the shape
  that costs something: a resume replans against the definition AS IT STANDS, so the only thing that can put
  the same backlog under the same cap is the age window sliding over it — which is exactly what phase 4
  pins must NOT release a halt by itself. It therefore takes a `FakeTimeProvider` (`StartHostAsync` accepts
  one) and a `RunUntil` already past, so the backlog can only shrink and the arithmetic is fixed; the halt is
  produced by calling `OccurrenceMaterializer.RunAsync` on an unstarted host, as the durable suite does.
  The period arithmetic is `RecurringTests/ScheduleRebaseTests` (pure, no host), including the Rome →
  Kiritimati case where the rebased instant moves BACKWARDS while the logical day stays put, and the two
  cadences that name no day (`EveryWeek()`, `EveryMonth()`), whose weekday or day of the month is the grid's
  phase and lives on the cursor. The periods that hold SEVERAL slots are their own section, and the shapes
  there are chosen because the grid really produces both: `OnDays(Mon, Wed).AtTimes(...)` fires every listed
  time on every listed day, while a plain `EveryDay().AtTimes(9, 15)` or `EveryMonth().OnDays(1, 15)` steps
  its period first and so fires ONCE — pick one of those and the test proves nothing. `EveryWeek()` and
  `EveryMonth()` are also where the `RunUntil` cases live: they place their slot by hand and never ask the
  grid, which is the only thing that applies the bound, so each has a refusal test AND a control that still
  rebases inside a bound it has not reached.
  `SchedulerVersionedRegistrationTests` is the primitive underneath all of it: `TrySchedule` refuses a
  registration older than the one parked, on both schedulers, while `Schedule` still replaces whatever it
  finds — and an `IScheduler` that does not implement the member keeps registering unconditionally through
  its default body.
  What the manager refuses BEFORE it does anything is `IntegrationTests/ScheduleManagementValidationTests`:
  every argument of the surface the phase introduced, the capability gate ONE BRANCH AT A TIME (the three
  calls that rewrite a schedule row over a store with durable occurrences and no versioning, a requeue over
  the mirror image, and a cancel that goes through over a store with neither), and a host with no storage
  under it. Its double is `TestHelpers/CapabilityBlindStorage` — `MemoryTaskStorage` re-implementing
  `ITaskStorage` to answer whatever the test asks for the two capability flags, the only way to reach those
  refusals without a mock that would break the host.
- **Running startup recovery without a host**: `TestHelpers/RecoveryHarness.CreateRecoveryService(storage, …)`
  builds the REAL `WorkerService` around a storage you choose, so pagination, the two waves and the L18
  accounting all execute. `internal`, and shared with `EverTask.Tests.Storage` through this assembly's
  `InternalsVisibleTo` — `WorkerService` is internal, so no public signature can hand it back. Used by
  `RecoveryFinalizationFailureTests`, `RecoveryDurableScheduleBarrierTests` (the M7 barrier over a
  multi-page backlog, where the dispatcher is the observation point) and the storage suite's L18 test.
- `[Collection("TimingSensitiveTests")]` has NO `[CollectionDefinition]` — the bare attribute is the only
  thing serializing those classes against `parallelizeTestCollections: true` in `xunit.runner.json`.
- Queue/recovery resilience suite: listed in `src/EverTask/CLAUDE.md`; its shared state is
  `TestTasks/TestTasks.Resilience.cs` (`ResilienceTestState`, register one per host).
