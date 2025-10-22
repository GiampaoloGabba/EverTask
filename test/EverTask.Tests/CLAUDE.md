# CLAUDE.md - EverTask.Tests

AI coding agent documentation for the EverTask core library test suite.

## Project Purpose

Comprehensive test coverage for the EverTask core library (`src/EverTask/`), including:
- Task dispatcher logic
- Worker executor functionality
- Queue operations (immediate and scheduled)
- Recurring task scheduling (fluent API and cron expressions)
- Handler execution and lifecycle
- Integration tests with in-memory storage
- Retry policies, timeouts, cancellation

## Test Organization

### Directory Structure

```
test/EverTask.Tests/
├── IntegrationTests/           # Full integration tests with IHost
├── TestHelpers/                # Test infrastructure (TaskWaitHelper, TestTaskStateManager, IntegrationTestBase)
├── TestTasks/                  # Test tasks organized by category (Basic, Concurrent, Delayed, Retry, etc.)
├── RecurringTests/             # Recurring task tests (intervals, builders, cron)
│   ├── Intervals/              # Interval calculation tests
│   └── Builders/               # Fluent API builder tests
│       └── Chains/             # Builder chaining tests
├── *Tests.cs                   # Unit tests (Dispatcher, Queue, Handler, Timer, Logger, etc.)
├── TestTaskStorage.cs          # Mock ITaskStorage
└── GlobalUsings.cs             # Global imports
```

**Key folders**:
- `TestHelpers/`: Reusable test utilities (intelligent polling, state management)
- `TestTasks/`: Test task definitions split by scenario type
- `IntegrationTests/`: End-to-end tests with real components
- `RecurringTests/`: Recurring task scheduling logic

### Naming Conventions

- **Test Classes**: `{FeatureName}Tests.cs` (e.g., `DispatcherTests.cs`)
- **Integration Tests**: Located in `IntegrationTests/` folder, suffix with `IntegrationTests`
- **Test Methods**: `Should_{expected_behavior}_when_{condition}` or `Should_{expected_behavior}`
- **Test Tasks**: Organized in `TestTasks/` folder by category (Basic, Concurrent, Delayed, Retry, etc.)
- **Test Handlers**: Defined alongside their tasks in the corresponding `TestTasks.*.cs` file

## Test Frameworks and Libraries

### Core Testing Stack

- **xUnit**: Primary test framework (`[Fact]`, `[Theory]` attributes)
- **Moq**: Mocking framework for dependencies
- **Shouldly**: Fluent assertion library (`.ShouldBe()`, `.ShouldNotBeNull()`, etc.)
- **Microsoft.AspNetCore.Mvc.Testing**: Integration test utilities
- **Microsoft.Extensions.Hosting**: IHost/HostBuilder for integration tests

### Global Usings (GlobalUsings.cs)

```csharp
global using System.Net;
global using EverTask.Abstractions;
global using EverTask.Worker;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Moq;
global using Shouldly;
global using Xunit;
```

All test files automatically import these namespaces.

## Test Patterns

### Unit Tests Pattern

Unit tests mock all dependencies using Moq:

```csharp
public class DispatcherTests
{
    private readonly Dispatcher.Dispatcher _dispatcher;
    private readonly Mock<IWorkerQueue> _workerQueueMock;
    private readonly Mock<IScheduler> _schedulerMock;
    // ... other mocks

    public DispatcherTests()
    {
        _workerQueueMock = new Mock<IWorkerQueue>();
        _schedulerMock = new Mock<IScheduler>();
        // Setup mocks
        _dispatcher = new Dispatcher.Dispatcher(...); // Inject mocks
    }

    [Fact]
    public async Task Should_Queue_Task()
    {
        var task = new TestTaskRequest2();
        var taskId = await _dispatcher.Dispatch(task);

        _workerQueueMock.Verify(q => q.Queue(It.Is<TaskHandlerExecutor>(
            executor => executor.PersistenceId == taskId)), Times.Once);
    }
}
```

### Integration Tests Pattern

