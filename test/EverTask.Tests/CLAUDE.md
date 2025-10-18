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
├── IntegrationTests/
│   ├── TaskDispatcherIntegrationTests.cs       # Dispatcher + queue + storage integration
│   ├── WorkerServiceIntegrationTests6.cs       # Full worker service lifecycle tests
│   └── WorkerServiceScheduledIntegrationTests.cs # Scheduled/recurring task execution
├── RecurringTests/
│   ├── RecurringTaskTests.cs                   # Core recurring task logic
│   ├── RecurringTaskToStringTests.cs           # String representation
│   ├── DateTimeOffsetExtensionsTests.cs        # Date utility methods
│   ├── Intervals/                              # Interval calculation tests
│   │   ├── CronIntervalTests.cs
│   │   ├── DayIntervalTests.cs
│   │   ├── MonthIntervalTests.cs
│   │   └── SecondsMinutesHoursIntervalTests.cs
│   └── Builders/                               # Fluent API builder tests
│       ├── RecurringTaskBuilderTests.cs
│       ├── SchedulerBuilderTests.cs
│       ├── IntervalSchedulerBuilderTests.cs
│       ├── DailyTimeSchedulerBuilderTests.cs
│       ├── MinuteSchedulerBuilderTests.cs
│       ├── HourSchedulerBuilderTests.cs
│       ├── MonthlySchedulerBuilderTests.cs
│       └── Chains/                             # Builder chaining tests
│           ├── BuilderChainTests.cs
│           ├── EverySchedulerBuilderChainTests.cs
│           ├── EveryAndMonthlyBuilderChainTests.cs
│           ├── HourAndMinuteBuilderChainTests.cs
│           ├── IntervalAndMonthlyBuilderChainTests.cs
│           ├── IntervalSchedulerBuilderChainTests.cs
│           ├── MonthlyAndDailyTimeBuilderChainTests.cs
│           └── RecurringTaskAndIntervalBuilderChainTests.cs
├── DispatcherTests.cs                          # Dispatcher unit tests (mocked dependencies)
├── QueueTests.cs                               # WorkerQueue unit tests
├── HanlderExecutorTests.cs                     # TaskHandlerExecutor logic
├── TimerSchedulerTests.cs                      # TimerScheduler tests
├── ConcurrentPriorityQueueTests.cs             # Scheduler queue tests
├── CancellationSourceProviderTests.cs          # Cancellation token management
├── AssemblyResolutionTests.cs                  # Assembly scanning/registration
├── LoggerTests.cs                              # EverTaskLogger wrapper tests
├── ExceptionExtensionsTests.cs                 # Exception utility tests
├── TestTasks.cs                                # Test task/handler definitions
├── TestTaskStorage.cs                          # Mock ITaskStorage implementation
└── GlobalUsings.cs                             # Global imports
```

### Naming Conventions

- **Test Classes**: `{FeatureName}Tests.cs` (e.g., `DispatcherTests.cs`)
- **Integration Tests**: Located in `IntegrationTests/` folder, suffix with `IntegrationTests`
- **Test Methods**: `Should_{expected_behavior}_when_{condition}` or `Should_{expected_behavior}`
- **Test Tasks**: Defined in `TestTasks.cs` with names like `TestTaskRequest`, `TestTaskRequest2`, etc.
- **Test Handlers**: Defined in `TestTasks.cs` with names like `TestTaskHanlder`, `TestTaskHanlder2`, etc.

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

Integration tests use `IHost` with real implementations (except storage):

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
            }).Build();

        _dispatcher = _host.Services.GetRequiredService<ITaskDispatcher>();
        _storage = _host.Services.GetRequiredService<ITaskStorage>();
    }

    [Fact]
    public async Task Should_execute_task_and_verify_storage()
    {
        await _host.StartAsync();

        var task = new TestTaskConcurrent1();
        await _dispatcher.Dispatch(task);
        await Task.Delay(600); // Wait for execution

        var tasks = await _storage.GetAll();
        tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

        await _host.StopAsync(CancellationToken.None);
    }
}
```

### Test Task Definitions

All test tasks are in `TestTasks.cs`:

