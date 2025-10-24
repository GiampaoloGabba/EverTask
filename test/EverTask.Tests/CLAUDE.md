# EverTask.Tests

## Purpose

Comprehensive test coverage for EverTask core library (`src/EverTask/`): dispatcher, worker executor, queue, scheduling, handler execution, retry policies, timeouts, cancellation.

## Test Organization

```
test/EverTask.Tests/
├── IntegrationTests/           # Full integration tests with IHost
├── TestHelpers/                # Reusable test infrastructure
├── TestTasks/                  # Test tasks organized by category
│   ├── Basic/
│   ├── Concurrent/
│   ├── Delayed/
│   ├── Retry/
│   └── ...
├── RecurringTests/             # Recurring task tests
│   ├── Intervals/              # Interval calculation tests
│   └── Builders/               # Fluent API builder tests
│       └── Chains/             # Builder chaining tests
├── *Tests.cs                   # Unit tests (Dispatcher, Queue, Handler, etc.)
├── TestTaskStorage.cs          # Mock ITaskStorage
└── GlobalUsings.cs             # Global imports
```

## Test Framework

**IMPORTANT**: This project uses **xUnit** (NOT MSTest).

| Framework | Usage |
|-----------|-------|
| **xUnit** | Test framework (`[Fact]`, `[Theory]`) |
| **Shouldly** | Fluent assertions (`.ShouldBe()`, `.ShouldNotBeNull()`) |
| **Moq** | Mocking framework |

**Global Usings**: Automatically imports xUnit, Shouldly, Moq, EverTask namespaces.

## Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Test Classes | `{Feature}Tests.cs` | `DispatcherTests.cs` |
| Integration Tests | Located in `IntegrationTests/` | `IntegrationTests/RetryIntegrationTests.cs` |
| Test Methods | `Should_{expected}_when_{condition}` | `Should_retry_three_times_when_handler_fails` |
| Test Tasks | Organized in `TestTasks/` by category | `TestTasks/Retry/FailingTask.cs` |

## Quick Commands

**Run all tests**:
```bash
dotnet test test/EverTask.Tests/EverTask.Tests.csproj
```

**Run specific category**:
```bash
# Integration tests only
dotnet test test/EverTask.Tests/ --filter "FullyQualifiedName~IntegrationTests"

# Unit tests (exclude integration)
dotnet test test/EverTask.Tests/ --filter "FullyQualifiedName!~IntegrationTests"

# Recurring tests only
dotnet test test/EverTask.Tests/ --filter "FullyQualifiedName~RecurringTests"
```

## Key Test Helpers

| Helper | Location | Purpose | Usage |
|--------|----------|---------|-------|
| **TaskWaitHelper** | `TestHelpers/TaskWaitHelper.cs` | Intelligent polling for task state | `await TaskWaitHelper.WaitForTaskCompletion(taskId, storage, timeout)` |
| **TestTaskStateManager** | `TestHelpers/TestTaskStateManager.cs` | State coordination across handler executions | `TestTaskStateManager.WaitForState(taskId, expectedState)` |
| **IntegrationTestBase** | `IntegrationTests/IntegrationTestBase.cs` | Base class with IHost setup | Inherit for full integration tests |
| **TestTaskStorage** | `TestTaskStorage.cs` | Mock ITaskStorage for unit tests | Use for dispatcher/queue unit tests |

**IMPORTANT**: When adding new task states or timer logic, update `TestTaskStateManager` + `TaskWaitHelper` to support new scenarios.

## Common Test Patterns

**Unit Tests**:
```csharp
[Fact]
public async Task Should_dispatch_task_successfully()
{
    // Arrange
    var storage = new TestTaskStorage();
    var dispatcher = new Dispatcher(storage, ...);

    // Act
    var taskId = await dispatcher.Dispatch(new MyTask());

    // Assert
    taskId.ShouldNotBe(Guid.Empty);
    storage.Tasks.ShouldContain(t => t.Id == taskId);
}
```

**Integration Tests**:
```csharp
public class MyIntegrationTests : IntegrationTestBase
{
    [Fact]
    public async Task Should_execute_task_end_to_end()
    {
        // IntegrationTestBase provides IHost with real components
        var dispatcher = Services.GetRequiredService<ITaskDispatcher>();
        var storage = Services.GetRequiredService<ITaskStorage>();

        var taskId = await dispatcher.Dispatch(new MyTask());

        await TaskWaitHelper.WaitForTaskCompletion(taskId, storage, timeout: TimeSpan.FromSeconds(5));

        var task = await storage.GetTaskById(taskId);
        task.Status.ShouldBe(TaskStatus.Completed);
    }
}
```

**Async Coordination**: Use `TaskWaitHelper` instead of `Task.Delay()` for reliable timing.

## Integration Test Prerequisites

**SQL Server tests**: Require Docker container (see `src/Storage/EverTask.Storage.SqlServer/CLAUDE.md`).

**Exclude SQL Server tests**:
```bash
dotnet test test/EverTask.Tests.Storage/ --filter "FullyQualifiedName!~SqlServerEfCoreTaskStorageTests"
```