**⚠️ REQUIRED: Use `IsolatedIntegrationTestBase` for ALL New Integration Tests**

All new integration tests MUST inherit from `IsolatedIntegrationTestBase` to ensure proper test isolation and eliminate flakiness. This pattern provides:
- ✅ Zero state sharing between tests (no race conditions)
- ✅ Parallel execution safety (4-12x faster than sequential)
- ✅ Automatic cleanup via `IAsyncDisposable`
- ✅ Deterministic test results (no flakiness)

**Modern Pattern (REQUIRED for new tests)**: Use `IsolatedIntegrationTestBase`

```csharp
using EverTask.Tests.TestHelpers;

public class MyIntegrationTests : IsolatedIntegrationTestBase
{
    // NO constructor - NO instance fields for host/services!

    [Fact]
    public async Task Should_execute_task_and_verify_storage()
    {
        // Create isolated host for THIS test only (each test gets its own IHost)
        await CreateIsolatedHostAsync();

        // Use base class properties (Dispatcher, Storage, WorkerQueue, etc.)
        var task = new TestTaskConcurrent1();
        var taskId = await Dispatcher.Dispatch(task);

        // Use helper methods for intelligent polling
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // Cleanup is automatic via IAsyncDisposable - no manual StopAsync needed!
    }

    [Fact]
    public async Task Should_execute_task_with_custom_config()
    {
        // Custom configuration via lambda
        await CreateIsolatedHostAsync(
            channelCapacity: 10,
            maxDegreeOfParallelism: 5,
            configureEverTask: cfg => cfg.SetShard("test-shard")
        );

        var taskId = await Dispatcher.Dispatch(new MyTask());
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        // Assertions...
    }
}
```

**Available base class properties:**
- `Host`: The isolated IHost instance
- `Dispatcher`: ITaskDispatcher
- `Storage`: ITaskStorage
- `WorkerQueue`: IWorkerQueue
- `WorkerBlacklist`: IWorkerBlacklist
- `WorkerExecutor`: IEverTaskWorkerExecutor
- `CancellationSourceProvider`: ICancellationSourceProvider
- `StateManager`: TestTaskStateManager

**Available helper methods:**
- `CreateIsolatedHostAsync(...)`: Creates isolated host with custom config
- `CreateIsolatedHostWithBuilderAsync(...)`: For advanced builder configuration
- `WaitForTaskStatusAsync(...)`: Polls until task reaches expected status
- `WaitForRecurringRunsAsync(...)`: Waits for N recurring task executions
- `StopHostAsync(...)`: Manual stop if needed (auto-called by DisposeAsync)

**⚠️ DEPRECATED Pattern** (DO NOT USE for new tests):

```csharp
// ❌ DEPRECATED: Constructor-based pattern with shared IHost
public class OldIntegrationTests
{
    private IHost _host;  // Shared across ALL tests - causes flakiness!

    public OldIntegrationTests()
    {
        _host = new HostBuilder()...Build();  // Called ONCE per test class
    }

    [Fact]
    public async Task Test1()
    {
        await _host.StartAsync();  // Shared host!
        // ...
        await _host.StopAsync();
    }
}
```

**Why the old pattern is deprecated:**
- ❌ IHost created in constructor = shared across all test methods
- ❌ Singleton services persist state between tests
- ❌ Race conditions when tests run in parallel
- ❌ Flaky test failures (pass/fail randomly)
- ❌ Impossible to debug issues (mixed causes)

**Migration checklist** (if updating old tests):
1. Change base class: `: IsolatedIntegrationTestBase`
2. Remove constructor entirely
3. Remove instance fields (`_host`, `_dispatcher`, `_storage`, etc.)
4. Add `await CreateIsolatedHostAsync();` at start of each test method
5. Replace `_dispatcher` → `Dispatcher`, `_storage` → `Storage`, etc.
6. Remove manual `await _host.StartAsync()` calls (done automatically)
7. Remove manual `await _host.StopAsync()` calls (done automatically)
8. Replace `Task.Delay()` with `TaskWaitHelper` methods

