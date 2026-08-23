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
  a new storage member stops being a default one, this class fails to build.
- **Binary compatibility pin**: `test/EverTask.ConsumerCompatibility.Baseline` is compiled against the
  `issue23-baseline` packages in `nupkg/issue23-baseline` and RUN against the current assemblies by the same
  test class, a major apart (3.11 → 4.0) — the test asserts that distance, so a missed version bump cannot
  turn the proof into 3.11 against 3.11. It is the only thing that catches an optional parameter appended to
  an existing public method or constructor — source-level probes keep compiling while the IL signature
  changes. Add a public method, not an optional parameter; see that project's `README.md` for the wiring and
  for repacking the baseline.
- **Fault injection**: `TestHelpers/FaultInjectingTaskStorage` wraps a REAL storage and throws only where the
  test arms it (`FailNext` / `FailAlways` / `Heal`), so the failure and the recovery from it both execute for
  real. Used by `RecoveryFinalizationFailureTests`; a mock in its place would make both fictional.
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
