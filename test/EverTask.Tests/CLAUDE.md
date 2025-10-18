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

**Modern Pattern (Recommended)**: Use intelligent polling with `TaskWaitHelper` to avoid timing issues:

```csharp
public class WorkerServiceIntegrationTests
{
    private IHost _host;
    private ITaskDispatcher _dispatcher;
    private ITaskStorage _storage;

    public WorkerServiceIntegrationTests()
    {
        _host = new HostBuilder()
            .ConfigureServices((hostContext, services) =>
            {
                services.AddLogging();
                services.AddEverTask(cfg => cfg
                    .RegisterTasksFromAssembly(typeof(TestTaskRequest).Assembly)
                    .SetChannelOptions(3)
                    .SetMaxDegreeOfParallelism(3))
                    .AddMemoryStorage();
                services.AddSingleton<ITaskStorage, MemoryTaskStorage>();
                services.AddSingleton<TestTaskStateManager>(); // For thread-safe state tracking
            }).Build();

        _dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage = _host.Services.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public async Task Should_execute_task_and_verify_storage()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        var taskId = await _dispatcher.Dispatch(task);

        // Use TaskWaitHelper for intelligent polling instead of fixed delays
        await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);

        var tasks = await _storage.GetAll();
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        await _host.StopAsync(CancellationToken.None);
    }
}
```

**Legacy Pattern**: Fixed delays (deprecated, causes flaky tests):
```csharp
// ❌ DON'T: Fixed delays can cause race conditions
await Task.Delay(600); // Unreliable!

// ✅ DO: Use TaskWaitHelper for intelligent polling
await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed);
```

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

// 2. Add integration test to WorkerServiceIntegrationTests6.cs
[Fact]
public async Task Should_execute_task_with_new_retry_policy()
{
    await _host.StartAsync();

    var task = new TestTaskWithNewRetryPolicy();
    TestTaskWithNewRetryPolicy.Counter = 0;
    var taskId = await _dispatcher.Dispatch(task);

    // ✅ Use TaskWaitHelper instead of fixed delay
    await TaskWaitHelper.WaitForTaskStatusAsync(_storage, taskId, QueuedTaskStatus.Completed, timeoutMs: 5000);

    var tasks = await _storage.GetAll();
    tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

    // ✅ Verify via storage (preferred for parallel test safety)
    tasks[0].RunsAudits.Count(x => x.Status == QueuedTaskStatus.Completed).ShouldBe(1);

    await _host.StopAsync(CancellationToken.None);
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

### IntegrationTestBase

Base class with common setup for integration tests:
- **InitializeHost**: Creates and configures IHost with test services
- **WaitForTaskStatusAsync**: Convenience wrapper for TaskWaitHelper
- **ResetState**: Clears TestTaskStateManager between tests

## Test Statistics

- **Total Test Files**: 39 (including 3 TestHelpers files and 9 TestTasks files)
- **Total Tests**: 232 (all passing ✅)
- **Unit Tests**: ~60% (mocked dependencies)
- **Integration Tests**: ~40% (real implementations)
- **Test Execution Time**: ~40 seconds

## Notes

- All tests target .NET 6.0, 7.0, and 8.0 (via TargetFrameworks in Directory.Build.props)
- No external dependencies required (SQL Server, containers, etc.) - this project uses in-memory implementations
- **Test execution time improved 2-4x** by replacing fixed delays with intelligent polling
- Tests are designed to be run in parallel (xUnit default behavior)
- **Zero flaky tests** thanks to TaskWaitHelper and storage-based verification