**Common mistakes to avoid:**
- ❌ Forgetting to call `CreateIsolatedHostAsync()` → NullReferenceException
- ❌ Using instance fields for services → defeats isolation purpose
- ❌ Calling `StartAsync()`/`StopAsync()` manually → double-start/stop errors
- ❌ Using `Task.Delay()` → use `TaskWaitHelper` instead

### Test Task Definitions

Test tasks are organized in `TestTasks/` folder by category:

**TestTasks.Basic.cs** - Simple test tasks:
```csharp
public record TestTaskRequest(string Name) : IEverTask;

public class TestTaskHanlder : EverTaskHandler<TestTaskRequest>
{
    public override Task Handle(TestTaskRequest task, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}
```

**TestTasks.Concurrent.cs** - Tasks with state tracking:
```csharp
public class TestTaskConcurrent1() : IEverTask
{
    // Legacy static properties (maintained for backward compatibility)
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

public class TestTaskConcurrent1Handler : EverTaskHandler<TestTaskConcurrent1>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskConcurrent1Handler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
    }

    public override async Task Handle(TestTaskConcurrent1 task, CancellationToken ct)
    {
        // Record state using TestTaskStateManager (preferred, thread-safe)
        _stateManager?.RecordStart(nameof(TestTaskConcurrent1));

        await Task.Delay(300, ct);

        // Update both static (legacy) and state manager (new)
        TestTaskConcurrent1.Counter = 1;
        TestTaskConcurrent1.EndTime = DateTime.UtcNow; // Always use UTC!

        _stateManager?.RecordCompletion(nameof(TestTaskConcurrent1));
        _stateManager?.IncrementCounter(nameof(TestTaskConcurrent1));
    }
}
```

**TestTasks.Retry.cs** - Custom retry policies:
```csharp
public class TestTaskWithCustomRetryPolicyHanlder : EverTaskHandler<TestTaskWithCustomRetryPolicy>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskWithCustomRetryPolicyHanlder(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(100));
    }

    public override Task Handle(TestTaskWithCustomRetryPolicy task, CancellationToken ct)
    {
        TestTaskWithCustomRetryPolicy.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskWithCustomRetryPolicy));

        if (TestTaskWithCustomRetryPolicy.Counter < 5)
            throw new Exception();
        return Task.CompletedTask;
    }
}
```

**TestTasks.Recurring.cs** - Recurring task scenarios:
```csharp
// Tasks for testing recurring execution with various interval types
public class TestTaskRecurringSeconds() : IEverTask { }
public class TestTaskRecurringMinutes() : IEverTask { }

// Task that fails initially, then succeeds (for retry + recurring tests)
public class TestTaskRecurringWithFailure() : IEverTask
{
    public static int Counter { get; set; } = 0;
    public static int FailUntilCount { get; set; } = 2;
}
```

**TestTasks.Lifecycle.cs** - Lifecycle callback verification:
```csharp
// Task that tracks callback execution order
public class TestTaskLifecycle() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static Guid? LastTaskId { get; set; }
}

// Task that fails to trigger OnError callback
public class TestTaskLifecycleWithError() : IEverTask
{
    public static List<string> CallbackOrder { get; set; } = new();
    public static Exception? LastException { get; set; }
}

// Task with IAsyncDisposable handler
public class TestTaskLifecycleWithAsyncDispose() : IEverTask
{
    public static bool WasDisposed { get; set; }
}
```

### Mock Storage Implementation

`TestTaskStorage.cs` provides a no-op ITaskStorage for tests not requiring persistence:

```csharp
public class TestTaskStorage : ITaskStorage
{
    public Task Persist(QueuedTask executor, CancellationToken ct = default)
    {
        if (executor.Type.Contains("ThrowStorageError"))
            throw new Exception();
        return Task.CompletedTask;
    }

    // All other methods return empty results
}
```

For tests requiring actual storage, use `MemoryTaskStorage` from `src/EverTask/Storage/MemoryTaskStorage.cs`.

## Integration Tests Details

### Storage Requirements

