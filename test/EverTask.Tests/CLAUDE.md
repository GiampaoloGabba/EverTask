# EverTask.Tests

Refer to the root CLAUDE.md for project-wide rules. Core-library tests (`src/EverTask`), fully in-memory — no Docker
or Testcontainers here. Subsets filter on namespace: `--filter "FullyQualifiedName~IntegrationTests"` (also
`~RecurringTests`, `~RateLimiting`, `~Serialization`, `~MultiQueue`).

## Helpers (`TestHelpers/`)

- Integration tests inherit `IsolatedIntegrationTestBase` and build their host with
  `CreateIsolatedHostAsync(channelCapacity, maxDegreeOfParallelism, configureEverTask, configureServices, clock)`: one
  `IHost` per test, disposed by the base, which re-exposes the common waits as instance methods.
- Poll with `TaskWaitHelper`, never `Task.Delay`. Argument order is `WaitForTaskStatusAsync(storage, taskId,
  expectedStatus, timeoutMs)` — storage FIRST, timeout an `int` in ms.
- `TestTaskStateManager` is keyed by a `string taskKey`, not by task id, and only records — no wait method; pair it
  with `WaitForCounterAsync`.
- **Order a test AFTER startup recovery with `StartupRecoveryWatch`** (registered as `IEverTaskLogger<WorkerService>`;
  it completes on the recovery's terminal log line). Recovery captures its cutoff when it BEGINS, on a thread pool
  thread, so a row dispatched between `StartAsync` returning and that moment is re-dispatched like any leftover — one
  extra resolution, registration and delivery for the very id under test. Any test that counts, arms or reads back one
  of those must wait for it.
- **Running startup recovery without a host**: `RecoveryHarness.CreateRecoveryService(storage, …)` builds the REAL
  `WorkerService`, so pagination, the two waves and the dispatch-failure counting all execute. It is `internal` (so is
  `WorkerService`), shared with `EverTask.Tests.Storage` through this assembly's `InternalsVisibleTo`. To wedge one
  page while the reader walks the others, block a chosen row's re-dispatch on a `TaskCompletionSource`;
  `MaxRecoveryPagesInFlight = 1` reproduces the pre-#39 behaviour.

## Gotchas

- **`startHost: false` does NOT pause the scheduler**: both schedulers start `ProcessScheduledTasksAsync` in their
  constructor, so an already-due occurrence can reach the queue and be `Queued` right after `Dispatch` returns — only
  the consumers wait for `Host.StartAsync()`. Use `WaitForTaskAcceptedAsync` (or assert `ShouldBeOneOf(WaitingQueue,
  Queued)`); `WaitingQueue` alone is flaky under load. A REGISTRATION is the same trap from the other side: a slot
  already past is consumed within the scheduler's check interval, so the rescue is `IsScheduled(id) ||
  deliveries.IsDelivering(id)`.
- **A handler's counter is not the run counter**: the test tasks bump theirs inside `Handle`, while `CurrentRunCount`
  is written afterwards in the delivery's `finally`. Wait with `WaitForRecurringRunsAsync` (audit AND counter) before
  asserting on `CurrentRunCount`.
- **Driving the clock**: pass `clock: new FakeTimeProvider(instant)` to either `CreateIsolatedHost…` overload. The
  base registers it AFTER `AddEverTask` (which uses `TryAddSingleton(TimeProvider.System)`, so an earlier registration
  would lose) and exposes it as `Clock`. Seed rows from `Clock.GetUtcNow()`, never `DateTimeOffset.UtcNow`, and the
  test reads identically on either clock. It also overrides `CreateTimer`, so the `Task.Delay(delay, timeProvider)`
  the schedulers and the parking lot wait on is VIRTUAL: a frozen clock means a wait only `Advance()` ends — arm the
  delay first (`WaitForPendingTimersAsync`). Retry delays run on `IRetryPolicy`'s own clock and are out of scope.
- **Which clock the CONTAINER hands out is a separate question**: `RateLimiting/RateLimiterDeterministicClockTests`
  resolves the limiter, the gate and the parking lot from a real `AddEverTask` container, because two of the three are
  built by hand-written factories passing the clock explicitly; constructing them directly only proves they read the
  clock they are GIVEN.
- **Cronos is the oracle, not a second implementation** (`RecurringTests/CronOracleTests`): the fluent schedule and
  the equivalent cron expression must agree from the SECOND occurrence on, since `DayInterval`/`MonthInterval` advance
  their period before they land. A zone the tz database lacks cannot reach a `RecurringTask` at all, so that case is
  compared one level down, against the two production pieces the zoned walk is made of.
- **Compile-time compatibility pin**: `Serialization/ConsumerCompatibilityTests.LegacyMinimalTaskStorage` implements
  only what `ITaskStorage` required before durable occurrences and exists to be COMPILED — it fails to build the day a
  new storage member stops being a default one. Its handler twins are `RawInterfaceTaskHandler` (no
  `SetExecutionContext`, and `ExecutionContextIntegrationTests` DISPATCHES it so the interface's default body runs on
  a real delivery) and `RawInterfaceContextTaskHandler`, which proves the injector reaches the interface slot. The
  3.11-compiled binary fixture is gone — recreate one against the published 4.0.0 packages if needed.
- **Fault injection wraps a REAL storage**: `TestHelpers/FaultInjectingTaskStorage` throws only where the test arms it
  (`FailNext`/`FailAlways`/`Heal`), so the failure and the recovery from it both execute for real. It must forward
  EVERY default interface member explicitly — one left out silently runs the interface's own default and nothing can
  arm a fault on it — which `FaultInjectingTaskStorageContractTests` enforces by reflection, with a control on
  `TestTaskStorage` so an empty answer means "all forwarded", not "the reflection matched nothing".
  - `SwallowNext(operation, times)` RETURNS without reaching the store, the shape of a write every relational provider
    swallows; only writes that really behave that way honour it (a fault that throws is a different test).
    `TryReviveCancelledSchedule` honours it because it ANSWERS: a swallowed call comes back `false`.
- **Produce races, never wait for them**: `RunBefore(operation, hook)` runs the hook on the calling thread just before
  the operation reaches the inner store, so the interfering call lands inside the window between the row a write read
  and the write it is about to make. A blocking hook blocks its caller — that is the point, and it is also the trap:
  **an in-memory cancel ends its run on the CANCELLING thread**, nothing in that unwind suspends, so a hook there
  deadlocks the test. `CancellationSourceProvider.Delete(id)` before the cancel is how a test says "this run is
  already past the point where the token turns it back".
- **The in-memory store hands back LIVE entities**: read a version or a cursor AFTER an update and it reports what was
  just written. It also assigns audits **no identity** (every `Id` is 0), so an order-by-id assertion holds only over
  a relational provider — over memory, assert against a full page from the same storage.
- **Monitoring events are read through `EventsOfAsync`**, which subscribes, runs the call and WAITS for every phrase
  given: publishing is fire-and-forget, so a read taken when the call returns is a race and two events published in a
  row arrive in no order. Where a branch logs synchronously and publishes afterwards, assert on the LOG
  (`TestHelpers/RecordingLogger<T>`) — "no event yet" passes on a build that had not published one.
- **Restarts are seeded rows, not sleeps**: the durable-occurrence, reschedule and provider suites build every host
  over ONE shared `MemoryTaskStorage`, so a downtime is a seeded backlog and never a wait.
- **Test doubles that exist to reach a single branch** (do not "fix" them into ordinary helpers):
  `InspectionBlindScheduler` is the real scheduler minus `IsScheduled`, the only way to reach the branch a custom
  `IScheduler` lands on; `RegistrationWatchingScheduler` records every registration with the answer it got and can run
  a reschedule just before a chosen one; `ScheduleFaultingScheduler`/`ReParkRefusingScheduler` fail or refuse a
  re-park (the latter in BOTH shapes — a throw, and the `false` `TrySchedule` answers, which is no error anywhere and
  reaches another branch); `CapabilityBlindStorage` fakes the two capability flags.
- **Handlers built to fail in a specific way**: `HandlerlessOccurrenceTask` has NO handler on purpose (a row that
  rebuilds and finds nothing to run it) — writing one for it retires the test; `FlakyResolutionTask` IS registered and
  throws from its constructor, a scoped dependency failing to build, which must reach the opposite verdict.
  `ResolutionGate` blocks in the handler's CONSTRUCTOR to hold a delivery inside DI resolution: **arm it only after
  building the executor**, since the dispatch path resolves the handler once already for its metadata. A test driving
  `DoWork` by hand must also make the `TaskDeliveryRegistry.TryBegin(id, scheduleId)` registration.
- **Counting anything over an unbounded grid is a race**: give the probe a bounded calendar (it answers `null` past a
  chosen last slot) or the wall clock keeps adding slots underneath the assertion. `OccurrenceProviderProbe`
  (`TestTasks.OccurrenceProviders.cs`) is a SINGLETON the scoped provider depends on — the shape a real provider has —
  and its `Fault`/`Hang`/`FailNextConstructions` produce outages a test does not have to time. `AsyncOnlyCalendar`
  makes the provider's scope throw ON DISPOSAL, after the answer: assert the ANSWER. `ProviderScopeLedger` makes "a
  fresh scope per call" observable at all.
- `[Collection("TimingSensitiveTests")]` has NO `[CollectionDefinition]` — the bare attribute is the only thing
  serializing those classes against `parallelizeTestCollections: true` in `xunit.runner.json`.
- Queue/recovery resilience suite: listed in `src/EverTask/CLAUDE.md`; its shared state is
  `TestTasks/TestTasks.Resilience.cs` (`ResilienceTestState`, register one per host).
