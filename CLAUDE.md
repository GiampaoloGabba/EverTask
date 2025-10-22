# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

EverTask is a .NET background task execution library inspired by MediatR. It provides persistent, resilient task execution with support for scheduled, delayed, and recurring tasks. The library targets .NET 6.0, 7.0, and 8.0.

## Solution Structure

The solution follows a modular architecture organized into several key areas:

- **src/EverTask**: Core library implementing the task dispatcher and worker executor
- **src/EverTask.Abstractions**: Lightweight package for application layers (contains interfaces like `IEverTask`, `ITaskDispatcher`)
- **src/Storage/**: Persistence implementations (EfCore base, SqlServer, Sqlite)
- **src/Logging/**: Logging integrations (Serilog)
- **src/Monitoring/**: Monitoring implementations (SignalR)
- **samples/**: Example implementations (AspnetCore, Console)
- **test/**: Test projects for core, logging, and storage

## Build and Test Commands

### Build
```bash
dotnet restore EverTask.sln
dotnet build EverTask.sln
```

### Run Tests
```bash
# Run all tests
dotnet test EverTask.sln

# Run core tests only
dotnet test test/EverTask.Tests/EverTask.Tests.csproj

# Run tests excluding SQL Server storage tests (requires container)
dotnet test test/EverTask.Tests/EverTask.Tests.csproj --filter FullyQualifiedName!~EverTask.Tests.Storage.SqlServer.SqlServerEfCoreTaskStorageTests

# Run storage tests
dotnet test test/EverTask.Tests.Storage/EverTask.Tests.Storage.csproj

# Run logging tests
dotnet test test/EverTask.Tests.Logging/EverTask.Tests.Logging.csproj
```

### Create NuGet Packages
```bash
dotnet pack src/EverTask.Abstractions/EverTask.Abstractions.csproj -o nupkg
dotnet pack src/EverTask/EverTask.csproj -o nupkg
dotnet pack src/Storage/EverTask.Storage.SqlServer/EverTask.Storage.SqlServer.csproj -o nupkg
dotnet pack src/Storage/EverTask.Storage.Sqlite/EverTask.Storage.Sqlite.csproj -o nupkg
dotnet pack src/Logging/EverTask.Logging.Serilog/EverTask.Logging.Serilog.csproj -o nupkg
dotnet pack src/Monitoring/EverTask.Monitor.AspnetCore.SignalR/EverTask.Monitor.AspnetCore.SignalR.csproj -o nupkg
```

### Run Examples
```bash
# ASP.NET Core example
dotnet run --project samples/EverTask.Example.AspnetCore/EverTask.Example.AspnetCore.csproj

# Console example
dotnet run --project samples/EverTask.Example.Console/EverTask.Example.Console.csproj
```

## Architecture Overview

### Task Execution Flow
1. **Dispatch**: Tasks are dispatched via `ITaskDispatcher` (implemented by `Dispatcher.cs`)
2. **Persistence**: Tasks are persisted to storage via `ITaskStorage` implementations
3. **Queueing**: Tasks enter a `BoundedQueue` (using `System.Threading.Channels`) for immediate execution, or are added to a `ConcurrentPriorityQueue` for scheduled/recurring tasks
4. **Execution**: The `WorkerExecutor` dequeues and executes tasks with retry policies, timeouts, and lifecycle callbacks
5. **Monitoring**: Events are published via `TaskEventOccurredAsync` for monitoring integrations

### Key Components

**Dispatcher** (`src/EverTask/Dispatcher/Dispatcher.cs`)
- Entry point for task dispatching
- Handles task persistence, scheduling, and queueing
- Adapted from MediatR's `Mediator.cs`

**WorkerExecutor** (`src/EverTask/Worker/WorkerExecutor.cs`)
- Executes tasks with configurable retry policies, timeouts, and CPU-bound handling
- Manages task lifecycle callbacks (`OnStarted`, `OnCompleted`, `OnError`)
- Publishes events to monitoring systems

**TaskHandlerExecutor** (`src/EverTask/Handler/TaskHandlerExecutor.cs`)
- Record type containing task execution metadata
- Converts to/from `QueuedTask` for persistence
- Adapted from MediatR's `NotificationHandlerExecutor.cs`

**ITaskStorage** (various implementations in `src/Storage/`)
- Abstract persistence layer
- Implementations: SqlServer, Sqlite, InMemory
- Stores task state, scheduled executions, and audit trails

**Scheduler** (`src/EverTask/Scheduler/`)
- Manages delayed and recurring task execution
- Supports cron expressions and fluent scheduling API
- Uses `ConcurrentPriorityQueue` for efficient scheduling

### Request/Handler Pattern

EverTask uses a request/handler pattern similar to MediatR:

```csharp
// Request
public record MyTaskRequest(string Data) : IEverTask;

// Handler
public class MyTaskHandler : EverTaskHandler<MyTaskRequest>
{
    public override Task Handle(MyTaskRequest task, CancellationToken cancellationToken)
    {
        // Task logic here
        return Task.CompletedTask;
    }
}
```

Handlers support optional overrides: `OnStarted`, `OnCompleted`, `OnError`, `OnRetry`, `DisposeAsyncCore`

### Retry Policy Architecture (v1.6.0+)

**Overview**: EverTask's retry system supports exception filtering and lifecycle callbacks to fail-fast on permanent errors while providing visibility into retry attempts.

**Key Files**:
- `src/EverTask.Abstractions/IRetryPolicy.cs` - Interface with default `ShouldRetry(Exception)` method
- `src/EverTask.Abstractions/LinearRetryPolicy.cs` - Built-in retry policy with fluent exception filtering API
- `src/EverTask.Abstractions/RetryPolicyExtensions.cs` - Predefined exception sets (database, network, all transient errors)
- `src/EverTask.Abstractions/EverTaskHandler.cs` - `OnRetry` virtual method (line ~94)
- `src/EverTask/Worker/WorkerExecutor.cs` - Integrates retry policy with `OnRetry` callbacks

**Exception Filtering Flow**:

1. Handler execution throws exception in `WorkerExecutor.ExecuteTask()`
2. `IRetryPolicy.Execute()` catches exception and calls `ShouldRetry(exception)`
3. `LinearRetryPolicy.ShouldRetry()` evaluates filters in priority order:
   - **Predicate** (`HandleWhen`): If configured, uses custom `Func<Exception, bool>` (highest priority)
   - **Whitelist** (`Handle<T>`): If configured, only retry if exception type matches whitelist
   - **Blacklist** (`DoNotHandle<T>`): If configured, retry all except blacklisted types
   - **Default**: Retry all except `OperationCanceledException` and `TimeoutException`
4. If `ShouldRetry()` returns `false`: Throw immediately (fail-fast)
5. If `ShouldRetry()` returns `true`: Wait for delay, invoke `onRetryCallback`, then retry

**OnRetry Callback Flow**:

1. `WorkerExecutor.ExecuteTask()` passes `onRetryCallback` lambda to `IRetryPolicy.Execute()`
2. Lambda invokes handler's `OnRetry()` virtual method with task ID, attempt number, exception, and delay
3. `LinearRetryPolicy.Execute()` calls `onRetryCallback` after delay, before retry attempt
4. Callback exceptions are logged but don't prevent retry (reliability over observability)
5. `OnRetry` is NOT called for initial execution, only retries (attempt numbers are 1-based)

**Implementation Details**:

- `IRetryPolicy.Execute()` signature includes optional `Func<int, Exception, TimeSpan, ValueTask>? onRetryCallback` parameter (backward compatible, defaults to null)
- `LinearRetryPolicy` uses `HashSet<Type>` for whitelist/blacklist with `Type.IsAssignableFrom()` for derived type matching (e.g., `Handle<IOException>()` also catches `FileNotFoundException`)
- Exception filter validation prevents mixing `Handle<T>()` and `DoNotHandle<T>()` (throws `InvalidOperationException` on first conflicting call)
- `OnRetry` attempt number is 1-based (first retry = 1, not 0) for user-friendly logging
- `WorkerExecutor` wraps `OnRetry` in try-catch to prevent callback failures from impacting retry execution

**Predefined Exception Sets** (extension methods):

```csharp
// Database transient errors
.HandleTransientDatabaseErrors()  // DbException, TimeoutException

// Network transient errors
.HandleTransientNetworkErrors()   // HttpRequestException, SocketException, WebException, TaskCanceledException

// All transient errors (combines above)
.HandleAllTransientErrors()
```

**Fluent API Examples**:

```csharp
// Whitelist (type-safe generics)
RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .Handle<DbException>()
    .Handle<HttpRequestException>();

// Whitelist (params Type[] for many types)
RetryPolicy = new LinearRetryPolicy(5, TimeSpan.FromSeconds(2))
    .Handle(typeof(DbException), typeof(SqlException), typeof(HttpRequestException));

// Blacklist
RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .DoNotHandle<ArgumentException>()
    .DoNotHandle<ValidationException>();

// Predicate (custom logic)
RetryPolicy = new LinearRetryPolicy(3, TimeSpan.FromSeconds(1))
    .HandleWhen(ex => ex is HttpRequestException httpEx && httpEx.StatusCode >= 500);
```

**Integration Points**:

- `IRetryPolicy.ShouldRetry(Exception)`: Default interface method (C# 8+) with backward-compatible default implementation
- `IRetryPolicy.Execute(...)`: Signature updated with optional `onRetryCallback` parameter (existing implementations remain compatible)
- `WorkerExecutor.ExecuteTask()`: Passes handler instance to callback lambda for `OnRetry()` invocation

### Dependency Injection

Register tasks from assemblies containing `IEverTask` implementations:

```csharp
services.AddEverTask(opt => opt.RegisterTasksFromAssembly(typeof(Program).Assembly))
    .AddMemoryStorage(); // or AddSqlServerStorage, AddSqliteStorage
```

## Package Management

This project uses **Central Package Management**. Package versions are defined in `Directory.Packages.props` with conditional versioning based on target framework (.NET 6, 7, or 8).

## Serialization

EverTask uses **Newtonsoft.Json** for task serialization/deserialization due to its robust polymorphism support. Keep task requests simple (primitives or basic objects) to ensure reliable persistence.

## MediatR Attribution

Several core files were adapted from MediatR (Apache 2.0 License). These files contain attribution comments:
- `src/EverTask/Dispatcher/Dispatcher.cs`
- `src/EverTask/Handler/TaskHandlerExecutor.cs`
- `src/EverTask/Handler/TaskHandlerWrapper.cs`
- `src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs`

## Version Management

Current version: 1.5.4 (defined in `Directory.Build.props`)

## CI/CD

- **Build**: Triggered on push/PR to master (.github/workflows/build.yml)
- **Release**: Manual workflow dispatch for NuGet publishing (.github/workflows/release.yml)

## Storage Implementations

When working with storage:
- EfCore base is in `src/Storage/EverTask.Storage.EfCore/`
- SQL Server uses schema `EverTask` by default (configurable)
- Migrations can be auto-applied or manual
- DbContext is scoped per task execution for thread safety (see `WorkerExecutor.cs:25`)