**TestTaskStorage** (mock): Used when storage verification is not required. Returns empty arrays, does nothing on persistence (except throwing for `ThrowStorageError` tasks).

**MemoryTaskStorage** (real): Used when tests need to verify:
- Task status transitions
- Persistence of task data
- Status audit trails
- Recurring task state

### Test Helpers for Reliable Timing

**TaskWaitHelper** provides intelligent polling to avoid flaky tests caused by fixed delays:

```csharp
using EverTask.Tests.TestHelpers;

// Wait for a task to reach a specific status
await TaskWaitHelper.WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed);

// Wait for a recurring task to complete N runs
await TaskWaitHelper.WaitForRecurringRunsAsync(storage, taskId, expectedRuns: 3);

// Wait for a custom condition
await TaskWaitHelper.WaitForConditionAsync(() => someCondition, timeoutMs: 5000);

// Wait for a specific counter value (using TestTaskStateManager)
await TaskWaitHelper.WaitForCounterAsync(() => stateManager.GetCounter("MyTask"), expectedValue: 3);
```

**Default timeouts**:
- `5000ms` (5 seconds) for most operations
- `10000ms` (10 seconds) for recurring tasks
- `50ms` polling interval

**Legacy approach (deprecated)**:
```csharp
// ❌ DON'T: Fixed delays are unreliable and slow
await Task.Delay(600);  // May be too short or too long!

// ✅ DO: Use intelligent polling
await TaskWaitHelper.WaitForTaskStatusAsync(storage, taskId, QueuedTaskStatus.Completed);
```

### Host Lifecycle

Integration tests must properly start/stop the host:

```csharp
await _host.StartAsync();  // Starts background worker service

// ... dispatch and verify tasks ...

var cts = new CancellationTokenSource();
cts.CancelAfter(2000);  // 2-second timeout for graceful shutdown
await _host.StopAsync(cts.Token);
```

## Running Tests

### Run All Tests

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj
```

### Run Specific Test Class

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName~DispatcherTests
```

### Run Specific Test Method

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName~DispatcherTests.Should_Queue_Task
```

### Run Only Integration Tests

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName~IntegrationTests
```