```csharp
// Simple task request
public record TestTaskRequest(string Name) : IEverTask;

// Handler for TestTaskRequest
public class TestTaskHanlder : EverTaskHandler<TestTaskRequest>
{
    public override Task Handle(TestTaskRequest task, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

// Tasks with static counters for verification
public class TestTaskConcurrent1() : IEverTask
{
    public static int Counter { get; set; } = 0;
    public static DateTime StartTime { get; set; }
    public static DateTime EndTime { get; set; }
}

// Handler with custom retry policy
public class TestTaskWithCustomRetryPolicyHanlder : EverTaskHandler<TestTaskWithCustomRetryPolicy>
{
    public TestTaskWithCustomRetryPolicyHanlder()
    {
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromMilliseconds(100));
    }

    public override Task Handle(TestTaskWithCustomRetryPolicy task, CancellationToken ct)
    {
        TestTaskWithCustomRetryPolicy.Counter++;
        if (TestTaskWithCustomRetryPolicy.Counter < 5)
            throw new Exception();
        return Task.CompletedTask;
    }
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

### Test Execution Delays

Integration tests use `Task.Delay()` to wait for asynchronous operations:

```csharp
await _dispatcher.Dispatch(task);
await Task.Delay(100);  // Wait for task to start
var ctsToken = _cancSourceProvider.TryGet(taskId);
await Task.Delay(600);  // Wait for execution completion
```

Typical delays:
- `100ms`: Wait for task to queue/start
- `300-600ms`: Wait for task execution (test handlers use `Task.Delay(300)`)
- `1600ms`: Wait for retry policy tests (3 retries with exponential backoff)
- `4000ms`: Wait for recurring task tests

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
4. **Test Tasks**: Add new test task/handler to `TestTasks.cs` if needed

### Example: Adding Tests for New Retry Policy

```csharp
// 1. Add test task to TestTasks.cs
public class TestTaskWithNewRetryPolicy() : IEverTask
{
    public static int Counter { get; set; } = 0;
}

public class TestTaskWithNewRetryPolicyHandler : EverTaskHandler<TestTaskWithNewRetryPolicy>
{
    public TestTaskWithNewRetryPolicyHandler()
    {
        RetryPolicy = new YourNewRetryPolicy(params);
    }

    public override Task Handle(TestTaskWithNewRetryPolicy task, CancellationToken ct)
    {
        TestTaskWithNewRetryPolicy.Counter++;
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
    await _dispatcher.Dispatch(task);

    await Task.Delay(expected_duration_with_retries);

    var tasks = await _storage.GetAll();
    tasks[0].Status.ShouldBe(QueuedTaskStatus.Completed);

    TestTaskWithNewRetryPolicy.Counter.ShouldBe(3);

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

- Reset static counters in test tasks before each test (e.g., `TestTaskConcurrent1.Counter = 0`)
- Use unique test tasks per test when possible to avoid state pollution
- Integration tests create new IHost instances to ensure clean state

### Best Practices

1. **Descriptive Test Names**: Use `Should_{behavior}_when_{condition}` pattern
2. **Single Assertion Focus**: Each test verifies one behavior (though multiple assertions for related state are fine)
3. **Arrange-Act-Assert**: Structure tests clearly
4. **Async All The Way**: Use `async Task` for all test methods, never `.Wait()` or `.Result`
5. **Shouldly Assertions**: Prefer Shouldly over xUnit assertions for better failure messages
6. **Verify Mocks**: Always verify mock interactions in unit tests
7. **Cleanup Hosts**: Always stop IHost in integration tests (use CancellationTokenSource with timeout)
8. **Test Data Builders**: Use TestTasks.cs for reusable test task definitions

### Common Pitfalls

- **Forgetting to start host**: Integration tests will hang if `await _host.StartAsync()` is missing
- **Insufficient delays**: Increase `Task.Delay()` if tests are flaky
- **Static state pollution**: Reset static counters in test tasks
- **Not stopping host**: Tests may hang or leak resources
- **Using TestTaskStorage for persistence tests**: Use MemoryTaskStorage instead
- **DateTimeOffset timezone issues**: Executor converts all DateTimeOffset to UTC, tests should expect UTC times

## Test Statistics

- **Total Test Files**: 34
- **Total Tests**: 203 (as of last count)
- **Unit Tests**: ~60% (mocked dependencies)
- **Integration Tests**: ~40% (real implementations)

## Notes

- All tests target .NET 6.0, 7.0, and 8.0 (via TargetFrameworks in Directory.Build.props)
- No external dependencies required (SQL Server, containers, etc.) - this project uses in-memory implementations
- Test execution time: ~30-60 seconds for full suite (due to delays in integration tests)
- Tests are designed to be run in parallel (xUnit default behavior)
