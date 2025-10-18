# EverTask Core Library

## Project Purpose

This is the core implementation of EverTask - a persistent, resilient background task execution library for .NET 6+. It provides MediatR-inspired request/handler patterns with persistent storage, scheduled/recurring execution, retry policies, and monitoring capabilities. This package contains the task dispatcher, worker executor, scheduler, and in-memory storage implementation.

## Architecture Overview

### Execution Flow

```
Task Dispatch -> Persistence -> Queue Selection -> Execution -> Monitoring
     |              |                 |                |            |
 Dispatcher -> ITaskStorage -> WorkerQueue/    -> WorkerExecutor -> ITaskMonitor
                              TimerScheduler
```

1. **Dispatch Phase** (`Dispatcher.cs:21-126`): Tasks enter via `ITaskDispatcher.Dispatch()` with optional scheduling
2. **Persistence** (`Dispatcher.cs:101-113`): Tasks serialized to `ITaskStorage` if configured
3. **Queue Routing** (`Dispatcher.cs:116-123`): Immediate tasks -> `WorkerQueue`, scheduled/recurring -> `TimerScheduler`
4. **Execution** (`WorkerExecutor.cs:21-62`): `WorkerService` dequeues tasks with configurable parallelism
5. **Monitoring** (`WorkerExecutor.cs:301-316`): Events published to `ITaskMonitor` implementations

### Design Patterns

- **Request/Handler Pattern** (MediatR-inspired): `IEverTask` requests handled by `IEverTaskHandler<T>`
- **Decorator Pattern**: `TaskHandlerExecutor` wraps handler execution with metadata
- **Producer-Consumer**: `WorkerQueue` uses `System.Threading.Channels` for bounded async queueing
- **Priority Queue**: `TimerScheduler` uses `ConcurrentPriorityQueue` for scheduled task ordering
- **Service Wrapper**: Handlers are wrapped in `TaskHandlerWrapperImp<T>` for type-safe execution

## Key Components

### Dispatcher (`Dispatcher/Dispatcher.cs`)

**Purpose**: Entry point for task dispatching with scheduling support.

**Critical Lines**:
- `10-18`: Constructor dependencies (service provider, queue, scheduler, storage, logger, blacklist, cancellation provider)
- `21-39`: Public dispatch methods (immediate, delayed, scheduled, recurring)
- `88-95`: Handler resolution via `TaskHandlerWrapperImp<>` (uses reflection to create generic wrapper)
- `101-113`: Task persistence with optional throw on failure (`serviceConfiguration.ThrowIfUnableToPersist`)
- `116-123`: Queue routing - future/recurring tasks to scheduler, immediate to worker queue

**MediatR Attribution**: Lines 5-7 acknowledge adaptation from MediatR's `Mediator.cs`.

**Thread Safety**: Creates new `TaskHandlerExecutor` per dispatch. Service provider scopes handled by caller.

### WorkerExecutor (`Worker/WorkerExecutor.cs`)

**Purpose**: Executes tasks with retry policies, timeouts, CPU-bound handling, and lifecycle callbacks.

**Critical Lines**:
- `11-17`: Constructor dependencies (blacklist, config, scope factory, scheduler, cancellation provider, logger)
- `19`: Event `TaskEventOccurredAsync` for monitoring integrations (SignalR, etc.)
- `23-26`: **Thread Safety**: Creates new service scope per task (required for DbContext-based storage)
- `32-33`: Blacklist check prevents execution of user-cancelled tasks
- `37-38`: Storage status update to `InProgress`
- `40`: `OnStarted` lifecycle callback
- `77-127`: Task execution with retry policy, timeout, and CPU-bound handling
- `83-86`: Handler options override defaults (retry policy, timeout, CPU-bound flag)
- `91-95`: **CPU-Bound Handling**: Uses `Task.Factory.StartNew` with `TaskCreationOptions.LongRunning` (see line 93 comment)
- `110-127`: Retry policy execution with timeout enforcement via `WaitAsync` (line 109 comment)
- `130-155`: Timeout implementation using linked `CancellationTokenSource` with proper disposal (line 152 comment)
- `157-169`: Async dispose of handlers implementing `IAsyncDisposable`
- `207-236`: Exception handling - distinguishes `OperationCanceledException` (service vs user) from failures
- `239-253`: Recurring task rescheduling - calculates next run and updates storage

