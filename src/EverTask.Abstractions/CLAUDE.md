# EverTask.Abstractions

## Project Purpose

EverTask.Abstractions is a lightweight, dependency-minimal package containing the core contracts and base classes for the EverTask background task execution library. This package is designed to be referenced by application layers (API, application services, domain layers) without pulling in the full EverTask runtime.

**Key Design Goal**: Enable application code to define task requests and handlers without requiring dependencies on persistence, execution, or infrastructure concerns. This follows the same philosophy as MediatR.Contracts vs MediatR.

**Consumers**:
- Application layers defining task requests (records implementing `IEverTask`)
- Application layers defining task handlers (classes extending `EverTaskHandler<T>`)
- Infrastructure layers implementing custom retry policies
- The core EverTask runtime (which depends on this package)

**Dependencies**: Only `Microsoft.Extensions.Logging.Abstractions` (for `ILogger` in retry policies)

## Key Interfaces

### IEverTask
**Location**: `IEverTask.cs`

```csharp
public interface IEverTask;
```

**Purpose**: Marker interface for background task requests. Similar to MediatR's `INotification`.

**Contract**:
- Empty marker interface
- Implementation should be a serializable record or class
- Use records with simple types (primitives, strings, DateTimeOffset, etc.) for reliable JSON serialization via Newtonsoft.Json

**Usage**:
```csharp
public record SendEmailTask(string To, string Subject, string Body) : IEverTask;
public record ProcessOrderTask(Guid OrderId, int RetryCount) : IEverTask;
```

**Serialization Considerations**:
- EverTask serializes tasks to JSON for persistence using Newtonsoft.Json
- Avoid complex object graphs, circular references, or non-serializable types
- Prefer value types and simple reference types (string, Guid, DateTime, etc.)
- Do not include services, DbContexts, or other infrastructure objects in task requests

### ITaskDispatcher
**Location**: `ITaskDispatcher.cs`

**Purpose**: Entry point for dispatching tasks to the background execution queue. Implemented by `Dispatcher` in the core EverTask package.

**Contract**:

```csharp
public interface ITaskDispatcher
{
    // Immediate execution (queued to BoundedQueue)
    Task<Guid> Dispatch(IEverTask task, CancellationToken cancellationToken = default);

    // Delayed execution (TimeSpan delay)
    Task<Guid> Dispatch(IEverTask task, TimeSpan scheduleDelay, CancellationToken cancellationToken = default);

    // Scheduled execution (specific DateTimeOffset)
    Task<Guid> Dispatch(IEverTask task, DateTimeOffset scheduleTime, CancellationToken cancellationToken = default);

    // Recurring execution (cron or fluent builder)
    Task<Guid> Dispatch(IEverTask task, Action<IRecurringTaskBuilder> recurring, CancellationToken cancellationToken = default);

    // Cancel a dispatched task
    Task Cancel(Guid taskId, CancellationToken cancellationToken = default);
}
```

**Return Value**: All `Dispatch` methods return a `Guid` representing the task's persistence identifier. This ID is used for:
- Cancellation via `Cancel(Guid taskId)`
- Lifecycle callbacks (`OnStarted`, `OnCompleted`, `OnError`)
- Monitoring and tracking

**Usage Pattern**:
```csharp
// Inject in controllers, services, etc.
public class OrderService
{
    private readonly ITaskDispatcher _dispatcher;

    public OrderService(ITaskDispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task PlaceOrder(Order order)
    {
        // ... business logic ...

        // Fire and forget
        await _dispatcher.Dispatch(new ProcessOrderTask(order.Id, 0));

        // Delayed
        await _dispatcher.Dispatch(
            new SendReminderEmailTask(order.UserId),
            TimeSpan.FromHours(24));

        // Recurring
        await _dispatcher.Dispatch(
            new CleanupExpiredOrdersTask(),
            r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0)));
    }
}
```

### IEverTaskHandler<TTask>
**Location**: `IEverTaskHandler.cs`

**Purpose**: Defines the contract for task handlers. All handlers must implement this interface (typically via `EverTaskHandler<TTask>` base class).

**Contract**:

