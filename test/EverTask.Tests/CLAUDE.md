# EverTask.Tests

Refer to the root CLAUDE.md for project-wide rules. Core-library tests (`src/EverTask`), fully in-memory — no
Docker or Testcontainers here. Subsets filter on namespace: `--filter "FullyQualifiedName~IntegrationTests"`
(also `~RecurringTests`, `~RateLimiting`, `~Serialization`, `~MultiQueue`).

## Helpers (`TestHelpers/`)

- Integration tests inherit `IsolatedIntegrationTestBase` and build their host with
  `CreateIsolatedHostAsync(channelCapacity, maxDegreeOfParallelism, configureEverTask, configureServices)`:
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
- `[Collection("TimingSensitiveTests")]` has NO `[CollectionDefinition]` — the bare attribute is the only
  thing serializing those classes against `parallelizeTestCollections: true` in `xunit.runner.json`.
- Queue/recovery resilience suite: listed in `src/EverTask/CLAUDE.md`; its shared state is
  `TestTasks/TestTasks.Resilience.cs` (`ResilienceTestState`, register one per host).