**Async Guidance References**: Lines 93, 109, 152 reference David Fowl's AsyncGuidance.md best practices.

**Monitoring**: Lines 255-318 handle logging and event publishing. Events are fire-and-forget (line 314).

### TaskHandlerExecutor (`Handler/TaskHandlerExecutor.cs`)

**Purpose**: Immutable record containing task execution metadata and serialization.

**Critical Lines**:
- `7-16`: Record definition - task, handler instance, execution time, recurring config, callbacks, persistence ID
- `20-67`: `ToQueuedTask()` extension - serializes to database entity using Newtonsoft.Json
- `25-27`: Serializes task request and captures `AssemblyQualifiedName` for deserialization
- `36-44`: Recurring task metadata serialization

**MediatR Attribution**: Lines 3-5 acknowledge adaptation from MediatR's `NotificationHandlerExecutor.cs`.

**Serialization**: Uses Newtonsoft.Json for polymorphism support. Tasks must be serializable (primitives/simple objects).

### TaskHandlerWrapper (`Handler/TaskHandlerWrapper.cs`)

**Purpose**: Type-safe handler resolution and callback binding.

**Critical Lines**:
- `7-11`: Abstract base for generic wrapper implementation
- `13-36`: `TaskHandlerWrapperImp<TTask>` - resolves `IEverTaskHandler<TTask>` from DI
- `18`: Service resolution - throws `ArgumentNullException` if handler not registered (line 22)
- `24-34`: Constructs `TaskHandlerExecutor` with bound callbacks (lambda closures)
- `33`: Generates or reuses `Guid` for persistence ID

**MediatR Attribution**: Lines 3-5 acknowledge adaptation from MediatR's `NotificationHandlerWrapper.cs`.

### WorkerQueue (`Worker/WorkerQueue.cs`)

**Purpose**: Bounded async queue for immediate task execution.

**Critical Lines**:
- `3-7`: Constructor dependencies (config, logger, blacklist, optional storage)
- `9-10`: Uses `System.Threading.Channels.Channel<T>` with bounded capacity (default 500)
- `12-32`: `Queue()` method - checks blacklist, updates storage to `Queued`, writes to channel
- `22-24`: **Async Write**: Uses `WriteAsync` - blocks if channel full (configured via `BoundedChannelFullMode.Wait`)
- `34-42`: `Dequeue()` and `DequeueAll()` - consumed by `WorkerService` with `Parallel.ForEachAsync`

**Configuration**: Channel capacity and full mode set in `EverTaskServiceConfiguration.ChannelOptions` (lines 5-8).

### TimerScheduler (`Scheduler/TimerScheduler.cs`)

**Purpose**: Manages delayed and recurring task execution using priority queue and timer.

**Critical Lines**:
- `5-23`: Constructor initializes `ConcurrentPriorityQueue` and `System.Threading.Timer`
- `8`: `ConcurrentPriorityQueue<TaskHandlerExecutor, DateTimeOffset>` - priority is execution time
- `28-42`: `Schedule()` - enqueues with execution time as priority, updates timer
- `44-52`: `TimerCallback()` - dequeues tasks ready for execution (execution time <= now)
- `54-86`: `UpdateTimer()` - calculates delay to next task, caps at 1.5 hours (line 66)
- `60-62`: Negative delay handling - sets to zero for immediate execution
- `64-66`: **Large Delay Optimization**: Delays > 2 hours capped at 1.5 hours to periodically recheck queue
- `83-85`: Disables timer when queue empty (`Timeout.Infinite`)
- `89-107`: `DispatchToWorkerQueue()` - transfers task from scheduler to worker queue

**Thread Safety**: Uses `ConcurrentPriorityQueue` wrapper around `PriorityQueue<T>`.

### ConcurrentPriorityQueue (`Scheduler/ConcurrentPriorityQueue.cs`)

**Purpose**: Thread-safe wrapper around `PriorityQueue<TElement, TPriority>`.