```csharp
public interface IEverTaskHandler<in TTask> : IEverTaskHandlerOptions, IAsyncDisposable
    where TTask : IEverTask
{
    // Main execution method - REQUIRED
    Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

    // Lifecycle callbacks - OPTIONAL
    ValueTask OnStarted(Guid persistenceId);
    ValueTask OnCompleted(Guid persistenceId);
    ValueTask OnError(Guid persistenceId, Exception? exception, string? message);
}
```

**Inherits From**:
- `IEverTaskHandlerOptions`: Provides `RetryPolicy`, `Timeout` configuration
- `IAsyncDisposable`: Enables cleanup of resources after task execution

**Lifecycle Flow**:
1. Handler instance created via DI (scoped per task execution)
2. `OnStarted(Guid)` called with persistence ID
3. `Handle(TTask, CancellationToken)` executed with retry policy, timeout
4. `OnCompleted(Guid)` called on success OR `OnError(Guid, Exception, string)` called on failure
5. `DisposeAsync()` called to clean up resources

**Handler Scoping**: Each task execution gets a fresh handler instance from a scoped DI container. This allows safe use of scoped dependencies like `DbContext`.

### IEverTaskHandlerOptions
**Location**: `IEverTaskHandlerOptions.cs`

**Purpose**: Configuration options for task execution behavior. Implemented by `EverTaskHandler<TTask>` base class.

**Contract**:

```csharp
public interface IEverTaskHandlerOptions
{
    // Default: LinearRetryPolicy(3 retries, 500ms delay)
    IRetryPolicy? RetryPolicy { get; set; }

    // Default: null (no timeout)
    TimeSpan? Timeout { get; set; }

    // Deprecated: This property has no effect. Use Task.Run within your handler for CPU-intensive synchronous work.
    bool CpuBoundOperation { get; set; }
### IRetryPolicy
**Location**: `IRetryPolicy.cs`

**Purpose**: Contract for implementing custom retry policies. Can be integrated with libraries like Polly.

**Contract**:

```csharp
public interface IRetryPolicy
{
    Task Execute(Func<CancellationToken, Task> action, ILogger attemptLogger, CancellationToken token = default);
}
```

**Implementation Requirements**:
- Must execute the provided `action` with retry logic
- Must respect `CancellationToken` to allow cancellation
- Must use `attemptLogger` for logging retry attempts
- Should NOT retry `OperationCanceledException` or `TimeoutException` (these are terminal)
- Should collect exceptions and throw `AggregateException` if all retries fail

**Built-in Implementation**: `LinearRetryPolicy` (see below)

**Custom Implementation Example**:
```csharp
// Integration with Polly
public class PollyRetryPolicy : IRetryPolicy
{
    private readonly IAsyncPolicy _policy;

    public PollyRetryPolicy(IAsyncPolicy policy) => _policy = policy;

    public async Task Execute(Func<CancellationToken, Task> action, ILogger attemptLogger, CancellationToken token)
    {
        await _policy.ExecuteAsync(async (ct) => await action(ct), token);
    }
}
```

### IRecurringTaskBuilder
**Location**: `IRecurringTaskBuilder.cs`

**Purpose**: Fluent builder interface for configuring recurring task schedules. Implementation provided by core EverTask package.

**Contract**: Chain of fluent interfaces:

```
IRecurringTaskBuilder
├── RunNow() -> IThenableSchedulerBuilder
├── RunDelayed(TimeSpan) -> IThenableSchedulerBuilder
├── RunAt(DateTimeOffset) -> IThenableSchedulerBuilder
└── Schedule() -> IIntervalSchedulerBuilder
    ├── UseCron(string) -> IBuildableSchedulerBuilder
    ├── Every(int) -> IEverySchedulerBuilder
    │   ├── Seconds() -> IBuildableSchedulerBuilder
    │   ├── Minutes() -> IMinuteSchedulerBuilder
    │   ├── Hours() -> IBuildableSchedulerBuilder
    │   ├── Days() -> IDailyTimeSchedulerBuilder
    │   └── Months() -> IMonthlySchedulerBuilder
    ├── EverySecond() -> IBuildableSchedulerBuilder
    ├── EveryMinute() -> IMinuteSchedulerBuilder
    ├── EveryHour() -> IHourSchedulerBuilder
    ├── EveryDay() -> IDailyTimeSchedulerBuilder
    ├── EveryMonth() -> IMonthlySchedulerBuilder
    ├── OnDays(params DayOfWeek[]) -> IDailyTimeSchedulerBuilder
    └── OnMonths(params int[]) -> IMonthlySchedulerBuilder