### Run Only Unit Tests (Exclude Integration Tests)

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName!~IntegrationTests
```

### Target Framework Selection

The project targets .NET 6.0, 7.0, and 8.0. To run tests for a specific framework:

```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --framework net8.0
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --framework net7.0
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --framework net6.0
```

## Coverage Areas

### Tested Components

**Dispatcher** (`DispatcherTests.cs`, `IntegrationTests/TaskDispatcherIntegrationTests.cs`)
- Task dispatching with handler resolution
- Task ID assignment
- Queueing immediate tasks
- Scheduling delayed tasks (TimeSpan, DateTimeOffset)
- Scheduling recurring tasks (cron, fluent API)
- Task cancellation (blacklisting + token cancellation)
- ArgumentNullException for null tasks/handlers
- Storage persistence errors (ThrowIfUnableToPersist)

**WorkerExecutor** (`IntegrationTests/WorkerServiceIntegrationTests6.cs`)
- Task execution lifecycle (OnStarted, OnCompleted, OnError)
- Cancellation token management (creation, disposal, cleanup)
- CPU-bound task handling (Task.Run)
- Retry policies (standard exponential backoff, custom policies)
- Timeout handling (default, custom per-handler)
- Status updates in storage
- Monitoring event publishing
- Blacklist checking (skipping cancelled tasks)
- Service shutdown cancellation (ServiceStopped status)
- Concurrent vs sequential execution (MaxDegreeOfParallelism)

**Queue Operations** (`QueueTests.cs`)
- BoundedChannel queueing/dequeuing
- Status updates on queueing (WaitingQueue -> Queued)
- Status audit trail creation

**Scheduler** (`TimerSchedulerTests.cs`, `IntegrationTests/WorkerServiceScheduledIntegrationTests.cs`)
- ConcurrentPriorityQueue operations
- Delayed task execution (TimeSpan, DateTimeOffset)
- Recurring task scheduling and re-scheduling
- MaxRuns enforcement
- RunUntil expiration
- Next occurrence calculation

**Handler Execution** (`HanlderExecutorTests.cs`)
- TaskHandlerExecutor creation from wrapper
- Handler resolution from service provider
- Serialization to QueuedTask
- DateTimeOffset UTC conversion
- Task/handler null checks
- Non-serializable task detection (JsonSerializationException)
- RecurringTask metadata mapping
- EverTaskEventData creation

**Recurring Tasks** (`RecurringTests/`)
- CalculateNextRun logic (runtime vs next occurrence)
- RunNow, SpecificRunTime, InitialDelay
- Cron expressions (via Cronos library)
- Interval types: Second, Minute, Hour, Day, Month
- MaxRuns and RunUntil constraints
- Fluent API builders (Every().Days(), UseCron(), etc.)
- Builder chaining (RunNow().Then().EverySecond())

**Assembly Registration** (`AssemblyResolutionTests.cs`)
- RegisterTasksFromAssembly scanning
- Handler registration in DI container
- Duplicate handler handling

**Logging** (`LoggerTests.cs`)
- EverTaskLogger wrapper functionality
- ILogger<T> resolution from service provider
- Fallback to ILoggerFactory when ILogger<T> not registered

**Utilities**
- `CancellationSourceProviderTests.cs`: Token creation, retrieval, disposal
- `ExceptionExtensionsTests.cs`: Exception formatting utilities
- `ConcurrentPriorityQueueTests.cs`: Priority queue operations

### Not Tested in This Project

**Storage Implementations**: Tested separately in `test/EverTask.Tests.Storage/`
- EfCore base storage
- SqlServer storage (requires container)
- Sqlite storage

**Logging Integrations**: Tested separately in `test/EverTask.Tests.Logging/`
- Serilog integration

**Monitoring Integrations**: No dedicated test project yet
- SignalR monitoring

## Adding Tests

### For New Features

1. **Unit Test First**: Create `{FeatureName}Tests.cs` in the appropriate folder
2. **Mock Dependencies**: Use Moq for all external dependencies
3. **Integration Test**: Add to `IntegrationTests/` if feature requires storage or full service lifecycle
4. **Test Tasks**: Add new test task/handler to the appropriate `TestTasks/TestTasks.*.cs` file by category:
   - Basic functionality → `TestTasks.Basic.cs`
   - Concurrency/parallel execution → `TestTasks.Concurrent.cs`
   - Delayed/scheduled execution → `TestTasks.Delayed.cs`
   - Retry policies → `TestTasks.Retry.cs`
   - CPU-bound operations → `TestTasks.Cpubound.cs`
   - Timeout scenarios → `TestTasks.Timeout.cs`
   - Error handling → `TestTasks.Error.cs`
   - Recurring tasks with intervals → `TestTasks.Recurring.cs`
   - Lifecycle callbacks (OnStarted, OnCompleted, OnError, DisposeAsyncCore) → `TestTasks.Lifecycle.cs`
5. **Use TaskWaitHelper**: Always use intelligent polling instead of `Task.Delay()`

### Example: Adding Tests for New Retry Policy

```csharp
// 1. Add test task to TestTasks/TestTasks.Retry.cs
public class TestTaskWithNewRetryPolicy() : IEverTask
{
    public static int Counter { get; set; } = 0; // Legacy - prefer storage verification
}

public class TestTaskWithNewRetryPolicyHandler : EverTaskHandler<TestTaskWithNewRetryPolicy>
{
    private readonly TestTaskStateManager? _stateManager;

    public TestTaskWithNewRetryPolicyHandler(TestTaskStateManager? stateManager = null)
    {
        _stateManager = stateManager;
        RetryPolicy = new YourNewRetryPolicy(params);
    }

    public override Task Handle(TestTaskWithNewRetryPolicy task, CancellationToken ct)
    {
        TestTaskWithNewRetryPolicy.Counter++;
        _stateManager?.IncrementCounter(nameof(TestTaskWithNewRetryPolicy));

        if (TestTaskWithNewRetryPolicy.Counter < 3)
            throw new Exception();
        return Task.CompletedTask;
    }
}