**Critical Lines**:
- `6-8`: Wraps `PriorityQueue<T>` with lock object
- `21-27`: `Enqueue()` - locks for thread safety
- `68-74`: `TryDequeue()` - locks and returns element with priority
- `101-107`: `TryPeek()` - locks and peeks without removing
- `112-121`: `Count` property - locks for read

**Usage**: Used exclusively by `TimerScheduler` to maintain scheduled task order.

### WorkerService (`Worker/WorkerService.cs`)

**Purpose**: Background service that processes queued tasks and restores pending tasks on startup.

**Critical Lines**:
- `5-11`: Constructor dependencies (worker queue, scope factory, dispatcher, config, executor, logger)
- `13-27`: `ExecuteAsync()` - main execution loop
- `18`: `ProcessPendingAsync()` - restores tasks from storage on startup
- `20-24`: Parallel execution options (configured via `MaxDegreeOfParallelism`)
- `26`: **Parallel Task Processing**: Uses `Parallel.ForEachAsync` with `WorkerQueue.DequeueAll()` async enumerable
- `29-99`: `ProcessPendingAsync()` - deserializes persisted tasks and redispatches
- `41`: Retrieves pending tasks from storage (statuses: `WaitingQueue`, `InProgress`, `Scheduled`)
- `54-58`: Task deserialization using `Type.GetType()` and `JsonConvert.DeserializeObject()`
- `67-75`: Recurring task deserialization
- `81-82`: Redispatch with original execution time, recurring config, and run count

**Lifecycle**: Runs as `IHostedService` (registered line 25 in `ServiceCollectionExtensions.cs`).

### ITaskStorage (`Storage/ITaskStorage.cs`)

**Purpose**: Abstract persistence interface for task state management.

**Critical Methods**:
- `16`: `Get()` - query tasks with expression predicate
- `23`: `GetAll()` - retrieve all tasks
- `31`: `Persist()` - save new task
- `38`: `RetrievePending()` - get tasks to restore on startup
- `46`: `SetQueued()` - update status to queued
- `54`: `SetInProgress()` - update status to in progress
- `61`: `SetCompleted()` - mark task complete
- `68`: `SetCancelledByUser()` - user-initiated cancellation
- `76`: `SetCancelledByService()` - service shutdown cancellation
- `86`: `SetStatus()` - generic status update with optional exception
- `94`: `GetCurrentRunCount()` - retrieve run count for recurring tasks
- `102`: `UpdateCurrentRun()` - increment run count and update next run time

**Implementations**:
- `MemoryTaskStorage.cs`: In-memory storage (for testing, registered via `.AddMemoryStorage()`)
- `EverTask.Storage.EfCore`: EF Core base implementation (separate package)
- `EverTask.Storage.SqlServer`: SQL Server provider (separate package)
- `EverTask.Storage.Sqlite`: SQLite provider (separate package)

**Thread Safety**: Storage accessed via scoped instances (`WorkerExecutor.cs:25`, `WorkerService.cs:31`).

### QueuedTask (`Storage/QueuedTask.cs`)

**Purpose**: Database entity representing persisted task state.

**Critical Properties**:
- `5`: `Id` - task persistence GUID
- `6`: `CreatedAtUtc` - task creation timestamp
- `7`: `LastExecutionUtc` - most recent execution timestamp
- `8`: `ScheduledExecutionUtc` - future execution time (null for immediate)
- `9-11`: `Type`, `Request`, `Handler` - serialized task metadata (assembly-qualified names)
- `12`: `Exception` - serialized exception if failed
- `13-19`: Recurring task metadata (`IsRecurring`, `RecurringTask`, `CurrentRunCount`, `MaxRuns`, `RunUntil`, `NextRunUtc`)
- `21`: `Status` - enum (`WaitingQueue`, `Queued`, `InProgress`, `Completed`, `Failed`, `CancelledByUser`, `CancelledByService`)
- `22-23`: Audit collections (`StatusAudits`, `RunsAudits`)

**Serialization**: `Type`, `Request`, `Handler`, `RecurringTask` store Newtonsoft.Json serialized data.

### RecurringTask (`Scheduler/Recurring/RecurringTask.cs`)

**Purpose**: Defines recurring task schedule with multiple interval types.