IBuildableSchedulerBuilder
├── RunUntil(DateTimeOffset) -> IBuildableSchedulerBuilder
└── MaxRuns(int) -> void
```

**Usage Examples**:
```csharp
// Every day at 3 AM
r => r.Schedule().EveryDay().AtTime(new TimeOnly(3, 0))

// Every 5 minutes
r => r.Schedule().Every(5).Minutes()

// Every Monday and Friday at 9 AM, max 10 runs
r => r.Schedule()
     .OnDays(DayOfWeek.Monday, DayOfWeek.Friday)
     .AtTime(new TimeOnly(9, 0))
     .MaxRuns(10)

// Cron expression
r => r.Schedule().UseCron("0 0 * * *") // Daily at midnight

// Run now, then every hour
r => r.RunNow().Then().EveryHour()

// Run at specific time, then every 30 minutes until end date
r => r.RunAt(DateTimeOffset.Now.AddHours(1))
     .Then()
     .Every(30).Minutes()
     .RunUntil(DateTimeOffset.Now.AddDays(7))
```

## Base Classes

### EverTaskHandler<TTask>
**Location**: `EverTaskHandler.cs`

**Purpose**: Base class for task handlers providing default implementations of `IEverTaskHandler<TTask>`.

**Contract**:

```csharp
public abstract class EverTaskHandler<TTask> : IEverTaskHandler<TTask> where TTask : IEverTask
{
    // Configuration properties (from IEverTaskHandlerOptions)
    public IRetryPolicy? RetryPolicy       { get; set; } // Default: LinearRetryPolicy(3, 500ms)
    public TimeSpan?     Timeout           { get; set; } // Default: null
    public bool          CpuBoundOperation { get; set; } // Deprecated

    // REQUIRED: Main execution logic
    public abstract Task Handle(TTask backgroundTask, CancellationToken cancellationToken);

    // OPTIONAL: Lifecycle hooks
    public virtual ValueTask OnStarted(Guid taskId) => ValueTask.CompletedTask;
    public virtual ValueTask OnCompleted(Guid taskId) => ValueTask.CompletedTask;
    public virtual ValueTask OnError(Guid taskId, Exception? exception, string? message) => ValueTask.CompletedTask;

    // OPTIONAL: Async disposal
    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore();
        GC.SuppressFinalize(this);
    }

    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;
}
```

**Usage Pattern**:
```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    private readonly OrderDbContext _db;
    private readonly ILogger<ProcessOrderHandler> _logger;

    public ProcessOrderHandler(OrderDbContext db, ILogger<ProcessOrderHandler> logger)
    {
        _db = db;
        _logger = logger;

        // Configure execution options
        RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));
        Timeout = TimeSpan.FromMinutes(5);
    }

    public override async Task Handle(ProcessOrderTask task, CancellationToken cancellationToken)
    {
        var order = await _db.Orders.FindAsync(task.OrderId, cancellationToken);
        // ... processing logic ...
    }

    public override ValueTask OnStarted(Guid taskId)
    {
        _logger.LogInformation("Order processing started: {TaskId}", taskId);
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string? message)
    {
        _logger.LogError(exception, "Order processing failed: {TaskId}, {Message}", taskId, message);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await _db.DisposeAsync();
    }
}
```

**DI Registration**: Handlers are automatically registered during `AddEverTask(opt => opt.RegisterTasksFromAssembly(...))`.

### LinearRetryPolicy
**Location**: `LinearRetryPolicy.cs`
**Namespace**: `EverTask.Resilience` (not `EverTask.Abstractions`)

**Purpose**: Default retry policy implementation with linear (fixed) delays between retries.

**Contract**:

```csharp
public class LinearRetryPolicy : IRetryPolicy
{
    // Constructor 1: Fixed retry count and delay
    public LinearRetryPolicy(int retryCount, TimeSpan retryDelay);