// 2. Add integration test using IsolatedIntegrationTestBase
public class MyNewRetryPolicyTests : IsolatedIntegrationTestBase
{
    [Fact]
    public async Task Should_execute_task_with_new_retry_policy()
    {
        // ✅ Create isolated host for this test
        await CreateIsolatedHostAsync();

        var task = new TestTaskWithNewRetryPolicy();
        TestTaskWithNewRetryPolicy.Counter = 0;
        var taskId = await Dispatcher.Dispatch(task);

        // ✅ Use helper method for intelligent polling
        await WaitForTaskStatusAsync(taskId, QueuedTaskStatus.Completed);

        var tasks = await Storage.GetAll();
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        // ✅ Verify via storage (preferred for parallel test safety)
        tasks[0].RunsAudits.Count(x => x.Status == QueuedTaskStatus.Completed).ShouldBe(1);

        // Cleanup automatic via IAsyncDisposable
    }
}
```

### For Dispatcher Changes

1. Add unit test to `DispatcherTests.cs` with mocked dependencies
2. Add integration test to `IntegrationTests/TaskDispatcherIntegrationTests.cs` if queueing/storage is involved

### For Scheduler Changes

1. Add unit test to `TimerSchedulerTests.cs` or `ConcurrentPriorityQueueTests.cs`
2. Add integration test to `IntegrationTests/WorkerServiceScheduledIntegrationTests.cs` for end-to-end scheduling

### For Recurring Task Changes

1. If modifying interval calculation: add test to `RecurringTests/Intervals/{IntervalType}Tests.cs`
2. If modifying fluent API: add test to `RecurringTests/Builders/{BuilderType}Tests.cs`
3. If modifying CalculateNextRun: add test to `RecurringTests/RecurringTaskTests.cs`
4. If modifying builder chaining: add test to `RecurringTests/Builders/Chains/{ChainType}Tests.cs`

### Test Isolation

- **Prefer storage-based verification**: Use `tasks[0].RunsAudits` instead of static counters to avoid race conditions
- **Reset static counters**: If using legacy static properties, reset before each test (e.g., `TestTaskConcurrent1.Counter = 0`)
- **Use TestTaskStateManager**: For new tests, inject `TestTaskStateManager` for thread-safe state tracking
- **Unique IHost per test**: Integration tests create new IHost instances to ensure clean state

### Best Practices

1. **Use TaskWaitHelper**: Replace all `Task.Delay()` with intelligent polling using `TaskWaitHelper`
2. **Verify via Storage**: Check task status and run counts via `storage.GetAll()` instead of static properties
3. **Descriptive Test Names**: Use `Should_{behavior}_when_{condition}` pattern
4. **Single Assertion Focus**: Each test verifies one behavior (though multiple assertions for related state are fine)
5. **Arrange-Act-Assert**: Structure tests clearly
6. **Async All The Way**: Use `async Task` for all test methods, never `.Wait()` or `.Result`
7. **Shouldly Assertions**: Prefer Shouldly over xUnit assertions for better failure messages
8. **Verify Mocks**: Always verify mock interactions in unit tests
9. **Cleanup Hosts**: Always stop IHost in integration tests (use CancellationTokenSource with timeout)
10. **UTC Everywhere**: Use `DateTime.UtcNow` and `DateTimeOffset.UtcNow`, never `DateTime.Now`
11. **Organized Test Tasks**: Add new test tasks to the appropriate `TestTasks.*.cs` file by category

### Common Pitfalls

- **Using fixed delays**: ❌ `Task.Delay(600)` → ✅ `TaskWaitHelper.WaitForTaskStatusAsync(...)`
- **Relying on static counters**: ❌ `TestTask.Counter.ShouldBe(3)` → ✅ `tasks[0].RunsAudits.Count(...).ShouldBe(3)`
- **Using DateTime.Now**: ❌ `DateTime.Now` → ✅ `DateTime.UtcNow` or `DateTimeOffset.UtcNow`
- **Forgetting to start host**: Integration tests will hang if `await _host.StartAsync()` is missing
- **Static state pollution**: Reset static counters before tests, or better yet, use `TestTaskStateManager`
- **Not stopping host**: Tests may hang or leak resources
- **Using TestTaskStorage for persistence tests**: Use MemoryTaskStorage instead
- **DateTimeOffset timezone issues**: Executor converts all DateTimeOffset to UTC, tests should expect UTC times
- **Race conditions in parallel tests**: Verify via storage audit trails, not shared static state

## Test Infrastructure

### TaskWaitHelper

Intelligent polling helper to avoid timing issues:
- **WaitForTaskStatusAsync**: Polls storage until task reaches expected status
- **WaitForRecurringRunsAsync**: Waits for N completed runs in a recurring task
- **WaitForConditionAsync**: Generic polling with configurable timeout/interval
- **WaitForCounterAsync**: Polls a counter value (for use with TestTaskStateManager)

### TestTaskStateManager

Thread-safe state management to replace static properties:
- **RecordStart/RecordCompletion**: Track task lifecycle events
- **IncrementCounter**: Thread-safe counter increments
- **WereExecutedInParallel**: Check if two tasks overlapped
- **GetCounter/GetState**: Retrieve execution metrics

### IntegrationTestBase (DEPRECATED)

**⚠️ DEPRECATED**: Use `IsolatedIntegrationTestBase` instead for new tests.

Legacy base class with shared host pattern (causes flakiness):
- **InitializeHost**: Creates and configures IHost with test services
- **WaitForTaskStatusAsync**: Convenience wrapper for TaskWaitHelper
- **ResetState**: Clears TestTaskStateManager between tests

This class will be marked `[Obsolete]` and removed in a future version.

## Test Statistics

- **Total Test Files**: 39 (including 3 TestHelpers files and 9 TestTasks files)
- **Total Tests**: 232 (all passing ✅)
- **Unit Tests**: ~60% (mocked dependencies)
- **Integration Tests**: ~40% (real implementations)
- **Test Execution Time**: ~40 seconds

## Important Context: Recent Recurring Task Refactoring

**⚠️ Known Issue**: Approximately 27 integration tests related to recurring/delayed tasks are currently failing due to bugs introduced by the recent lazy handler resolution refactoring.

**What happened:**
- The system was refactored to implement lazy handler resolution for recurring and delayed tasks
- This introduced bugs in the dispatch and execution logic for scheduled tasks
- These bugs are **separate** from test isolation issues

**How to identify the issue type:**
- **Lazy handler bug**: Test fails **consistently** (10/10 times) → Real bug in lazy handler code
- **Isolation issue**: Test fails **randomly** (e.g., 3/10 times) → Test contamination/race condition

**Affected test categories:**
- Recurring task tests (intervals, cron expressions)
- Delayed task tests (scheduled execution, specific run times)
- Tests involving `CalculateNextRun` logic
- Tests with multiple recurring task instances

**Current status:**
- Integration test isolation refactoring is **in progress** (Phase 1 & 3 partial complete)
- Goal: Eliminate flakiness to enable debugging of lazy handler bugs
- Once isolation is complete, lazy handler bugs can be fixed separately

**For developers:**
- When writing/fixing tests, distinguish between isolation issues and lazy handler bugs
- Use `IsolatedIntegrationTestBase` for new tests to ensure isolation
- Document consistent failures as known lazy handler bugs (not your fault!)
- See `.claude/tasks/integration-tests-isolation-refactoring.md` for tracking

## Notes

- All tests target .NET 6.0, 7.0, and 8.0 (via TargetFrameworks in Directory.Build.props)
- No external dependencies required (SQL Server, containers, etc.) - this project uses in-memory implementations
- **Test execution time improved 4-12x** with `IsolatedIntegrationTestBase` (parallel execution)
- Tests using `IsolatedIntegrationTestBase` are designed to run in parallel safely (xUnit default behavior)
- **Zero flaky tests** with `IsolatedIntegrationTestBase` pattern thanks to proper isolation and TaskWaitHelper