**Critical Properties**:
- `5`: `RunNow` - execute immediately on first run
- `6`: `InitialDelay` - delay before first execution
- `7`: `SpecificRunTime` - exact first run time
- `8-13`: Interval types (`CronInterval`, `SecondInterval`, `MinuteInterval`, `HourInterval`, `DayInterval`, `MonthInterval`)
- `14`: `MaxRuns` - maximum execution count (null = infinite)
- `15`: `RunUntil` - stop after date

**Critical Methods**:
- `21-58`: `CalculateNextRun()` - calculates next execution time based on interval and run count
- `23`: Stops if `currentRun >= MaxRuns`
- `27`: Stops if `RunUntil < current`
- `33-56`: First run logic - prioritizes `RunNow`, `SpecificRunTime`, `InitialDelay` over interval
- `50-55`: **30-Second Gap Rule**: Prevents closely spaced executions when runtime < next interval
- `60-81`: `GetNextOccurrence()` - calculates next run using interval hierarchy
- `62-68`: Cron expression takes precedence over other intervals
- `71-75`: Interval cascade - month -> day -> hour -> minute -> second

**Builder API**: Constructed via fluent `RecurringTaskBuilder` (see `Scheduler/Recurring/Builder/`).

### CancellationSourceProvider (`Worker/CancellationSourceProvider.cs`)

**Purpose**: Manages per-task cancellation token sources.

**Critical Lines**:
- `15`: `ConcurrentDictionary<Guid, CancellationTokenSource>` for thread-safe access
- `17-26`: `CreateToken()` - creates linked token source from service cancellation token
- `19-20`: Deletes existing token for task ID if present (handles retry scenarios)
- `28-32`: `TryGet()` - retrieves token source for task
- `34-40`: `Delete()` - disposes and removes token source
- `42-49`: `CancelTokenForTask()` - cancels and deletes token (used for user cancellation)

**Lifecycle**: Tokens deleted after task execution (`WorkerExecutor.cs:59`).

### WorkerBlacklist (`Worker/WorkerBlacklist.cs`)

**Purpose**: Prevents execution of user-cancelled tasks that are already in queue/scheduler.

**Critical Lines**:
- `7`: `ConcurrentDictionary<Guid, EmptyStruct>` for memory-efficient tracking
- `9-12`: `Add()` - blacklist task ID
- `14-17`: `IsBlacklisted()` - check if task ID is blacklisted
- `19-22`: `Remove()` - remove from blacklist after check

**Usage Flow**:
1. User calls `ITaskDispatcher.Cancel(taskId)` (`Dispatcher.cs:42-50`)
2. Task added to blacklist (`Dispatcher.cs:49`)
3. Storage marked as cancelled (`Dispatcher.cs:45`)
4. Cancellation token cancelled (`Dispatcher.cs:47`)
5. Task dequeued by `WorkerExecutor`, blacklist checked (`WorkerExecutor.cs:32-33`)
6. Task skipped, blacklist entry removed (`WorkerExecutor.cs:70`)

## Dependencies

### Core NuGet Packages (from `EverTask.csproj`)
- **Cronos**: Cron expression parsing for `CronInterval`
- **Microsoft.Extensions.Configuration.Abstractions**: Configuration binding
- **Microsoft.Extensions.DependencyInjection.Abstractions**: DI container abstractions
- **Microsoft.Extensions.Hosting.Abstractions**: `IHostedService` for `WorkerService`
- **Microsoft.Extensions.Logging**: Logging abstractions (wrapped by `IEverTaskLogger<T>`)
- **Newtonsoft.Json**: Serialization for task persistence (required for polymorphism)

### Project References
- **EverTask.Abstractions**: Interfaces (`IEverTask`, `ITaskDispatcher`, `IEverTaskHandler<T>`, `IRetryPolicy`, etc.)

## Entry Points

### Registration (`MicrosoftExtensionsDI/ServiceCollectionExtensions.cs`)

```csharp
services.AddEverTask(opt => opt
    .RegisterTasksFromAssembly(typeof(Program).Assembly)
    .SetMaxDegreeOfParallelism(10)
    .SetChannelOptions(1000)
    .SetDefaultRetryPolicy(new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(1)))
    .SetDefaultTimeout(TimeSpan.FromMinutes(5))
    .SetThrowIfUnableToPersist(true)
).AddSqlServerStorage(connectionString); // or .AddMemoryStorage()
```