    // Constructor 2: Custom delay array for each retry
    public LinearRetryPolicy(TimeSpan[] retryDelays);

    public Task Execute(Func<CancellationToken, Task> action, ILogger attemptLogger, CancellationToken token);
}
```

**Behavior**:
- Executes the action up to `retryCount + 1` times (initial attempt + retries)
- Waits `retryDelay` between each retry attempt
- Does NOT retry `OperationCanceledException` or `TimeoutException` (throws immediately)
- Collects all exceptions and throws `AggregateException` if all retries fail
- Respects `CancellationToken` between retry attempts

**Usage**:
```csharp
// 5 retries with 2 second delay
RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2));

// Custom delays: 1s, 2s, 5s, 10s
RetryPolicy = new LinearRetryPolicy(new[]
{
    TimeSpan.FromSeconds(1),
    TimeSpan.FromSeconds(2),
    TimeSpan.FromSeconds(5),
    TimeSpan.FromSeconds(10)
});
```

## Design Philosophy

### Abstraction Layer Benefits
1. **Dependency Inversion**: Application code depends on abstractions, not concrete implementations
2. **Minimal Dependencies**: Only requires `Microsoft.Extensions.Logging.Abstractions`
3. **Clean Architecture**: Enables application/domain layers to define tasks without infrastructure concerns
4. **Package Size**: Lightweight package for projects that only need to define tasks, not execute them

### MediatR Inspiration
EverTask is heavily inspired by MediatR's design:
- `IEverTask` = `INotification` (marker interface for messages)
- `IEverTaskHandler<T>` = `INotificationHandler<T>` (handler contract)
- `ITaskDispatcher` = `IMediator` (entry point for sending messages)
- Request/handler pattern with automatic DI registration

**Key Differences from MediatR**:
- EverTask is asynchronous and persistent (tasks survive app restarts)
- EverTask supports scheduling, delays, and recurring execution
- EverTask provides retry policies, timeouts, and lifecycle hooks
- EverTask is focused on background execution, not in-process request/response

### Serialization Constraints
Tasks are serialized to JSON using Newtonsoft.Json for persistence. This choice was made over System.Text.Json due to:
- More robust polymorphic type handling
- Better support for complex object graphs
- Wider compatibility with existing .NET libraries

**Guidelines**:
- Use records with simple, serializable types
- Avoid circular references
- Avoid large object graphs (prefer IDs over full entities)
- Test serialization roundtrip for complex tasks

### Thread Safety
Handlers are NOT required to be thread-safe because:
- Each task execution gets a fresh handler instance from a scoped DI container
- Handler lifetime is tied to single task execution
- No shared state between concurrent executions

## Versioning Considerations

### Breaking Change Avoidance
This package is distributed as a separate NuGet package from the core EverTask library. Changes must be carefully managed to avoid breaking application code.

**Protected Surface Area**:
- `IEverTask` interface (empty marker - very stable)
- `ITaskDispatcher` method signatures
- `IEverTaskHandler<T>` method signatures
- `EverTaskHandler<TTask>` virtual method signatures
- `IRetryPolicy` interface

**Safe Changes**:
- Adding new optional methods to fluent builder interfaces (with default implementations)
- Adding new overloads to `ITaskDispatcher` (existing overloads unchanged)
- Adding new properties to `IEverTaskHandlerOptions` with defaults
- Adding new virtual methods to `EverTaskHandler<TTask>` with default implementations

**Breaking Changes to Avoid**:
- Changing `IEverTask` to non-empty interface
- Removing or renaming methods on `ITaskDispatcher`
- Changing return types on `IEverTaskHandler<T>` methods
- Changing `Handle()` method signature
- Removing or renaming properties on `IEverTaskHandlerOptions`

### Version Synchronization
While this package can version independently of the core EverTask package, major versions should remain synchronized:
- EverTask.Abstractions 1.x.x should work with EverTask 1.x.x
- Breaking changes to abstractions should increment major version of both packages

## Usage Patterns

### Application Layer Reference
Application layers (ASP.NET Core controllers, application services, domain handlers) should:
1. Reference only `EverTask.Abstractions` package
2. Define task requests as records implementing `IEverTask`
3. Define task handlers extending `EverTaskHandler<TTask>`
4. Inject `ITaskDispatcher` to dispatch tasks

**Example Project References**:
```xml
<!-- MyApp.Application.csproj -->
<ItemGroup>
  <PackageReference Include="EverTask.Abstractions" Version="1.5.4" />
