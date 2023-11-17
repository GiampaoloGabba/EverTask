![EverTask Logo](https://raw.githubusercontent.com/GiampaoloGabba/EverTask/master/assets/logo-main.png)

[![Build](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml/badge.svg)](https://github.com/GiampaoloGabba/EverTask/actions/workflows/build.yml)
[![NuGet](https://img.shields.io/nuget/vpre/evertask.svg)](https://www.nuget.org/packages/evertask)

## Overview
EverTask is a .NET library for executing background tasks in .NET applications. It is designed to be simple and focuses on task persistence, ensuring that pending tasks resume upon application restart.

> This project is in its initial stages, more detailed documentation will be provided in the future.

## Features
| Feature                          | Description                                                                                                                        |
|----------------------------------|------------------------------------------------------------------------------------------------------------------------------------|
| **Background Task Execution**    | Easily run background tasks with parameters in .NET                                                                                |
| **Persistence**                  | Resumes pending tasks after application restarts.                                                                                  |
| **Managed Parallelism**          | Efficiently handles concurrent task execution with configurable parallelism.                                                       |
| **Scheduled and Delayed tasks**  | 🌟 Now available! Schedule tasks for future execution or delay them using a TimeSpan or DateTimeOffset.|
| **Async All The Way**            | Fully asynchronous architecture, enhancing performance and scalability in modern environments.                                     |
| **Simplicity by Design**         | Created for simplicity, using the latest .NET technologies.                                                                        |
| **Inspiration from MediaTr**     | Implementation based on creating requests and handlers.                                                                            |
| **Error Handling**               | Method overrides for error observation and task completion.                                                                        |
| **In-Memory Storage**            | Provides an in-memory storage solution for testing and lightweight applications.                                                   |
| **SQL Storage**                  | Includes support for SQL Server storage, enabling persistent task management.                                                      |
| **Serilog Integration**          | Supports integration with Serilog for detailed and customizable logging.                                                           |
| **Extensible Storage & Logging** | Designed to allow easy plug-in of additional database solutions or logging systems.                                                |                                                                      |


## Efficient Task Processing

EverTask employs a non-polling approach for task management, utilizing the .NET's `System.Threading.Channels` to create a `BoundedQueue`. This queue efficiently manages task execution without the need for constant database polling. Upon application restart after a stop, any unprocessed tasks are retrieved from the database in bulk and re-queued in the channel's queue for execution by the background service. This design ensures a seamless and efficient task processing cycle, even across application restarts.


## Creating Requests and Handlers with Lifecycle Control

This example demonstrates how to create a request and its corresponding handler in EverTask. The `SampleTaskRequest` and `SampleTaskRequestHandler` illustrate the basic structure. Additionally, the handler includes optional overrides that allow you to control and monitor the lifecycle of a background task, providing hooks for when a task starts, completes, or encounters an error.

```csharp
public record SampleTaskRequest(string TestProperty) : IEverTask;

public class SampleTaskRequestHanlder : EverTaskHandler<SampleTaskRequest>
{
    private readonly ILogger<SampleTaskRequestHanlder> _logger;

    public SampleTaskRequestHanlder(ILogger<SampleTaskRequestHanlder> logger)
    {
        _logger = logger;
    }

    public override Task Handle(SampleTaskRequest backgroundTask, CancellationToken cancellationToken)
    {
        _logger.LogInformation($"Property value: {backgroundTask.TestProperty}");
        return Task.CompletedTask;
    }

    // Optional Overrides
    public override ValueTask OnStarted(Guid persistenceId)
    {
        _logger.LogInformation($"====== TASK WITH ID {persistenceId} STARTED IN BACKGROUND ======");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnCompleted(Guid persistenceId)
    {
        _logger.LogInformation($"====== TASK WITH ID {persistenceId} COMPLETED IN BACKGROUND ======");
        return ValueTask.CompletedTask;
    }

    public override ValueTask OnError(Guid persistenceId, Exception? exception, string? message)
    {
        _logger.LogError(exception, $"Error in task with ID {persistenceId}: {message}");
        return ValueTask.CompletedTask;
    }
    
    protected override ValueTask DisposeAsyncCore()
    {
        _logger.LogInformation("====== TASK DISPOSED IN BACKGROUND ======");
        return base.DisposeAsyncCore();
    }
}
```

## Task Dispatch

To dispatch a task, obtain an instance of `ITaskDispatcher`. This can be done using Dependency Injection:

```csharp
// Retrieving ITaskDispatcher via method injection
var _dispatcher = serviceProvider.GetService<ITaskDispatcher>();

// Alternatively, ITaskDispatcher can be injected directly into the constructor of your class
_dispatcher.Dispatch(new SampleTaskRequest("Hello World"));
```

### Dispatching Tasks with Delay

You can also schedule tasks to be executed after a certain delay. This can be achieved using either `TimeSpan` or `DateTimeOffset`.

#### Using TimeSpan for Relative Delay
To delay task execution by a relative time period, use `TimeSpan`. This is useful when you want to postpone a task by a specific duration, such as 30 minutes or 2 hours from now.

```csharp
// Delaying task execution by 30 minutes
var delay = TimeSpan.FromMinutes(30);
_dispatcher.Dispatch(new SampleTaskRequest("Delayed Task"), delay);
```

#### Using DateTimeOffset for Absolute Delay

Alternatively, use `DateTimeOffset` for scheduling a task at a specific future point in time. This is particularly useful for tasks that need to be executed at a specific date and time, regardless of the current moment.

```csharp
// Scheduling a task for a specific time in the future
var scheduledTime = DateTimeOffset.Now.AddHours(2); // 2 hours from now
_dispatcher.Dispatch(new SampleTaskRequest("Scheduled Task"), scheduledTime);
```
&nbsp;
> 💡 **Remember:** Delayed and scheduled tasks are also persistent. If your app restarts, you won't lose these tasks – they'll be executed at the right time!



## Basic Configuration
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(50)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(Program).Assembly);
})
.AddMemoryStorage();
```

## Advanced Configuration
```csharp
builder.Services.AddEverTask(opt =>
{
    opt.SetChannelOptions(settingsManager.Settings.Properties.BackgroundQueueCapacity)
       .SetThrowIfUnableToPersist(true)
       .RegisterTasksFromAssembly(typeof(AppSettings).Assembly);
})
.AddSqlServerStorage(configuration.GetConnectionString("QueueWorkerSqlStorage")!,
    opt =>
    {
        opt.SchemaName          = "EverTask";
        opt.AutoApplyMigrations = true;
    })
.AddSerilog(opt => opt.ReadFrom.Configuration(configuration, new ConfigurationReaderOptions { SectionName = "EverTaskSerilog" }));
```

# Fluent Service Configuration

`EverTaskService` can be configured using a series of fluent methods, allowing a clear and user-friendly way to set up the service. These methods enable precise control over task processing, persistence, and parallel execution. Below are the available configuration methods, along with their default values and types:

### `SetChannelOptions (Overloaded Methods)`
- **Type:** `Action<BoundedChannelOptions>` or `int`
- **Default:** Capacity set to 100, `FullMode` set to `BoundedChannelFullMode.Wait`
- **Functionality:** Configures the behavior of the task queue. You can directly specify the queue capacity or provide a `BoundedChannelOptions` object. This defines the maximum number of tasks that can be queued and the behavior when the queue is full.

### `SetThrowIfUnableToPersist`
- **Type:** `bool`
- **Default:** `true`
- **Functionality:** Determines whether the service should throw an exception if it is unable to persist a task. When enabled, it ensures that task persistence failures are explicitly managed, aiding in data integrity.

### `SetMaxDegreeOfParallelism`
- **Type:** `int`
- **Default:** `1`
- **Functionality:** Sets the maximum number of tasks that can be executed concurrently. The default sequential execution can be adjusted to enable parallel processing, optimizing task throughput in multi-core systems.

### `RegisterTasksFromAssembly`
- **Functionality:** Facilitates the registration of task handlers from a single assembly. This is particularly beneficial for applications structured in a modular fashion, enabling easy integration of task handlers.

### `RegisterTasksFromAssemblies`
- **Functionality:** Allows for the registration of task handlers from multiple assemblies. This approach suits larger applications with distributed task handling logic spread across various modules or libraries.

## SQL Server Persistence and Logging

### SQL Server Storage
- **Default Schema:** Creates a new schema named `EverTask` by default. This approach avoids adding clutter to the main data schema.
- **Migration Table:** Places the Entity Framework Core migration table in the custom `EverTask` schema.
- **Schema Customization:** Allows specifying a different schema or using `null` to default to the main schema.
- **Migration Handling:** Option to apply database migrations automatically or handle them manually.
 
### Serilog Integration
- **Default Logging:** Uses .NET configured `ILogger` by default.
- **Serilog Option:** Enables adding Serilog as a separate logger for EverTask, with customizable options.
- **Example Configuration in appsettings.json:**
  ```json
  "EverTaskSerilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Default": "Information",
        "Microsoft": "Warning",
        "Microsoft.AspNetCore.SpaProxy": "Information",
        "Microsoft.Hosting.Lifetime": "Information",
        "Microsoft.EntityFrameworkCore.Database.Command": "Information"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "Logs/evertask-log-.txt",
          "rollingInterval": "Day",
          "fileSizeLimitBytes": 10485760,
          "retainedFileCountLimit": 10,
          "shared": true,
          "flushToDiskInterval": "00:00:01"
        }
      }
    ],
    "Enrich": [
      "FromLogContext",
      "WithMachineName",
      "WithThreadId"
    ],
    "Properties": {
      "Application": "CliClub"
    }
  }