**Registration Flow** (`ServiceCollectionExtensions.cs:5-29`):
1. `EverTaskServiceConfiguration` created and configured (line 8-9)
2. Core services registered as singletons (lines 16-24)
3. `WorkerService` registered as `IHostedService` (line 25)
4. Handlers scanned from assemblies via `HandlerRegistrar` (line 26)

**Handler Registration** (`MicrosoftExtensionsDI/HandlerRegistrar.cs`):
- Scans assemblies for `IEverTaskHandler<T>` implementations
- Registers handlers as transient services
- Adapted from MediatR's handler registration logic

### Task Execution

**Immediate Execution**:
```csharp
await dispatcher.Dispatch(new MyTask("data"), cancellationToken);
// Flow: Dispatcher -> Persist -> WorkerQueue -> WorkerExecutor
```

**Delayed Execution**:
```csharp
await dispatcher.Dispatch(new MyTask("data"), TimeSpan.FromHours(1), cancellationToken);
// Flow: Dispatcher -> Persist -> TimerScheduler -> (wait) -> WorkerQueue -> WorkerExecutor
```

**Recurring Execution**:
```csharp
await dispatcher.Dispatch(new MyTask("data"), recurring => recurring
    .Every(1).Days()
    .At(14, 30)
    .Until(DateTimeOffset.UtcNow.AddMonths(6)),
    cancellationToken);
// Flow: Dispatcher -> Persist -> TimerScheduler -> WorkerQueue -> WorkerExecutor -> TimerScheduler (loop)
```

### Startup Flow (`WorkerService.ExecuteAsync`)

1. `ProcessPendingAsync()` restores tasks from storage (lines 29-99)
2. `Parallel.ForEachAsync` starts consuming worker queue (line 26)
3. Each task executed by `WorkerExecutor.DoWork()` (lines 21-62)
4. Recurring tasks rescheduled after completion (lines 239-253)

## Critical Implementation Details

### Thread Safety

**Service Scoping**:
- `WorkerExecutor.cs:25` - New scope per task (required for DbContext-based storage)
- `WorkerService.cs:31` - New scope for pending task processing
- Handlers resolved from scoped service provider

**Concurrent Collections**:
- `ConcurrentPriorityQueue` - locks `PriorityQueue<T>` operations
- `CancellationSourceProvider` - uses `ConcurrentDictionary<Guid, CancellationTokenSource>`
- `WorkerBlacklist` - uses `ConcurrentDictionary<Guid, EmptyStruct>`

**Channel-Based Queue**:
- `WorkerQueue` uses `System.Threading.Channels` (naturally thread-safe)
- Bounded capacity with configurable full mode (`Wait`, `DropWrite`, `DropOldest`)

### Async Patterns

**Task Execution** (`WorkerExecutor.cs:77-127`):
- Uses `WaitAsync()` for cancellation (line 115, 121) - see comment line 109
- CPU-bound tasks use `Task.Factory.StartNew` with `LongRunning` (lines 93-95) - see comment line 93
- Timeout via linked `CancellationTokenSource` with proper disposal (lines 130-154) - see comment line 152

**Timer Callbacks** (`TimerScheduler.cs:89-91`):
- Fire-and-forget pattern for timer callbacks - see comment line 89
- Uses `ConfigureAwait(false)` on line 91 to avoid capturing context

**Event Publishing** (`WorkerExecutor.cs:301-316`):
- Fire-and-forget monitoring events (line 314 uses `_` discard)
- Errors logged but don't fail task execution (lines 295-298)

### Serialization

**Newtonsoft.Json Usage**:
- Required for polymorphic serialization (`Type.GetType()` + `JsonConvert.DeserializeObject()`)
- Task requests serialized: `TaskHandlerExecutor.cs:25`
- Recurring config serialized: `TaskHandlerExecutor.cs:38`
- Deserialization: `WorkerService.cs:54-75`