</ItemGroup>

<!-- MyApp.Web.csproj or MyApp.Infrastructure.csproj -->
<ItemGroup>
  <PackageReference Include="EverTask" Version="1.5.4" />
  <PackageReference Include="EverTask.Storage.SqlServer" Version="1.5.4" />
  <ProjectReference Include="..\MyApp.Application\MyApp.Application.csproj" />
</ItemGroup>
```

### Task Definition Best Practices
```csharp
// GOOD: Simple, serializable record
public record SendEmailTask(string To, string Subject, string Body) : IEverTask;

// GOOD: Using value types
public record ProcessPaymentTask(Guid PaymentId, decimal Amount, string Currency) : IEverTask;

// BAD: Complex object graph
public record BadTask(Order Order, Customer Customer, List<Product> Products) : IEverTask;

// BETTER: Use IDs instead
public record GoodTask(Guid OrderId, Guid CustomerId, List<Guid> ProductIds) : IEverTask;
```

### Handler Registration
Handlers are registered during `AddEverTask()`:

```csharp
// Startup.cs or Program.cs
builder.Services.AddEverTask(opt =>
{
    // Scans assembly for classes implementing IEverTaskHandler<>
    opt.RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage(); // or AddSqlServerStorage, AddSqliteStorage
```

### Dependency Injection in Handlers
Handlers support constructor injection of services:

```csharp
public class ProcessOrderHandler : EverTaskHandler<ProcessOrderTask>
{
    private readonly IOrderRepository _repository;
    private readonly IEmailService _emailService;
    private readonly ILogger<ProcessOrderHandler> _logger;

    public ProcessOrderHandler(
        IOrderRepository repository,
        IEmailService emailService,
        ILogger<ProcessOrderHandler> logger)
    {
        _repository = repository;
        _emailService = emailService;
        _logger = logger;
    }

    public override async Task Handle(ProcessOrderTask task, CancellationToken cancellationToken)
    {
        var order = await _repository.GetByIdAsync(task.OrderId, cancellationToken);
        await _emailService.SendOrderConfirmationAsync(order, cancellationToken);
        _logger.LogInformation("Order {OrderId} processed", task.OrderId);
    }
}
```

**Scoped Services**: Each handler instance is created in a scoped DI container, so scoped services like `DbContext` are safe to inject.

### Testing Handlers
Handlers are regular classes and can be unit tested:

```csharp
[Fact]
public async Task Handle_ValidOrder_SendsEmail()
{
    // Arrange
    var mockRepo = new Mock<IOrderRepository>();
    var mockEmail = new Mock<IEmailService>();
    var mockLogger = new Mock<ILogger<ProcessOrderHandler>>();

    var handler = new ProcessOrderHandler(mockRepo.Object, mockEmail.Object, mockLogger.Object);
    var task = new ProcessOrderTask(Guid.NewGuid(), 0);

    // Act
    await handler.Handle(task, CancellationToken.None);

    // Assert
    mockEmail.Verify(x => x.SendOrderConfirmationAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
}
```

### Testing Task Dispatch
For testing code that dispatches tasks, mock `ITaskDispatcher`:

```csharp
[Fact]
public async Task PlaceOrder_DispatchesProcessOrderTask()
{
    // Arrange
    var mockDispatcher = new Mock<ITaskDispatcher>();
    var service = new OrderService(mockDispatcher.Object);
    var order = new Order { Id = Guid.NewGuid() };

    // Act
    await service.PlaceOrder(order);

    // Assert
    mockDispatcher.Verify(x => x.Dispatch(
        It.Is<ProcessOrderTask>(t => t.OrderId == order.Id),
        It.IsAny<CancellationToken>()), Times.Once);
}
```
