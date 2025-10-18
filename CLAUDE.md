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

Handlers support optional overrides: `OnStarted`, `OnCompleted`, `OnError`, `DisposeAsyncCore`

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