**Constraints**:
- Tasks must be JSON-serializable (use primitives, POCOs)
- Avoid complex object graphs, delegates, or non-serializable types
- `AssemblyQualifiedName` used for type resolution (`TaskHandlerExecutor.cs:26-27`)

### Retry Policies

**Configuration**:
- Default policy: `LinearRetryPolicy(3, TimeSpan.FromMilliseconds(500))` (`EverTaskServiceConfiguration.cs:15`)
- Per-handler override: Implement `IEverTaskHandlerOptions.RetryPolicy` property
- Execution: `WorkerExecutor.cs:110-127`

**Policy Interface** (from `EverTask.Abstractions`):
```csharp
public interface IRetryPolicy
{
    Task Execute(Func<CancellationToken, Task> action, IEverTaskLogger logger, CancellationToken ct);
}
```

**Built-in Policies**:
- `LinearRetryPolicy`: Fixed delay between retries
- `ExponentialRetryPolicy`: Exponential backoff
- `NoRetryPolicy`: No retries (fail immediately)

### Timeout Handling

**Configuration**:
- Default timeout: `null` (no timeout) (`EverTaskServiceConfiguration.cs:17`)
- Per-handler override: Implement `IEverTaskHandlerOptions.Timeout` property
- Enforcement: `WorkerExecutor.cs:112-117`, `130-154`

**Implementation** (`WorkerExecutor.cs:130-154`):
- Creates linked `CancellationTokenSource` with `CancelAfter(timeout)`
- Distinguishes timeout (`TimeoutException`) from user cancellation
- Properly disposes `CancellationTokenSource` (line 153) - see comment line 152

### CPU-Bound Operations

**Configuration**:
- Per-handler only: Implement `IEverTaskHandlerOptions.CpuBoundOperation` property
- Execution: `WorkerExecutor.cs:91-100`

**Implementation** (`WorkerExecutor.cs:93-95`):
- Uses `Task.Factory.StartNew` with `TaskCreationOptions.LongRunning`
- Dedicated thread prevents thread pool starvation - see comment line 93

### Lifecycle Callbacks

**Handler Overrides**:
- `OnStarted(Guid taskId)` - called before task execution (`WorkerExecutor.cs:40`)
- `OnCompleted(Guid taskId)` - called after successful completion (`WorkerExecutor.cs:49`)
- `OnError(Guid taskId, Exception? ex, string message)` - called on failure/cancellation (`WorkerExecutor.cs:220-233`)
- `DisposeAsyncCore()` - called after task completion (`WorkerExecutor.cs:157-169`)

**Execution Order**:
1. Task dequeued
2. Blacklist checked
3. Storage -> `InProgress`
4. `OnStarted` callback
5. Handler execution (with retry/timeout)
6. `DisposeAsync` if handler is `IAsyncDisposable`
7. Storage -> `Completed`
8. `OnCompleted` callback
9. (If error) Storage -> `Failed`, `OnError` callback

**Error Handling**:
- Callback exceptions logged but don't fail task (`WorkerExecutor.cs:182-186`, `199-204`)

## Extension Points

### Custom Task Handlers

**Implementation**:
```csharp
public record MyTaskRequest(string Data) : IEverTask;

public class MyTaskHandler : EverTaskHandler<MyTaskRequest>
{
    private readonly IMyService _service;

    public MyTaskHandler(IMyService service) => _service = service;

    public override async Task Handle(MyTaskRequest task, CancellationToken ct)
    {
        await _service.ProcessData(task.Data, ct);
    }

    // Optional overrides
    public override IRetryPolicy RetryPolicy => new ExponentialRetryPolicy(5, TimeSpan.FromSeconds(2));
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(10);
    public override bool CpuBoundOperation => false;

    public override ValueTask OnStarted(Guid taskId)
    {
        Console.WriteLine($"Task {taskId} started");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid taskId)
    {
        Console.WriteLine($"Task {taskId} completed");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid taskId, Exception? exception, string message)
    {
        Console.WriteLine($"Task {taskId} error: {message}");
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        // Cleanup logic
        await base.DisposeAsyncCore();
    }
}
```