## EverTask and EverTask.Abstractions

EverTask is complemented by the `EverTask.Abstractions` package, designed for use in Application projects where additional implementations are not required. This allows separation of concerns, keeping your application layer free from infrastructural code.

In your Infrastructure project, where EverTask is added, specify the assembly (or assemblies) containing `IEverTask` requests. This modular approach ensures that the application layer remains clean and focused, while the infrastructure layer handles task execution and management.

## Serialization and deserialization of Requests for Persistence

EverTask uses Newtonsoft.Json for serializing and deserializing task requests, due to its robust support for polymorphism and inheritance, features that are limited in System.Text.Json. It is recommended to use simple objects for task requests, preferably primitives or uncomplicated complex objects, to ensure smooth serialization. In cases where EverTask is unable to serialize a request, it will throw an exception during the `Dispatch` method. This design choice emphasizes reliability in task persistence, ensuring that only serializable tasks are queued for execution.


## Future Developments

| Feature                             | Description                                                                                                                                                                                |
|-------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **Web Dashboard**                   | Implement a simple web dashboard for monitoring tasks.                                                                                                                                     |
| **Recurring Tasks**       | Recurring tasks using cron expressions or fluent builder.                                                                                                                                  |
| **Support for New Storage Options** | Considering the inclusion of additional storage options like MySql, Postgres, and various DocumentDBs initially supported by EfCore, with the possibility of expanding to other databases. |
| **Improving documentation**         | docs needs more love...                                                                                                                                                                    |


&nbsp;

## 🌟 Acknowledgements

Special thanks to **[jbogard](https://github.com/jbogard)** for the **[MediaTr](https://github.com/jbogard/MediatR)** project, providing significant inspiration in the development of key components of this library, especially in the creation of: 

[`TaskDispatcher.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/TaskDispatcher.cs)

[`TaskHandlerExecutor.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerExecutor.cs)

[`TaskHandlerWrapper.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/Handler/TaskHandlerWrapper.cs)

[`HandlerRegistrar.cs`](https://github.com/GiampaoloGabba/EverTask/blob/master/src/EverTask/MicrosoftExtensionsDI/HandlerRegistrar.cs)

I have included comments within these files to acknowledge and reference the specific parts of the MediaTr project that inspired them.

Their approach and architecture have been instrumental in shaping the functionality and design of these elements.

This project includes code from [MediatR](https://github.com/jbogard/MediatR), which is licensed under the Apache 2.0 License. The full text of the license can be found in the [LICENSE](LICENSE) file.