**Registration**:
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(MyTaskHandler).Assembly));
```

### Custom Retry Policies

**Implementation**:
```csharp
public class CustomRetryPolicy : IRetryPolicy
{
    public async Task Execute(Func<CancellationToken, Task> action,
                              IEverTaskLogger logger,
                              CancellationToken ct)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                await action(ct);
                return;
            }
            catch (Exception ex) when (attempt < 4)
            {
                logger.LogWarning(ex, "Retry attempt {Attempt}", attempt + 1);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }
}
```

**Usage**:
- Global: `services.AddEverTask(opt => opt.SetDefaultRetryPolicy(new CustomRetryPolicy()))`
- Per-handler: Override `IEverTaskHandlerOptions.RetryPolicy` property

### Custom Storage Implementation

**Implementation**:
```csharp
public class RedisTaskStorage : ITaskStorage
{
    // Implement all ITaskStorage methods
    public async Task Persist(QueuedTask task, CancellationToken ct) { /* Redis logic */ }
    public async Task<QueuedTask[]> RetrievePending(CancellationToken ct) { /* Redis logic */ }
    // ... other methods
}
```

**Registration**:
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(assembly));
services.AddSingleton<ITaskStorage, RedisTaskStorage>();
```

### Custom Monitoring

**Implementation**:
```csharp
public class MyMonitor : ITaskMonitor
{
    private readonly IEverTaskWorkerExecutor _executor;

    public MyMonitor(IEverTaskWorkerExecutor executor)
    {
        _executor = executor;
        _executor.TaskEventOccurredAsync += OnTaskEventOccurred;
    }

    private async Task OnTaskEventOccurred(EverTaskEventData data)
    {
        Console.WriteLine($"[{data.SeverityLevel}] Task {data.TaskId}: {data.Message}");

        // Send to monitoring service
        await _monitoringService.SendEvent(data);
    }
}
```

**Registration**:
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(assembly));
services.AddSingleton<ITaskMonitor, MyMonitor>();
```

**Event Data** (`Monitoring/EverTaskEventData.cs`):
- `TaskId`, `TaskType`, `HandlerType`
- `SeverityLevel` (Information, Warning, Error)
- `Message`, `Exception`
- `CreatedAtUtc`

## Common Tasks

### Modify Default Configuration

**Location**: `EverTaskServiceConfiguration.cs:1-77`

**Examples**:
```csharp
services.AddEverTask(opt => opt
    .SetMaxDegreeOfParallelism(20)           // Line 31-35: Parallel task execution
    .SetChannelOptions(2000)                  // Line 19-23: Queue capacity
    .SetThrowIfUnableToPersist(false)         // Line 37-41: Continue on persistence failure
    .SetDefaultRetryPolicy(policy)            // Line 43-47: Global retry policy
    .SetDefaultTimeout(TimeSpan.FromMinutes(5)) // Line 49-53: Global timeout
);
```

### Add New Handler

1. Create task request: `public record MyTask(string Data) : IEverTask;`
2. Create handler: `public class MyTaskHandler : EverTaskHandler<MyTask> { ... }`
3. Register assembly: `opt.RegisterTasksFromAssembly(typeof(MyTaskHandler).Assembly)`
4. Dispatch: `await dispatcher.Dispatch(new MyTask("data"), ct);`

### Create Recurring Task

```csharp
// Every day at 14:30
await dispatcher.Dispatch(new DailyReportTask(), recurring => recurring
    .Every(1).Days()
    .At(14, 30),
    ct);

// Every hour on the hour
await dispatcher.Dispatch(new HourlyTask(), recurring => recurring
    .Every(1).Hours()
    .OnMinute(0),
    ct);

// Cron expression
await dispatcher.Dispatch(new CronTask(), recurring => recurring
    .Cron("0 0 * * MON"), // Every Monday at midnight
    ct);
```

**Builder Location**: `Scheduler/Recurring/Builder/RecurringTaskBuilder.cs`

### Cancel Running Task

```csharp
Guid taskId = await dispatcher.Dispatch(new LongRunningTask(), ct);
// Later...
await dispatcher.Cancel(taskId, ct);
```

**Flow** (`Dispatcher.cs:42-50`):
1. Storage marked as `CancelledByUser`
2. Cancellation token cancelled via `CancellationSourceProvider`
3. Task added to blacklist (prevents execution if already queued)

### Query Task Status

```csharp
var storage = serviceProvider.GetService<ITaskStorage>();
if (storage != null)
{
    var tasks = await storage.Get(t => t.Id == taskId, ct);
    var task = tasks.FirstOrDefault();
    if (task != null)
    {
        Console.WriteLine($"Status: {task.Status}");
        Console.WriteLine($"Created: {task.CreatedAtUtc}");
        Console.WriteLine($"Last Execution: {task.LastExecutionUtc}");
    }
}
```

### Override Per-Handler Options

```csharp
public class MyTaskHandler : EverTaskHandler<MyTask>
{
    public override IRetryPolicy RetryPolicy => new ExponentialRetryPolicy(10, TimeSpan.FromSeconds(5));
    public override TimeSpan? Timeout => TimeSpan.FromMinutes(30);
    public override bool CpuBoundOperation => true; // Use dedicated thread

    public override async Task Handle(MyTask task, CancellationToken ct)
    {
        // Handler logic
    }
}
```

### Access Task ID in Handler

```csharp
public class MyTaskHandler : EverTaskHandler<MyTask>
{
    public override async Task Handle(MyTask task, CancellationToken ct)
    {
        // Task ID not available in Handle method
        // Use OnStarted override to capture it
    }

    private Guid _taskId;

    public override ValueTask OnStarted(Guid taskId)
    {
        _taskId = taskId;
        return ValueTask.CompletedTask;
    }
}
```

## Testing Considerations

**In-Memory Storage**:
```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(assembly))
    .AddMemoryStorage(); // Thread-safe in-memory implementation
```

**Location**: `Storage/MemoryTaskStorage.cs`

**Parallelism**:
- Set `MaxDegreeOfParallelism = 1` for deterministic testing
- Use `.SetChannelOptions(capacity)` to test queue overflow scenarios

**Timer Testing**:
- `TimerScheduler.UpdateTimer()` is internal - use scheduled tasks in tests
- Debug builds expose `LastSetDelay` property for validation (`TimerScheduler.cs:11-14, 68-71`)

## Known Issues & Gotchas

1. **Serialization Constraints**: Tasks must be JSON-serializable. Avoid delegates, complex object graphs.
2. **Storage Thread Safety**: Always use scoped instances for DbContext-based storage (`WorkerExecutor.cs:25`).
3. **Cancellation Timing**: Tasks already executing cannot be interrupted - cancellation token must be respected in handler.
4. **Recurring Task Gaps**: 30-second minimum gap between initial run and first interval run (`RecurringTask.cs:53-54`).
5. **Large Delays**: Scheduled delays > 2 hours checked every 1.5 hours (`TimerScheduler.cs:66`).
6. **Blacklist Cleanup**: Blacklist entries removed after check - cancelled tasks in scheduler may still execute once.
7. **Event Publishing**: Monitoring events are fire-and-forget - exceptions don't fail task (`WorkerExecutor.cs:295-298`).

## Performance Notes

**Channel Capacity**: Default 500 (`EverTaskServiceConfiguration.cs:5`). Increase for high-throughput scenarios.

**Parallelism**: Default 1 (`EverTaskServiceConfiguration.cs:10`). Increase based on workload (CPU-bound vs I/O-bound).

**Timer Granularity**: 1.5-hour recheck for large delays (`TimerScheduler.cs:66`) balances memory and responsiveness.

**Lock Contention**: `ConcurrentPriorityQueue` uses exclusive locks - high-frequency scheduling may bottleneck.

**Scope Creation**: New scope per task execution (`WorkerExecutor.cs:25`) - overhead for high-frequency tasks.

## Related Files

- `EverTask.Abstractions`: Interfaces and base classes (`IEverTask`, `ITaskDispatcher`, `IEverTaskHandler<T>`, `EverTaskHandler<T>`)
- `EverTask.Storage.EfCore`: EF Core storage base implementation
- `EverTask.Storage.SqlServer`: SQL Server persistence provider
- `EverTask.Storage.Sqlite`: SQLite persistence provider
- `EverTask.Logging.Serilog`: Serilog logging integration
- `EverTask.Monitor.AspnetCore.SignalR`: SignalR monitoring integration
